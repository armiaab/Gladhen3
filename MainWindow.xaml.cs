using Gladhen3.Models;
using Gladhen3.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;
using System.Runtime.InteropServices;

namespace Gladhen3;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<DocumentItem> _documentItems = [];
    private readonly DocumentService _documentService = new();
    private readonly PdfService _pdfService = new();
    private readonly Microsoft.Windows.ApplicationModel.Resources.ResourceLoader _resourceLoader = new();
    private bool _isGridView = true;
    private bool _isSelectMode;
    private bool _isInitialized;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        SetWindowIcon();

        DocumentGridView.ItemsSource = _documentItems;
        DocumentListView.ItemsSource = _documentItems;

        // The estimate answers "how big will this be when I save it", so it follows the list
        // itself rather than each of the dozen code paths that happen to mutate it.
        _documentItems.CollectionChanged += DocumentItems_CollectionChanged;

        _isInitialized = true;
        StatusTextBlock.Text = _resourceLoader.GetString("StatusReady");
        _ = AppSettings.LoadAsync();
        UpdateUIState();
    }

    private void SetWindowIcon()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

            appWindow.SetIcon("Assets/Square44x44Logo.png");
        }
        // Cosmetic: the window works perfectly well with the default icon.
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to set window icon");
        }
    }

    public MainWindow(IEnumerable<string> paths) : this()
    {
        if (paths?.Any() == true)
        {
            var fileNames = paths.Select(Path.GetFileName).Take(3).ToList();
            StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusLoadingNamedFiles"), string.Join(", ", fileNames));
            _ = LoadDocumentsAsync(paths);
        }
    }

    #region UI State

    private void UpdateUIState()
    {
        if (!_isInitialized) return;

        var hasItems = _documentItems.Count > 0;
        FadeTo(EmptyStatePanel, hasItems ? 0 : 1);

        var imageCount = _documentItems.Count(d => d.Type == DocumentType.Image);
        var pdfPageCount = _documentItems.Count(d => d.Type == DocumentType.PdfPage);
        ItemCountTextBlock.Text = DescribeContents(imageCount, pdfPageCount);

        SaveButton.IsEnabled = hasItems;
        UpdateSelectionInfo();
    }

    /// <summary>
    /// Summarises what is in the list for the header subtitle.
    /// </summary>
    /// <remarks>
    /// The single format string this replaced read "0 pages (0 images, 0 PDF pages)" on an
    /// empty window and "12 pages (0 images, 12 PDF pages)" for one PDF - counting things
    /// that are not there. Only the mixed case actually needs the breakdown.
    /// </remarks>
    private string DescribeContents(int imageCount, int pdfPageCount)
    {
        if (imageCount == 0 && pdfPageCount == 0) return string.Empty;
        if (pdfPageCount == 0) return string.Format(_resourceLoader.GetString("ItemCountImagesOnly"), imageCount);
        if (imageCount == 0) return string.Format(_resourceLoader.GetString("ItemCountPagesOnly"), pdfPageCount);

        return string.Format(_resourceLoader.GetString("ItemCountFormat"), imageCount + pdfPageCount, imageCount, pdfPageCount);
    }

    private void UpdateViewToggle()
    {
        if (!_isInitialized) return;

        if (GridViewToggle != null && ListViewToggle != null)
        {
            GridViewToggle.Checked -= GridViewToggle_Checked;
            ListViewToggle.Checked -= ListViewToggle_Checked;

            GridViewToggle.IsChecked = _isGridView;
            ListViewToggle.IsChecked = !_isGridView;

            GridViewToggle.Checked += GridViewToggle_Checked;
            ListViewToggle.Checked += ListViewToggle_Checked;
        }

        DocumentGridView.Visibility = _isGridView ? Visibility.Visible : Visibility.Collapsed;
        DocumentListView.Visibility = _isGridView ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateSelectMode()
    {
        if (!_isInitialized) return;

        if (!_isSelectMode)
        {
            try
            {
                if (DocumentGridView.SelectionMode == ListViewSelectionMode.Multiple)
                    DocumentGridView.SelectedItems.Clear();
                if (DocumentListView.SelectionMode == ListViewSelectionMode.Multiple)
                    DocumentListView.SelectedItems.Clear();
            }
            // WinUI can throw while the selection is being rebuilt underneath a mode switch.
            // The selection is about to be replaced anyway, so there is nothing to recover.
            catch (COMException ex)
            {
                Log.Debug(ex, "Selection was already being changed while clearing it");
            }
        }

        var mode = _isSelectMode ? ListViewSelectionMode.Multiple : ListViewSelectionMode.None;
        DocumentGridView.SelectionMode = mode;
        DocumentListView.SelectionMode = mode;

        SelectModeMenuItem.Text = _isSelectMode ? _resourceLoader.GetString("TextExitSelectMode") : _resourceLoader.GetString("TextEnterSelectMode");
        SelectionInfoPanel.Visibility = _isSelectMode ? Visibility.Visible : Visibility.Collapsed;

        UpdateSelectionInfo();
    }

    private void UpdateSelectionInfo()
    {
        if (!_isInitialized || !_isSelectMode) return;

        var view = _isGridView ? (ListViewBase)DocumentGridView : DocumentListView;
        var count = view.SelectedItems.Count;
        SelectionCountText.Text = count == 1 ? _resourceLoader.GetString("SelectionCountFormatSingle") : string.Format(_resourceLoader.GetString("SelectionCountFormatMultiple"), count);
    }

    private void ToggleSelectModeMenu_Click(object sender, RoutedEventArgs e)
    {
        SelectModeToggle.IsChecked = !SelectModeToggle.IsChecked;
    }

    private void SelectModeToggle_Checked(object sender, RoutedEventArgs e)
    {
        _isSelectMode = true;
        UpdateSelectMode();
        StatusTextBlock.Text = _resourceLoader.GetString("StatusSelectMode");
    }

    private void SelectModeToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        _isSelectMode = false;
        UpdateSelectMode();
        StatusTextBlock.Text = _resourceLoader.GetString("StatusReady");
    }

    #endregion

    #region Output Size Estimate

    /// <remarks>
    /// Estimating runs the real encoder over a sample of the largest images, which costs a
    /// few hundred milliseconds on a big scan. Dropping twenty files in one go must not queue
    /// up twenty of those, so the work waits for the list to settle first.
    /// </remarks>
    private static readonly TimeSpan EstimateDebounce = TimeSpan.FromMilliseconds(500);

    private DispatcherTimer? _estimateTimer;
    private CancellationTokenSource? _estimateCts;

    private void DocumentItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Reordering changes which page comes first, not how many bytes come out.
        if (e.Action == NotifyCollectionChangedAction.Move) return;

        ScheduleEstimate();
    }

    /// <summary>
    /// Asks for a fresh estimate, replacing any run already under way.
    /// </summary>
    private void ScheduleEstimate()
    {
        if (!_isInitialized) return;

        // Anything in flight is now answering a question about a list that no longer exists.
        _estimateCts?.Cancel();

        if (_documentItems.Count == 0)
        {
            _estimateTimer?.Stop();
            FadeTo(EstimatePanel, 0);
            return;
        }

        EstimateProgress.IsActive = true;
        EstimateProgress.Visibility = Visibility.Visible;
        EstimateIcon.Visibility = Visibility.Collapsed;
        EstimatedSizeText.Text = _resourceLoader.GetString("EstimatedSizeWorking");
        FadeTo(EstimatePanel, 1);

        if (_estimateTimer == null)
        {
            _estimateTimer = new DispatcherTimer { Interval = EstimateDebounce };
            _estimateTimer.Tick += EstimateTimer_Tick;
        }

        _estimateTimer.Stop();
        _estimateTimer.Start();
    }

    private async void EstimateTimer_Tick(object? sender, object e)
    {
        _estimateTimer?.Stop();
        await RunEstimateAsync();
    }

    private async Task RunEstimateAsync()
    {
        var items = _documentItems.ToList();
        if (items.Count == 0) return;

        var cts = new CancellationTokenSource();
        _estimateCts = cts;
        var token = cts.Token;

        try
        {
            var bytes = await Task.Run(() => PdfService.EstimateOutputSize(items, token), token);
            if (token.IsCancellationRequested) return;

            EstimateProgress.IsActive = false;
            EstimateProgress.Visibility = Visibility.Collapsed;
            EstimateIcon.Visibility = Visibility.Visible;
            EstimatedSizeText.Text = string.Format(
                _resourceLoader.GetString("EstimatedSizeFormat"),
                DocumentService.FormatFileSize((ulong)Math.Max(0, bytes)));
        }
        catch (OperationCanceledException)
        {
            // The list moved on while we were measuring; a newer run is already queued.
        }
        // An estimate is a convenience, not a precondition for saving. If one cannot be
        // produced - an unreadable source, a codec that refuses - the label goes away
        // rather than the failure being pushed at the user.
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not estimate output size");
            if (!token.IsCancellationRequested) FadeTo(EstimatePanel, 0);
        }
        finally
        {
            if (ReferenceEquals(_estimateCts, cts))
            {
                _estimateCts = null;
                cts.Dispose();
            }
        }
    }

    /// <summary>
    /// Fades <paramref name="element"/> to <paramref name="opacity"/>, collapsing it once it
    /// has faded out.
    /// </summary>
    /// <remarks>
    /// The empty state sits on top of the item views and accepts drops, so leaving it at zero
    /// opacity but still visible would silently swallow every pointer event aimed at the list.
    /// The visibility flip therefore has to happen at the far end of the animation - and only
    /// if the element really did end up transparent, since a later call may have reversed
    /// direction while this one was still running.
    /// </remarks>
    private static void FadeTo(UIElement element, double opacity, double milliseconds = 150)
    {
        var fadingIn = opacity > 0;
        if (fadingIn && element.Visibility == Visibility.Visible && element.Opacity == opacity) return;
        if (!fadingIn && element.Visibility == Visibility.Collapsed) return;

        if (fadingIn) element.Visibility = Visibility.Visible;

        var animation = new DoubleAnimation
        {
            To = opacity,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        if (!fadingIn)
        {
            storyboard.Completed += (_, _) =>
            {
                if (element.Opacity <= 0.01) element.Visibility = Visibility.Collapsed;
            };
        }
        storyboard.Begin();
    }

    #endregion

    #region File Operations

    private async void AddFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.Thumbnail,
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };

        foreach (var ext in DocumentService.GetAllSupportedExtensions())
            picker.FileTypeFilter.Add(ext);

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var files = await picker.PickMultipleFilesAsync();

        if (files?.Count > 0)
            await AddFilesAsync(files);
    }

    private async void AddImagesButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.Thumbnail,
            SuggestedStartLocation = PickerLocationId.PicturesLibrary
        };

        foreach (var ext in DocumentService.GetImageExtensions())
            picker.FileTypeFilter.Add(ext);

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var files = await picker.PickMultipleFilesAsync();

        if (files?.Count > 0)
            await AddFilesAsync(files);
    }

    private async void AddPdfsButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };

        foreach (var ext in DocumentService.GetPdfExtensions())
            picker.FileTypeFilter.Add(ext);

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var files = await picker.PickMultipleFilesAsync();

        if (files?.Count > 0)
            await AddFilesAsync(files);
    }

    private async Task AddFilesAsync(IReadOnlyList<StorageFile> files)
    {
        StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusLoadingFiles"), files.Count);

        var addedCount = 0;
        var progress = new Progress<(int current, int total, string fileName)>(p =>
        {
            StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusLoadingFileProgress"), p.current, p.total, p.fileName);
        });

        try
        {
            var loaded = await _documentService.CreateDocumentItemsBatchAsync(files, progress);

            await AddItemsInBatchesAsync(loaded.Items);
            addedCount = loaded.Items.Count;

            UpdateUIState();

            if (addedCount > 0)
            {
                StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusAddedPages"), addedCount);

                _ = LoadThumbnailsInBackgroundAsync(loaded.Items);
            }

            await ReportSkippedFilesAsync(loaded.FailedFiles);
        }
        // Event-handler boundary: this is the last place an exception can be turned into
        // something the user can see rather than a silent no-op.
        catch (Exception ex)
        {
            Log.Error(ex, "Error adding files");
            StatusTextBlock.Text = _resourceLoader.GetString("StatusUnexpectedError");
            await ShowDialogAsync(_resourceLoader.GetString("DialogTitleError"), _resourceLoader.GetString("DialogContentUnexpectedError"));
        }
    }

    private async Task LoadDocumentsAsync(IEnumerable<string> paths)
    {
        var pathList = paths.ToList();
        StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusLoadingFiles"), pathList.Count);

        try
        {
            var loaded = await _documentService.LoadDocumentsFromPathsAsync(pathList);

            await AddItemsInBatchesAsync(loaded.Items);

            UpdateUIState();

            if (loaded.Items.Count > 0)
            {
                StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusLoadedPages"), loaded.Items.Count);

                _ = LoadThumbnailsInBackgroundAsync(loaded.Items);
            }
            else
            {
                StatusTextBlock.Text = _resourceLoader.GetString("StatusNoSupportedFiles");
            }

            await ReportSkippedFilesAsync(loaded.FailedFiles);
        }
        // Event-handler boundary; the list is left as it was and the user is told.
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading documents");
            StatusTextBlock.Text = _resourceLoader.GetString("StatusUnexpectedError");
            await ShowDialogAsync(_resourceLoader.GetString("DialogTitleError"), _resourceLoader.GetString("DialogContentUnexpectedError"));
        }
    }

    /// <summary>
    /// Adds items to the bound collection in chunks, yielding to the dispatcher between
    /// chunks so large batches (multi-hundred-page PDFs) render progressively instead of
    /// blocking the UI thread until every item is added.
    /// </summary>
    private async Task AddItemsInBatchesAsync(IReadOnlyList<DocumentItem> items)
    {
        const int batchSize = 100;
        for (var i = 0; i < items.Count; i++)
        {
            _documentItems.Add(items[i]);
            if ((i + 1) % batchSize == 0)
                await Task.Delay(1);
        }
    }

    /// <summary>
    /// Loads thumbnails in background without blocking UI using parallel processing.
    /// Optimized with PDF document caching and controlled concurrency.
    /// </summary>
    private async Task LoadThumbnailsInBackgroundAsync(IList<DocumentItem> items)
    {
        if (items.Count == 0) return;

        var pdfGroups = new Dictionary<string, List<DocumentItem>>(StringComparer.OrdinalIgnoreCase);
        var imageItems = new List<DocumentItem>();

        foreach (var item in items)
        {
            if (item.Thumbnail != null) continue;

            if (item.Type == DocumentType.Image)
            {
                imageItems.Add(item);
            }
            else if (item.Type == DocumentType.PdfPage)
            {
                var key = item.SourcePdfPath ?? item.FilePath;
                if (!string.IsNullOrEmpty(key))
                {
                    if (!pdfGroups.TryGetValue(key, out var list))
                    {
                        list = new List<DocumentItem>();
                        pdfGroups[key] = list;
                    }
                    list.Add(item);
                }
            }
        }

        const int maxParallelism = 6;
        using var semaphore = new SemaphoreSlim(maxParallelism);

        var tasks = new List<Task>(imageItems.Count + pdfGroups.Count);

        foreach (var item in imageItems)
            tasks.Add(LoadImageThumbnailWithSemaphoreAsync(item, semaphore));

        foreach (var kvp in pdfGroups)
            tasks.Add(LoadPdfGroupThumbnailsWithSemaphoreAsync(kvp.Key, kvp.Value, semaphore));

        await Task.WhenAll(tasks);
    }

    private static async Task LoadImageThumbnailWithSemaphoreAsync(DocumentItem item, SemaphoreSlim semaphore)
    {
        await semaphore.WaitAsync();
        try
        {
            var thumbnail = await LoadImageThumbnailAsync(item.FilePath);
            if (thumbnail != null)
                item.Thumbnail = thumbnail;
        }
        // Thumbnails are decoration on a background task. A missing one costs a preview
        // image, not the page it stands for.
        catch (Exception ex)
        {
            Log.Warning(ex, "Error loading thumbnail for: {Path}", item.FilePath);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static async Task LoadPdfGroupThumbnailsWithSemaphoreAsync(string sourcePath, List<DocumentItem> items, SemaphoreSlim semaphore)
    {
        await semaphore.WaitAsync();
        try
        {
            if (!File.Exists(sourcePath)) return;

            var file = await StorageFile.GetFileFromPathAsync(sourcePath);
            var pdfDocument = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);

            const uint thumbnailWidth = 200;

            foreach (var item in items)
            {
                if (item.PageNumber < 1 || item.PageNumber > pdfDocument.PageCount)
                    continue;

                try
                {
                    using var page = pdfDocument.GetPage((uint)(item.PageNumber - 1));
                    using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();

                    var aspectRatio = page.Size.Height / page.Size.Width;
                    var options = new Windows.Data.Pdf.PdfPageRenderOptions
                    {
                        DestinationWidth = thumbnailWidth,
                        DestinationHeight = (uint)(thumbnailWidth * aspectRatio)
                    };

                    await page.RenderToStreamAsync(stream, options);

                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(stream);
                    item.Thumbnail = bitmap;
                }
                // Per-page isolation: one page that will not render should not cost the
                // other ninety-nine their thumbnails.
                catch (Exception ex)
                {
                    Log.Warning(ex, "Error loading PDF page thumbnail: {Path} page {Page}", sourcePath, item.PageNumber);
                }
            }
        }
        // Background task with no caller to return to; the pages are already in the list.
        catch (Exception ex)
        {
            Log.Warning(ex, "Error loading PDF thumbnails for: {Path}", sourcePath);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static async Task<BitmapImage?> LoadImageThumbnailAsync(string filePath)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(filePath);
            using var stream = await file.OpenAsync(FileAccessMode.Read);

            var bitmap = new BitmapImage { DecodePixelWidth = 200 };
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        // As above: decoration only.
        catch (Exception ex)
        {
            Log.Warning(ex, "Error loading image thumbnail: {Path}", filePath);
            return null;
        }
    }

    #endregion

    #region PDF Operations

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_documentItems.Count == 0)
        {
            await ShowDialogAsync(_resourceLoader.GetString("DialogTitleNoPages"), _resourceLoader.GetString("DialogContentNoPages"));
            return;
        }

        var savePicker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = _resourceLoader.GetString("SuggestedFileNameDocument")
        };
        savePicker.FileTypeChoices.Add(_resourceLoader.GetString("FileTypePdfDocument"), new List<string> { ".pdf" });

        InitializeWithWindow.Initialize(savePicker, WindowNative.GetWindowHandle(this));

        StorageFile file;
        try
        {
            file = await savePicker.PickSaveFileAsync();
        }
        catch (COMException comEx)
        {
            Log.Warning(comEx, "Save file picker failed (COMException)");
            await ShowDialogAsync(_resourceLoader.GetString("DialogTitleSaveFailed"), _resourceLoader.GetString("DialogContentSaveFailedPicker"));
            StatusTextBlock.Text = _resourceLoader.GetString("StatusSaveCancelled");
            return;
        }
        // The shell picker is out-of-process and can fail in ways that are not COM errors.
        // Either way the user gets told the dialog would not open, rather than nothing.
        catch (Exception ex)
        {
            Log.Warning(ex, "Save file picker failed");
            await ShowDialogAsync(_resourceLoader.GetString("DialogTitleSaveFailed"), string.Format(_resourceLoader.GetString("DialogContentSaveFailedPickerException"), ex.Message));
            StatusTextBlock.Text = _resourceLoader.GetString("StatusSaveCancelled");
            return;
        }

        if (file == null)
        {
            StatusTextBlock.Text = _resourceLoader.GetString("StatusCancelled");
            return;
        }

        var items = _documentItems.ToList();
        var outputPath = file.Path;

        StatusTextBlock.Text = _resourceLoader.GetString("StatusCreatingPdf");
        SaveButton.IsEnabled = false;
        BusyBar.Visibility = Visibility.Visible;
        try
        {
            while (true)
            {
                try
                {
                    var result = await Task.Run(() => _pdfService.CreatePdfFromDocuments(items, outputPath));

                    StatusTextBlock.Text = _resourceLoader.GetString("StatusPdfCreatedSuccessfully");
                    await ShowDialogAsync(_resourceLoader.GetString("DialogTitleSuccess"), string.Format(_resourceLoader.GetString("DialogContentPdfSaved"), outputPath));

                    // A partial save is still a save, but the user has to know what is missing.
                    if (result.SkippedItems.Count > 0)
                    {
                        Log.Warning("{Count} item(s) omitted from {Path}", result.SkippedItems.Count, outputPath);
                        await ShowDialogAsync(
                            _resourceLoader.GetString("DialogTitleSomeFilesSkipped"),
                            string.Format(_resourceLoader.GetString("DialogContentPagesSkipped"), FormatNameList(result.SkippedItems)));
                    }
                    break;
                }
                // Only a locked destination is offered a retry - retrying anything else just
                // repeats the same failure.
                catch (PdfOperationException ex) when (ex.Reason == PdfFailureReason.FileInUse)
                {
                    Log.Warning(ex, "Destination in use: {Path}", outputPath);

                    var dialog = new ContentDialog
                    {
                        Title = _resourceLoader.GetString("DialogTitleFileInUse"),
                        Content = string.Format(_resourceLoader.GetString("DialogContentFileInUse"), Path.GetFileName(outputPath)),
                        PrimaryButtonText = _resourceLoader.GetString("DialogButtonRetry"),
                        SecondaryButtonText = _resourceLoader.GetString("DialogButtonChooseLocation"),
                        CloseButtonText = _resourceLoader.GetString("DialogButtonCancel"),
                        XamlRoot = Content.XamlRoot
                    };

                    var result = await dialog.ShowAsync();
                    if (result == ContentDialogResult.Primary)
                    {
                        continue;
                    }
                    else if (result == ContentDialogResult.Secondary)
                    {
                        var newPicker = new FileSavePicker
                        {
                            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                            SuggestedFileName = Path.GetFileNameWithoutExtension(outputPath)
                        };
                        newPicker.FileTypeChoices.Add(_resourceLoader.GetString("FileTypePdfDocument"), new List<string> { ".pdf" });
                        InitializeWithWindow.Initialize(newPicker, WindowNative.GetWindowHandle(this));

                        try
                        {
                            var newFile = await newPicker.PickSaveFileAsync();
                            if (newFile == null)
                            {
                                StatusTextBlock.Text = _resourceLoader.GetString("StatusCancelled");
                                return;
                            }
                            outputPath = newFile.Path;
                            continue;
                        }
                        catch (COMException comEx)
                        {
                            Log.Warning(comEx, "Save file picker failed (COMException) when choosing new location");
                            await ShowDialogAsync(_resourceLoader.GetString("DialogTitleSaveFailed"), _resourceLoader.GetString("DialogContentSaveFailedPickerShort"));
                            StatusTextBlock.Text = _resourceLoader.GetString("StatusSaveCancelled");
                            return;
                        }
                    }
                    else
                    {
                        StatusTextBlock.Text = _resourceLoader.GetString("StatusCancelled");
                        return;
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    Log.Warning(ex, "Access denied while saving PDF: {Path}", outputPath);

                    var dialog = new ContentDialog
                    {
                        Title = _resourceLoader.GetString("DialogTitleAccessDenied"),
                        Content = string.Format(_resourceLoader.GetString("DialogContentAccessDenied"), Path.GetFileName(outputPath)),
                        PrimaryButtonText = "Choose Location",
                        CloseButtonText = _resourceLoader.GetString("DialogButtonCancel"),
                        XamlRoot = Content.XamlRoot
                    };

                    var result = await dialog.ShowAsync();
                    if (result == ContentDialogResult.Primary)
                    {
                        var newPicker = new FileSavePicker
                        {
                            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                            SuggestedFileName = Path.GetFileNameWithoutExtension(outputPath)
                        };
                        newPicker.FileTypeChoices.Add(_resourceLoader.GetString("FileTypePdfDocument"), new List<string> { ".pdf" });
                        InitializeWithWindow.Initialize(newPicker, WindowNative.GetWindowHandle(this));

                        try
                        {
                            var newFile = await newPicker.PickSaveFileAsync();
                            if (newFile == null)
                            {
                                StatusTextBlock.Text = _resourceLoader.GetString("StatusCancelled");
                                return;
                            }
                            outputPath = newFile.Path;
                            continue;
                        }
                        catch (COMException comEx)
                        {
                            Log.Warning(comEx, "Save file picker failed (COMException) when choosing new location");
                            await ShowDialogAsync(_resourceLoader.GetString("DialogTitleSaveFailed"), _resourceLoader.GetString("DialogContentSaveFailedFileInUse"));
                            StatusTextBlock.Text = _resourceLoader.GetString("StatusSaveCancelled");
                            return;
                        }
                    }
                    else
                    {
                        StatusTextBlock.Text = _resourceLoader.GetString("StatusCancelled");
                        return;
                    }
                }
                // Expected, actionable failures carry a reason the UI can explain properly.
                catch (PdfOperationException ex)
                {
                    Log.Warning(ex, "Could not create PDF ({Reason}): {Path}", ex.Reason, outputPath);
                    var (title, message) = DescribeFailure(ex);
                    StatusTextBlock.Text = _resourceLoader.GetString("StatusError").Replace("{0}", title);
                    await ShowDialogAsync(title, message);
                    return;
                }
                // Anything else is a defect rather than a situation, so the user gets a plain
                // apology and the detail goes to the log instead of into a dialog.
                catch (Exception ex)
                {
                    Log.Error(ex, "Unexpected failure creating PDF: {Path}", outputPath);
                    StatusTextBlock.Text = _resourceLoader.GetString("StatusUnexpectedError");
                    await ShowDialogAsync(_resourceLoader.GetString("DialogTitleError"), _resourceLoader.GetString("DialogContentUnexpectedError"));
                    return;
                }
            }
        }
        finally
        {
            SaveButton.IsEnabled = true;
            BusyBar.Visibility = Visibility.Collapsed;
        }
    }

    #endregion

    #region View & Preview

    private void GridViewToggle_Checked(object sender, RoutedEventArgs e)
    {
        _isGridView = true;
        UpdateViewToggle();
    }

    private void GridViewToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_isGridView && GridViewToggle != null)
        {
            GridViewToggle.IsChecked = true;
        }
    }

    private void ListViewToggle_Checked(object sender, RoutedEventArgs e)
    {
        _isGridView = false;
        UpdateViewToggle();
    }

    private void ListViewToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (!_isGridView && ListViewToggle != null)
        {
            ListViewToggle.IsChecked = true;
        }
    }

    private void SwitchToGridView_Click(object sender, RoutedEventArgs e)
    {
        _isGridView = true;
        UpdateViewToggle();
    }

    private void SwitchToListView_Click(object sender, RoutedEventArgs e)
    {
        _isGridView = false;
        UpdateViewToggle();
    }

    private void DocumentView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectionInfo();
    }

    private async void DocumentView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        try
        {
            if (_isSelectMode) return;

            if (_documentItems.Count == 0) return;

            var element = e.OriginalSource as FrameworkElement;
            DocumentItem? clickedItem = null;

            while (element != null)
            {
                if (element.DataContext is DocumentItem item)
                {
                    clickedItem = item;
                    break;
                }

                if (element == DocumentGridView || element == DocumentListView)
                    break;

                element = element.Parent as FrameworkElement;
            }

            if (clickedItem != null && !string.IsNullOrEmpty(clickedItem.FilePath))
            {
                await ShowPreviewDialogAsync(clickedItem);
            }
        }
        // Event-handler boundary for an optional view; the list itself is unaffected.
        catch (Exception ex)
        {
            Log.Error(ex, "Error opening preview");
            StatusTextBlock.Text = _resourceLoader.GetString("StatusErrorOpeningPreview");
        }
    }

    private async Task ShowPreviewDialogAsync(DocumentItem item)
    {
        var currentIndex = _documentItems.IndexOf(item);
        if (currentIndex < 0) return;

        await ShowPreviewDialogAtIndexAsync(currentIndex);
    }

    private async Task ShowPreviewDialogAtIndexAsync(int startIndex)
    {
        var currentIndex = startIndex;
        var pdfCache = new Dictionary<string, Windows.Data.Pdf.PdfDocument>(StringComparer.OrdinalIgnoreCase);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            MinWidth = 800,
            MaxWidth = 1000
        };

        var mainGrid = new Grid();
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Footer

        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var imageContainer = new Border
        {
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            CornerRadius = new CornerRadius(8),
            Height = 500,
            Margin = new Thickness(8, 0, 8, 0)
        };

        var image = new Image
        {
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var scrollViewer = new ScrollViewer
        {
            Content = image,
            ZoomMode = ZoomMode.Enabled,
            MinZoomFactor = 0.1f,
            MaxZoomFactor = 5f,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        imageContainer.Child = scrollViewer;
        Grid.SetRow(imageContainer, 1);
        Grid.SetColumn(imageContainer, 1);

        var prevButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE76B", FontSize = 16 },
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(20),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.8
        };
        ToolTipService.SetToolTip(prevButton, "Previous (←)");
        Grid.SetRow(prevButton, 1);
        Grid.SetColumn(prevButton, 0);

        var nextButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE76C", FontSize = 16 },
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(20),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.8
        };
        ToolTipService.SetToolTip(nextButton, "Next (→)");
        Grid.SetRow(nextButton, 1);
        Grid.SetColumn(nextButton, 2);

        var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(headerGrid, 0);
        Grid.SetColumn(headerGrid, 0);
        Grid.SetColumnSpan(headerGrid, 3);

        var titleText = new TextBlock
        {
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(titleText, 0);

        var headerActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        Grid.SetColumn(headerActions, 1);

        var zoomOutButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE71F", FontSize = 14 },
            Width = 32,
            Height = 32
        };
        ToolTipService.SetToolTip(zoomOutButton, _resourceLoader.GetString("TooltipZoomOut"));

        var zoomText = new TextBlock
        {
            Text = "100%",
            VerticalAlignment = VerticalAlignment.Center,
            Width = 45,
            TextAlignment = TextAlignment.Center,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };

        var zoomInButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE8A3", FontSize = 14 },
            Width = 32,
            Height = 32
        };
        ToolTipService.SetToolTip(zoomInButton, _resourceLoader.GetString("TooltipZoomIn"));

        var fitButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE9A6", FontSize = 14 },
            Width = 32,
            Height = 32,
            Margin = new Thickness(8, 0, 0, 0)
        };
        ToolTipService.SetToolTip(fitButton, _resourceLoader.GetString("TooltipFitToWindow"));

        var openButton = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new FontIcon { Glyph = "\uE8A7", FontSize = 12 },
                    new TextBlock { Text = _resourceLoader.GetString("ButtonOpenText"), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 }
                }
            },
            Margin = new Thickness(8, 0, 0, 0)
        };

        headerActions.Children.Add(zoomOutButton);
        headerActions.Children.Add(zoomText);
        headerActions.Children.Add(zoomInButton);
        headerActions.Children.Add(fitButton);
        headerActions.Children.Add(openButton);
        headerGrid.Children.Add(titleText);
        headerGrid.Children.Add(headerActions);

        var footerGrid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(footerGrid, 2);
        Grid.SetColumn(footerGrid, 0);
        Grid.SetColumnSpan(footerGrid, 3);

        var fileInfoPanel = new StackPanel { Spacing = 2 };
        Grid.SetColumn(fileInfoPanel, 0);

        var filePathText = new TextBlock
        {
            FontSize = 11,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextSelectionEnabled = true
        };

        var fileDetailsText = new TextBlock
        {
            FontSize = 11,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"]
        };

        fileInfoPanel.Children.Add(filePathText);
        fileInfoPanel.Children.Add(fileDetailsText);

        var pageIndicator = new Border
        {
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 6, 12, 6)
        };
        Grid.SetColumn(pageIndicator, 1);

        var pageText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        pageIndicator.Child = pageText;

        footerGrid.Children.Add(fileInfoPanel);
        footerGrid.Children.Add(pageIndicator);

        mainGrid.Children.Add(headerGrid);
        mainGrid.Children.Add(prevButton);
        mainGrid.Children.Add(imageContainer);
        mainGrid.Children.Add(nextButton);
        mainGrid.Children.Add(footerGrid);

        dialog.Content = mainGrid;
        dialog.CloseButtonText = _resourceLoader.GetString("DialogButtonClose");

        var loadingRing = new ProgressRing
        {
            IsActive = false,
            Width = 48,
            Height = 48,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        scrollViewer.ViewChanged += (s, args) =>
        {
            var zoomPercent = (int)(scrollViewer.ZoomFactor * 100);
            zoomText.Text = $"{zoomPercent}%";
        };

        async Task LoadItemAsync(int index)
        {
            if (index < 0 || index >= _documentItems.Count) return;

            var item = _documentItems[index];
            currentIndex = index;

            titleText.Text = item.DisplayName;
            filePathText.Text = item.FilePath;
            pageText.Text = $"{index + 1} / {_documentItems.Count}";

            prevButton.IsEnabled = index > 0;
            nextButton.IsEnabled = index < _documentItems.Count - 1;
            prevButton.Opacity = index > 0 ? 0.8 : 0.3;
            nextButton.Opacity = index < _documentItems.Count - 1 ? 0.8 : 0.3;

            if (File.Exists(item.FilePath))
            {
                var fileInfo = new FileInfo(item.FilePath);
                var details = item.Type == DocumentType.PdfPage
                    ? $"{item.FileSize} • Page {item.PageNumber} of {item.TotalPages} • {fileInfo.LastWriteTime:g}"
                    : $"{item.FileSize} • {fileInfo.LastWriteTime:g}";
                fileDetailsText.Text = details;
            }

            image.Source = null;
            scrollViewer.Content = loadingRing;
            loadingRing.IsActive = true;

            try
            {
                BitmapImage? bitmap = null;

                if (item.Type == DocumentType.Image && File.Exists(item.FilePath))
                {
                    bitmap = new BitmapImage();
                    var file = await StorageFile.GetFileFromPathAsync(item.FilePath);
                    using var stream = await file.OpenAsync(FileAccessMode.Read);
                    await bitmap.SetSourceAsync(stream);
                }
                else if (item.Type == DocumentType.PdfPage)
                {
                    bitmap = await RenderPdfPageHighResAsync(item, pdfCache);
                    bitmap ??= item.Thumbnail;
                }

                loadingRing.IsActive = false;
                image.Source = bitmap;
                scrollViewer.Content = image;

                if (bitmap == null)
                {
                    scrollViewer.Content = new TextBlock
                    {
                        Text = _resourceLoader.GetString("TextUnableToLoadPreview"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"]
                    };
                }
                else
                {
                    ApplyFitZoom();
                }
            }
            // Boundary for the preview pane, which replaces its own content with an error
            // message rather than propagating.
            catch (Exception ex)
            {
                Log.Error(ex, "Error loading preview");
                loadingRing.IsActive = false;
                scrollViewer.Content = new TextBlock
                {
                    Text = _resourceLoader.GetString("TextErrorLoadingPreview"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"]
                };
            }
        }

        void ApplyFitZoom()
        {
            if (image.Source is not BitmapImage bmp || bmp.PixelWidth == 0 || bmp.PixelHeight == 0) return;
            var cw = imageContainer.ActualWidth > 0 ? imageContainer.ActualWidth : 700;
            var ch = imageContainer.ActualHeight > 0 ? imageContainer.ActualHeight : 500;
            var fitZoom = (float)Math.Clamp(Math.Min(cw / bmp.PixelWidth, ch / bmp.PixelHeight), 0.05f, 5f);
            scrollViewer.ChangeView(0, 0, fitZoom);
            zoomText.Text = $"{(int)(fitZoom * 100)}%";
        }

        // Event handlers
        prevButton.Click += async (s, args) =>
        {
            if (currentIndex > 0)
                await LoadItemAsync(currentIndex - 1);
        };

        nextButton.Click += async (s, args) =>
        {
            if (currentIndex < _documentItems.Count - 1)
                await LoadItemAsync(currentIndex + 1);
        };

        openButton.Click += async (s, args) =>
        {
            try
            {
                var item = _documentItems[currentIndex];
                var file = await StorageFile.GetFileFromPathAsync(item.FilePath);
                await Launcher.LaunchFileAsync(file);
            }
            // Nothing visible happened if this failed, so the user is told rather than left
            // wondering whether the button works.
            catch (Exception ex)
            {
                Log.Error(ex, "Could not open the file in another application");
                StatusTextBlock.Text = _resourceLoader.GetString("StatusUnexpectedError");
            }
        };

        zoomInButton.Click += (s, args) =>
        {
            var newZoom = Math.Min(scrollViewer.ZoomFactor * 1.25f, 5f);
            scrollViewer.ChangeView(null, null, newZoom);
        };

        zoomOutButton.Click += (s, args) =>
        {
            var newZoom = Math.Max(scrollViewer.ZoomFactor / 1.25f, 0.1f);
            scrollViewer.ChangeView(null, null, newZoom);
        };

        fitButton.Click += (s, args) => ApplyFitZoom();

        dialog.KeyDown += async (s, args) =>
        {
            switch (args.Key)
            {
                case VirtualKey.Left:
                    if (currentIndex > 0)
                    {
                        await LoadItemAsync(currentIndex - 1);
                        args.Handled = true;
                    }
                    break;
                case VirtualKey.Right:
                    if (currentIndex < _documentItems.Count - 1)
                    {
                        await LoadItemAsync(currentIndex + 1);
                        args.Handled = true;
                    }
                    break;
                case VirtualKey.Add:
                    var zoomIn = Math.Min(scrollViewer.ZoomFactor * 1.25f, 5f);
                    scrollViewer.ChangeView(null, null, zoomIn);
                    args.Handled = true;
                    break;
                case VirtualKey.Subtract:
                    var zoomOut = Math.Max(scrollViewer.ZoomFactor / 1.25f, 0.1f);
                    scrollViewer.ChangeView(null, null, zoomOut);
                    args.Handled = true;
                    break;
                case VirtualKey.Number0:
                    scrollViewer.ChangeView(0, 0, 1f);
                    args.Handled = true;
                    break;
            }
        };

        dialog.Opened += (s, e) => ApplyFitZoom();

        await LoadItemAsync(startIndex);

        await dialog.ShowAsync();
    }

    private static async Task<BitmapImage?> RenderPdfPageHighResAsync(
        DocumentItem item, Dictionary<string, Windows.Data.Pdf.PdfDocument> pdfCache)
    {
        try
        {
            var sourcePath = item.SourcePdfPath ?? item.FilePath;
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                return null;

            if (!pdfCache.TryGetValue(sourcePath, out var pdfDocument))
            {
                var file = await StorageFile.GetFileFromPathAsync(sourcePath);
                pdfDocument = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
                pdfCache[sourcePath] = pdfDocument;
            }

            if (item.PageNumber < 1 || item.PageNumber > pdfDocument.PageCount)
                return null;

            using var page = pdfDocument.GetPage((uint)(item.PageNumber - 1));
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();

            var options = new Windows.Data.Pdf.PdfPageRenderOptions
            {
                DestinationWidth = 800,
                DestinationHeight = (uint)(800 * page.Size.Height / page.Size.Width)
            };

            await page.RenderToStreamAsync(stream, options);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        // The caller falls back to the existing thumbnail, so a failure here costs sharpness
        // rather than function.
        catch (Exception ex)
        {
            Log.Warning(ex, "Falling back to the thumbnail: the full-size page would not render");
            return null;
        }
    }
    #endregion

    #region Drag & Drop

    private void DocumentView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        StatusTextBlock.Text = _resourceLoader.GetString("StatusReordering");
    }

    private void DocumentView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (args.DropResult == Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move)
            StatusTextBlock.Text = _resourceLoader.GetString("StatusReorderComplete");
    }

    private void DocumentView_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        e.DragUIOverride.Caption = _resourceLoader.GetString("DragDropAdd");
        e.DragUIOverride.IsContentVisible = true;
        e.DragUIOverride.IsCaptionVisible = true;
        StatusTextBlock.Text = _resourceLoader.GetString("StatusDropToAdd");
    }

    private async void DocumentView_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            return;

        var items = await e.DataView.GetStorageItemsAsync();
        var files = items.OfType<StorageFile>()
            .Where(f => DocumentService.IsSupportedFile(f.Path))
            .ToList();

        if (files.Count > 0)
            await AddFilesAsync(files);
    }

    #endregion

    #region Selection & Sorting

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isSelectMode)
        {
            SelectModeToggle.IsChecked = true;
        }

        if (_isGridView)
            DocumentGridView.SelectAll();
        else
            DocumentListView.SelectAll();

        StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusSelectedPages"), _documentItems.Count);
    }

    private void DeselectAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSelectMode)
        {
            DocumentGridView.SelectedItems.Clear();
            DocumentListView.SelectedItems.Clear();
        }
        StatusTextBlock.Text = _resourceLoader.GetString("StatusDeselectedAll");
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (!_isSelectMode)
        {
            StatusTextBlock.Text = _resourceLoader.GetString("StatusEnterSelectModeFirst");
            return;
        }

        var view = _isGridView ? (ListViewBase)DocumentGridView : DocumentListView;
        var selectedItems = view.SelectedItems.Cast<DocumentItem>().ToList();

        if (selectedItems.Count == 0)
        {
            StatusTextBlock.Text = _resourceLoader.GetString("StatusNoPagesSelected");
            return;
        }

        foreach (var item in selectedItems)
            _documentItems.Remove(item);

        UpdateUIState();
        StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusRemovedPages"), selectedItems.Count);
    }

    private void RemoveAll_Click(object sender, RoutedEventArgs e)
    {
        var count = _documentItems.Count;
        _documentItems.Clear();
        UpdateUIState();
        StatusTextBlock.Text = count > 0 ? string.Format(_resourceLoader.GetString("StatusRemovedPages"), count) : _resourceLoader.GetString("StatusNoPagesToRemove");
    }

    private void SortDocuments<T>(Func<DocumentItem, T?> keySelector, bool ascending) where T : IComparable
    {
        var sorted = ascending
            ? _documentItems.OrderBy(keySelector).ToList()
            : _documentItems.OrderByDescending(keySelector).ToList();

        for (var target = 0; target < sorted.Count; target++)
        {
            var current = _documentItems.IndexOf(sorted[target]);
            if (current != target)
                _documentItems.Move(current, target);
        }
    }

    private void SortByFileNameAsc_Click(object sender, RoutedEventArgs e)
    {
        SortDocuments(d => d.FileName, true);
        StatusTextBlock.Text = _resourceLoader.GetString("StatusSortedByFilenameAsc");
    }

    private void SortByFileNameDesc_Click(object sender, RoutedEventArgs e)
    {
        SortDocuments(d => d.FileName, false);
        StatusTextBlock.Text = _resourceLoader.GetString("StatusSortedByFilenameDesc");
    }

    private void SortByTypeAsc_Click(object sender, RoutedEventArgs e)
    {
        SortDocuments(d => d.Type, true);
        StatusTextBlock.Text = _resourceLoader.GetString("StatusSortedByTypeImagesFirst");
    }

    private void SortByTypeDesc_Click(object sender, RoutedEventArgs e)
    {
        SortDocuments(d => d.Type, false);
        StatusTextBlock.Text = _resourceLoader.GetString("StatusSortedByTypePdfsFirst");
    }

    #endregion

    #region Settings & Dialogs

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Dialogs.SettingsDialog
        {
            XamlRoot = Content.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            StatusTextBlock.Text = _resourceLoader.GetString("StatusSettingsSaved");

            // Compression and paper size are exactly what the estimate is measuring.
            ScheduleEstimate();
        }
    }

    private async void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Dialogs.AboutDialog
        {
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    /// <summary>
    /// Turns a service-layer failure reason into a title and message worth showing a person.
    /// </summary>
    /// <remarks>
    /// Mapping happens here, not in the service: the service has no business choosing English
    /// text, and the UI needs something stable to switch on. Showing raw exception messages
    /// was both untranslatable and, for anything unexpected, meaningless to the reader.
    /// </remarks>
    private (string Title, string Message) DescribeFailure(PdfOperationException failure)
    {
        var name = Path.GetFileName(failure.Path ?? string.Empty);

        return failure.Reason switch
        {
            PdfFailureReason.FileInUse => (
                _resourceLoader.GetString("DialogTitleFileInUse"),
                string.Format(_resourceLoader.GetString("DialogContentFileInUse"), name)),

            PdfFailureReason.AccessDenied => (
                _resourceLoader.GetString("DialogTitleAccessDenied"),
                string.Format(_resourceLoader.GetString("DialogContentAccessDenied"), name)),

            PdfFailureReason.DirectoryNotFound => (
                _resourceLoader.GetString("DialogTitleSaveFailed"),
                string.Format(_resourceLoader.GetString("DialogContentDirectoryNotFound"), Path.GetDirectoryName(failure.Path ?? string.Empty))),

            PdfFailureReason.NoPages => (
                _resourceLoader.GetString("DialogTitleNoPages"),
                _resourceLoader.GetString("DialogContentNoPages")),

            _ => (
                _resourceLoader.GetString("DialogTitleError"),
                _resourceLoader.GetString("DialogContentUnexpectedError"))
        };
    }

    private async Task ShowDialogAsync(string title, object content)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = _resourceLoader.GetString("DialogButtonOK"),
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    #endregion

    #region Misc

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        Log.Information("Application closed");
        _estimateTimer?.Stop();
        _estimateCts?.Cancel();
        App.Cleanup();
        // Synchronous on purpose: the fire-and-forget async flush that was here raced the
        // process exit, so the last entries - including anything about why it closed - could
        // be lost exactly when they were most wanted.
        Log.CloseAndFlush();
    }

    private async void LogButton_Click(object sender, RoutedEventArgs e)
    {
        // FileService no longer swallows this, so pressing the button either opens the folder
        // or says why it could not - rather than appearing to do nothing.
        try
        {
            FileService.OpenLogDirectory();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not open the log directory");
            await ShowDialogAsync(
                _resourceLoader.GetString("DialogTitleOpenLogsFailed"),
                _resourceLoader.GetString("DialogContentOpenLogsFailed"));
        }
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    public async void AddFilesFromPaths(IEnumerable<string> paths)
    {
        try
        {
            var pathList = paths.Where(p => DocumentService.IsSupportedFile(p) && File.Exists(p)).ToList();

            if (pathList.Count == 0)
            {
                StatusTextBlock.Text = _resourceLoader.GetString("StatusNoSupportedFilesReceived");
                return;
            }

            StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusReceivingFiles"), pathList.Count);

            var loaded = await _documentService.LoadDocumentsFromPathsAsync(pathList);

            await AddItemsInBatchesAsync(loaded.Items);

            UpdateUIState();

            if (loaded.Items.Count > 0)
            {
                StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusAddedPagesFromAnotherInstance"), loaded.Items.Count);

                _ = LoadThumbnailsInBackgroundAsync(loaded.Items);
            }

            await ReportSkippedFilesAsync(loaded.FailedFiles);
        }
        // "async void" is forced by the event signature, so nothing above can observe a
        // failure here. It stops at this method or it takes the process down.
        catch (Exception ex)
        {
            Log.Error(ex, "Error adding files from paths");
            StatusTextBlock.Text = _resourceLoader.GetString("StatusUnexpectedError");
        }
    }

    /// <summary>
    /// Tells the user which files did not make it in, if any.
    /// </summary>
    private async Task ReportSkippedFilesAsync(IReadOnlyList<string> skipped)
    {
        if (skipped.Count == 0) return;

        Log.Warning("{Count} file(s) skipped: {Files}", skipped.Count, string.Join(", ", skipped));
        StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusFilesFailed"), skipped.Count);

        await ShowDialogAsync(
            _resourceLoader.GetString("DialogTitleSomeFilesSkipped"),
            string.Format(_resourceLoader.GetString("DialogContentSomeFilesSkipped"), FormatNameList(skipped)));
    }

    /// <summary>Caps a name list so a hundred failures do not produce an unreadable dialog.</summary>
    private static string FormatNameList(IReadOnlyList<string> names)
    {
        const int maxShown = 10;
        var shown = string.Join(Environment.NewLine, names.Take(maxShown));
        return names.Count > maxShown
            ? shown + Environment.NewLine + $"... and {names.Count - maxShown} more"
            : shown;
    }

    #endregion
}
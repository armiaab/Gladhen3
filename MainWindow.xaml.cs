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
    private readonly PdfService _pdfService = new();
    private readonly Microsoft.Windows.ApplicationModel.Resources.ResourceLoader _resourceLoader = new();
    private bool _isGridView = true;
    private bool _isSelectMode;
    private bool _isInitialized;

    public MainWindow()
    {
        InitializeComponent();

        // Pages label themselves, and the model cannot reach the resources, so it is handed the
        // wording here - before the first page is loaded and its label cached.
        DocumentItem.DisplayNameFormat = _resourceLoader.GetString("PageDisplayNameFormat");
        DocumentItem.PageInfoFormat = _resourceLoader.GetString("PageInfoFormat");

        if (AppLog.InitializationFailure != null)
            ((FrameworkElement)Content).Loaded += ReportLoggingFailure;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        SetWindowIcon();

        DocumentGridView.ItemsSource = _documentItems;
        DocumentListView.ItemsSource = _documentItems;

        // The estimate answers "how big will this be when I save it", so it follows the list
        // itself rather than each of the dozen code paths that happen to mutate it.
        _documentItems.CollectionChanged += DocumentItems_CollectionChanged;

        _isInitialized = true;
        ClearOriginals();
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
        // Cosmetic: the window works perfectly well with the default icon. Narrow on
        // purpose - the asset ships inside the package, so the only thing that can really go
        // wrong here is the shell refusing the handle. Anything else is a defect and is left
        // to escape rather than being written off as a missing icon.
        catch (COMException ex)
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

        // Section bands are rows but not pages, so a list holding nothing but a leftover
        // divider is still empty as far as the empty state and the Save button are concerned.
        RefreshSections();

        var hasPages = Pages.Any();
        FadeTo(EmptyStatePanel, hasPages ? 0 : 1);

        // An inserted blank page becomes a PDF page like any other, so it is counted as one.
        var imageCount = _documentItems.Count(d => d.Type == DocumentType.Image);
        var pdfPageCount = _documentItems.Count(d => d.Type is DocumentType.PdfPage or DocumentType.BlankPage);
        ItemCountTextBlock.Text = DescribeContents(imageCount, pdfPageCount);

        SaveButton.IsEnabled = hasPages;
        RotateLeftButton.IsEnabled = hasPages;
        RotateRightButton.IsEnabled = hasPages;
        DuplicateButton.IsEnabled = hasPages;
        RemovePagesButton.IsEnabled = hasPages;
        UpdateUndoState();
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
        // Counts only. Inserting a divider from here would be a mutation raised from the
        // collection's own notification, which ObservableCollection refuses once the views are
        // also subscribed, and naming has to see the finished structure rather than one
        // insertion at a time; RefreshSections does both from the places that can.
        UpdateSectionSummaries();

        // Reordering changes which page comes first, not how many bytes come out - unless the
        // list is cut into several documents, where dragging a page across a divider moves its
        // bytes out of one file and into another.
        if (e.Action == NotifyCollectionChangedAction.Move && !HasSections) return;

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

        if (!Pages.Any())
        {
            _estimateTimer?.Stop();
            FadeTo(EstimatePanel, 0);
            OutputCountText.Visibility = Visibility.Collapsed;
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
        // One measurement covering every section, so the bands and the total cannot
        // disagree and a source document is not re-parsed once per output file.
        var sections = PdfSection.Split(_documentItems);
        var writable = sections.Where(s => !s.IsEmpty).ToList();
        if (writable.Count == 0) return;

        var payload = writable.Select(s => (IReadOnlyList<DocumentItem>)s.Items).ToList();

        var cts = new CancellationTokenSource();
        _estimateCts = cts;
        var token = cts.Token;

        try
        {
            var sizes = await Task.Run(() => PdfService.EstimateSectionSizes(payload, token), token);
            if (token.IsCancellationRequested) return;

            long total = 0;
            for (var i = 0; i < writable.Count && i < sizes.Count; i++)
            {
                total += sizes[i];

                if (writable[i].BreakItem is { } band)
                {
                    band.SectionEstimate = string.Format(
                        _resourceLoader.GetString("EstimatedSizeFormat"),
                        DocumentService.FormatFileSize((ulong)Math.Max(0, sizes[i])));
                }
            }

            EstimateProgress.IsActive = false;
            EstimateProgress.Visibility = Visibility.Collapsed;
            EstimateIcon.Visibility = Visibility.Visible;
            EstimatedSizeText.Text = string.Format(
                _resourceLoader.GetString("EstimatedSizeFormat"),
                DocumentService.FormatFileSize((ulong)Math.Max(0, total)));

            // The total means something different once it is spread over several files, so it
            // says how many rather than leaving the reader to assume one.
            var multiple = writable.Count > 1;
            OutputCountText.Text = multiple
                ? string.Format(_resourceLoader.GetString("OutputFileCount"), writable.Count)
                : string.Empty;
            OutputCountText.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
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
    /// <remarks>
    /// A fade still running decides what happens next, not the opacity read back: undo empties
    /// the list and refills it in one go, and the fade-in that follows the fade-out arrives before
    /// the opacity has moved at all. It used to see "visible, fully opaque", do nothing, and let
    /// the fade-out finish and collapse the size estimate for good. Now the running fade is kept
    /// if it is already heading the same way, and stopped if it is not.
    /// </remarks>
    private static void FadeTo(UIElement element, double opacity, double milliseconds = 150)
    {
        var fadingIn = opacity > 0;

        if (RunningFades.TryGetValue(element, out var running))
        {
            if (running.Target == opacity) return;

            running.Storyboard.Stop();
            RunningFades.Remove(element);
        }
        else
        {
            if (fadingIn && element.Visibility == Visibility.Visible && element.Opacity == opacity) return;
            if (!fadingIn && element.Visibility == Visibility.Collapsed) return;
        }

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
        storyboard.Completed += (_, _) =>
        {
            if (RunningFades.TryGetValue(element, out var current) && current.Storyboard == storyboard) RunningFades.Remove(element);
            if (!fadingIn && element.Opacity <= 0.01) element.Visibility = Visibility.Collapsed;
        };
        RunningFades[element] = (storyboard, opacity);
        storyboard.Begin();
    }

    private static readonly Dictionary<UIElement, (Storyboard Storyboard, double Target)> RunningFades = [];

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
            var loaded = await DocumentService.CreateDocumentItemsBatchAsync(files, progress);

            if (loaded.Items.Count > 0) RecordUndo();
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
            var loaded = await DocumentService.LoadDocumentsFromPathsAsync(pathList);

            if (loaded.Items.Count > 0) RecordUndo();
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

    /// <summary>What came back from asking the user where to save.</summary>
    private enum DestinationOutcome { Picked, Cancelled, Failed }

    /// <summary>
    /// Asks the user where to write the PDF.
    /// </summary>
    /// <remarks>
    /// This existed three times over in the save path - once up front and once in each of the
    /// two "choose another location" branches - each with its own copy of the picker setup and
    /// its own catch, and one of those copies had drifted onto the wrong error message. The
    /// picker runs out of process and can fail as a COM error or as anything else the shell
    /// decides to surface, so the catch stays general; but there is one of it now, and the
    /// user is told the same thing wherever the picker was opened from.
    /// </remarks>
    /// <param name="original">
    /// The PDF every page came from, if there is exactly one. The picker then opens on that file,
    /// in its folder, so the quickest edit there is - turn a page, save - writes back over the
    /// original after the usual "replace?" from Windows, instead of starting in Documents with a
    /// name like "Document".
    /// </param>
    /// <remarks>
    /// The App SDK's picker rather than the WinRT one: <c>FileSavePicker.SuggestedSaveFile</c>
    /// in a desktop app sets the name but not the folder, so the "save over the original" offer
    /// opened in Documents. This one takes the folder as a path.
    /// </remarks>
    private async Task<(DestinationOutcome Outcome, string Path)> PickPdfDestinationAsync(string? suggestedName, string? original = null)
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(AppWindow.Id)
        {
            SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggestedName ?? _resourceLoader.GetString("SuggestedFileNameDocument"),
            DefaultFileExtension = ".pdf"
        };
        picker.FileTypeChoices.Add(_resourceLoader.GetString("FileTypePdfDocument"), new List<string> { ".pdf" });

        // Only if it is still there: a folder that has gone would leave the picker nowhere to open.
        if (original != null && File.Exists(original))
        {
            picker.SuggestedFolder = Path.GetDirectoryName(original);
            picker.SuggestedFileName = Path.GetFileNameWithoutExtension(original);
        }

        Microsoft.Windows.Storage.Pickers.PickFileResult? file;
        try
        {
            file = await picker.PickSaveFileAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Save file picker failed");
            await ShowDialogAsync(
                _resourceLoader.GetString("DialogTitleSaveFailed"),
                _resourceLoader.GetString("DialogContentSaveFailedPicker"));
            StatusTextBlock.Text = _resourceLoader.GetString("StatusSaveCancelled");
            return (DestinationOutcome.Failed, string.Empty);
        }

        if (file == null)
        {
            StatusTextBlock.Text = _resourceLoader.GetString("StatusCancelled");
            return (DestinationOutcome.Cancelled, string.Empty);
        }

        return (DestinationOutcome.Picked, file.Path);
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // Sections are derived here rather than stored, so with no dividers in the list this
        // is one section holding every page and the save below is exactly what it always was.
        var sections = PdfSection.Split(_documentItems).Where(s => !s.IsEmpty).ToList();

        if (sections.Count == 0)
        {
            await ShowDialogAsync(_resourceLoader.GetString("DialogTitleNoPages"), _resourceLoader.GetString("DialogContentNoPages"));
            return;
        }

        if (sections.Count > 1)
        {
            await SaveSectionsAsync(sections);
            return;
        }

        await SaveItemsAsync(sections[0].Items, null, SingleSourcePdf(sections[0].Items));
    }

    /// <summary>The one PDF every page here came from, or null if they came from more than one, or from images.</summary>
    /// <remarks>
    /// FilePath, not SourcePdfPath: once the original has been saved over, rows read from a copy
    /// of it, but the file they belong to is still the original.
    /// </remarks>
    private static string? SingleSourcePdf(IEnumerable<DocumentItem> items)
    {
        string? source = null;
        foreach (var item in items)
        {
            if (item.Type == DocumentType.BlankPage) continue;
            if (item.Type != DocumentType.PdfPage) return null;

            var path = item.FilePath;
            if (source == null) source = path;
            else if (!string.Equals(source, path, StringComparison.OrdinalIgnoreCase)) return null;
        }
        return source;
    }

    /// <summary>Writes these pages to a single PDF the user chooses the place for.</summary>
    private async Task SaveItemsAsync(List<DocumentItem> items, string? suggestedName, string? original = null)
    {
        var (outcome, outputPath) = await PickPdfDestinationAsync(suggestedName, original);
        if (outcome != DestinationOutcome.Picked) return;

        StatusTextBlock.Text = _resourceLoader.GetString("StatusCreatingPdf");
        SaveButton.IsEnabled = false;
        BusyBar.Visibility = Visibility.Visible;
        try
        {
            while (true)
            {
                try
                {
                    PreserveSourceBeforeOverwrite(outputPath);
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

                    await ReportCodecFailuresAsync(result.CodecFailures);
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

                    var choice = await Dialogs.DialogHost.ShowAsync(dialog);
                    if (choice == ContentDialogResult.Primary) continue;

                    if (choice != ContentDialogResult.Secondary)
                    {
                        StatusTextBlock.Text = _resourceLoader.GetString("StatusCancelled");
                        return;
                    }

                    var (inUseOutcome, inUsePath) = await PickPdfDestinationAsync(Path.GetFileNameWithoutExtension(outputPath));
                    if (inUseOutcome != DestinationOutcome.Picked) return;
                    outputPath = inUsePath;
                }
                catch (UnauthorizedAccessException ex)
                {
                    Log.Warning(ex, "Access denied while saving PDF: {Path}", outputPath);

                    var dialog = new ContentDialog
                    {
                        Title = _resourceLoader.GetString("DialogTitleAccessDenied"),
                        Content = string.Format(_resourceLoader.GetString("DialogContentAccessDenied"), Path.GetFileName(outputPath)),
                        PrimaryButtonText = _resourceLoader.GetString("DialogButtonChooseLocation"),
                        CloseButtonText = _resourceLoader.GetString("DialogButtonCancel"),
                        XamlRoot = Content.XamlRoot
                    };

                    if (await Dialogs.DialogHost.ShowAsync(dialog) != ContentDialogResult.Primary)
                    {
                        StatusTextBlock.Text = _resourceLoader.GetString("StatusCancelled");
                        return;
                    }

                    var (deniedOutcome, deniedPath) = await PickPdfDestinationAsync(Path.GetFileNameWithoutExtension(outputPath));
                    if (deniedOutcome != DestinationOutcome.Picked) return;
                    outputPath = deniedPath;
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
        if (_isSelectMode) return;
        if (ItemFromVisual(e.OriginalSource) is not { } item || !IsPage(item)) return;

        // Event-handler boundary for an optional view; the list itself is unaffected.
        try
        {
            await OpenViewerAsync(item);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error opening the viewer");
            StatusTextBlock.Text = _resourceLoader.GetString("StatusUnexpectedError");
        }
    }

    #endregion

    #region Drag & Drop

    private void DocumentView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        // Taken now, because by the time the drop is reported the list has already moved.
        _dragStart = CaptureState();
        StatusTextBlock.Text = _resourceLoader.GetString("StatusReordering");
    }

    private void DocumentView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        var before = _dragStart;
        _dragStart = null;

        if (args.DropResult != Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move) return;

        if (before != null) PushUndo(before);

        // A drag can move a page across a divider, or move the divider itself, so the bands
        // have to be re-derived. This fires after the collection has settled, which is why the
        // divider-inserting half of the refresh is safe to run here and not in CollectionChanged.
        RefreshSections();
        StatusTextBlock.Text = _resourceLoader.GetString("StatusReorderComplete");
    }

    private void DocumentView_DragOver(object sender, DragEventArgs e)
    {
        // Only a drop from outside the app is an "add". This used to be unconditional, so
        // dragging a page into a new position - the thing this list exists for - was reported
        // back as a Copy and captioned "Drop files here to add them", and DragUIOverride is
        // not even available on an internal reorder.
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
            return;
        }

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

    private void RemoveAll_Click(object sender, RoutedEventArgs e)
    {
        var count = _documentItems.Count;
        if (count > 0) RecordUndo();
        _documentItems.Clear();
        UpdateUIState();
        StatusTextBlock.Text = count > 0 ? string.Format(_resourceLoader.GetString("StatusRemovedPages"), count) : _resourceLoader.GetString("StatusNoPagesToRemove");
    }

    /// <summary>
    /// Sorts the pages, leaving the section dividers where they are.
    /// </summary>
    /// <remarks>
    /// Each run between two dividers is sorted on its own. Sorting the whole list would drag
    /// pages across dividers and silently re-file them into a different output document -
    /// and would sort the dividers themselves into the middle of it.
    /// </remarks>
    private void SortDocuments<T>(Func<DocumentItem, T?> keySelector, bool ascending) where T : IComparable
    {
        var ordered = new List<DocumentItem>(_documentItems.Count);
        var run = new List<DocumentItem>();

        void FlushRun()
        {
            if (run.Count == 0) return;
            ordered.AddRange(ascending ? run.OrderBy(keySelector) : run.OrderByDescending(keySelector));
            run.Clear();
        }

        foreach (var item in _documentItems)
        {
            if (item.Type == DocumentType.SectionBreak)
            {
                FlushRun();
                ordered.Add(item);
            }
            else
            {
                run.Add(item);
            }
        }
        FlushRun();

        RecordUndo();
        ApplyOrder(ordered);

        UpdateSectionSummaries();
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
        var dialog = new Dialogs.SettingsDialog(WindowNative.GetWindowHandle(this))
        {
            XamlRoot = Content.XamlRoot
        };

        if (await Dialogs.DialogHost.ShowAsync(dialog) == ContentDialogResult.Primary)
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
        await Dialogs.DialogHost.ShowAsync(dialog);
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

            PdfFailureReason.CodecUnavailable => (
                _resourceLoader.GetString("DialogTitleCodecUnavailable"),
                string.Format(
                    _resourceLoader.GetString("DialogContentCodecUnavailable"),
                    Dialogs.CodecText.Reason(_resourceLoader, failure.CodecReason ?? CodecFailureReason.Unknown))),

            _ => (
                _resourceLoader.GetString("DialogTitleError"),
                _resourceLoader.GetString("DialogContentUnexpectedError"))
        };
    }

    /// <summary>Says so when the chosen codec failed on some images, which were kept as they were.</summary>
    /// <remarks>
    /// The file is fine and the save succeeded, but a plugin that quietly fails on every image
    /// looks exactly like one that found nothing to shrink, and only one of those needs fixing.
    /// </remarks>
    private async Task ReportCodecFailuresAsync(int failures)
    {
        if (failures == 0) return;

        await ShowDialogAsync(
            _resourceLoader.GetString("DialogTitleCodecFailed"),
            string.Format(_resourceLoader.GetString("DialogContentCodecFailed"), failures));
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
        await Dialogs.DialogHost.ShowAsync(dialog);
    }

    #endregion

    #region Misc

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        Log.Information("Application closed");
        _estimateTimer?.Stop();
        _estimateCts?.Cancel();
        ClearOriginals();
        App.Cleanup();
        Services.AppLog.Shutdown();
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

            var loaded = await DocumentService.LoadDocumentsFromPathsAsync(pathList);

            if (loaded.Items.Count > 0) RecordUndo();
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
    private string FormatNameList(IReadOnlyList<string> names)
    {
        const int maxShown = 10;
        var shown = string.Join(Environment.NewLine, names.Take(maxShown));
        return names.Count > maxShown
            ? shown + Environment.NewLine + string.Format(_resourceLoader.GetString("NameListMoreFormat"), names.Count - maxShown)
            : shown;
    }

    /// <summary>Says once, when the window is up, that this session is running without a log.</summary>
    private async void ReportLoggingFailure(object sender, RoutedEventArgs e)
    {
        ((FrameworkElement)Content).Loaded -= ReportLoggingFailure;

        await ShowDialogAsync(
            _resourceLoader.GetString("DialogTitleLoggingFailed"),
            string.Format(_resourceLoader.GetString("DialogContentLoggingFailed"), AppLog.Directory));
    }

    #endregion
}
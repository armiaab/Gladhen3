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

        // The model cannot reach resources, so it gets its label formats here, before any page loads.
        DocumentItem.DisplayNameFormat = _resourceLoader.GetString("PageDisplayNameFormat");
        DocumentItem.PageInfoFormat = _resourceLoader.GetString("PageInfoFormat");

        if (AppLog.InitializationFailure != null)
            ((FrameworkElement)Content).Loaded += ReportLoggingFailure;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        SetWindowIcon();

        DocumentGridView.ItemsSource = _documentItems;
        DocumentListView.ItemsSource = _documentItems;

        // The estimate follows the collection rather than each code path that mutates it.
        _documentItems.CollectionChanged += DocumentItems_CollectionChanged;

        _isInitialized = true;
        ClearOriginals();
        StatusTextBlock.Text = _resourceLoader.GetString("StatusReady");
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
        // Cosmetic; only a shell failure is expected here.
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
            InitialLoad = LoadDocumentsAsync(paths);
        }
    }

    /// <summary>The load of the files this window opened with; completed if there were none.</summary>
    /// <remarks>Later activations wait for it so their pages land after it.</remarks>
    public Task InitialLoad { get; } = Task.CompletedTask;

    #region UI State

    private void UpdateUIState()
    {
        if (!_isInitialized) return;

        // Section bands are rows, not pages: a list holding only a divider counts as empty.
        RefreshSections();

        var hasPages = Pages.Count > 0;
        FadeTo(EmptyStatePanel, hasPages ? 0 : 1);

        // Inserted blank pages count as PDF pages.
        var imageCount = _documentItems.Count(d => d.Type == DocumentType.Image);
        var pdfPageCount = _documentItems.Count(d => d.Type is DocumentType.PdfPage or DocumentType.BlankPage);
        ItemCountTextBlock.Text = DescribeContents(imageCount, pdfPageCount);

        SaveButton.IsEnabled = hasPages;
        RotateLeftButton.IsEnabled = hasPages;
        RotateRightButton.IsEnabled = hasPages;
        NormalizeButton.IsEnabled = hasPages;
        DuplicateButton.IsEnabled = hasPages;
        RemovePagesButton.IsEnabled = hasPages;
        UpdateUndoState();
        UpdateSelectionInfo();
    }

    /// <summary>The header subtitle; the image/PDF breakdown only when both are present.</summary>
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

        // The incoming view takes over the other's selection.
        var (shown, hidden) = _isGridView ? ((ListViewBase)DocumentGridView, (ListViewBase)DocumentListView) : (DocumentListView, DocumentGridView);
        if (!shown.SelectedItems.SequenceEqual(hidden.SelectedItems))
        {
            shown.SelectedItems.Clear();
            foreach (var item in hidden.SelectedItems) shown.SelectedItems.Add(item);
        }

        DocumentGridView.Visibility = _isGridView ? Visibility.Visible : Visibility.Collapsed;
        DocumentListView.Visibility = _isGridView ? Visibility.Collapsed : Visibility.Visible;
        UpdateSelectionInfo();
    }

    /// <summary>Select mode: a check box on every page, so a click toggles it (touch friendly).</summary>
    /// <remarks>The selection is kept across the switch.</remarks>
    private void UpdateSelectMode()
    {
        if (!_isInitialized) return;

        // WinUI clears the selection on a mode change, so carry it across.
        var kept = ActiveView.SelectedItems.ToList();

        var mode = _isSelectMode ? ListViewSelectionMode.Multiple : ListViewSelectionMode.Extended;
        DocumentGridView.SelectionMode = mode;
        DocumentListView.SelectionMode = mode;

        foreach (var item in kept) ActiveView.SelectedItems.Add(item);

        SelectModeMenuItem.Text = _isSelectMode ? _resourceLoader.GetString("TextExitSelectMode") : _resourceLoader.GetString("TextEnterSelectMode");

        UpdateSelectionInfo();
    }

    /// <summary>The selected-page count and the remove button.</summary>
    /// <remarks>Shown in select mode or with two or more pages selected.</remarks>
    private void UpdateSelectionInfo()
    {
        if (!_isInitialized) return;

        var count = ActiveView.SelectedItems.Count;
        SelectionInfoPanel.Visibility = !IsViewerOpen && (_isSelectMode || count >= 2) ? Visibility.Visible : Visibility.Collapsed;
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

    /// <remarks>Debounced: an estimate encodes sample images, so a burst of changes runs it once.</remarks>
    private static readonly TimeSpan EstimateDebounce = TimeSpan.FromMilliseconds(500);

    private DispatcherTimer? _estimateTimer;
    private CancellationTokenSource? _estimateCts;

    private void DocumentItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _pagesCache = null;

        // Counts only. Dividers cannot be inserted from inside the collection's own notification;
        // RefreshSections does that from the callers.
        UpdateSectionSummaries();

        // A move only changes the size when it crosses a section divider.
        if (e.Action == NotifyCollectionChangedAction.Move && !HasSections) return;

        ScheduleEstimate();
    }

    /// <summary>Schedules a fresh estimate, cancelling any run in progress.</summary>
    private void ScheduleEstimate()
    {
        if (!_isInitialized) return;

        _estimateCts?.Cancel();

        if (Pages.Count == 0)
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
        // One pass for all sections, so the bands and the total agree.
        var sections = PdfSection.Split(_documentItems);
        var writable = sections.Where(s => !s.IsEmpty).ToList();
        if (writable.Count == 0) return;

        // Clones, so the user can keep editing the originals.
        var payload = writable.Select(s => (IReadOnlyList<DocumentItem>)SnapshotItems(s.Items)).ToList();
        var settings = AppSettings.Current;

        var cts = new CancellationTokenSource();
        _estimateCts = cts;
        var token = cts.Token;

        try
        {
            var sizes = await Task.Run(() => PdfService.EstimateSectionSizes(payload, settings, token), token);
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

            // With several output files, say how many.
            var multiple = writable.Count > 1;
            OutputCountText.Text = multiple
                ? string.Format(_resourceLoader.GetString("OutputFileCount"), writable.Count)
                : string.Empty;
            OutputCountText.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer run.
        }
        // The estimate is optional; on failure just hide it.
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

    /// <summary>Fades <paramref name="element"/> to <paramref name="opacity"/>, collapsing it once faded out.</summary>
    /// <remarks>
    /// The empty state overlays the lists and accepts drops, so it must be collapsed, not just
    /// transparent. A running fade is the source of truth, not the current opacity: undo can
    /// request a fade-in before a fade-out has moved, so the running fade is kept if it heads the
    /// same way and stopped otherwise.
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
        => await PickAndAddFilesAsync(PickerViewMode.Thumbnail, PickerLocationId.DocumentsLibrary, DocumentService.GetAllSupportedExtensions());

    private async void AddImagesButton_Click(object sender, RoutedEventArgs e)
        => await PickAndAddFilesAsync(PickerViewMode.Thumbnail, PickerLocationId.PicturesLibrary, DocumentService.GetImageExtensions());

    private async void AddPdfsButton_Click(object sender, RoutedEventArgs e)
        => await PickAndAddFilesAsync(PickerViewMode.List, PickerLocationId.DocumentsLibrary, DocumentService.GetPdfExtensions());

    /// <summary>Asks for files and adds them. Never throws.</summary>
    /// <remarks>The picker is a COM call and can fail; the callers are async void handlers.</remarks>
    private async Task PickAndAddFilesAsync(PickerViewMode viewMode, PickerLocationId start, IEnumerable<string> extensions)
    {
        IReadOnlyList<StorageFile>? files;
        try
        {
            var picker = new FileOpenPicker
            {
                ViewMode = viewMode,
                SuggestedStartLocation = start
            };

            foreach (var ext in extensions)
                picker.FileTypeFilter.Add(ext);

            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            files = await picker.PickMultipleFilesAsync();
        }
        // Event-handler boundary, as in AddFilesAsync.
        catch (Exception ex)
        {
            Log.Error(ex, "File picker failed");
            StatusTextBlock.Text = _resourceLoader.GetString("StatusUnexpectedError");
            await ShowDialogAsync(_resourceLoader.GetString("DialogTitleError"), _resourceLoader.GetString("DialogContentUnexpectedError"));
            return;
        }

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
        // Event-handler boundary.
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
        // Event-handler boundary; the list is left unchanged.
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading documents");
            StatusTextBlock.Text = _resourceLoader.GetString("StatusUnexpectedError");
            await ShowDialogAsync(_resourceLoader.GetString("DialogTitleError"), _resourceLoader.GetString("DialogContentUnexpectedError"));
        }
    }

    /// <summary>Adds items in chunks, yielding between them so large PDFs appear progressively.</summary>
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

    /// <summary>Loads thumbnails in the background with bounded concurrency, one PDF open per source.</summary>
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
        // Thumbnails are decoration; a failure only loses the preview.
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

            var pdfDocument = await OffUiStorage.LoadPdfAsync(sourcePath);

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
                // One bad page must not cost the others their thumbnails.
                catch (Exception ex)
                {
                    Log.Warning(ex, "Error loading PDF page thumbnail: {Path} page {Page}", sourcePath, item.PageNumber);
                }
            }
        }
        // Background task; the pages are already in the list.
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
            using var stream = await OffUiStorage.OpenReadAsync(filePath);

            var bitmap = new BitmapImage { DecodePixelWidth = 200 };
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        // Decoration only.
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

    /// <summary>Asks the user where to write the PDF.</summary>
    /// <param name="original">
    /// The single source PDF, if any: the picker then opens on it, so saving over the original
    /// is one step.
    /// </param>
    /// <remarks>
    /// Uses the App SDK picker because the WinRT one cannot preselect a folder in a desktop app.
    /// The picker runs out of process and can fail in many ways, so the catch is general.
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

        // A deleted folder would leave the picker nowhere to open.
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
        // With no dividers this is a single section holding every page.
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

    /// <summary>Clones of <paramref name="items"/> for a background worker.</summary>
    /// <remarks>Settings are copy-on-write, so <see cref="AppSettings.Current"/> taken alongside is already a snapshot.</remarks>
    private static List<DocumentItem> SnapshotItems(IEnumerable<DocumentItem> items)
        => [.. items.Select(item => item.Clone())];

    /// <summary>The one PDF every page came from, or null for several sources or images.</summary>
    /// <remarks>FilePath, not SourcePdfPath: after saving over the original, rows read from a copy.</remarks>
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
        SetBusy(true);
        try
        {
            while (true)
            {
                try
                {
                    PreserveSourceBeforeOverwrite(outputPath);

                    // Snapshot now: edits made during the build belong to the next save.
                    var snapshot = SnapshotItems(items);
                    var settings = AppSettings.Current;
                    var result = await Task.Run(() => _pdfService.CreatePdfFromDocuments(snapshot, outputPath, settings));

                    StatusTextBlock.Text = _resourceLoader.GetString("StatusPdfCreatedSuccessfully");
                    await ShowDialogAsync(_resourceLoader.GetString("DialogTitleSuccess"), string.Format(_resourceLoader.GetString("DialogContentPdfSaved"), outputPath));

                    // The save succeeded, but the user must know what was left out.
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
                // Only a locked destination is worth retrying.
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
                // Anything else is a defect: a generic message, details in the log.
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
            SetBusy(false);
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

        // Event-handler boundary; the list is unaffected.
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
        // Captured now; by the time the drop is reported the list has moved.
        _dragStart = CaptureState();
        StatusTextBlock.Text = _resourceLoader.GetString("StatusReordering");
    }

    private void DocumentView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        var before = _dragStart;
        _dragStart = null;

        if (args.DropResult != Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move) return;

        if (before != null) PushUndo(before);

        // A drag can cross a divider, so re-derive the bands. Safe here, after the collection settles.
        ReindexViewerPage();
        RefreshSections();
        StatusTextBlock.Text = _resourceLoader.GetString("StatusReorderComplete");
    }

    private void DocumentView_DragOver(object sender, DragEventArgs e)
    {
        // Only a drop from outside the app adds files; an internal drag is a reorder.
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
        ActiveView.SelectAll();
        StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusSelectedPages"), ActiveView.SelectedItems.OfType<DocumentItem>().Count(IsPage));
    }

    private void DeselectAllButton_Click(object sender, RoutedEventArgs e) => ClearSelection();

    private void RemoveAll_Click(object sender, RoutedEventArgs e)
    {
        var count = _documentItems.Count;
        if (count > 0) RecordUndo();
        _documentItems.Clear();
        UpdateUIState();
        StatusTextBlock.Text = count > 0 ? string.Format(_resourceLoader.GetString("StatusRemovedPages"), count) : _resourceLoader.GetString("StatusNoPagesToRemove");
    }

    /// <summary>Sorts the pages within each section, leaving the dividers in place.</summary>
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
            if (dialog.SaveFailure != null)
            {
                await ShowDialogAsync(
                    _resourceLoader.GetString("DialogTitleError"),
                    _resourceLoader.GetString("DialogContentSettingsNotSaved"));
                return;
            }

            StatusTextBlock.Text = _resourceLoader.GetString("StatusSettingsSaved");

            // Compression and paper size change the estimate.
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

    /// <summary>Maps a service failure reason to a localized title and message.</summary>
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

    /// <summary>Reports images the chosen codec failed on (kept unchanged).</summary>
    /// <remarks>Otherwise a broken plugin looks the same as one with nothing to shrink.</remarks>
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

    /// <summary>Adds files handed over by another launch. Never throws.</summary>
    public async Task AddFilesFromPathsAsync(IEnumerable<string> paths)
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
        // Activation boundary.
        catch (Exception ex)
        {
            Log.Error(ex, "Error adding files from paths");
            StatusTextBlock.Text = _resourceLoader.GetString("StatusUnexpectedError");
        }
    }

    /// <summary>Tells the user which files were skipped, if any.</summary>
    private async Task ReportSkippedFilesAsync(IReadOnlyList<string> skipped)
    {
        if (skipped.Count == 0) return;

        Log.Warning("{Count} file(s) skipped: {Files}", skipped.Count, string.Join(", ", skipped));
        StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusFilesFailed"), skipped.Count);

        await ShowDialogAsync(
            _resourceLoader.GetString("DialogTitleSomeFilesSkipped"),
            string.Format(_resourceLoader.GetString("DialogContentSomeFilesSkipped"), FormatNameList(skipped)));
    }

    /// <summary>Caps a name list so a long one stays readable.</summary>
    private string FormatNameList(IReadOnlyList<string> names)
    {
        const int maxShown = 10;
        var shown = string.Join(Environment.NewLine, names.Take(maxShown));
        return names.Count > maxShown
            ? shown + Environment.NewLine + string.Format(_resourceLoader.GetString("NameListMoreFormat"), names.Count - maxShown)
            : shown;
    }

    /// <summary>Tells the user once, after start-up, that logging is unavailable.</summary>
    private async void ReportLoggingFailure(object sender, RoutedEventArgs e)
    {
        ((FrameworkElement)Content).Loaded -= ReportLoggingFailure;

        await ShowDialogAsync(
            _resourceLoader.GetString("DialogTitleLoggingFailed"),
            string.Format(_resourceLoader.GetString("DialogContentLoggingFailed"), AppLog.Directory));
    }

    #endregion
}
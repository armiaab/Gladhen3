using Gladhen3.Models;
using Gladhen3.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
        _isInitialized = true;
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
        EmptyStatePanel.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;

        var imageCount = _documentItems.Count(d => d.Type == DocumentType.Image);
        var pdfPageCount = _documentItems.Count(d => d.Type == DocumentType.PdfPage);
        ItemCountTextBlock.Text = string.Format(_resourceLoader.GetString("ItemCountFormat"), _documentItems.Count, imageCount, pdfPageCount);

        SaveButton.IsEnabled = hasItems;
        UpdateSelectionInfo();
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
            catch (Exception ex)
            {
                Log.Warning(ex, "Error clearing selection");
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

    // Called from menu item
    private void ToggleSelectModeMenu_Click(object sender, RoutedEventArgs e)
    {
        SelectModeToggle.IsChecked = !SelectModeToggle.IsChecked;
    }

    // Called when toggle button is checked
    private void SelectModeToggle_Checked(object sender, RoutedEventArgs e)
    {
        _isSelectMode = true;
        UpdateSelectMode();
        StatusTextBlock.Text = _resourceLoader.GetString("StatusSelectMode");
    }

    // Called when toggle button is unchecked
    private void SelectModeToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        _isSelectMode = false;
        UpdateSelectMode();
        StatusTextBlock.Text = _resourceLoader.GetString("StatusReady");
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
            var items = await _documentService.CreateDocumentItemsBatchAsync(files, progress);

            await AddItemsInBatchesAsync(items);
            addedCount = items.Count;

            UpdateUIState();

            if (addedCount > 0)
            {
                StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusAddedPages"), addedCount);

                _ = LoadThumbnailsInBackgroundAsync(items);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error adding files");
            StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusError"), ex.Message);
        }
    }

    private async Task LoadDocumentsAsync(IEnumerable<string> paths)
    {
        var pathList = paths.ToList();
        StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusLoadingFiles"), pathList.Count);

        try
        {
            var newItems = await _documentService.LoadDocumentsFromPathsAsync(pathList);

            await AddItemsInBatchesAsync(newItems);

            UpdateUIState();

            if (newItems.Count > 0)
            {
                StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusLoadedPages"), newItems.Count);

                _ = LoadThumbnailsInBackgroundAsync(newItems);
            }
            else
            {
                StatusTextBlock.Text = _resourceLoader.GetString("StatusNoSupportedFiles");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading documents");
            StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusError"), ex.Message);
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
                catch (Exception ex)
                {
                    Log.Warning(ex, "Error loading PDF page thumbnail: {Path} page {Page}", sourcePath, item.PageNumber);
                }
            }
        }
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
        try
        {
            while (true)
            {
                try
                {
                    await Task.Run(() => _pdfService.CreatePdfFromDocuments(items, outputPath));

                    StatusTextBlock.Text = _resourceLoader.GetString("StatusPdfCreatedSuccessfully");
                    await ShowDialogAsync(_resourceLoader.GetString("DialogTitleSuccess"), string.Format(_resourceLoader.GetString("DialogContentPdfSaved"), outputPath));
                    break;
                }
                catch (IOException ex)
                {
                    Log.Warning(ex, "I/O error while saving PDF: {Path}", outputPath);

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
                catch (Exception ex)
                {
                    StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusError"), ex.Message);
                    Log.Error(ex, "Error creating PDF");
                    await ShowDialogAsync(_resourceLoader.GetString("DialogTitleError"), string.Format(_resourceLoader.GetString("DialogContentCreatePdfError"), ex.Message));
                    return;
                }
            }
        }
        finally
        {
            SaveButton.IsEnabled = true;
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
            catch (Exception ex)
            {
                Log.Error(ex, "Error opening file externally");
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
        catch (Exception ex)
        {
            Log.Error(ex, "Error rendering high-res PDF page");
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
        var dialog = new Gladhen3.Dialogs.SettingsDialog
        {
            XamlRoot = Content.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            StatusTextBlock.Text = _resourceLoader.GetString("StatusSettingsSaved");
        }
    }

    private async void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Gladhen3.Dialogs.AboutDialog
        {
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
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
        App.Cleanup();
        _ = Log.CloseAndFlushAsync();
    }

    private void LogButton_Click(object sender, RoutedEventArgs e)
    {
        FileService.OpenLogDirectory();
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// Public method to add files from paths (used by single-instance IPC)
    /// </summary>
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

            var newItems = await _documentService.LoadDocumentsFromPathsAsync(pathList);

            await AddItemsInBatchesAsync(newItems);

            UpdateUIState();

            if (newItems.Count > 0)
            {
                StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusAddedPagesFromAnotherInstance"), newItems.Count);

                _ = LoadThumbnailsInBackgroundAsync(newItems);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error adding files from paths");
            StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusError"), ex.Message);
        }
    }

    #endregion
}
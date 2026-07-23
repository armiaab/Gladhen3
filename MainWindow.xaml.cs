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
            StatusTextBlock.Text = $"Loading {string.Join(", ", fileNames)}...";
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
        ItemCountTextBlock.Text = $"{_documentItems.Count} pages ({imageCount} images, {pdfPageCount} PDF pages)";

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

        SelectModeMenuItem.Text = _isSelectMode ? "Exit Select Mode" : "Enter Select Mode";
        SelectionInfoPanel.Visibility = _isSelectMode ? Visibility.Visible : Visibility.Collapsed;

        UpdateSelectionInfo();
    }

    private void UpdateSelectionInfo()
    {
        if (!_isInitialized || !_isSelectMode) return;

        var view = _isGridView ? (ListViewBase)DocumentGridView : DocumentListView;
        var count = view.SelectedItems.Count;
        SelectionCountText.Text = count == 1 ? "1 selected" : $"{count} selected";
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
        StatusTextBlock.Text = "Select mode: click items to select";
    }

    // Called when toggle button is unchecked
    private void SelectModeToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        _isSelectMode = false;
        UpdateSelectMode();
        StatusTextBlock.Text = "Ready";
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
        StatusTextBlock.Text = $"Loading {files.Count} file(s)...";

        var addedCount = 0;
        var progress = new Progress<(int current, int total, string fileName)>(p =>
        {
            StatusTextBlock.Text = $"Loading {p.current}/{p.total}: {p.fileName}";
        });

        try
        {
            var items = await _documentService.CreateDocumentItemsBatchAsync(files, progress);

            await AddItemsInBatchesAsync(items);
            addedCount = items.Count;

            UpdateUIState();

            if (addedCount > 0)
            {
                StatusTextBlock.Text = $"Added {addedCount} page(s)";

                _ = LoadThumbnailsInBackgroundAsync(items);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error adding files");
            StatusTextBlock.Text = $"Error: {ex.Message}";
        }
    }

    private async Task LoadDocumentsAsync(IEnumerable<string> paths)
    {
        var pathList = paths.ToList();
        StatusTextBlock.Text = $"Loading {pathList.Count} file(s)...";

        try
        {
            var newItems = await _documentService.LoadDocumentsFromPathsAsync(pathList);

            await AddItemsInBatchesAsync(newItems);

            UpdateUIState();

            if (newItems.Count > 0)
            {
                StatusTextBlock.Text = $"Loaded {newItems.Count} page(s)";

                _ = LoadThumbnailsInBackgroundAsync(newItems);
            }
            else
            {
                StatusTextBlock.Text = "No supported files found";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading documents");
            StatusTextBlock.Text = $"Error: {ex.Message}";
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
            await ShowDialogAsync("No Pages", "Add images or PDFs to create a PDF.");
            return;
        }

        var savePicker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = "Document"
        };
        savePicker.FileTypeChoices.Add("PDF Document", new List<string> { ".pdf" });

        InitializeWithWindow.Initialize(savePicker, WindowNative.GetWindowHandle(this));

        StorageFile file;
        try
        {
            file = await savePicker.PickSaveFileAsync();
        }
        catch (COMException comEx)
        {
            Log.Warning(comEx, "Save file picker failed (COMException)");
            await ShowDialogAsync("Save Failed", "Unable to open the save dialog. This can happen if the system file picker failed. Try again or restart the app.");
            StatusTextBlock.Text = "Save cancelled";
            return;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Save file picker failed");
            await ShowDialogAsync("Save Failed", $"Unable to open the save dialog: {ex.Message}");
            StatusTextBlock.Text = "Save cancelled";
            return;
        }

        if (file == null)
        {
            StatusTextBlock.Text = "Cancelled";
            return;
        }

        var items = _documentItems.ToList();
        var outputPath = file.Path;

        StatusTextBlock.Text = "Creating PDF...";
        SaveButton.IsEnabled = false;
        try
        {
            while (true)
            {
                try
                {
                    await Task.Run(() => _pdfService.CreatePdfFromDocuments(items, outputPath));

                    StatusTextBlock.Text = "PDF created successfully";
                    await ShowDialogAsync("Success", $"PDF saved to:\n{outputPath}");
                    break;
                }
                catch (IOException ex)
                {
                    Log.Warning(ex, "I/O error while saving PDF: {Path}", outputPath);

                    var dialog = new ContentDialog
                    {
                        Title = "File in Use",
                        Content = $"Cannot save to '{Path.GetFileName(outputPath)}' because it is open in another application. Close it and retry, or choose a different location.",
                        PrimaryButtonText = "Retry",
                        SecondaryButtonText = "Choose Location",
                        CloseButtonText = "Cancel",
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
                        newPicker.FileTypeChoices.Add("PDF Document", new List<string> { ".pdf" });
                        InitializeWithWindow.Initialize(newPicker, WindowNative.GetWindowHandle(this));

                        try
                        {
                            var newFile = await newPicker.PickSaveFileAsync();
                            if (newFile == null)
                            {
                                StatusTextBlock.Text = "Cancelled";
                                return;
                            }
                            outputPath = newFile.Path;
                            continue;
                        }
                        catch (COMException comEx)
                        {
                            Log.Warning(comEx, "Save file picker failed (COMException) when choosing new location");
                            await ShowDialogAsync("Save Failed", "Unable to open the save dialog. Try again or restart the app.");
                            StatusTextBlock.Text = "Save cancelled";
                            return;
                        }
                    }
                    else
                    {
                        StatusTextBlock.Text = "Cancelled";
                        return;
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    Log.Warning(ex, "Access denied while saving PDF: {Path}", outputPath);

                    var dialog = new ContentDialog
                    {
                        Title = "Access Denied",
                        Content = $"Access denied saving to '{Path.GetFileName(outputPath)}'. Check permissions or choose a different location.",
                        PrimaryButtonText = "Choose Location",
                        CloseButtonText = "Cancel",
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
                        newPicker.FileTypeChoices.Add("PDF Document", new List<string> { ".pdf" });
                        InitializeWithWindow.Initialize(newPicker, WindowNative.GetWindowHandle(this));

                        try
                        {
                            var newFile = await newPicker.PickSaveFileAsync();
                            if (newFile == null)
                            {
                                StatusTextBlock.Text = "Cancelled";
                                return;
                            }
                            outputPath = newFile.Path;
                            continue;
                        }
                        catch (COMException comEx)
                        {
                            Log.Warning(comEx, "Save file picker failed (COMException) when choosing new location");
                            await ShowDialogAsync("Save Failed", "Unable to save the pdf file, the file is used by another process. Please close the file in the other application and try again.");
                            StatusTextBlock.Text = "Save cancelled";
                            return;
                        }
                    }
                    else
                    {
                        StatusTextBlock.Text = "Cancelled";
                        return;
                    }
                }
                catch (Exception ex)
                {
                    StatusTextBlock.Text = $"Error: {ex.Message}";
                    Log.Error(ex, "Error creating PDF");
                    await ShowDialogAsync("Error", $"Failed to create PDF:\n{ex.Message}");
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
            StatusTextBlock.Text = "Error opening preview";
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
        ToolTipService.SetToolTip(zoomOutButton, "Zoom Out (-)");

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
        ToolTipService.SetToolTip(zoomInButton, "Zoom In (+)");

        var fitButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE9A6", FontSize = 14 },
            Width = 32,
            Height = 32,
            Margin = new Thickness(8, 0, 0, 0)
        };
        ToolTipService.SetToolTip(fitButton, "Fit to Window");

        var openButton = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new FontIcon { Glyph = "\uE8A7", FontSize = 12 },
                    new TextBlock { Text = "Open", VerticalAlignment = VerticalAlignment.Center, FontSize = 12 }
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
        dialog.CloseButtonText = "Close";

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
                        Text = "Unable to load preview",
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
                    Text = "Error loading preview",
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
        StatusTextBlock.Text = "Reordering...";
    }

    private void DocumentView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (args.DropResult == Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move)
            StatusTextBlock.Text = "Reorder complete";
    }

    private void DocumentView_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Add";
        e.DragUIOverride.IsContentVisible = true;
        e.DragUIOverride.IsCaptionVisible = true;
        StatusTextBlock.Text = "Drop to add";
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

        StatusTextBlock.Text = $"Selected {_documentItems.Count} pages";
    }

    private void DeselectAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSelectMode)
        {
            DocumentGridView.SelectedItems.Clear();
            DocumentListView.SelectedItems.Clear();
        }
        StatusTextBlock.Text = "Deselected all";
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (!_isSelectMode)
        {
            StatusTextBlock.Text = "Enter select mode first";
            return;
        }

        var view = _isGridView ? (ListViewBase)DocumentGridView : DocumentListView;
        var selectedItems = view.SelectedItems.Cast<DocumentItem>().ToList();

        if (selectedItems.Count == 0)
        {
            StatusTextBlock.Text = "No pages selected";
            return;
        }

        foreach (var item in selectedItems)
            _documentItems.Remove(item);

        UpdateUIState();
        StatusTextBlock.Text = $"Removed {selectedItems.Count} page(s)";
    }

    private void RemoveAll_Click(object sender, RoutedEventArgs e)
    {
        var count = _documentItems.Count;
        _documentItems.Clear();
        UpdateUIState();
        StatusTextBlock.Text = count > 0 ? $"Removed {count} page(s)" : "No pages to remove";
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
        StatusTextBlock.Text = "Sorted by filename (A-Z)";
    }

    private void SortByFileNameDesc_Click(object sender, RoutedEventArgs e)
    {
        SortDocuments(d => d.FileName, false);
        StatusTextBlock.Text = "Sorted by filename (Z-A)";
    }

    private void SortByTypeAsc_Click(object sender, RoutedEventArgs e)
    {
        SortDocuments(d => d.Type, true);
        StatusTextBlock.Text = "Sorted by type (Images first)";
    }

    private void SortByTypeDesc_Click(object sender, RoutedEventArgs e)
    {
        SortDocuments(d => d.Type, false);
        StatusTextBlock.Text = "Sorted by type (PDFs first)";
    }

    #endregion

    #region Settings & Dialogs

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 16 };

        panel.Children.Add(new TextBlock
        {
            Text = "Paper Size (for images):",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });

        var paperSizeCombo = new ComboBox { Width = 220 };
        paperSizeCombo.Items.Add("Automatic (Use Image Size)");
        paperSizeCombo.Items.Add("A4 (210 ×297 mm)");
        paperSizeCombo.Items.Add("Letter (8.5 ×11 in)");
        paperSizeCombo.Items.Add("Legal (8.5 ×14 in)");
        paperSizeCombo.Items.Add("A3 (297 ×420 mm)");
        paperSizeCombo.Items.Add("Custom...");
        paperSizeCombo.SelectedIndex = (int)AppSettings.Current.PaperSize;
        panel.Children.Add(paperSizeCombo);

        var customSizePanel = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = AppSettings.Current.PaperSize == PdfPaperSize.Custom ? Visibility.Visible : Visibility.Collapsed
        };

        var unitPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        unitPanel.Children.Add(new TextBlock
        {
            Text = "Unit:",
            VerticalAlignment = VerticalAlignment.Center,
            Width = 50
        });

        var unitCombo = new ComboBox { Width = 120 };
        unitCombo.Items.Add("Millimeters (mm)");
        unitCombo.Items.Add("Inches (in)");
        unitCombo.Items.Add("Points (pt)");
        unitCombo.SelectedIndex = AppSettings.Current.CustomSizeUnit;
        unitPanel.Children.Add(unitCombo);
        customSizePanel.Children.Add(unitPanel);

        var unitMarginPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var widthPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        widthPanel.Children.Add(new TextBlock
        {
            Text = "Width:",
            VerticalAlignment = VerticalAlignment.Center,
            Width = 50
        });

        var widthBox = new NumberBox
        {
            Value = AppSettings.Current.CustomWidth,
            Minimum = 1,
            Maximum = 10000,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 120,
            SmallChange = 1,
            LargeChange = 10
        };
        widthPanel.Children.Add(widthBox);

        var widthUnitLabel = new TextBlock
        {
            Text = AppSettings.Current.GetUnitName(),
            VerticalAlignment = VerticalAlignment.Center
        };
        widthPanel.Children.Add(widthUnitLabel);
        unitMarginPanel.Children.Add(widthPanel);

        var heightPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        heightPanel.Children.Add(new TextBlock
        {
            Text = "Height:",
            VerticalAlignment = VerticalAlignment.Center,
            Width = 50
        });

        var heightBox = new NumberBox
        {
            Value = AppSettings.Current.CustomHeight,
            Minimum = 1,
            Maximum = 10000,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 120,
            SmallChange = 1,
            LargeChange = 10
        };
        heightPanel.Children.Add(heightBox);

        var heightUnitLabel = new TextBlock
        {
            Text = AppSettings.Current.GetUnitName(),
            VerticalAlignment = VerticalAlignment.Center
        };
        heightPanel.Children.Add(heightUnitLabel);
        unitMarginPanel.Children.Add(heightPanel);

        customSizePanel.Children.Add(unitMarginPanel);

        var presetPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
        presetPanel.Children.Add(new TextBlock
        {
            Text = "Presets:",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });

        var presetA4 = new Button { Content = "A4", FontSize = 11, Padding = new Thickness(8, 4, 8, 4) };
        var presetLetter = new Button { Content = "Letter", FontSize = 11, Padding = new Thickness(8, 4, 8, 4) };
        var presetA5 = new Button { Content = "A5", FontSize = 11, Padding = new Thickness(8, 4, 8, 4) };
        var preset4x6 = new Button { Content = "4×6\"", FontSize = 11, Padding = new Thickness(8, 4, 8, 4) };

        presetA4.Click += (s, args) => { unitCombo.SelectedIndex = 0; widthBox.Value = 210; heightBox.Value = 297; };
        presetLetter.Click += (s, args) => { unitCombo.SelectedIndex = 1; widthBox.Value = 8.5; heightBox.Value = 11; };
        presetA5.Click += (s, args) => { unitCombo.SelectedIndex = 0; widthBox.Value = 148; heightBox.Value = 210; };
        preset4x6.Click += (s, args) => { unitCombo.SelectedIndex = 1; widthBox.Value = 4; heightBox.Value = 6; };

        presetPanel.Children.Add(presetA4);
        presetPanel.Children.Add(presetLetter);
        presetPanel.Children.Add(presetA5);
        presetPanel.Children.Add(preset4x6);
        customSizePanel.Children.Add(presetPanel);

        panel.Children.Add(customSizePanel);

        int previousUnitIndex = unitCombo.SelectedIndex;

        unitCombo.SelectionChanged += (s, args) =>
        {
            var unitName = unitCombo.SelectedIndex switch
            {
                0 => "mm",
                1 => "in",
                2 => "pt",
                _ => "mm"
            };
            widthUnitLabel.Text = unitName;
            heightUnitLabel.Text = unitName;

            var mmPerInch = 25.4;
            var ptPerInch = 72.0;

            double ConvertValue(double value, int fromUnit, int toUnit)
            {
                if (fromUnit == toUnit) return value;
                double valueInMm = fromUnit switch
                {
                    0 => value, // mm
                    1 => value * mmPerInch, // in -> mm
                    2 => value * (mmPerInch / ptPerInch), // pt -> mm
                    _ => value
                };
                return toUnit switch
                {
                    0 => valueInMm, // mm
                    1 => valueInMm / mmPerInch, // mm -> in
                    2 => valueInMm * (ptPerInch / mmPerInch), // mm -> pt
                    _ => valueInMm
                };
            }

            var newUnitIndex = unitCombo.SelectedIndex;
            if (newUnitIndex != previousUnitIndex)
            {
                widthBox.Value = ConvertValue(widthBox.Value, previousUnitIndex, newUnitIndex);
                heightBox.Value = ConvertValue(heightBox.Value, previousUnitIndex, newUnitIndex);
                previousUnitIndex = newUnitIndex;
            }
        };

        paperSizeCombo.SelectionChanged += (s, args) =>
        {
            customSizePanel.Visibility = paperSizeCombo.SelectedIndex == 5
                ? Visibility.Visible
                : Visibility.Collapsed;
        };

        panel.Children.Add(new TextBlock
        {
            Text = "Orientation:",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 0)
        });

        var orientationCombo = new ComboBox { Width = 220 };
        orientationCombo.Items.Add("Automatic");
        orientationCombo.Items.Add("Portrait");
        orientationCombo.Items.Add("Landscape");
        orientationCombo.SelectedIndex = (int)AppSettings.Current.Orientation;
        panel.Children.Add(orientationCombo);

        panel.Children.Add(new TextBlock
        {
            Text = "Page Margins:",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 0)
        });

        var marginCombo = new ComboBox { Width = 220 };
        marginCombo.Items.Add("None (0\")");
        marginCombo.Items.Add("Narrow (0.25\")");
        marginCombo.Items.Add("Normal (0.5\")");
        marginCombo.Items.Add("Wide (1\")");
        marginCombo.Items.Add("Extra Wide (1.5\")");
        marginCombo.Items.Add("Custom...");
        marginCombo.SelectedIndex = (int)AppSettings.Current.Margin;
        panel.Children.Add(marginCombo);

        var customMarginPanel = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = AppSettings.Current.Margin == (PdfPageMargin)5 ? Visibility.Visible : Visibility.Collapsed
        };

        var marginUnitPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        marginUnitPanel.Children.Add(new TextBlock
        {
            Text = "Unit:",
            VerticalAlignment = VerticalAlignment.Center,
            Width = 50
        });
        var marginUnitCombo = new ComboBox { Width = 120 };
        marginUnitCombo.Items.Add("Millimeters (mm)");
        marginUnitCombo.Items.Add("Inches (in)");
        marginUnitCombo.Items.Add("Points (pt)");
        marginUnitCombo.SelectedIndex = (int)AppSettings.Current.CustomMarginUnit;
        marginUnitPanel.Children.Add(marginUnitCombo);
        customMarginPanel.Children.Add(marginUnitPanel);

        var marginGrid = new Grid { ColumnSpacing = 8, RowSpacing = 4 };
        for (int i = 0; i < 4; i++) marginGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leftMarginBox = new NumberBox
        {
            Header = "Left",
            Value = AppSettings.Current.CustomMarginLeft,
            Minimum = 0,
            Maximum = 100,
            Width = 100,
            SmallChange = 0.1,
            LargeChange = 0.5
        };
        var rightMarginBox = new NumberBox
        {
            Header = "Right",
            Value = AppSettings.Current.CustomMarginRight,
            Minimum = 0,
            Maximum = 100,
            Width = 100,
            SmallChange = 0.1,
            LargeChange = 0.5
        };
        var topMarginBox = new NumberBox
        {
            Header = "Top",
            Value = AppSettings.Current.CustomMarginTop,
            Minimum = 0,
            Maximum = 100,
            Width = 100,
            SmallChange = 0.1,
            LargeChange = 0.5
        };
        var bottomMarginBox = new NumberBox
        {
            Header = "Bottom",
            Value = AppSettings.Current.CustomMarginBottom,
            Minimum = 0,
            Maximum = 100,
            Width = 100,
            SmallChange = 0.1,
            LargeChange = 0.5
        };

        marginGrid.Children.Add(leftMarginBox);
        Grid.SetColumn(leftMarginBox, 0);
        marginGrid.Children.Add(topMarginBox);
        Grid.SetColumn(topMarginBox, 1);
        marginGrid.Children.Add(rightMarginBox);
        Grid.SetColumn(rightMarginBox, 2);
        marginGrid.Children.Add(bottomMarginBox);
        Grid.SetColumn(bottomMarginBox, 3);

        customMarginPanel.Children.Add(marginGrid);
        panel.Children.Add(customMarginPanel);

        int previousMarginUnit = marginUnitCombo.SelectedIndex;
        marginUnitCombo.SelectionChanged += (s, args) =>
        {
            var mmPerInch = 25.4;
            var ptPerInch = 72.0;
            double ConvertValue(double value, int fromUnit, int toUnit)
            {
                if (fromUnit == toUnit) return value;
                double valueInMm = fromUnit switch
                {
                    0 => value, // mm
                    1 => value * mmPerInch, // in -> mm
                    2 => value * (mmPerInch / ptPerInch), // pt -> mm
                    _ => value
                };
                return toUnit switch
                {
                    0 => valueInMm, // mm
                    1 => valueInMm / mmPerInch, // mm -> in
                    2 => valueInMm * (ptPerInch / mmPerInch), // mm -> pt
                    _ => valueInMm
                };
            }
            var newUnit = marginUnitCombo.SelectedIndex;
            if (newUnit != previousMarginUnit)
            {
                leftMarginBox.Value = ConvertValue(leftMarginBox.Value, previousMarginUnit, newUnit);
                rightMarginBox.Value = ConvertValue(rightMarginBox.Value, previousMarginUnit, newUnit);
                topMarginBox.Value = ConvertValue(topMarginBox.Value, previousMarginUnit, newUnit);
                bottomMarginBox.Value = ConvertValue(bottomMarginBox.Value, previousMarginUnit, newUnit);
                previousMarginUnit = newUnit;
            }
        };

        marginCombo.SelectionChanged += (s, args) =>
        {
            customMarginPanel.Visibility = marginCombo.SelectedIndex == 5
                ? Visibility.Visible
                : Visibility.Collapsed;
        };

        var infoText = new TextBlock
        {
            Text = "Note: Margins only apply when using a specific page size (not Automatic).",
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
        panel.Children.Add(infoText);

        panel.Children.Add(new TextBlock
        {
            Text = "Image Compression:",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 0)
        });

        var compressionCombo = new ComboBox { Width = 220 };
        compressionCombo.Items.Add("None (Original Quality)");
        compressionCombo.Items.Add("Low (JPEG 85%)");
        compressionCombo.Items.Add("Medium (JPEG 65%)");
        compressionCombo.Items.Add("High (JPEG 40%)");
        compressionCombo.SelectedIndex = (int)AppSettings.Current.ImageCompression;
        panel.Children.Add(compressionCombo);

        panel.Children.Add(new TextBlock
        {
            Text = "Note: Compression re-encodes all images to JPEG. Higher compression means smaller file size but lower quality.",
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });

        var scrollViewer = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 400
        };

        var dialog = new ContentDialog
        {
            Title = "PDF Settings",
            Content = scrollViewer,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            XamlRoot = Content.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            AppSettings.Current.PaperSize = (PdfPaperSize)paperSizeCombo.SelectedIndex;
            AppSettings.Current.Orientation = (PdfPaperOrientation)orientationCombo.SelectedIndex;
            AppSettings.Current.Margin = (PdfPageMargin)marginCombo.SelectedIndex;
            AppSettings.Current.ImageCompression = (PdfImageCompression)compressionCombo.SelectedIndex;

            if (AppSettings.Current.PaperSize == PdfPaperSize.Custom)
            {
                AppSettings.Current.CustomSizeUnit = unitCombo.SelectedIndex;
                AppSettings.Current.CustomWidth = widthBox.Value;
                AppSettings.Current.CustomHeight = heightBox.Value;
            }
            if (AppSettings.Current.Margin == PdfPageMargin.Custom)
            {
                AppSettings.Current.CustomMarginUnit = (MarginUnit)marginUnitCombo.SelectedIndex;
                AppSettings.Current.CustomMarginLeft = leftMarginBox.Value;
                AppSettings.Current.CustomMarginRight = rightMarginBox.Value;
                AppSettings.Current.CustomMarginTop = topMarginBox.Value;
                AppSettings.Current.CustomMarginBottom = bottomMarginBox.Value;
            }

            await AppSettings.SaveAsync();
            StatusTextBlock.Text = "Settings saved";
        }
    }

    private async void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 8 };

        panel.Children.Add(new TextBlock
        {
            Text = "Gladhen3 - Convert images to PDF and merge PDF files.\nDrag to reorder, double-click to preview.",
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(new TextBlock { Text = "Version: 1.0.8" });

        var linkPanel = new StackPanel { Orientation = Orientation.Horizontal };
        linkPanel.Children.Add(new TextBlock { Text = "Repository: ", VerticalAlignment = VerticalAlignment.Center });
        linkPanel.Children.Add(new HyperlinkButton
        {
            Content = "github.com/armiaab/Gladhen3",
            NavigateUri = new Uri("https://github.com/armiaab/Gladhen3")
        });
        panel.Children.Add(linkPanel);

        await ShowDialogAsync("About Gladhen3", panel);
    }

    private async Task ShowDialogAsync(string title, object content)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "OK",
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
                StatusTextBlock.Text = "No supported files received";
                return;
            }

            StatusTextBlock.Text = $"Receiving {pathList.Count} file(s)...";

            var newItems = await _documentService.LoadDocumentsFromPathsAsync(pathList);

            await AddItemsInBatchesAsync(newItems);

            UpdateUIState();

            if (newItems.Count > 0)
            {
                StatusTextBlock.Text = $"Added {newItems.Count} page(s) from another instance";

                _ = LoadThumbnailsInBackgroundAsync(newItems);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error adding files from paths");
            StatusTextBlock.Text = $"Error: {ex.Message}";
        }
    }

    #endregion
}
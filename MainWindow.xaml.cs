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
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

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

        // Set up custom title bar
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Set window icon
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
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

            // Set the icon from the app's assets
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
            // Fast load: create items without thumbnails
            var items = await _documentService.CreateDocumentItemsBatchAsync(files, progress);

            // Add items immediately so user sees them
            foreach (var item in items)
            {
                _documentItems.Add(item);
                addedCount++;
            }

            UpdateUIState();

            if (addedCount > 0)
            {
                StatusTextBlock.Text = $"Added {addedCount} page(s)";

                // Load thumbnails in background without blocking - fire and forget
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
            // Fast load without thumbnails
            var newItems = await _documentService.LoadDocumentsFromPathsAsync(pathList);

            // Add items immediately
            foreach (var item in newItems)
                _documentItems.Add(item);

            UpdateUIState();

            if (newItems.Count > 0)
            {
                StatusTextBlock.Text = $"Loaded {newItems.Count} page(s)";

                // Load thumbnails in background without blocking - fire and forget
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
    /// Loads thumbnails in background without blocking UI. Uses DispatcherQueue for UI updates.
    /// </summary>
    private async Task LoadThumbnailsInBackgroundAsync(IList<DocumentItem> items)
    {
        var dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        foreach (var item in items)
        {
            if (item.Thumbnail != null) continue;

            try
            {
                BitmapImage? thumbnail = null;

                // Load thumbnail on background/IO thread conceptually, but BitmapImage needs UI thread
                if (item.Type == DocumentType.Image)
                {
                    thumbnail = await LoadImageThumbnailAsync(item.FilePath);
                }
                else if (item.Type == DocumentType.PdfPage)
                {
                    thumbnail = await LoadPdfThumbnailAsync(item);
                }

                // Update on UI thread
                if (thumbnail != null)
                {
                    item.Thumbnail = thumbnail;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error loading thumbnail for: {Path}", item.FilePath);
            }

            // Yield to allow UI to remain responsive
            await Task.Delay(1);
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

    private static async Task<BitmapImage?> LoadPdfThumbnailAsync(DocumentItem item)
    {
        try
        {
            var sourcePath = item.SourcePdfPath ?? item.FilePath;
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                return null;

            var file = await StorageFile.GetFileFromPathAsync(sourcePath);
            var pdfDocument = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);

            if (item.PageNumber < 1 || item.PageNumber > pdfDocument.PageCount)
                return null;

            using var page = pdfDocument.GetPage((uint)(item.PageNumber - 1));
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();

            var options = new Windows.Data.Pdf.PdfPageRenderOptions
            {
                DestinationWidth = 200,
                DestinationHeight = (uint)(200 * page.Size.Height / page.Size.Width)
            };

            await page.RenderToStreamAsync(stream, options);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error loading PDF thumbnail");
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
        savePicker.FileTypeChoices.Add("PDF Document", [".pdf"]);

        InitializeWithWindow.Initialize(savePicker, WindowNative.GetWindowHandle(this));
        var file = await savePicker.PickSaveFileAsync();

        if (file == null)
        {
            StatusTextBlock.Text = "Cancelled";
            return;
        }

        try
        {
            StatusTextBlock.Text = "Creating PDF...";
            var items = _documentItems.ToList();
            var outputPath = file.Path;

            await Task.Run(() => _pdfService.CreatePdfFromDocuments(items, outputPath));

            StatusTextBlock.Text = "PDF created successfully";
            await ShowDialogAsync("Success", $"PDF saved to:\n{outputPath}");
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Error: {ex.Message}";
            Log.Error(ex, "Error creating PDF");
            await ShowDialogAsync("Error", $"Failed to create PDF:\n{ex.Message}");
        }
    }

    #endregion

    #region View & Preview

    private void GridViewToggle_Checked(object sender, RoutedEventArgs e)
    {
        _isGridView = true;
        UpdateViewToggle();
    }

    private void ListViewToggle_Checked(object sender, RoutedEventArgs e)
    {
        _isGridView = false;
        UpdateViewToggle();
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
            // Don't preview in select mode
            if (_isSelectMode) return;

            // Check if we have items
            if (_documentItems.Count == 0) return;

            // Find the item that was double-clicked by traversing up the visual tree
            var element = e.OriginalSource as FrameworkElement;
            DocumentItem? clickedItem = null;

            while (element != null)
            {
                // Check if this element has a DocumentItem as DataContext
                if (element.DataContext is DocumentItem item)
                {
                    clickedItem = item;
                    break;
                }

                // Stop if we've reached the GridView/ListView itself (not inside an item)
                if (element == DocumentGridView || element == DocumentListView)
                    break;

                element = element.Parent as FrameworkElement;
            }

            // Only open preview if we actually clicked on a document item with valid data
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
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            MinWidth = 800,
            MaxWidth = 1000
        };

        // Main layout grid
        var mainGrid = new Grid();
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Footer

        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Left nav
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Content
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Right nav

        // Image container with background
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

        // Navigation buttons with subtle style
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

        // Header with title and actions
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

        // Footer with file info and page indicator
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

        // Add all to main grid
        mainGrid.Children.Add(headerGrid);
        mainGrid.Children.Add(prevButton);
        mainGrid.Children.Add(imageContainer);
        mainGrid.Children.Add(nextButton);
        mainGrid.Children.Add(footerGrid);

        dialog.Content = mainGrid;
        dialog.CloseButtonText = "Close";

        // Loading indicator
        var loadingRing = new ProgressRing
        {
            IsActive = false,
            Width = 48,
            Height = 48,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Update zoom text when zoom changes
        scrollViewer.ViewChanged += (s, args) =>
        {
            var zoomPercent = (int)(scrollViewer.ZoomFactor * 100);
            zoomText.Text = $"{zoomPercent}%";
        };

        // Function to load and display item
        async Task LoadItemAsync(int index)
        {
            if (index < 0 || index >= _documentItems.Count) return;

            var item = _documentItems[index];
            currentIndex = index;

            // Update UI
            titleText.Text = item.DisplayName;
            filePathText.Text = item.FilePath;
            pageText.Text = $"{index + 1} / {_documentItems.Count}";

            // Update nav button states
            prevButton.IsEnabled = index > 0;
            nextButton.IsEnabled = index < _documentItems.Count - 1;
            prevButton.Opacity = index > 0 ? 0.8 : 0.3;
            nextButton.Opacity = index < _documentItems.Count - 1 ? 0.8 : 0.3;

            // File details
            if (File.Exists(item.FilePath))
            {
                var fileInfo = new FileInfo(item.FilePath);
                var details = item.Type == DocumentType.PdfPage
                    ? $"{item.FileSize} • Page {item.PageNumber} of {item.TotalPages} • {fileInfo.LastWriteTime:g}"
                    : $"{item.FileSize} • {fileInfo.LastWriteTime:g}";
                fileDetailsText.Text = details;
            }

            // Load image
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
                    bitmap = await RenderPdfPageHighResAsync(item);
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
                    // Reset to fit view
                    scrollViewer.ChangeView(null, null, 1f);
                    zoomText.Text = "100%";
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

        fitButton.Click += (s, args) =>
        {
            scrollViewer.ChangeView(0, 0, 1f);
        };

        // Keyboard navigation
        dialog.KeyDown += async (s, args) =>
        {
            switch (args.Key)
            {
                case Windows.System.VirtualKey.Left:
                    if (currentIndex > 0)
                    {
                        await LoadItemAsync(currentIndex - 1);
                        args.Handled = true;
                    }
                    break;
                case Windows.System.VirtualKey.Right:
                    if (currentIndex < _documentItems.Count - 1)
                    {
                        await LoadItemAsync(currentIndex + 1);
                        args.Handled = true;
                    }
                    break;
                case Windows.System.VirtualKey.Add:
                    var zoomIn = Math.Min(scrollViewer.ZoomFactor * 1.25f, 5f);
                    scrollViewer.ChangeView(null, null, zoomIn);
                    args.Handled = true;
                    break;
                case Windows.System.VirtualKey.Subtract:
                    var zoomOut = Math.Max(scrollViewer.ZoomFactor / 1.25f, 0.1f);
                    scrollViewer.ChangeView(null, null, zoomOut);
                    args.Handled = true;
                    break;
                case Windows.System.VirtualKey.Number0:
                    scrollViewer.ChangeView(0, 0, 1f);
                    args.Handled = true;
                    break;
            }
        };

        // Load initial item
        await LoadItemAsync(startIndex);

        await dialog.ShowAsync();
    }

    private static async Task<BitmapImage?> RenderPdfPageHighResAsync(DocumentItem item)
    {
        try
        {
            var sourcePath = item.SourcePdfPath ?? item.FilePath;
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                return null;

            var file = await StorageFile.GetFileFromPathAsync(sourcePath);
            var pdfDocument = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);

            if (item.PageNumber < 1 || item.PageNumber > pdfDocument.PageCount)
                return null;

            using var page = pdfDocument.GetPage((uint)(item.PageNumber - 1));
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();

            // Render at higher resolution (600px width instead of 200px)
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

        _documentItems.Clear();
        foreach (var item in sorted)
            _documentItems.Add(item);
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
        var panel = new StackPanel { Spacing = 12 };

        panel.Children.Add(new TextBlock
        {
            Text = "Paper Size (for images):",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });

        var paperSizeCombo = new ComboBox { Width = 220 };
        paperSizeCombo.Items.Add("Automatic (Use Image Size)");
        paperSizeCombo.Items.Add("A4");
        paperSizeCombo.Items.Add("Letter");
        paperSizeCombo.Items.Add("Legal");
        paperSizeCombo.Items.Add("A3");
        paperSizeCombo.SelectedIndex = (int)AppSettings.Current.PaperSize;
        panel.Children.Add(paperSizeCombo);

        panel.Children.Add(new TextBlock
        {
            Text = "Orientation:",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 0)
        });

        var orientationCombo = new ComboBox { Width = 220 };
        orientationCombo.Items.Add("Automatic");
        orientationCombo.Items.Add("Portrait");
        orientationCombo.Items.Add("Landscape");
        orientationCombo.SelectedIndex = (int)AppSettings.Current.Orientation;
        panel.Children.Add(orientationCombo);

        var dialog = new ContentDialog
        {
            Title = "PDF Settings",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            XamlRoot = Content.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            AppSettings.Current.PaperSize = (PdfPaperSize)paperSizeCombo.SelectedIndex;
            AppSettings.Current.Orientation = (PdfPaperOrientation)orientationCombo.SelectedIndex;
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

        panel.Children.Add(new TextBlock { Text = "Version: 1.0.3" });

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

            // Fast load without thumbnails
            var newItems = await _documentService.LoadDocumentsFromPathsAsync(pathList);

            // Add items immediately
            foreach (var item in newItems)
                _documentItems.Add(item);

            UpdateUIState();

            if (newItems.Count > 0)
            {
                StatusTextBlock.Text = $"Added {newItems.Count} page(s) from another instance";

                // Load thumbnails in background
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
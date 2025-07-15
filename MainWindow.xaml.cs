using Gladhen3.Models;
using Gladhen3.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Gladhen3;

public sealed partial class MainWindow : Window
{
    private ObservableCollection<ImageItem> ImageItems { get; set; } = [];
    private readonly ImageService _imageService;
    private readonly PdfService _pdfService;

    public MainWindow()
    {
        InitializeComponent();
        _imageService = new ImageService();
        _pdfService = new PdfService();

        ImageGridView.ItemsSource = ImageItems;
        _ = AppSettings.LoadAsync();

        UpdateUIState();
    }

    public MainWindow(IEnumerable<string> paths)
    {
        InitializeComponent();
        _imageService = new ImageService();
        _pdfService = new PdfService();

        ImageGridView.ItemsSource = ImageItems;
        _ = AppSettings.LoadAsync();

        if (paths != null && paths.Any())
        {
            StatusTextBlock.Text = $"Load {paths.Select(p => Path.GetFileName(p)).Aggregate((current, next) => $"{current}, {next}")}";
            _ = LoadImagesAndSelectNew(paths);
        }
        else
            UpdateUIState();
    }

    private void UpdateUIState()
    {
        EmptyStateTextBlock.Visibility = ImageItems.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void AddImagesButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.Thumbnail,
            SuggestedStartLocation = PickerLocationId.PicturesLibrary
        };

        foreach (var ext in FileService.ValidImageExtensions)
        {
            picker.FileTypeFilter.Add(ext);
        }

        var hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hwnd);

        var files = await picker.PickMultipleFilesAsync();

        if (files != null)
        {
            foreach (var file in files)
            {
                var item = await _imageService.CreateImageItemFromFileAsync(file);
                if (item != null)
                    ImageItems.Add(item);
            }

            UpdateUIState();
        }
    }

    private async void ConvertButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedItems = ImageGridView.SelectedItems.Cast<ImageItem>().ToList();

        if (selectedItems.Count > 0)
        {
            var message = $"Converting {selectedItems.Count} selected images to PDF...";
            StatusTextBlock.Text = message;

            try
            {
                var savePicker = new FileSavePicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                    SuggestedFileName = "SelectedImages",
                    FileTypeChoices = {
                        { "PDF Document", new List<string>() { ".pdf" } }
                    }
                };

            var hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(savePicker, hwnd);

            StorageFile file = await savePicker.PickSaveFileAsync();
            if (file != null)
            {
                StatusTextBlock.Text = "Creating PDF...";

                try
                {
                    string outputPath = file.Path;

                        await Task.Run(() =>
                        {
                            _pdfService.ConvertImagesToPdf(selectedItems, outputPath);
                        });

                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        StatusTextBlock.Text = "PDF creation completed";

                            var dialog = new ContentDialog
                            {
                                Title = "Conversion Complete",
                                Content = $"PDF file created successfully at:\n{outputPath}",
                                CloseButtonText = "OK",
                                XamlRoot = Content.XamlRoot
                            };
                            await dialog.ShowAsync();
                        });
                    }
                    catch (Exception ex)
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            StatusTextBlock.Text = $"Error: {ex.Message}";
                        });
                    }
                }
                else
                {
                    StatusTextBlock.Text = "Conversion cancelled";
                }
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"Error: {ex.Message}";
            }
        }
        else
        {
            var noSelectionDialog = new ContentDialog()
            {
                Title = "No Images Selected",
                Content = "Please select one or more images to convert.",
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot
            };
            await noSelectionDialog.ShowAsync();
        }
    }

    private void ImageGridView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        StatusTextBlock.Text = "Reordering images...";
    }

    private void ImageGridView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (args.DropResult == Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                StatusTextBlock.Text = "Reordering complete";
                await Task.Delay(1000);
            });
        }
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (ImageItems.Count > 0)
        {
            ImageGridView.SelectAll();
            StatusTextBlock.Text = $"Selected {ImageGridView.SelectedItems.Count} images";
        }
    }

    private void DeselectAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (ImageGridView.SelectedItems.Count > 0)
        {
            ImageGridView.SelectedItems.Clear();
            StatusTextBlock.Text = "All images deselected";
        }
    }

    private void ImageGridView_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;

        StatusTextBlock.Text = "Drop files here to add images";

        e.DragUIOverride.Caption = "Add images";
        e.DragUIOverride.IsContentVisible = true;
        e.DragUIOverride.IsCaptionVisible = true;
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        Log.Information("Application closed");
        Log.CloseAndFlushAsync();
    }

    private void LogButton_Click(object sender, RoutedEventArgs e)
    {
        FileService.OpenLogDirectory();
    }

    private void MenuFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SortByFileNameAsc_Click(object sender, RoutedEventArgs e)
    {
        SortImages(item => item.FileName, true);
        StatusTextBlock.Text = "Sorted by filename (A-Z)";
    }

    private void SortByFileNameDesc_Click(object sender, RoutedEventArgs e)
    {
        SortImages(item => item.FileName, false);
        StatusTextBlock.Text = "Sorted by filename (Z-A)";
    }

    private void SortByFilePathAsc_Click(object sender, RoutedEventArgs e)
    {
        SortImages(item => item.FilePath, true);
        StatusTextBlock.Text = "Sorted by file path (A-Z)";
    }

    private void SortByFilePathDesc_Click(object sender, RoutedEventArgs e)
    {
        SortImages(item => item.FilePath, false);
        StatusTextBlock.Text = "Sorted by file path (Z-A)";
    }

    private void SortImages<T>(Func<ImageItem, T?> keySelector, bool ascending) where T : IComparable
    {
        var selectedItems = ImageGridView.SelectedItems.Cast<ImageItem>().ToList();

        var sortedList = ascending
            ? ImageItems.OrderBy(keySelector).ToList()
            : ImageItems.OrderByDescending(keySelector).ToList();

        ImageItems.Clear();
        foreach (var item in sortedList)
            ImageItems.Add(item);

        foreach (var item in selectedItems)
        {
            var newItem = ImageItems.FirstOrDefault(i => i.FilePath == item.FilePath);
            if (newItem != null)
                ImageGridView.SelectedItems.Add(newItem);
        }
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (ImageGridView.SelectedItems.Count > 0)
        {
            var itemsToRemove = ImageGridView.SelectedItems.Cast<ImageItem>().ToList();
            foreach (var item in itemsToRemove)
            {
                ImageItems.Remove(item);
            }

            StatusTextBlock.Text = $"Removed {itemsToRemove.Count} image(s)";
            UpdateUIState();
        }
        else
        {
            StatusTextBlock.Text = "No images selected to remove";
        }
    }

    private void RemoveAll_Click(object sender, RoutedEventArgs e)
    {
        int count = ImageItems.Count;

        if (count > 0)
        {
            ImageItems.Clear();
            StatusTextBlock.Text = $"Removed all {count} image(s)";
            UpdateUIState();
        }
        else
        {
            StatusTextBlock.Text = "No images to remove";
        }
    }

    private void SortByFileDateAsc_Click(object sender, RoutedEventArgs e)
    {
        SortImages(item => File.GetLastWriteTime(item.FilePath!), true);
        StatusTextBlock.Text = "Sorted by file date (oldest first)";
    }

    private void SortByFileDateDesc_Click(object sender, RoutedEventArgs e)
    {
        SortImages(item => File.GetLastWriteTime(item.FilePath!), false);
        StatusTextBlock.Text = "Sorted by file date (newest first)";
    }

    private void SortByFileSizeDateAsc_Click(object sender, RoutedEventArgs e)
    {
        SortImages(item => new FileInfo(item.FilePath!).Length, true);
        StatusTextBlock.Text = "Sorted by file size (smallest first)";
    }

    private void SortByFileSizeDateDesc_Click(object sender, RoutedEventArgs e)
    {
        SortImages(item => new FileInfo(item.FilePath!).Length, false);
        StatusTextBlock.Text = "Sorted by file size (largest first)";
    }

    private void RegisterShellExtension_Click(object sender, RoutedEventArgs e)
    {
        //TODO: implement soon
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "PDF Settings",
            XamlRoot = Content.XamlRoot,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel"
        };

        var panel = new StackPanel { Spacing = 10 };

        panel.Children.Add(new TextBlock { Text = "Paper Size:" });
        var paperSizeCombo = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        paperSizeCombo.Items.Add("Automatic (Use Image Size)");
        paperSizeCombo.Items.Add("A4");
        paperSizeCombo.Items.Add("Letter");
        paperSizeCombo.Items.Add("Legal");
        paperSizeCombo.Items.Add("A3");
        paperSizeCombo.SelectedIndex = (int)AppSettings.Current.PaperSize;
        panel.Children.Add(paperSizeCombo);

        panel.Children.Add(new TextBlock { Text = "Orientation:" });
        var orientationCombo = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        orientationCombo.Items.Add("Automatic");
        orientationCombo.Items.Add("Portrait");
        orientationCombo.Items.Add("Landscape");
        orientationCombo.SelectedIndex = (int)AppSettings.Current.Orientation;
        panel.Children.Add(orientationCombo);

        dialog.Content = panel;

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            AppSettings.Current.PaperSize = (PdfPaperSize)paperSizeCombo.SelectedIndex;
            AppSettings.Current.Orientation = (PdfPaperOrientation)orientationCombo.SelectedIndex;
            await AppSettings.SaveAsync();
            StatusTextBlock.Text = "Settings saved";
        }
    }

    private void SelectNewlyAddedImages(List<ImageItem> newItems)
    {
        if (newItems.Count == 0)
            return;

        ImageGridView.SelectedItems.Clear();
        foreach (var item in newItems)
        {
            ImageGridView.SelectedItems.Add(item);
        }

        StatusTextBlock.Text = $"Selected {newItems.Count} newly added images";
    }

    public async Task LoadImagesAndSelectNew(IEnumerable<string> paths)
    {
        var newItems = await _imageService.LoadImagesFromPaths(paths);

        foreach (var item in newItems)
        {
            ImageItems.Add(item);
        }

        UpdateUIState();
        SelectNewlyAddedImages(newItems);
    }

    private async void ImageGridView_Drop(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            var items = await e.DataView.GetStorageItemsAsync();

            var imageFiles = items.OfType<StorageFile>()
                                  .Where(file => FileService.IsImageFile(file.Path))
                                  .ToList();

            StatusTextBlock.Text = $"Adding {imageFiles.Count} images...";

            var newItems = new List<ImageItem>();
            foreach (var file in imageFiles)
            {
                var newItem = await _imageService.CreateImageItemFromFileAsync(file);
                if (newItem != null)
                {
                    ImageItems.Add(newItem);
                    newItems.Add(newItem);
                }
            }

            SelectNewlyAddedImages(newItems);
            UpdateUIState();

            StatusTextBlock.Text = $"Added {imageFiles.Count} images";
        }
    }
}
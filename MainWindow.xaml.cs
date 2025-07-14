using Gladhen3.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace Gladhen3;

public sealed partial class MainWindow : Window
{
    private ObservableCollection<ImageItem> ImageItems { get; set; } = [];

    public MainWindow()
    {
        InitializeComponent();

        ImageGridView.ItemsSource = ImageItems;
        _ = AppSettings.LoadAsync();

        UpdateUIState();
    }

    public MainWindow(IEnumerable<string> paths)
    {
        InitializeComponent();

        ImageGridView.ItemsSource = ImageItems;
        _ = AppSettings.LoadAsync();

        if (paths != null && paths.Any())
        {
            StatusTextBlock.Text = $"Load {paths.Select(p => Path.GetFileName(p)).Aggregate((current, next) => $"{current}, {next}")}";
            LoadImagesFromPaths(paths);
        }

        UpdateUIState();
    }

    private async void LoadImagesFromPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                if (string.IsNullOrEmpty(path))
                    continue;

                string normalizedPath = path;

                if (path.Length >= 260 && !path.StartsWith(@"\\?\"))
                {
                    normalizedPath = @"\\?\" + path;
                }

                if (!File.Exists(normalizedPath))
                {
                    System.Diagnostics.Debug.WriteLine($"File not found: {path}");
                    continue;
                }

                if (IsImageFile(normalizedPath))
                {
                    StorageFile? file = null;
                    try
                    {
                        file = await StorageFile.GetFileFromPathAsync(normalizedPath);
                    }
                    catch (Exception ex) when (ex is ArgumentException || ex is FileNotFoundException)
                    {
                        try
                        {
                            file = await StorageFile.GetFileFromPathAsync(path);
                        }
                        catch
                        {
                            file = await OpenFileWithTempCopyAsync(path);
                        }
                    }

                    if (file != null)
                    {
                        await AddImageFromFileAsync(file);
                    }
                }
            }
            catch (FileNotFoundException)
            {
                Log.Logger.Error($"File not found: {path}");
            }
            catch (UnauthorizedAccessException)
            {
                Log.Logger.Error($"Access denied to file: {path}");
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, $"Error loading image from path: {path}");
                Log.Logger.Error(ex, $"Exception type: {ex.GetType().Name}");
                Log.Logger.Error(ex, $"Stack trace: {ex.StackTrace}");
            }
        }

        UpdateUIState();
    }

    private static async Task<StorageFile?> OpenFileWithTempCopyAsync(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return null;

            var fileName = string.Concat(Path.GetFileNameWithoutExtension(filePath), "_", Guid.NewGuid().ToString().AsSpan(0, 8), Path.GetExtension(filePath));

            var tempPath = Path.Combine(Path.GetTempPath(), fileName);

            File.Copy(filePath, tempPath, true);

            return await StorageFile.GetFileFromPathAsync(tempPath);
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, $"Error creating temporary copy of file: {filePath}");
            return null;
        }
    }

    private async Task AddImageFromFileAsync(StorageFile file)
    {
        try
        {
            var bitmapImage = new BitmapImage();
            using (IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read))
            {
                await bitmapImage.SetSourceAsync(stream);
            }

            var item = new ImageItem
            {
                ImagePath = bitmapImage,
                FileName = file.Name,
                FilePath = file.Path
            };

            ImageItems.Add(item);
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, $"Error adding image from file: {file.Path}");
        }
    }

    private static bool IsImageFile(string filePath)
    {
        string[] validExtensions = [
        ".jpg", ".jpeg", ".png", ".bmp"];
        var extension = Path.GetExtension(filePath).ToLower();
        return validExtensions.Contains(extension);
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

        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".bmp");

        var hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hwnd);

        var files = await picker.PickMultipleFilesAsync();

        if (files != null)
        {
            foreach (var file in files)
            {
                await AddImageFromFileAsync(file);
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

                        await Task.Run(() => {
                            ConvertImagesToPdf(selectedItems, outputPath);
                        });

                        DispatcherQueue.TryEnqueue(async () => {
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
                        DispatcherQueue.TryEnqueue(() => {
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

    private static void ConvertImagesToPdf(List<ImageItem> images, string outputPath)
    {
        try
        {
            PdfDocument document = new();
            document.Info.Title = "Converted Images";

            foreach (var imageItem in images)
            {
                try
                {
                    using var bitmap = new Bitmap(imageItem.FilePath!);

                    var page = document.AddPage();

                    var imageWidth = bitmap.Width;
                    var imageHeight = bitmap.Height;

                    if (AppSettings.Current.PaperSize == PdfPaperSize.Automatic)
                    {
                        var pointWidth = imageWidth * 72.0 / 96.0;
                        var pointHeight = imageHeight * 72.0 / 96.0;

                        page.Width = new XUnit(pointWidth);
                        page.Height = new XUnit(pointHeight);

                        using XGraphics gfx = XGraphics.FromPdfPage(page);
                        using var xImage = XImage.FromFile(imageItem.FilePath!);
                        gfx.DrawImage(xImage, 0, 0, pointWidth, pointHeight);
                    }
                    else
                    {
                        SetPageSize(page, AppSettings.Current.PaperSize);

                        bool usePortrait = AppSettings.Current.Orientation == PdfPaperOrientation.Portrait ||
                                          (AppSettings.Current.Orientation == PdfPaperOrientation.Automatic &&
                                           imageHeight > imageWidth);

                        if (!usePortrait)
                        {
                            var temp = page.Height.Point;
                            page.Height = page.Width;
                            page.Width = new XUnit(temp);
                        }

                        using XGraphics gfx = XGraphics.FromPdfPage(page);
                        using var xImage = XImage.FromFile(imageItem.FilePath!);

                        double scaleX = page.Width.Point / imageWidth;
                        double scaleY = page.Height.Point / imageHeight;
                        double scale = Math.Min(scaleX, scaleY);

                        double width = imageWidth * scale;
                        double height = imageHeight * scale;
                        double x = (page.Width.Point - width) / 2;
                        double y = (page.Height.Point - height) / 2;

                        gfx.DrawImage(xImage, x, y, width, height);
                    }
                }
                catch (Exception ex)
                {
                    Log.Logger.Error(ex, $"Error processing image: {imageItem.FilePath}");
                }
            }

            document.Save(outputPath);
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, $"Error converting images to PDF: {outputPath}");
            throw;
        }
    }

    private static void SetPageSize(PdfPage page, PdfPaperSize size)
    {
        switch (size)
        {
            case PdfPaperSize.A4:
                page.Width = XUnit.FromMillimeter(210);
                page.Height = XUnit.FromMillimeter(297);
                break;
            case PdfPaperSize.Letter:
                page.Width = XUnit.FromInch(8.5);
                page.Height = XUnit.FromInch(11);
                break;
            case PdfPaperSize.Legal:
                page.Width = XUnit.FromInch(8.5);
                page.Height = XUnit.FromInch(14);
                break;
            case PdfPaperSize.A3:
                page.Width = XUnit.FromMillimeter(297);
                page.Height = XUnit.FromMillimeter(420);
                break;
            default:
                page.Width = XUnit.FromMillimeter(210);
                page.Height = XUnit.FromMillimeter(297);
                break;
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
            var newOrder = ImageItems.Select(item => item.FileName).ToList();

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

    private async void ImageGridView_Drop(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            var items = await e.DataView.GetStorageItemsAsync();

            var imageFiles = items.OfType<StorageFile>()
                                  .Where(file => IsImageFile(file.Path))
                                  .ToList();

            StatusTextBlock.Text = $"Adding {imageFiles.Count} images...";

            foreach (var file in imageFiles)
            {
                await AddImageFromFileAsync(file);
            }

            UpdateUIState();

            StatusTextBlock.Text = $"Added {imageFiles.Count} images";
        }
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        Log.Information("Application closed");
        Log.CloseAndFlushAsync();
    }

    private void LogButton_Click(object sender, RoutedEventArgs e)
    {
        OpenLogDirectory();
    }

    public static void OpenLogDirectory()
    {
        var logDirectory = Path.Combine(ApplicationData.Current.LocalFolder.Path, "Logs");
        try
        {
            if (Directory.Exists(logDirectory))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = logDirectory,
                    UseShellExecute = true
                });
                Log.Information("Log directory opened: {Path}", logDirectory);
            }
            else
            {
                Log.Warning("Log directory does not exist: {Path}", logDirectory);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open log directory: {Path}", logDirectory);
        }
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
}
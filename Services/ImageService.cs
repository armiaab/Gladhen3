using Gladhen3.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Gladhen3.Services;

public class ImageService
{
    public async Task<List<ImageItem>> LoadImagesFromPaths(IEnumerable<string> paths)
    {
        var addedItems = new List<ImageItem>();
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

                if (FileService.IsImageFile(normalizedPath))
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
                            file = await FileService.OpenFileWithTempCopyAsync(path);
                        }
                    }

                    if (file != null)
                    {
                        var newItem = await CreateImageItemFromFileAsync(file);
                        if (newItem != null)
                            addedItems.Add(newItem);
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

        return addedItems;
    }

    public async Task<ImageItem?> CreateImageItemFromFileAsync(StorageFile file)
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

            return item;
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, $"Error adding image from file: {file.Path}");
            return null;
        }
    }
}
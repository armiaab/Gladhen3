using Gladhen3.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using PdfSharp.Pdf.IO;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Gladhen3.Services;

public class DocumentService
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif"];
    private static readonly string[] PdfExtensions = [".pdf"];

    public static bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return Array.Exists(ImageExtensions, e => e == ext);
    }

    public static bool IsPdfFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return Array.Exists(PdfExtensions, e => e == ext);
    }

    public static bool IsSupportedFile(string path)
    {
        return IsImageFile(path) || IsPdfFile(path);
    }

    /// <summary>
    /// Creates document items with thumbnail loading deferred for better performance
    /// </summary>
    public async Task<List<DocumentItem>> CreateDocumentItemsAsync(string filePath, bool loadThumbnails = true)
    {
        var items = new List<DocumentItem>();

        try
        {
            if (!File.Exists(filePath))
                return items;

            var fileInfo = new FileInfo(filePath);
            var fileSize = FormatFileSize((ulong)fileInfo.Length);

            if (IsImageFile(filePath))
            {
                var item = new DocumentItem
                {
                    FileName = fileInfo.Name,
                    FilePath = filePath,
                    Type = DocumentType.Image,
                    PageNumber = 1,
                    TotalPages = 1,
                    FileSize = fileSize
                };

                if (loadThumbnails)
                {
                    item.Thumbnail = await LoadImageThumbnailAsync(filePath);
                }

                items.Add(item);
            }
            else if (IsPdfFile(filePath))
            {
                items.AddRange(await CreatePdfPageItemsAsync(filePath, fileInfo.Name, fileSize, loadThumbnails));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error creating document items from path: {Path}", filePath);
        }

        return items;
    }

    /// <summary>
    /// Creates document items from StorageFile with optional thumbnail loading
    /// </summary>
    public async Task<List<DocumentItem>> CreateDocumentItemsAsync(StorageFile file, bool loadThumbnails = true)
    {
        var items = new List<DocumentItem>();

        try
        {
            var basicProperties = await file.GetBasicPropertiesAsync();
            var fileSize = FormatFileSize(basicProperties.Size);

            if (IsImageFile(file.Path))
            {
                var item = new DocumentItem
                {
                    FileName = file.Name,
                    FilePath = file.Path,
                    Type = DocumentType.Image,
                    PageNumber = 1,
                    TotalPages = 1,
                    FileSize = fileSize
                };

                if (loadThumbnails)
                {
                    item.Thumbnail = await LoadImageThumbnailAsync(file.Path);
                }

                items.Add(item);
            }
            else if (IsPdfFile(file.Path))
            {
                items.AddRange(await CreatePdfPageItemsAsync(file.Path, file.Name, fileSize, loadThumbnails));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error creating document items from file: {Path}", file.Path);
        }

        return items;
    }

    /// <summary>
    /// Batch load multiple files with progress reporting
    /// </summary>
    public async Task<List<DocumentItem>> CreateDocumentItemsBatchAsync(
     IReadOnlyList<StorageFile> files,
        IProgress<(int current, int total, string fileName)>? progress = null,
CancellationToken cancellationToken = default)
    {
        var allItems = new List<DocumentItem>();
        var total = files.Count;

        for (int i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = files[i];
            progress?.Report((i + 1, total, file.Name));

            // Create items without thumbnails first for speed
            var items = await CreateDocumentItemsAsync(file, loadThumbnails: false);
            allItems.AddRange(items);
        }

        return allItems;
    }

    /// <summary>
    /// Load thumbnails for items that don't have them (can be called after initial load)
    /// </summary>
    public async Task LoadThumbnailsAsync(
  IList<DocumentItem> items,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var count = 0;
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (item.Thumbnail == null)
            {
                try
                {
                    if (item.Type == DocumentType.Image)
                    {
                        item.Thumbnail = await LoadImageThumbnailAsync(item.FilePath);
                    }
                    else if (item.Type == DocumentType.PdfPage)
                    {
                        item.Thumbnail = await LoadPdfPageThumbnailAsync(item);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Error loading thumbnail for: {Path}", item.FilePath);
                }
            }

            count++;
            progress?.Report(count);
        }
    }

    private async Task<List<DocumentItem>> CreatePdfPageItemsAsync(string pdfPath, string fileName, string fileSize, bool loadThumbnails)
    {
        var items = new List<DocumentItem>();

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(pdfPath);
            var pdfDocument = await PdfDocument.LoadFromFileAsync(file);
            var pageCount = (int)pdfDocument.PageCount;

            for (var i = 0; i < pageCount; i++)
            {
                var item = new DocumentItem
                {
                    FileName = fileName,
                    FilePath = pdfPath,
                    SourcePdfPath = pdfPath,
                    Type = DocumentType.PdfPage,
                    PageNumber = i + 1,
                    TotalPages = pageCount,
                    FileSize = fileSize
                };

                if (loadThumbnails)
                {
                    item.Thumbnail = await RenderPdfPageThumbnailAsync(pdfDocument, i);
                }

                items.Add(item);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error creating PDF page items: {Path}", pdfPath);

            // Fallback: create items without thumbnails
            var pageCount = GetPdfPageCountFallback(pdfPath);
            for (var i = 0; i < pageCount; i++)
            {
                items.Add(new DocumentItem
                {
                    FileName = fileName,
                    FilePath = pdfPath,
                    SourcePdfPath = pdfPath,
                    Type = DocumentType.PdfPage,
                    PageNumber = i + 1,
                    TotalPages = pageCount,
                    FileSize = fileSize
                });
            }
        }

        return items;
    }

    private static async Task<BitmapImage?> LoadPdfPageThumbnailAsync(DocumentItem item)
    {
        try
        {
            var sourcePath = item.SourcePdfPath ?? item.FilePath;
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                return null;

            var file = await StorageFile.GetFileFromPathAsync(sourcePath);
            var pdfDocument = await PdfDocument.LoadFromFileAsync(file);

            if (item.PageNumber < 1 || item.PageNumber > pdfDocument.PageCount)
                return null;

            return await RenderPdfPageThumbnailAsync(pdfDocument, item.PageNumber - 1);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading PDF page thumbnail");
            return null;
        }
    }

    private static async Task<BitmapImage?> RenderPdfPageThumbnailAsync(PdfDocument pdfDocument, int pageIndex)
    {
        try
        {
            using var page = pdfDocument.GetPage((uint)pageIndex);
            using var stream = new InMemoryRandomAccessStream();

            var options = new PdfPageRenderOptions
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
            Log.Error(ex, "Error rendering PDF page thumbnail: page {PageIndex}", pageIndex);
            return null;
        }
    }

    public async Task<List<DocumentItem>> LoadDocumentsFromPathsAsync(IEnumerable<string> paths)
    {
        var items = new List<DocumentItem>();
        var pathList = paths.Where(p => IsSupportedFile(p) && File.Exists(p)).ToList();

        foreach (var path in pathList)
        {
            items.AddRange(await CreateDocumentItemsAsync(path, loadThumbnails: false));
        }

        return items;
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
            Log.Error(ex, "Error loading thumbnail for image: {Path}", filePath);
            return null;
        }
    }

    private static int GetPdfPageCountFallback(string filePath)
    {
        try
        {
            using var document = PdfReader.Open(filePath, PdfDocumentOpenMode.Import);
            return document.PageCount;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error getting page count for PDF: {Path}", filePath);
            return 1;
        }
    }

    public static string FormatFileSize(ulong bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB"];
        var len = (double)bytes;
        var order = 0;

        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }

        return $"{len:0.##} {sizes[order]}";
    }

    public static string[] GetAllSupportedExtensions()
    {
        var extensions = new List<string>();
        extensions.AddRange(ImageExtensions);
        extensions.AddRange(PdfExtensions);
        return [.. extensions];
    }

    public static string[] GetImageExtensions() => ImageExtensions;

    public static string[] GetPdfExtensions() => PdfExtensions;
}

using Gladhen3.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using PdfSharp.Pdf.IO;
using Serilog;
using System;
using System.Buffers;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Gladhen3.Services;

public class DocumentService
{
    private static readonly string[] ImageExtensions = [
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif",
        ".webp", ".heic", ".heif",
        ".ico", ".wdp", ".hdp",
        ".jxr",
        ".dds",
        ".raw", ".cr2", ".nef", ".arw", ".dng"
    ];
    private static readonly string[] PdfExtensions = [".pdf"];
    private static readonly string[] AllExtensions = [.. ImageExtensions, .. PdfExtensions];
    private static readonly string[] Sizes = ["B", "KB", "MB", "GB"];

    // O(1) set lookup instead of O(n) linear scan for hot-path extension checks.
    private static readonly FrozenSet<string> _imageExtSet =
        ImageExtensions.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Maximum degree of parallelism for file loading operations
    /// </summary>
    private const int MaxDegreeOfParallelism = 4;

    /// <summary>
    /// Thumbnail width for rendering
    /// </summary>
    private const uint ThumbnailWidth = 200;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Length > 0 && _imageExtSet.Contains(ext);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPdfFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Length > 0 && ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSupportedFile(string path)
    {
        return IsImageFile(path) || IsPdfFile(path);
    }

    /// <summary>
    /// Creates document items with thumbnail loading deferred for better performance
    /// </summary>
    public async Task<List<DocumentItem>> CreateDocumentItemsAsync(string filePath, bool loadThumbnails = true)
    {
        // Pre-allocate list with expected capacity
        var items = new List<DocumentItem>(1);

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
        var items = new List<DocumentItem>(1);

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
    /// Batch load multiple files with progress reporting using parallel processing
    /// </summary>
    public async Task<List<DocumentItem>> CreateDocumentItemsBatchAsync(
     IReadOnlyList<StorageFile> files,
        IProgress<(int current, int total, string fileName)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var total = files.Count;
        var processedCount = 0;

        // Use array instead of ConcurrentBag for better cache locality
        var results = new (int Index, List<DocumentItem> Items)[total];

        // Process files in parallel with controlled concurrency
        await Parallel.ForEachAsync(
        Enumerable.Range(0, total),
  new ParallelOptions
  {
      MaxDegreeOfParallelism = MaxDegreeOfParallelism,
      CancellationToken = cancellationToken
  },
            async (index, ct) =>
          {
              var file = files[index];

              // Create items without thumbnails first for speed
              var items = await CreateDocumentItemsAsync(file, loadThumbnails: false);
              results[index] = (index, items);

              // Report progress (thread-safe increment)
              var current = Interlocked.Increment(ref processedCount);
              progress?.Report((current, total, file.Name));
          });

        // Calculate total items for pre-allocation
        var totalItems = 0;
        foreach (var result in results)
        {
            totalItems += result.Items?.Count ?? 0;
        }

        // Sort results by original index and flatten - pre-allocate capacity
        var allItems = new List<DocumentItem>(totalItems);
        for (var i = 0; i < results.Length; i++)
        {
            if (results[i].Items != null)
            {
                allItems.AddRange(results[i].Items);
            }
        }

        return allItems;
    }

    /// <summary>
    /// Load thumbnails for items that don't have them using parallel processing
    /// </summary>
    public async Task LoadThumbnailsAsync(
    IList<DocumentItem> items,
    IProgress<int>? progress = null,
     CancellationToken cancellationToken = default)
    {
        var processedCount = 0;

        // Group PDF pages by source file to avoid opening same PDF multiple times
        var pdfGroups = items
                 .Where(i => i.Type == DocumentType.PdfPage && i.Thumbnail == null)
            .GroupBy(i => i.SourcePdfPath ?? i.FilePath)
          .ToList();

        var imageItems = items
          .Where(i => i.Type == DocumentType.Image && i.Thumbnail == null)
       .ToList();

        // Process images in parallel
        await Parallel.ForEachAsync(
          imageItems,
         new ParallelOptions
         {
             MaxDegreeOfParallelism = MaxDegreeOfParallelism,
             CancellationToken = cancellationToken
         },
     async (item, ct) =>
        {
            try
            {
                item.Thumbnail = await LoadImageThumbnailAsync(item.FilePath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error loading thumbnail for: {Path}", item.FilePath);
            }

            var count = Interlocked.Increment(ref processedCount);
            progress?.Report(count);
        });

        // Process PDF groups in parallel (one PDF file at a time per thread)
        await Parallel.ForEachAsync(
          pdfGroups,
               new ParallelOptions
               {
                   MaxDegreeOfParallelism = MaxDegreeOfParallelism,
                   CancellationToken = cancellationToken
               },
           async (group, ct) =>
             {
                 try
                 {
                     var sourcePath = group.Key;
                     if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                         return;

                     var file = await StorageFile.GetFileFromPathAsync(sourcePath);
                     var pdfDocument = await PdfDocument.LoadFromFileAsync(file);

                     foreach (var item in group)
                     {
                         ct.ThrowIfCancellationRequested();

                         if (item.PageNumber >= 1 && item.PageNumber <= pdfDocument.PageCount)
                         {
                             item.Thumbnail = await RenderPdfPageThumbnailAsync(pdfDocument, item.PageNumber - 1);
                         }

                         var count = Interlocked.Increment(ref processedCount);
                         progress?.Report(count);
                     }
                 }
                 catch (Exception ex)
                 {
                     Log.Warning(ex, "Error loading PDF thumbnails for: {Path}", group.Key);

                     // Still increment progress for failed items
                     foreach (var _ in group)
                     {
                         var count = Interlocked.Increment(ref processedCount);
                         progress?.Report(count);
                     }
                 }
             });
    }

    private async Task<List<DocumentItem>> CreatePdfPageItemsAsync(string pdfPath, string fileName, string fileSize, bool loadThumbnails)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(pdfPath);
            var pdfDocument = await PdfDocument.LoadFromFileAsync(file);
            var pageCount = (int)pdfDocument.PageCount;

            // Pre-allocate list with exact capacity
            var items = new List<DocumentItem>(pageCount);

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

            return items;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error creating PDF page items: {Path}", pdfPath);

            // Fallback: create items without thumbnails
            var pageCount = GetPdfPageCountFallback(pdfPath);
            var items = new List<DocumentItem>(pageCount);

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

            return items;
        }
    }

    private static async Task<BitmapImage?> RenderPdfPageThumbnailAsync(PdfDocument pdfDocument, int pageIndex)
    {
        try
        {
            using var page = pdfDocument.GetPage((uint)pageIndex);
            using var stream = new InMemoryRandomAccessStream();

            // Calculate height maintaining aspect ratio
            var aspectRatio = page.Size.Height / page.Size.Width;
            var options = new PdfPageRenderOptions
            {
                DestinationWidth = ThumbnailWidth,
                DestinationHeight = (uint)(ThumbnailWidth * aspectRatio)
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
        var pathList = paths.Where(p => IsSupportedFile(p) && File.Exists(p)).ToList();

        if (pathList.Count == 0)
            return [];

        // Use array instead of ConcurrentBag for better memory layout
        var results = new (int Index, List<DocumentItem> Items)[pathList.Count];

        // Process paths in parallel with controlled concurrency
        await Parallel.ForEachAsync(
   Enumerable.Range(0, pathList.Count),
  new ParallelOptions
  {
      MaxDegreeOfParallelism = MaxDegreeOfParallelism
  },
async (index, ct) =>
            {
                var path = pathList[index];
                var items = await CreateDocumentItemsAsync(path, loadThumbnails: false);
                results[index] = (index, items);
            });

        // Calculate total items for pre-allocation
        var totalItems = 0;
        foreach (var result in results)
        {
            totalItems += result.Items?.Count ?? 0;
        }

        // Flatten results maintaining order - pre-allocate capacity
        var allItems = new List<DocumentItem>(totalItems);
        for (var i = 0; i < results.Length; i++)
        {
            if (results[i].Items != null)
            {
                allItems.AddRange(results[i].Items);
            }
        }

        return allItems;
    }

    private static async Task<BitmapImage?> LoadImageThumbnailAsync(string filePath)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(filePath);
            using var stream = await file.OpenAsync(FileAccessMode.Read);

            var bitmap = new BitmapImage { DecodePixelWidth = (int)ThumbnailWidth };
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string FormatFileSize(ulong bytes)
    {
        var len = (double)bytes;
        var order = 0;

        while (len >= 1024 && order < Sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }

        return $"{len:0.##} {Sizes[order]}";
    }

    public static string[] GetAllSupportedExtensions() => AllExtensions;

    public static string[] GetImageExtensions() => ImageExtensions;

    public static string[] GetPdfExtensions() => PdfExtensions;
}

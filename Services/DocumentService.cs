using Gladhen3.Models;
using PdfSharp.Pdf.IO;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;
using System;
using System.Collections.Concurrent;
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

/// <summary>
/// The result of loading a batch of files.
/// </summary>
/// <param name="Items">Everything that loaded.</param>
/// <param name="FailedFiles">
/// Names of files that could not be read. One unreadable file should not abandon the rest of
/// a batch, but the user still has to be told which of their files did not arrive.
/// </param>
public sealed record DocumentLoadResult(List<DocumentItem> Items, IReadOnlyList<string> FailedFiles);

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

    private static readonly FrozenSet<string> _imageExtSet =
        ImageExtensions.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private const int MaxDegreeOfParallelism = 4;
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
    /// Creates document items with thumbnail loading deferred for better performance.
    /// </summary>
    /// <remarks>
    /// Failures propagate. This used to log and return an empty list, so a file that could not
    /// be read was indistinguishable from a file with no pages, and the caller had no way to
    /// tell the user anything had gone wrong.
    /// </remarks>
    /// <exception cref="FileNotFoundException">The file no longer exists.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    public static async Task<List<DocumentItem>> CreateDocumentItemsAsync(string filePath, bool loadThumbnails = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
            throw new FileNotFoundException("The file could not be found.", filePath);

        var fileInfo = new FileInfo(filePath);
        return await CreateItemsAsync(filePath, fileInfo.Name, FormatFileSize((ulong)fileInfo.Length), loadThumbnails);
    }

    /// <summary>
    /// Creates document items from a <see cref="StorageFile"/> with optional thumbnail loading.
    /// </summary>
    /// <inheritdoc cref="CreateDocumentItemsAsync(string, bool)" path="/exception"/>
    public static async Task<List<DocumentItem>> CreateDocumentItemsAsync(StorageFile file, bool loadThumbnails = true)
    {
        ArgumentNullException.ThrowIfNull(file);

        var basicProperties = await file.GetBasicPropertiesAsync();
        return await CreateItemsAsync(file.Path, file.Name, FormatFileSize(basicProperties.Size), loadThumbnails);
    }

    private async static Task<List<DocumentItem>> CreateItemsAsync(string filePath, string fileName, string fileSize, bool loadThumbnails)
    {
        if (IsImageFile(filePath))
        {
            var item = new DocumentItem
            {
                FileName = fileName,
                FilePath = filePath,
                Type = DocumentType.Image,
                PageNumber = 1,
                TotalPages = 1,
                FileSize = fileSize
            };

            if (loadThumbnails)
                item.Thumbnail = await LoadImageThumbnailAsync(filePath);

            return [item];
        }

        if (IsPdfFile(filePath))
            return await CreatePdfPageItemsAsync(filePath, fileName, fileSize, loadThumbnails);

        return [];
    }

    public static async Task<DocumentLoadResult> CreateDocumentItemsBatchAsync(
        IReadOnlyList<StorageFile> files,
        IProgress<(int current, int total, string fileName)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);

        var total = files.Count;
        var processedCount = 0;

        var results = new List<DocumentItem>?[total];
        var failures = new ConcurrentBag<string>();

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
                results[index] = await LoadOneAsync(() => CreateDocumentItemsAsync(file, loadThumbnails: false), file.Name, failures);

                var current = Interlocked.Increment(ref processedCount);
                progress?.Report((current, total, file.Name));
            });

        return Collect(results, failures);
    }

    public static async Task<DocumentLoadResult> LoadDocumentsFromPathsAsync(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var pathList = paths.Where(p => IsSupportedFile(p) && File.Exists(p)).ToList();

        if (pathList.Count == 0)
            return new DocumentLoadResult([], []);

        var results = new List<DocumentItem>?[pathList.Count];
        var failures = new ConcurrentBag<string>();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, pathList.Count),
            new ParallelOptions { MaxDegreeOfParallelism = MaxDegreeOfParallelism },
            async (index, ct) =>
            {
                var path = pathList[index];
                results[index] = await LoadOneAsync(() => CreateDocumentItemsAsync(path, loadThumbnails: false), Path.GetFileName(path), failures);
            });

        return Collect(results, failures);
    }

    private static async Task<List<DocumentItem>?> LoadOneAsync(Func<Task<List<DocumentItem>>> load, string name, ConcurrentBag<string> failures)
    {
        try
        {
            return await load();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not load {Name}", name);
            failures.Add(name);
            return null;
        }
    }

    private static DocumentLoadResult Collect(List<DocumentItem>?[] results, ConcurrentBag<string> failures)
    {
        var totalItems = 0;
        foreach (var result in results)
            totalItems += result?.Count ?? 0;

        var allItems = new List<DocumentItem>(totalItems);
        foreach (var result in results)
        {
            if (result != null)
                allItems.AddRange(result);
        }

        return new DocumentLoadResult(allItems, [.. failures]);
    }

    private static async Task<List<DocumentItem>> CreatePdfPageItemsAsync(string pdfPath, string fileName, string fileSize, bool loadThumbnails)
    {
        var pageCount = 0;
        PdfDocument? pdfDocument = null;

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(pdfPath);
            pdfDocument = await PdfDocument.LoadFromFileAsync(file);
            pageCount = (int)pdfDocument.PageCount;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Windows.Data.Pdf could not open {Path}; falling back to PDFsharp for the page count", pdfPath);
            pageCount = GetPdfPageCount(pdfPath);
        }

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

            if (loadThumbnails && pdfDocument != null)
                item.Thumbnail = await RenderPdfPageThumbnailAsync(pdfDocument, i);

            items.Add(item);
        }

        return items;
    }

    private static int GetPdfPageCount(string filePath)
    {
        using var document = PdfReader.Open(filePath, PdfDocumentOpenMode.Import);
        return document.PageCount;
    }

    /// <summary>
    /// Renders a page thumbnail, or returns null if it cannot be rendered.
    /// </summary>
    /// <remarks>
    /// Thumbnails are decoration. A page that will not render still belongs in the list, so
    /// this degrades to no image rather than failing the load.
    /// </remarks>
    private static async Task<BitmapImage?> RenderPdfPageThumbnailAsync(PdfDocument pdfDocument, int pageIndex)
    {
        try
        {
            using var page = pdfDocument.GetPage((uint)pageIndex);
            using var stream = new InMemoryRandomAccessStream();

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
            Log.Warning(ex, "No thumbnail for page {PageIndex}", pageIndex + 1);
            return null;
        }
    }

    /// <inheritdoc cref="RenderPdfPageThumbnailAsync" path="/remarks"/>
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
            Log.Warning(ex, "No thumbnail for {Path}", filePath);
            return null;
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

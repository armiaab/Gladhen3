using Gladhen3.Models;
using PdfSharp;
using PdfSharp.Pdf.IO;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Gladhen3.Services;

/// <summary>What told the detector which way up a page is - or why nothing did.</summary>
public enum OrientationEvidence
{
    /// <summary>The PDF's own text: which way the content stream draws it.</summary>
    TextLayer,

    /// <summary>Reading the page as a picture, every way round.</summary>
    Recognition,

    /// <summary>Looked, and found no text to go by, or none that agreed with itself.</summary>
    Undecided,

    /// <summary>The page needed reading as a picture, and Windows has no text recognizer installed.</summary>
    NoRecognizer,

    /// <summary>The page could not be opened or read at all.</summary>
    Failed
}

/// <param name="Turn">
/// The <see cref="DocumentItem.Rotation"/> that stands the page upright, or null if the
/// detector cannot tell. Absolute, not a step: checking an upright page again gives the same answer.
/// </param>
public readonly record struct OrientationFinding(int? Turn, OrientationEvidence Evidence);

/// <summary>A page to check, as it stood when the check began.</summary>
/// <param name="Rotation">The turn the page has now, which is tried first.</param>
public sealed record PageToCheck(DocumentItem Item, DocumentType Type, string Path, int PageNumber, int Rotation)
{
    public static PageToCheck Of(DocumentItem item)
        => new(item, item.Type, item.SourcePdfPath ?? item.FilePath, item.PageNumber, item.Rotation);
}

/// <summary>
/// Works out which way up each page is meant to be read, so that "normalize" can turn every
/// page upright in one go.
/// </summary>
/// <remarks>
/// A PDF page with a text layer is answered from it: exact, and costs no more than parsing the
/// page. Everything else - photos, scans, PDFs of scans - is rendered and read by Windows' own
/// text recognizer at each of the four turns. Pages that give no clear answer are left alone:
/// turning a page the wrong way is worse than not turning it.
/// </remarks>
public sealed class OrientationDetector : IDisposable
{
    /// <summary>Text-layer characters that settle a page without looking at its pixels.</summary>
    private const double TrustedCharacters = 40;

    /// <summary>Fewer characters, still worth going by when there is nothing better.</summary>
    private const double FallbackCharacters = 12;

    /// <summary>
    /// A page this much covered by images is probably a scan, and visible text on a scan may be
    /// a stamp or a footer added afterwards - upright on a page that is not.
    /// </summary>
    private const double ScanCoverage = 0.5;

    /// <summary>Text recognition reads ordinary print comfortably at this resolution.</summary>
    private const double RenderDpi = 150;

    /// <summary>The long side of a picture handed to the recognizer. More is slower, not more accurate.</summary>
    private const int MaxEdge = 2400;

    /// <summary>Recognizers run side by side, one per thread; past four the gain is small.</summary>
    private static int Lanes => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    // Shared by the lanes, so that a 200 MB scan is opened once rather than once per lane.
    // PDFsharp documents are not thread-safe, hence the locks.
    private readonly Dictionary<string, PdfSharp.Pdf.PdfDocument> _textDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Windows.Data.Pdf.PdfDocument> _renderDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _renderGate = new(1, 1);

    /// <summary>Checks every page, reporting each finding as it is made, several pages at a time.</summary>
    /// <param name="found">Told about each page as soon as it is checked, in no particular order.</param>
    public static async Task DetectAsync(IReadOnlyList<PageToCheck> pages, IProgress<(PageToCheck Page, OrientationFinding Finding)> found, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(found);

        using var detector = new OrientationDetector();
        var next = -1;

        async Task RunLaneAsync()
        {
            var recognizer = TextRecognitionOrientation.CreateEngine();
            int index;
            while ((index = Interlocked.Increment(ref next)) < pages.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var page = pages[index];
                OrientationFinding finding;
                try
                {
                    finding = await detector.DetectAsync(page, recognizer, cancellationToken);
                }
                // Per-item isolation: a page that cannot be opened is reported as such, and the
                // rest are still checked.
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warning(ex, "Could not check which way up {Path} page {Page} is", page.Path, page.PageNumber);
                    finding = new OrientationFinding(null, OrientationEvidence.Failed);
                }

                found.Report((page, finding));
            }
        }

        await Task.WhenAll(Enumerable.Range(0, Math.Min(Lanes, pages.Count)).Select(_ => Task.Run(RunLaneAsync, cancellationToken)));
    }

    /// <summary>Checks one page.</summary>
    /// <param name="recognizer">This lane's own recognizer, or null if Windows has none.</param>
    internal async Task<OrientationFinding> DetectAsync(PageToCheck page, OcrEngine? recognizer, CancellationToken cancellationToken)
    {
        switch (page.Type)
        {
            case DocumentType.PdfPage:
            {
                var reading = ReadTextLayer(page);
                if (reading != null && IsTrusted(reading) && reading.Turn(TrustedCharacters) is { } fromText)
                    return new OrientationFinding(fromText, OrientationEvidence.TextLayer);

                if (recognizer != null)
                {
                    var picture = await RenderPdfPageAsync(page);
                    if (await TextRecognitionOrientation.DetectAsync(recognizer, picture, page.Rotation, cancellationToken) is { } fromPicture)
                        return new OrientationFinding(fromPicture, OrientationEvidence.Recognition);
                }

                // A few characters in agreement still beat a guess.
                if (reading?.Turn(FallbackCharacters) is { } fromFewer)
                    return new OrientationFinding(fromFewer, OrientationEvidence.TextLayer);

                return new OrientationFinding(null, recognizer == null ? OrientationEvidence.NoRecognizer : OrientationEvidence.Undecided);
            }

            case DocumentType.Image:
            {
                if (recognizer == null) return new OrientationFinding(null, OrientationEvidence.NoRecognizer);

                var picture = await DecodeImageAsync(page.Path);
                return await TextRecognitionOrientation.DetectAsync(recognizer, picture, page.Rotation, cancellationToken) is { } turn
                    ? new OrientationFinding(turn, OrientationEvidence.Recognition)
                    : new OrientationFinding(null, OrientationEvidence.Undecided);
            }

            default:
                // Blank pages and dividers have nothing to read.
                return new OrientationFinding(null, OrientationEvidence.Undecided);
        }
    }

    /// <summary>
    /// The text layer settles the page unless the page looks like a scan with visible text on
    /// top: then the scan's own text, read from the pixels, is what counts.
    /// </summary>
    private static bool IsTrusted(TextLayerReading reading)
        => reading.ImageCoverage < ScanCoverage || reading.Invisible >= reading.Total / 2;

    /// <returns>Null if the page's content cannot be parsed; the pixels are read instead.</returns>
    private TextLayerReading? ReadTextLayer(PageToCheck page)
    {
        lock (_textDocuments)
        {
            if (!_textDocuments.TryGetValue(page.Path, out var document))
            {
                document = PdfReader.Open(page.Path, PdfDocumentOpenMode.Import);
                _textDocuments[page.Path] = document;
            }

            if (page.PageNumber < 1 || page.PageNumber > document.PageCount)
                throw new ArgumentOutOfRangeException(nameof(page), page.PageNumber, $"{page.Path} has {document.PageCount} pages.");

            try
            {
                return TextLayerOrientation.Read(document.Pages[page.PageNumber - 1]);
            }
            // A content stream PDFsharp cannot parse still renders in Windows' reader, which is
            // all the fallback needs.
            catch (PdfSharpException ex)
            {
                Log.Debug(ex, "Text layer of {Path} page {Page} unreadable; reading its pixels instead", page.Path, page.PageNumber);
                return null;
            }
        }
    }

    /// <summary>The page as a viewer shows it - turned by its own /Rotate - in grey.</summary>
    private async Task<GrayBitmap> RenderPdfPageAsync(PageToCheck page)
    {
        using var rendered = new InMemoryRandomAccessStream();

        // Only the rendering itself is one at a time; decoding it is each lane's own business.
        await _renderGate.WaitAsync();
        try
        {
            if (!_renderDocuments.TryGetValue(page.Path, out var document))
            {
                var file = await StorageFile.GetFileFromPathAsync(page.Path);
                document = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
                _renderDocuments[page.Path] = document;
            }

            using var pdfPage = document.GetPage((uint)(page.PageNumber - 1));

            // Size is in DIPs, 96 to the inch.
            var longSide = Math.Max(pdfPage.Size.Width, pdfPage.Size.Height);
            var scale = Math.Min(RenderDpi / 96, MaxEdge / longSide);

            await pdfPage.RenderToStreamAsync(rendered, new Windows.Data.Pdf.PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, Math.Round(pdfPage.Size.Width * scale)),
                DestinationHeight = (uint)Math.Max(1, Math.Round(pdfPage.Size.Height * scale)),
                BitmapEncoderId = BitmapEncoder.BmpEncoderId
            });
        }
        finally
        {
            _renderGate.Release();
        }

        var decoder = await BitmapDecoder.CreateAsync(rendered);
        return await GrayBitmap.DecodeAsync(decoder, MaxEdge, ExifOrientationMode.IgnoreExifOrientation);
    }

    /// <summary>The photo as the viewer shows it, EXIF turn included, in grey.</summary>
    private static async Task<GrayBitmap> DecodeImageAsync(string path)
    {
        using var stream = File.OpenRead(path);
        using var ras = stream.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(ras);
        return await GrayBitmap.DecodeAsync(decoder, MaxEdge, ExifOrientationMode.RespectExifOrientation);
    }

    public void Dispose()
    {
        lock (_textDocuments)
        {
            foreach (var document in _textDocuments.Values) document.Dispose();
            _textDocuments.Clear();
        }
        _renderDocuments.Clear();
        _renderGate.Dispose();
    }
}

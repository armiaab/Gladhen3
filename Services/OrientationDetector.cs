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

/// <summary>What decided a page's orientation, or why nothing could.</summary>
public enum OrientationEvidence
{
    /// <summary>The PDF's own text layer.</summary>
    TextLayer,

    /// <summary>An orientation model, on the page as a picture.</summary>
    Model,

    /// <summary>Windows text recognition, tried at every turn.</summary>
    Recognition,

    /// <summary>Checked, but inconclusive.</summary>
    Undecided,

    /// <summary>Text recognition was needed, and Windows has none installed.</summary>
    NoRecognizer,

    /// <summary>The page could not be opened or read at all.</summary>
    Failed
}

/// <param name="Turn">
/// The absolute <see cref="DocumentItem.Rotation"/> that makes the page upright, or null if unknown.
/// </param>
public readonly record struct OrientationFinding(int? Turn, OrientationEvidence Evidence);

/// <summary>A page to check, as it was when the check began.</summary>
/// <param name="Rotation">The page's current turn, tried first.</param>
public sealed record PageToCheck(DocumentItem Item, DocumentType Type, string Path, int PageNumber, int Rotation)
{
    public static PageToCheck Of(DocumentItem item)
        => new(item, item.Type, item.SourcePdfPath ?? item.FilePath, item.PageNumber, item.Rotation);
}

/// <summary>How pictures of pages are judged.</summary>
/// <param name="Method">A model id, or <see cref="OrientationModels.TextRecognitionId"/> for none.</param>
/// <param name="TextRecognitionFallback">Whether to try text recognition when the model cannot tell.</param>
public sealed record OrientationOptions(string Method, bool TextRecognitionFallback)
{
    public static OrientationOptions Default { get; } = new(OrientationModels.BuiltInId, true);

    public bool UsesModel => Method != OrientationModels.TextRecognitionId;
}

/// <summary>Works out which way up each page should be read.</summary>
/// <remarks>
/// A PDF text layer answers exactly. Everything else is judged as a picture by an
/// orientation model, optionally backed by Windows text recognition. Inconclusive pages
/// are left alone: a wrong turn is worse than none.
/// </remarks>
public sealed class OrientationDetector : IDisposable
{
    /// <summary>Text-layer characters enough to decide without the pixels.</summary>
    private const double TrustedCharacters = 40;

    /// <summary>Fewer characters, used when nothing better is available.</summary>
    private const double FallbackCharacters = 12;

    /// <summary>Image coverage above which a page is treated as a scan, whose text layer may be a stamp.</summary>
    private const double ScanCoverage = 0.5;

    /// <summary>Resolution that text recognition reads ordinary print at.</summary>
    private const double RenderDpi = 150;

    /// <summary>Long side of the picture given to the recognizer; larger is slower, not better.</summary>
    private const int MaxEdge = 2400;

    /// <summary>Pages checked in parallel, each lane with its own session and recognizer.</summary>
    private static int Lanes => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    // Shared across lanes so each source opens once; PDFsharp is not thread-safe, hence the locks.
    private readonly Dictionary<string, PdfSharp.Pdf.PdfDocument> _textDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Windows.Data.Pdf.PdfDocument> _renderDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _renderGate = new(1, 1);

    /// <summary>One lane's tools; sessions and recognizers take one page at a time.</summary>
    /// <param name="TextRecognitionOnly">No model was chosen, so a missing recognizer is worth reporting.</param>
    internal sealed record Tools(ImageOrientationModel.Session? Model, OcrEngine? Recognizer, bool TextRecognitionOnly);

    /// <summary>Checks every page in parallel, reporting each finding as it is made.</summary>
    /// <param name="found">Receives each finding, in no particular order.</param>
    /// <exception cref="OrientationModelException">The chosen model cannot be loaded (thrown before any page).</exception>
    public static async Task DetectAsync(
        IReadOnlyList<PageToCheck> pages,
        OrientationOptions options,
        IProgress<(PageToCheck Page, OrientationFinding Finding)> found,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(found);

        using var model = options.UsesModel ? OrientationModels.Open(options.Method) : null;
        using var detector = new OrientationDetector();
        var next = -1;

        async Task RunLaneAsync()
        {
            using var session = model?.CreateSession();
            var recognizer = model == null || options.TextRecognitionFallback ? TextRecognitionOrientation.CreateEngine() : null;
            var tools = new Tools(session, recognizer, model == null);

            int index;
            while ((index = Interlocked.Increment(ref next)) < pages.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var page = pages[index];
                OrientationFinding finding;
                try
                {
                    finding = await detector.DetectAsync(page, tools, cancellationToken);
                }
                // One unreadable page must not stop the rest.
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
    internal async Task<OrientationFinding> DetectAsync(PageToCheck page, Tools tools, CancellationToken cancellationToken)
    {
        TextLayerReading? reading = null;
        switch (page.Type)
        {
            case DocumentType.PdfPage:
                reading = ReadTextLayer(page);
                if (reading != null && IsTrusted(reading) && reading.Turn(TrustedCharacters) is { } fromText)
                    return new OrientationFinding(fromText, OrientationEvidence.TextLayer);
                break;

            case DocumentType.Image:
                break;

            default:
                // Blank pages and dividers have nothing to read.
                return new OrientationFinding(null, OrientationEvidence.Undecided);
        }

        if (tools.Model != null || tools.Recognizer != null)
        {
            using var picture = page.Type == DocumentType.PdfPage ? await RenderPdfPageAsync(page) : PagePicture.OpenImage(page.Path);
            await picture.InitializeAsync();

            if (tools.Model != null)
            {
                var read = tools.Model.Read(await picture.ForModelAsync(tools.Model.Spec));
                if (read.Turn is { } fromModel) return new OrientationFinding(fromModel, OrientationEvidence.Model);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (tools.Recognizer != null &&
                await TextRecognitionOrientation.DetectAsync(tools.Recognizer, await picture.GrayAsync(MaxEdge), page.Rotation, cancellationToken) is { } fromText)
            {
                return new OrientationFinding(fromText, OrientationEvidence.Recognition);
            }
        }

        // A few agreeing characters still beat a guess.
        if (reading?.Turn(FallbackCharacters) is { } fromFewer)
            return new OrientationFinding(fromFewer, OrientationEvidence.TextLayer);

        return new OrientationFinding(null, tools.TextRecognitionOnly && tools.Recognizer == null ? OrientationEvidence.NoRecognizer : OrientationEvidence.Undecided);
    }

    /// <summary>The text layer decides, unless the page looks like a scan with text added on top.</summary>
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
            // Windows' renderer may still handle what PDFsharp cannot parse.
            catch (PdfSharpException ex)
            {
                Log.Debug(ex, "Text layer of {Path} page {Page} unreadable; reading its pixels instead", page.Path, page.PageNumber);
                return null;
            }
        }
    }

    /// <summary>The page as a viewer shows it, turned by its own /Rotate.</summary>
    private async Task<PagePicture> RenderPdfPageAsync(PageToCheck page)
    {
        var rendered = new InMemoryRandomAccessStream();
        var done = false;

        // Only rendering is serialised; decoding happens per lane.
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
            done = true;
        }
        finally
        {
            _renderGate.Release();

            // Dispose unless ownership was handed to the caller.
            if (!done) rendered.Dispose();
        }

        return new PagePicture(rendered, ExifOrientationMode.IgnoreExifOrientation);
    }

    /// <summary>A page as a picture, opened once and decoded on demand (small colour for a model, large grey for OCR).</summary>
    private sealed class PagePicture(IRandomAccessStream stream, ExifOrientationMode exif, IDisposable? owner = null) : IDisposable
    {
        private BitmapDecoder? _decoder;

        /// <summary>A photo with its EXIF turn applied.</summary>
        public static PagePicture OpenImage(string path)
        {
            var file = File.OpenRead(path);
            return new PagePicture(file.AsRandomAccessStream(), ExifOrientationMode.RespectExifOrientation, file);
        }

        public async Task InitializeAsync() => _decoder = await BitmapDecoder.CreateAsync(stream);

        public Task<BgraPicture> ForModelAsync(OrientationModelSpec spec) => BgraPicture.DecodeAsync(Decoder, spec, exif);

        public Task<GrayBitmap> GrayAsync(int maxEdge) => GrayBitmap.DecodeAsync(Decoder, maxEdge, exif);

        private BitmapDecoder Decoder => _decoder ?? throw new InvalidOperationException("InitializeAsync first.");

        public void Dispose()
        {
            stream.Dispose();
            owner?.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_textDocuments)
        {
            foreach (var document in _textDocuments.Values) document.Dispose();
            _textDocuments.Clear();
        }
        // Windows.Data.Pdf.PdfDocument has no Close/Dispose; dropping the references releases it.
        _renderDocuments.Clear();
        _renderGate.Dispose();
    }
}

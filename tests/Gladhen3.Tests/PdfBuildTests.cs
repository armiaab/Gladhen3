using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Gladhen3.Models;
using Gladhen3.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Windows.Graphics.Imaging;
using Xunit;

namespace Gladhen3.Tests;

/*
 * Whole builds, written to disk and read back.  Blank pages and generated
 * PDFs need no fixtures, so these run anywhere the tests build.
 */
[Collection(SettingsCollection.Name)]
public sealed class PdfBuildTests : IDisposable
{
	private readonly string dir;

	public PdfBuildTests()
	{

		dir = Path.Combine(Path.GetTempPath(),
		    "gladhen3-tests-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		AppSettings.ReplaceForTesting(new AppSettings());
	}

	public void
	Dispose()
	{

		AppSettings.ReplaceForTesting(new AppSettings());
		Directory.Delete(dir, true);
	}

	private static DocumentItem
	Blank()
	{

		return (new DocumentItem {
			Type = DocumentType.BlankPage, FileName = "blank"
		});
	}

	/*
	 * Each page gets a few hundred lines: enough content stream to be
	 * worth compressing, drawn without fonts so no resolver is needed.
	 */
	private string
	SourcePdf(string name, int pages)
	{
		PdfDocument doc;
		XGraphics gfx;
		string path;
		int i, j;

		path = Path.Combine(dir, name);
		doc = new PdfDocument();
		for (i = 0; i < pages; i++) {
			gfx = XGraphics.FromPdfPage(doc.AddPage());
			for (j = 0; j < 300; j++) {
				gfx.DrawLine(XPens.Black, 10 + j, 10, 300 - j,
				    500 + i);
			}
			gfx.Dispose();
		}
		doc.Save(path);
		doc.Dispose();
		return (path);
	}

	private static DocumentItem
	PageOf(string path, int number, int total)
	{

		return (new DocumentItem {
			Type = DocumentType.PdfPage,
			FilePath = path,
			FileName = Path.GetFileName(path),
			PageNumber = number,
			TotalPages = total,
		});
	}

	[Fact]
	public void
	WritesBlankPages()
	{
		PdfBuildResult result;
		PdfDocument read;
		string output;

		output = Path.Combine(dir, "blank.pdf");
		result = new PdfService().CreatePdfFromDocuments(
		    new List<DocumentItem> { Blank(), Blank(), Blank() }, output);
		read = PdfReader.Open(output, PdfDocumentOpenMode.Import);

		Assert.Equal(3, read.PageCount);
		Assert.Empty(result.SkippedItems);
	}

	/*
	 * The settings passed in are the ones used, whatever Current says by
	 * the time the build runs: that is what lets a save run on while the
	 * user changes Settings.
	 */
	[Fact]
	public void
	BuildUsesPinnedSettingsNotCurrent()
	{
		AppSettings letter;
		PdfDocument read;
		string output;

		output = Path.Combine(dir, "letter.pdf");
		letter = new AppSettings { PaperSize = PdfPaperSize.Letter };
		AppSettings.ReplaceForTesting(
		    new AppSettings { PaperSize = PdfPaperSize.A3 });

		new PdfService().CreatePdfFromDocuments(
		    new List<DocumentItem> { Blank() }, output, letter);
		read = PdfReader.Open(output, PdfDocumentOpenMode.Import);

		Assert.Equal(612, read.Pages[0].Width.Point, 1);
		Assert.Equal(792, read.Pages[0].Height.Point, 1);
		Assert.Equal(PdfPaperSize.A3, AppSettings.Current.PaperSize);
	}

	[Fact]
	public void
	RejectsSectionBreaks()
	{
		List<DocumentItem> items;

		items = new List<DocumentItem> {
			Blank(),
			new DocumentItem { Type = DocumentType.SectionBreak },
		};

		Assert.Throws<ArgumentException>(() =>
		    new PdfService().CreatePdfFromDocuments(items,
		    Path.Combine(dir, "x.pdf")));
	}

	[Fact]
	public void
	ReordersAndRotatesSourcePages()
	{
		DocumentItem first, third;
		PdfDocument read;
		string source, output;

		source = SourcePdf("source.pdf", 3);
		output = Path.Combine(dir, "reordered.pdf");
		first = PageOf(source, 1, 3);
		third = PageOf(source, 3, 3);
		third.Rotation = 90;

		new PdfService().CreatePdfFromDocuments(
		    new List<DocumentItem> { third, first }, output);
		read = PdfReader.Open(output, PdfDocumentOpenMode.Import);

		Assert.Equal(2, read.PageCount);
		Assert.Equal(90, read.Pages[0].Elements.GetInteger("/Rotate"));
		Assert.Equal(0, read.Pages[1].Elements.GetInteger("/Rotate"));
	}

	/*
	 * PDFsharp leaves the zlib Adler-32 trailer off; SaveDocument puts it
	 * back.  Every Flate stream in the output must end in its checksum.
	 */
	[Fact]
	public void
	EveryFlateStreamIsTerminated()
	{
		PdfDocument read;
		string source, output;
		byte[] raw;
		uint sum;
		int checkedStreams;

		source = SourcePdf("plain.pdf", 2);
		output = Path.Combine(dir, "terminated.pdf");
		new PdfService().CreatePdfFromDocuments(new List<DocumentItem> {
			PageOf(source, 1, 2), Blank(), PageOf(source, 2, 2)
		}, output);

		read = PdfReader.Open(output, PdfDocumentOpenMode.Import);
		checkedStreams = 0;
		foreach (PdfObject obj in read.Internals.GetAllObjects()) {
			if (obj is not PdfDictionary { Stream: not null } dict)
				continue;
			if (dict.Elements.GetName("/Filter") != "/FlateDecode")
				continue;
			raw = dict.Stream.Value;
			Assert.True(PdfService.TryAdler32OfInflated(raw,
			    long.MaxValue, out sum));
			Assert.Equal(sum,
			    System.Buffers.Binary.BinaryPrimitives
			    .ReadUInt32BigEndian(raw.AsSpan(raw.Length - 4)));
			checkedStreams++;
		}

		Assert.True(checkedStreams > 0);
	}

	[Fact]
	public void
	EstimateIsPositiveAndCancellable()
	{
		List<DocumentItem> items;
		CancellationTokenSource cts;

		items = new List<DocumentItem> {
			PageOf(SourcePdf("estimate.pdf", 4), 1, 4), Blank()
		};
		cts = new CancellationTokenSource();

		Assert.True(PdfService.EstimateOutputSize(items) > 0);
		cts.Cancel();
		Assert.ThrowsAny<OperationCanceledException>(() =>
		    PdfService.EstimateOutputSize(items, cts.Token));
	}

	/* ------------------------------------------- embedded image limits */

	private static PdfDictionary
	AddGrayImage(PdfDocument doc, PdfPage page, int width, int height,
	    byte[] samples)
	{
		PdfDictionary image, xobjects, resources;

		image = new PdfDictionary(doc);
		image.Elements.SetName("/Type", "/XObject");
		image.Elements.SetName("/Subtype", "/Image");
		image.Elements.SetInteger("/Width", width);
		image.Elements.SetInteger("/Height", height);
		image.Elements.SetInteger("/BitsPerComponent", 8);
		image.Elements.SetName("/ColorSpace", "/DeviceGray");
		image.Elements.SetName("/Filter", "/FlateDecode");
		image.CreateStream(PdfService.ZlibCompress(samples));
		doc.Internals.AddObject(image);

		xobjects = new PdfDictionary(doc);
		xobjects.Elements["/Im0"] = image.Reference;
		resources = page.Elements.GetDictionary("/Resources") ??
		    new PdfDictionary(doc);
		resources.Elements["/XObject"] = xobjects;
		page.Elements["/Resources"] = resources;
		return (image);
	}

	[Fact]
	public void
	RecompressesWellFormedImage()
	{
		PdfDocument doc;
		PdfDictionary image;
		CountingCodec codec;
		byte[] samples;

		doc = new PdfDocument();
		samples = new byte[300 * 200];
		new Random(9).NextBytes(samples);
		image = AddGrayImage(doc, doc.AddPage(), 300, 200, samples);
		codec = new CountingCodec();

		Assert.Equal(0, PdfService.CompressDocumentImages(doc, 72, 0.5,
		    codec));
		Assert.Equal(1, codec.Calls);
		Assert.Equal(codec.Marker, image.Stream!.Value);
	}

	/*
	 * Declared 4 by 4, but the stream inflates to a megabyte: PDFsharp
	 * would have inflated all of it.  The image is left exactly as it was
	 * and the codec never sees it.
	 */
	[Fact]
	public void
	LeavesImageWhoseStreamOutgrowsItsSize()
	{
		PdfDocument doc;
		PdfDictionary image;
		CountingCodec codec;
		byte[] before;

		doc = new PdfDocument();
		image = AddGrayImage(doc, doc.AddPage(), 4, 4,
		    new byte[1 << 20]);
		before = image.Stream!.Value;
		codec = new CountingCodec();

		PdfService.CompressDocumentImages(doc, 72, 0.5, codec);

		Assert.Equal(0, codec.Calls);
		Assert.Same(before, image.Stream.Value);
	}

	[Fact]
	public void
	LeavesImageOverPixelLimit()
	{
		PdfDocument doc;
		PdfDictionary image;
		CountingCodec codec;

		doc = new PdfDocument();
		image = AddGrayImage(doc, doc.AddPage(), 4, 4, new byte[16]);
		image.Elements.SetInteger("/Width", 100_000);
		image.Elements.SetInteger("/Height", 100_000);
		codec = new CountingCodec();

		PdfService.CompressDocumentImages(doc, 72, 0.5, codec);

		Assert.Equal(0, codec.Calls);
	}

	/* Answers every image with the same few bytes, and counts. */
	private sealed class CountingCodec : IImageCodec
	{
		public readonly byte[] Marker =
		    PdfService.ZlibCompress(new byte[] { 1, 2, 3 });
		private int calls;

		public int Calls => calls;

		public bool ProducesJpegFile => false;

		public EncodedImage? Encode(SoftwareBitmap bitmap,
		    double quality)
		{

			Interlocked.Increment(ref calls);
			return (new EncodedImage(Marker, "/FlateDecode",
			    bitmap.PixelWidth, bitmap.PixelHeight, 1, 8));
		}

		public void Dispose()
		{
		}
	}
}

using System;
using System.Collections.Generic;
using System.IO;
using BenchmarkDotNet.Attributes;
using Gladhen3.Models;
using Gladhen3.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Gladhen3.Benchmarks;

/*
 * A whole save and a whole estimate, end to end through PDFsharp and the
 * disk, from a generated source document.
 */
[MemoryDiagnoser]
public class BuildBenchmarks
{
	private readonly PdfService service = new();
	private List<DocumentItem> items = new();
	private string dir = string.Empty;
	private string output = string.Empty;

	[Params(10, 200)]
	public int Pages { get; set; }

	[GlobalSetup]
	public void
	Setup()
	{
		PdfDocument doc;
		XGraphics gfx;
		string source;
		int i, j;

		dir = Path.Combine(Path.GetTempPath(),
		    "gladhen3-bench-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		source = Path.Combine(dir, "source.pdf");
		output = Path.Combine(dir, "output.pdf");

		doc = new PdfDocument();
		for (i = 0; i < Pages; i++) {
			gfx = XGraphics.FromPdfPage(doc.AddPage());
			for (j = 0; j < 200; j++)
				gfx.DrawLine(XPens.Black, j, 0, 400 - j, 600 + i);
			gfx.Dispose();
		}
		doc.Save(source);
		doc.Dispose();

		/* Every other page, half of them turned: not a straight copy. */
		items = new List<DocumentItem>();
		for (i = 1; i <= Pages; i += 2) {
			items.Add(new DocumentItem {
				Type = DocumentType.PdfPage,
				FilePath = source,
				FileName = "source.pdf",
				PageNumber = i,
				TotalPages = Pages,
				Rotation = i % 4 == 1 ? 90 : 0,
			});
		}

		AppSettings.ReplaceForTesting(new AppSettings());
	}

	[GlobalCleanup]
	public void
	Cleanup()
	{

		Directory.Delete(dir, true);
	}

	[Benchmark]
	public int
	Build()
	{

		return (service.CreatePdfFromDocuments(items, output)
		    .PagesWritten);
	}

	[Benchmark]
	public long
	Estimate()
	{

		return (PdfService.EstimateOutputSize(items));
	}
}

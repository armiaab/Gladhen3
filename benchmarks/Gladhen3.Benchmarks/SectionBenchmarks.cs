using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Gladhen3.Models;

namespace Gladhen3.Benchmarks;

/*
 * Sections are derived from the flat page list on every save and every
 * estimate, so splitting and naming scale with the whole document.
 */
[MemoryDiagnoser]
public class SectionBenchmarks
{
	private List<DocumentItem> items = new();
	private List<PdfSection> sections = new();

	[Params(100, 10_000)]
	public int Pages { get; set; }

	[GlobalSetup]
	public void
	Setup()
	{
		int i;

		items = new List<DocumentItem>(Pages + Pages / 10);
		for (i = 0; i < Pages; i++) {
			/* A divider every ten pages, some sharing a name. */
			if (i > 0 && i % 10 == 0) {
				items.Add(new DocumentItem {
					Type = DocumentType.SectionBreak,
					SectionName = "Chapter " + (i / 30),
				});
			}
			items.Add(new DocumentItem {
				Type = DocumentType.PdfPage,
				FilePath = @"C:\doc.pdf",
				PageNumber = i + 1,
				TotalPages = Pages,
			});
		}
		sections = PdfSection.Split(items);
	}

	[Benchmark]
	public int
	Split()
	{

		return (PdfSection.Split(items).Count);
	}

	[Benchmark]
	public int
	ResolveFileNames()
	{

		return (PdfSection.ResolveFileNames(sections, "Section").Count);
	}
}

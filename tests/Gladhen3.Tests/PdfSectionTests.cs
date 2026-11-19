using System.Collections.Generic;
using Gladhen3.Models;
using Xunit;

namespace Gladhen3.Tests;

/*
 * A split names each output file after its section.  Windows reserves the
 * device names with any extension, so "CON.report" is no more a file than
 * "CON" is.
 */
public sealed class PdfSectionTests
{
	[Theory]
	[InlineData("CON")]
	[InlineData("con")]
	[InlineData("CON.report")]
	[InlineData("nul .x")]
	[InlineData("COM1.txt")]
	[InlineData("LPT9")]
	[InlineData("CONIN$")]
	[InlineData("conout$.log")]
	[InlineData("COM¹")]
	public void
	ReservedDeviceNamesArePrefixed(string name)
	{
		string stem;

		stem = PdfSection.MakeSafeStem(name);

		Assert.StartsWith("_", stem);
		Assert.False(PdfSection.IsReservedDeviceName(stem));
	}

	[Theory]
	[InlineData("Contract", "Contract")]
	[InlineData("CONSOLE", "CONSOLE")]
	[InlineData("COM10", "COM10")]
	[InlineData("report.CON", "report.CON")]
	[InlineData("a/b:c", "a_b_c")]
	[InlineData("  trailing dots...  ", "trailing dots")]
	public void
	OrdinaryNamesAreKept(string name, string expected)
	{

		Assert.Equal(expected, PdfSection.MakeSafeStem(name));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("...")]
	public void
	EmptyNamesGiveEmptyStem(string? name)
	{

		Assert.Equal(string.Empty, PdfSection.MakeSafeStem(name));
	}

	[Fact]
	public void
	DuplicateAndEmptyNamesAreResolved()
	{
		List<PdfSection> sections;
		List<string> names;

		sections = new List<PdfSection> {
			new PdfSection("Part", null, new List<DocumentItem>()),
			new PdfSection("part", null, new List<DocumentItem>()),
			new PdfSection("", null, new List<DocumentItem>()),
			new PdfSection("CON", null, new List<DocumentItem>()),
		};

		names = PdfSection.ResolveFileNames(sections, "Section");

		Assert.Equal(new[] {
			"Part.pdf", "part (2).pdf", "Section 3.pdf", "_CON.pdf"
		}, names);
	}
}

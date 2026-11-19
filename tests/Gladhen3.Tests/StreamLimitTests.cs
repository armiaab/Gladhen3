using System;
using System.Text;
using Gladhen3.Services;
using Xunit;

namespace Gladhen3.Tests;

/*
 * Flate streams are inflated only through InflateBounded, which stops at a
 * limit instead of growing a buffer until the process runs out of memory.
 */
public sealed class StreamLimitTests
{
	[Fact]
	public void
	Adler32MatchesKnownValue()
	{

		/* RFC 1950's checksum of "Wikipedia", the usual test vector. */
		Assert.Equal(0x11E60398u,
		    PdfService.Adler32(Encoding.ASCII.GetBytes("Wikipedia")));
	}

	[Fact]
	public void
	Adler32OfEmptyIsOne()
	{

		Assert.Equal(1u, PdfService.Adler32(Array.Empty<byte>()));
	}

	/*
	 * Long enough to cross several of the 5552-byte runs the accumulator
	 * folds at, and several 64 KB inflate chunks.
	 */
	[Fact]
	public void
	StreamedChecksumMatchesWholeBuffer()
	{
		byte[] payload, zlib;
		uint streamed;
		Random random;

		random = new Random(1950);
		payload = new byte[300_000];
		random.NextBytes(payload);
		zlib = PdfService.ZlibCompress(payload);

		Assert.True(PdfService.TryAdler32OfInflated(zlib, long.MaxValue,
		    out streamed));
		Assert.Equal(PdfService.Adler32(payload), streamed);
	}

	[Fact]
	public void
	InflateReportsLength()
	{
		byte[] zlib;

		zlib = PdfService.ZlibCompress(new byte[12345]);

		Assert.Equal(12345, PdfService.InflateBounded(zlib, 12345, null));
	}

	/*
	 * Sixteen megabytes of zeros deflate to a few kilobytes; with a one
	 * megabyte limit the inflate stops early and says so.
	 */
	[Fact]
	public void
	InflateStopsAtLimit()
	{
		byte[] bomb;
		long seen;

		bomb = PdfService.ZlibCompress(new byte[16 << 20]);
		seen = 0;

		Assert.True(bomb.Length < 64 * 1024);
		Assert.Equal(-1, PdfService.InflateBounded(bomb, 1 << 20,
		    chunk => seen += chunk.Length));
		Assert.True(seen <= 1 << 20);
	}

	[Fact]
	public void
	ChecksumRefusesOverLimit()
	{
		byte[] bomb;

		bomb = PdfService.ZlibCompress(new byte[4 << 20]);

		Assert.False(PdfService.TryAdler32OfInflated(bomb, 1 << 20,
		    out _));
	}
}

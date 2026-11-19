using System;
using BenchmarkDotNet.Attributes;
using Gladhen3.Services;

namespace Gladhen3.Benchmarks;

/*
 * Every Flate stream in a saved PDF is checked for its zlib trailer, so
 * the checksum and the bounded inflate run once per stream per save.
 */
[MemoryDiagnoser]
public class StreamBenchmarks
{
	private byte[] payload = Array.Empty<byte>();
	private byte[] zlib = Array.Empty<byte>();

	[Params(64 * 1024, 4 * 1024 * 1024)]
	public int Size { get; set; }

	[GlobalSetup]
	public void
	Setup()
	{
		Random random;
		int i;

		/* Half noise, half runs: about what a page's content stream is. */
		random = new Random(1950);
		payload = new byte[Size];
		random.NextBytes(payload.AsSpan(0, Size / 2));
		for (i = Size / 2; i < Size; i++)
			payload[i] = (byte)(i / 512);
		zlib = PdfService.ZlibCompress(payload);
	}

	[Benchmark]
	public uint
	Adler32()
	{

		return (PdfService.Adler32(payload));
	}

	[Benchmark]
	public uint
	Adler32OfInflated()
	{
		uint sum;

		PdfService.TryAdler32OfInflated(zlib, long.MaxValue, out sum);
		return (sum);
	}

	[Benchmark]
	public int
	ZlibCompress()
	{

		return (PdfService.ZlibCompress(payload).Length);
	}
}

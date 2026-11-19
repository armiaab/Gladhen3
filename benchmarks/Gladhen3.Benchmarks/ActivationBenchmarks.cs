using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Gladhen3.Services;

namespace Gladhen3.Benchmarks;

/*
 * Opening a few thousand files from Explorer: the URI the shell extension
 * builds, and the pipe message a second launch sends.
 */
[MemoryDiagnoser]
public class ActivationBenchmarks
{
	private string[] paths = Array.Empty<string>();
	private string query = string.Empty;
	private byte[] message = Array.Empty<byte>();

	[Params(10, 2000)]
	public int Count { get; set; }

	[GlobalSetup]
	public void
	Setup()
	{
		MemoryStream stream;
		int i;

		paths = new string[Count];
		for (i = 0; i < Count; i++)
			paths[i] = $@"C:\Users\someone\Scans\batch, {i}\page {i}.jpg";
		query = "?files=" + string.Join(",",
		    Array.ConvertAll(paths, Uri.EscapeDataString));

		stream = new MemoryStream();
		ActivationProtocol.Write(stream, paths);
		message = stream.ToArray();
	}

	[Benchmark]
	public int
	ParseQuery()
	{

		return (ActivationProtocol.ParseFilesQuery(query).Count);
	}

	[Benchmark]
	public int
	ReadMessage()
	{
		List<string>? read;

		read = ActivationProtocol.ReadAsync(new MemoryStream(message),
		    CancellationToken.None).GetAwaiter().GetResult();
		return (read?.Count ?? 0);
	}
}

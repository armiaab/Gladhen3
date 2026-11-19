using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Gladhen3.Services;
using Xunit;

namespace Gladhen3.Tests;

/*
 * The pipe message a second launch sends the first, and the gladhen2: URI
 * the Explorer menu builds.  Both carry paths a user chose, so a lost or
 * split path is a lost file.
 */
public sealed class ActivationProtocolTests
{
	private static readonly string[] Paths = {
		@"C:\Scans\a.pdf",
		@"D:\Фото\снимок 1.jpg",
		@"C:\with, comma\b%20c+d.png",
	};

	[Fact]
	public async Task
	RoundTripsPaths()
	{
		MemoryStream stream;
		List<string>? read;

		stream = new MemoryStream();
		ActivationProtocol.Write(stream, Paths);
		stream.Position = 0;

		read = await ActivationProtocol.ReadAsync(stream,
		    CancellationToken.None);

		Assert.Equal(Paths, read);
	}

	/*
	 * A pipe may hand over a message a byte at a time.  The reader used to
	 * take whatever one read returned and drop the message if it was short.
	 */
	[Fact]
	public async Task
	ReadsMessageDeliveredInFragments()
	{
		MemoryStream inner;
		List<string>? read;

		inner = new MemoryStream();
		ActivationProtocol.Write(inner, Paths);
		inner.Position = 0;

		read = await ActivationProtocol.ReadAsync(
		    new TrickleStream(inner), CancellationToken.None);

		Assert.Equal(Paths, read);
	}

	[Fact]
	public async Task
	TruncatedMessageThrowsEndOfStream()
	{
		MemoryStream full, cut;
		byte[] bytes;

		full = new MemoryStream();
		ActivationProtocol.Write(full, Paths);
		bytes = full.ToArray();
		cut = new MemoryStream(bytes, 0, bytes.Length - 3);

		await Assert.ThrowsAsync<EndOfStreamException>(() =>
		    ActivationProtocol.ReadAsync(cut, CancellationToken.None));
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(ActivationProtocol.MaxMessageBytes + 1)]
	public async Task
	RejectsOutOfRangeLength(int length)
	{
		MemoryStream stream;

		stream = new MemoryStream(BitConverter.GetBytes(length));

		Assert.Null(await ActivationProtocol.ReadAsync(stream,
		    CancellationToken.None));
	}

	[Fact]
	public async Task
	EmptyMessageIsNoPaths()
	{
		MemoryStream stream;
		List<string>? read;

		stream = new MemoryStream();
		ActivationProtocol.Write(stream, Array.Empty<string>());
		stream.Position = 0;

		read = await ActivationProtocol.ReadAsync(stream,
		    CancellationToken.None);

		Assert.NotNull(read);
		Assert.Empty(read);
	}

	[Fact]
	public async Task
	RejectsInvalidUtf8()
	{
		MemoryStream stream;
		byte[] body;

		body = new byte[] { 0x43, 0x3A, 0xC3, 0x28 };
		stream = new MemoryStream();
		stream.Write(BitConverter.GetBytes(body.Length));
		stream.Write(body);
		stream.Position = 0;

		Assert.Null(await ActivationProtocol.ReadAsync(stream,
		    CancellationToken.None));
	}

	[Fact]
	public void
	WriteRefusesOversizedMessage()
	{
		string[] huge;

		huge = new[] {
		    new string('x', ActivationProtocol.MaxMessageBytes + 1)
		};

		Assert.Throws<ArgumentException>(() =>
		    ActivationProtocol.Write(new MemoryStream(), huge));
	}

	/*
	 * The shell extension percent-encodes every byte outside A-Z a-z 0-9
	 * - _ . ~ and joins the paths with a bare comma, which is exactly what
	 * Uri.EscapeDataString produces.  Commas, '%', '+', '&' and '?' inside a
	 * name must come back as they went in.
	 */
	[Fact]
	public void
	UriRoundTripsAwkwardNames()
	{
		string[] names;
		string uri;
		List<string> parsed;

		names = new[] {
			@"C:\a,b\c,d.pdf",
			@"C:\100%\x%41.pdf",
			@"C:\a+b\c d.jpg",
			@"C:\q?&=\#hash.png",
			@"C:\日本語\ü.tif",
		};
		uri = "gladhen2:///open?files=" + string.Join(",",
		    Array.ConvertAll(names, Uri.EscapeDataString));

		parsed = ActivationProtocol.ParseUri(uri);

		Assert.Equal(names, parsed);
	}

	[Theory]
	[InlineData("not a uri")]
	[InlineData("gladhen2:///open")]
	[InlineData("gladhen2:///open?other=1")]
	[InlineData("gladhen2:///open?files=")]
	public void
	UriWithoutFilesIsEmpty(string uri)
	{

		Assert.Empty(ActivationProtocol.ParseUri(uri));
	}

	[Fact]
	public void
	QueryIgnoresOtherParameters()
	{
		List<string> parsed;

		parsed = ActivationProtocol.ParseFilesQuery(
		    "?x=1&files=C%3A%5Ca.pdf,C%3A%5Cb.pdf&y=2");

		Assert.Equal(new[] { @"C:\a.pdf", @"C:\b.pdf" }, parsed);
	}

	/* A stream that never returns more than one byte per read. */
	private sealed class TrickleStream : Stream
	{
		private readonly Stream inner;

		public TrickleStream(Stream inner)
		{

			this.inner = inner;
		}

		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();

		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		public override int Read(byte[] buffer, int offset, int count)
		{

			return (inner.Read(buffer, offset, Math.Min(count, 1)));
		}

		public override ValueTask<int> ReadAsync(Memory<byte> buffer,
		    CancellationToken cancellationToken = default)
		{

			return (inner.ReadAsync(buffer[..Math.Min(buffer.Length, 1)],
			    cancellationToken));
		}

		public override void Flush()
		{
		}

		public override long Seek(long offset, SeekOrigin origin)
		{

			throw new NotSupportedException();
		}

		public override void SetLength(long value)
		{

			throw new NotSupportedException();
		}

		public override void Write(byte[] buffer, int offset, int count)
		{

			throw new NotSupportedException();
		}
	}
}

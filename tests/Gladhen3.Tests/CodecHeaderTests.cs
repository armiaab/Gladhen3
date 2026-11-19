using System;
using System.Buffers.Binary;
using Gladhen3.Services;
using Xunit;

namespace Gladhen3.Tests;

/*
 * Windows decodes neither JPEG 2000 nor JBIG2, so a plugin's output in
 * those formats is checked against the size its header declares.  These
 * build minimal headers by hand, byte for byte from the specifications.
 */
public sealed class CodecHeaderTests
{
	/* ISO/IEC 15444-1 A.5.1: SOC, then SIZ. */
	private static byte[]
	Codestream(uint width, uint height, uint xOffset, uint yOffset,
	    ushort components)
	{
		byte[] data;

		data = new byte[42];
		data[0] = 0xFF;
		data[1] = 0x4F;
		data[2] = 0xFF;
		data[3] = 0x51;
		BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4),
		    (ushort)(38 + 3 * components));
		BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), width);
		BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12), height);
		BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), xOffset);
		BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), yOffset);
		BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(38),
		    components);
		return (data);
	}

	private static byte[]
	Box(string type, byte[] body)
	{
		byte[] box;

		box = new byte[8 + body.Length];
		BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
		box[4] = (byte)type[0];
		box[5] = (byte)type[1];
		box[6] = (byte)type[2];
		box[7] = (byte)type[3];
		body.CopyTo(box, 8);
		return (box);
	}

	/* ISO/IEC 15444-1 I.5.3.1: ihdr is height, width, components. */
	private static byte[]
	Jp2File(uint width, uint height, ushort components)
	{
		byte[] ihdr, signature, ftyp, header;

		ihdr = new byte[14];
		BinaryPrimitives.WriteUInt32BigEndian(ihdr, height);
		BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), width);
		BinaryPrimitives.WriteUInt16BigEndian(ihdr.AsSpan(8), components);
		signature = Box("jP  ", new byte[] { 0x0D, 0x0A, 0x87, 0x0A });
		ftyp = Box("ftyp", new byte[12]);
		header = Box("jp2h", Box("ihdr", ihdr));
		return (Concat(signature, ftyp, header,
		    Box("jp2c", Codestream(width, height, 0, 0, components))));
	}

	/*
	 * ITU-T T.88 7.2 and 7.4.8: a page-information segment (type 48) with
	 * a one-byte page association, then width and height.
	 */
	private static byte[]
	Jbig2(uint width, uint height, byte type)
	{
		byte[] data;

		data = new byte[11 + 19];
		data[4] = type;
		data[5] = 0;
		data[6] = 1;
		BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(7), 19);
		BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(11), width);
		BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(15), height);
		return (data);
	}

	private static byte[]
	Concat(params byte[][] parts)
	{
		byte[] all;
		int at;

		at = 0;
		foreach (byte[] part in parts)
			at += part.Length;
		all = new byte[at];
		at = 0;
		foreach (byte[] part in parts) {
			part.CopyTo(all, at);
			at += part.Length;
		}
		return (all);
	}

	[Fact]
	public void
	ReadsRawCodestream()
	{

		Assert.Equal((640L, 480L, 3),
		    NativeImageCodec.JpxHeader(Codestream(650, 485, 10, 5, 3)));
	}

	[Fact]
	public void
	ReadsJp2Header()
	{

		Assert.Equal((800L, 600L, 1),
		    NativeImageCodec.JpxHeader(Jp2File(800, 600, 1)));
	}

	[Fact]
	public void
	RejectsOffsetPastSize()
	{

		Assert.Null(NativeImageCodec.JpxHeader(
		    Codestream(10, 10, 20, 0, 1)));
	}

	[Fact]
	public void
	RejectsBoxRunningPastEnd()
	{
		byte[] file;

		file = Jp2File(8, 8, 1);
		BinaryPrimitives.WriteUInt32BigEndian(file, 0x7FFFFFFF);

		Assert.Null(NativeImageCodec.JpxHeader(file));
	}

	[Theory]
	[InlineData(new byte[0])]
	[InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 })]
	public void
	RejectsNonJpx(byte[] data)
	{

		Assert.Null(NativeImageCodec.JpxHeader(data));
	}

	[Fact]
	public void
	ReadsJbig2PageInformation()
	{

		Assert.Equal((2480L, 3508L),
		    NativeImageCodec.Jbig2PageSize(Jbig2(2480, 3508, 48)));
	}

	[Fact]
	public void
	RejectsJbig2NotStartingWithPageInformation()
	{

		/* Type 38 is an immediate generic region. */
		Assert.Null(NativeImageCodec.Jbig2PageSize(Jbig2(10, 10, 38)));
	}

	[Fact]
	public void
	RejectsShortJbig2()
	{

		Assert.Null(NativeImageCodec.Jbig2PageSize(new byte[10]));
	}
}

using Gladhen3.Services;
using Xunit;

namespace Gladhen3.Tests;

/*
 * A model's description sits beside it where anything can edit it.  Every
 * number in it has to be one a picture can be fed with.
 */
public sealed class OrientationModelSpecTests
{
	private static readonly OrientationModelSpec Valid = new();

	[Fact]
	public void
	DefaultsAreWithinBounds()
	{

		Assert.True(Valid.IsWithinBounds);
	}

	[Theory]
	[InlineData(0, 224)]
	[InlineData(224, -1)]
	[InlineData(OrientationModelSpec.MaxInputEdge + 1, 224)]
	[InlineData(224, int.MaxValue)]
	public void
	RejectsInputSize(int width, int height)
	{

		Assert.False((Valid with {
			Width = width, Height = height
		}).IsWithinBounds);
	}

	[Theory]
	[InlineData(0.0)]
	[InlineData(1.5)]
	[InlineData(double.NaN)]
	[InlineData(double.PositiveInfinity)]
	public void
	RejectsCropShare(double share)
	{

		Assert.False((Valid with { CropShare = share }).IsWithinBounds);
	}

	[Theory]
	[InlineData(0f)]
	[InlineData(float.NaN)]
	[InlineData(float.NegativeInfinity)]
	public void
	RejectsScale(float scale)
	{

		Assert.False((Valid with { Scale = scale }).IsWithinBounds);
	}

	/* A zero deviation divides by zero: every input becomes Infinity. */
	[Fact]
	public void
	RejectsZeroDeviation()
	{

		Assert.False((Valid with {
			Std = new[] { 0.2f, 0f, 0.2f }
		}).IsWithinBounds);
	}

	[Fact]
	public void
	RejectsNaNMean()
	{

		Assert.False((Valid with {
			Mean = new[] { 0.5f, float.NaN, 0.5f }
		}).IsWithinBounds);
	}

	[Fact]
	public void
	RejectsWrongChannelCount()
	{

		Assert.False((Valid with {
			Mean = new[] { 0.5f, 0.5f }
		}).IsWithinBounds);
	}

	[Fact]
	public void
	RejectsUnknownChannelOrder()
	{

		Assert.False((Valid with { Channels = "RGBA" }).IsWithinBounds);
	}
}

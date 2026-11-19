using System;
using System.Threading.Tasks;
using Gladhen3.Models;
using Xunit;

namespace Gladhen3.Tests;

/*
 * Settings are copy-on-write: a save swaps in a new instance, and a worker
 * pins the one it started with.  These tests hold both halves of that.
 */
[Collection(SettingsCollection.Name)]
public sealed class AppSettingsTests
{
	[Fact]
	public void
	CloneIsIndependent()
	{
		AppSettings original, copy;

		original = new AppSettings { PaperSize = PdfPaperSize.A4 };
		copy = original.Clone();
		copy.PaperSize = PdfPaperSize.Letter;

		Assert.Equal(PdfPaperSize.A4, original.PaperSize);
		Assert.Equal(PdfPaperSize.Letter, copy.PaperSize);
	}

	[Fact]
	public void
	PinOverridesCurrentUntilDisposed()
	{
		AppSettings saved, pinned;

		saved = new AppSettings();
		pinned = new AppSettings();
		AppSettings.ReplaceForTesting(saved);

		using (AppSettings.Pin(pinned)) {
			Assert.Same(pinned, AppSettings.Current);
		}

		Assert.Same(saved, AppSettings.Current);
	}

	[Fact]
	public void
	PinsNest()
	{
		AppSettings outer, inner;

		outer = new AppSettings();
		inner = new AppSettings();
		AppSettings.ReplaceForTesting(new AppSettings());

		using (AppSettings.Pin(outer)) {
			using (AppSettings.Pin(inner)) {
				Assert.Same(inner, AppSettings.Current);
			}
			Assert.Same(outer, AppSettings.Current);
		}
	}

	/*
	 * A build fans out over the thread pool; every lane has to see the
	 * pinned values, and a save made meanwhile must not reach them.
	 */
	[Fact]
	public async Task
	PinFlowsIntoTasksAndIgnoresLaterSaves()
	{
		AppSettings pinned;
		PdfPaperSize seen;

		pinned = new AppSettings { PaperSize = PdfPaperSize.Legal };
		AppSettings.ReplaceForTesting(new AppSettings());

		using (AppSettings.Pin(pinned)) {
			AppSettings.ReplaceForTesting(
			    new AppSettings { PaperSize = PdfPaperSize.A3 });
			seen = await Task.Run(() => AppSettings.Current.PaperSize);
		}

		Assert.Equal(PdfPaperSize.Legal, seen);
		Assert.Equal(PdfPaperSize.A3, AppSettings.Current.PaperSize);
	}

	[Fact]
	public void
	PinRejectsNull()
	{

		Assert.Throws<ArgumentNullException>(() =>
		    AppSettings.Pin(null!));
	}
}

/*
 * AppSettings.Current is process-wide, so every test class that replaces it
 * runs in this one collection, one test at a time.
 */
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SettingsCollection
{
	public const string Name = "Settings";
}

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Gladhen3.Services;

/// <summary>
/// Opens files for WinRT APIs without calling <see cref="StorageFile"/> on the UI thread.
/// </summary>
/// <remarks>
/// <c>StorageFile.GetFileFromPathAsync</c> makes a blocking cross-process COM call before
/// it returns, and that call pumps messages. If a XAML timer runs inside that pump, XAML
/// fail-fasts with 0xC000027B (seen when paging quickly through images after a reorder).
/// Streams therefore come from <see cref="FileStream"/>, and anything that needs a
/// StorageFile is resolved on the thread pool.
/// </remarks>
internal static class OffUiStorage
{
    /// <summary>A read-only random-access stream over the file, for decoders and BitmapImage.</summary>
    public static Task<IRandomAccessStream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run(() => (IRandomAccessStream)File.OpenRead(path).AsRandomAccessStream(), cancellationToken);

    /// <summary>The StorageFile for a path, resolved on the thread pool.</summary>
    public static Task<StorageFile> GetFileAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run(() => StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken), cancellationToken);

    /// <summary>A PDF opened with Windows.Data.Pdf, entirely on the thread pool.</summary>
    public static Task<Windows.Data.Pdf.PdfDocument> LoadPdfAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run(async () =>
        {
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken);
            return await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file).AsTask(cancellationToken);
        }, cancellationToken);
}

using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Gladhen3.Dialogs;

/// <summary>Shows one <see cref="ContentDialog"/> at a time, queueing the rest.</summary>
/// <remarks>
/// WinUI allows one ContentDialog per XamlRoot; a second ShowAsync throws, and from an
/// async void handler that ends the process. Queued rather than dropped, because each
/// dialog is either a question the caller awaits or a result the user asked for.
/// </remarks>
internal static class DialogHost
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await Gate.WaitAsync();
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            Gate.Release();
        }
    }
}

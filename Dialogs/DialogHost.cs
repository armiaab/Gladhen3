using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Gladhen3.Dialogs;

/// <summary>
/// Shows one <see cref="ContentDialog"/> at a time, queueing any that arrive while another
/// is open.
/// </summary>
/// <remarks>
/// WinUI permits exactly one ContentDialog per <c>XamlRoot</c>; a second <c>ShowAsync</c>
/// throws <c>COMException 0x80000019</c> - "Only a single ContentDialog can be open at any
/// time". Almost every caller here is an <c>async void</c> event handler, which is the end
/// of the line for an exception: it reached <see cref="App"/>'s unhandled handler and took
/// the process down. That cost the whole app for something entirely recoverable - a PDF
/// that had already been written correctly still killed the window, because the previous
/// dialog had not been dismissed yet.
///
/// Queueing is the right response rather than dropping the second dialog: each one is
/// either a question whose answer the caller is waiting on (retry? choose another folder?)
/// or the result the user explicitly asked to see.
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

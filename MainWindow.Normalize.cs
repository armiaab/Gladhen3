using Gladhen3.Models;
using Gladhen3.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Gladhen3;

/// <summary>
/// Normalize: turn every page so that it reads the right way up - the fix for a stack of scans
/// fed in every which way, or a PDF assembled from pages that were never turned.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The check under way, if there is one.</summary>
    private CancellationTokenSource? _normalizeCts;

    /// <summary>The pages the last check could not judge, for "Select them".</summary>
    private List<DocumentItem> _leftAlone = [];

    private bool _watchingStatus;

    /// <summary>
    /// Save and normalize can run at once, and each used to hide the busy bar when it finished
    /// whether or not the other still needed it.
    /// </summary>
    private int _busyCount;

    private void SetBusy(bool busy)
    {
        _busyCount = Math.Max(0, _busyCount + (busy ? 1 : -1));
        BusyBar.Visibility = _busyCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The pages to normalize.</summary>
    /// <remarks>
    /// The page right-clicked or in the viewer; pages the user chose; otherwise every page,
    /// since "make all of these the right way up" is what the command is for. A single page
    /// selected by an ordinary click is just where the user happens to be, so it takes two or
    /// more to narrow the command - or any in select mode, where every tick is deliberate.
    /// Reverse draws the same line.
    /// </remarks>
    private List<DocumentItem> NormalizeTargets(DocumentItem? anchor)
    {
        if (anchor != null || _viewerItem != null) return TargetItems(anchor);

        var selected = SelectedItemsInOrder();
        return selected.Count >= (_isSelectMode ? 1 : 2) ? selected : Pages.ToList();
    }

    /// <summary>Checks each page and turns it upright as soon as its answer is in.</summary>
    /// <remarks>
    /// Turns are applied as they arrive, so a long scan visibly straightens itself rather than
    /// sitting still for a minute. The list stays editable meanwhile: a page that has been
    /// removed, or that the user has turned by hand since, is left as it is now - their turn
    /// wins - and undo or redo stops the check, so that it cannot turn pages back after them.
    /// </remarks>
    private async Task NormalizeAsync(List<DocumentItem> targets)
    {
        if (_normalizeCts != null)
        {
            StatusTextBlock.Text = _resourceLoader.GetString("StatusNormalizeBusy");
            return;
        }

        // Blank pages have nothing on them to read.
        var pages = targets.Where(item => item.Type is DocumentType.PdfPage or DocumentType.Image).Select(PageToCheck.Of).ToList();
        if (pages.Count == 0)
        {
            StatusTextBlock.Text = _resourceLoader.GetString(Pages.Any() ? "StatusNormalizeNothing" : "StatusNoPagesYet");
            return;
        }

        // One undo step for the whole run, taken now: a user's own edit made while this runs
        // then lands on top of it, and undoing that edit cannot also undo this.
        RecordUndo();
        var undoStep = _undo[^1];

        var cts = new CancellationTokenSource();
        _normalizeCts = cts;

        // Everything the lanes found, and - on this thread - what was acted on. They differ after
        // a stop: pages still being read finish and report, but are not turned, and must not be
        // counted as upright either.
        var results = new ConcurrentDictionary<PageToCheck, OrientationFinding>();
        var handled = new Dictionary<PageToCheck, OrientationFinding>();

        var found = new Progress<(PageToCheck Page, OrientationFinding Finding)>(report =>
        {
            if (cts.IsCancellationRequested) return;

            handled[report.Page] = report.Finding;
            Apply(report.Page, report.Finding);
            StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusNormalizing"), handled.Count, pages.Count);
        });

        NormalizeStopButton.Visibility = Visibility.Visible;
        SetBusy(true);
        StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusNormalizing"), 0, pages.Count);
        Log.Information("Normalize: checking {Count} page(s)", pages.Count);

        var options = new OrientationOptions(AppSettings.Current.OrientationMethod, AppSettings.Current.OrientationTextRecognitionFallback);
        OrientationModelException? modelFailure = null;

        bool stopped;
        try
        {
            await Task.Run(() => OrientationDetector.DetectAsync(pages, options, new Tee(results, found), cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Stopped; the token says so below.
        }
        // The chosen model would not load, so no page was checked. Said once, here, rather
        // than as a failure on every page.
        catch (OrientationModelException ex)
        {
            Log.Warning(ex, "Normalize: the orientation model {Method} could not be used ({Reason})", options.Method, ex.Reason);
            modelFailure = ex;
        }
        finally
        {
            // From the token, not from whether the run threw: a stop that arrives after the last
            // page was checked throws nothing, and still has to keep the turns below from landing.
            stopped = cts.IsCancellationRequested;
            NormalizeStopButton.Visibility = Visibility.Collapsed;
            SetBusy(false);
            _normalizeCts = null;
            cts.Dispose();
        }

        // Progress reports are posted to this thread and may still be queued behind the end of
        // the run; whatever has not arrived yet is taken from the full set instead.
        if (!stopped)
        {
            foreach (var (page, finding) in results)
            {
                if (handled.TryAdd(page, finding)) Apply(page, finding);
            }
        }

        var summary = Summarize(handled);
        if (summary.Turned == 0 && _undo.Count > 0 && ReferenceEquals(_undo[^1], undoStep))
        {
            // Nothing changed, so there is nothing to undo.
            _undo.RemoveAt(_undo.Count - 1);
            UpdateUndoState();
        }

        Log.Information("Normalize: {Handled}/{Count} checked, {Summary}{Stopped}; {Evidence}",
            handled.Count, pages.Count, summary, stopped ? ", stopped" : "",
            string.Join(", ", handled.Values.GroupBy(f => f.Evidence).Select(g => $"{g.Key}={g.Count()}")));

        if (modelFailure != null)
        {
            StatusTextBlock.Text = _resourceLoader.GetString("StatusNormalizeModelFailed");
            await ShowDialogAsync(
                _resourceLoader.GetString("DialogTitleModelUnavailable"),
                string.Format(_resourceLoader.GetString("DialogContentModelUnavailable"), Dialogs.OrientationModelText.Reason(_resourceLoader, modelFailure)));
            return;
        }

        StatusTextBlock.Text = DescribeNormalized(summary, handled.Count, pages.Count, stopped);
        OfferLeftAlone(handled.Where(h => h.Value.Turn == null).Select(h => h.Key.Item));

        var unreadable = handled.Values.Count(f => f.Evidence == OrientationEvidence.NoRecognizer);
        if (unreadable > 0) await OfferTextRecognitionAsync(unreadable);
    }

    /// <summary>
    /// Shows "Select them" beside the summary when some pages could not be judged: those are the
    /// ones still worth a look, and in a long scan they are hard to find by eye.
    /// </summary>
    /// <remarks>Not for the one page in the viewer, which is already the one on show.</remarks>
    private void OfferLeftAlone(IEnumerable<DocumentItem> pages)
    {
        _leftAlone = InListOrder(pages);
        if (_leftAlone.Count == 0 || IsViewerOpen) return;

        // The link describes the summary next to it, so it goes as soon as the status says
        // anything else.
        if (!_watchingStatus)
        {
            StatusTextBlock.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => NormalizeReviewButton.Visibility = Visibility.Collapsed);
            _watchingStatus = true;
        }
        NormalizeReviewButton.Visibility = Visibility.Visible;
    }

    private void NormalizeReview_Click(object sender, RoutedEventArgs e)
    {
        var pages = _leftAlone.Where(_documentItems.Contains).ToList();
        if (pages.Count == 0) return;

        var view = ActiveView;
        view.SelectedItems.Clear();
        foreach (var page in pages) view.SelectedItems.Add(page);
        view.ScrollIntoView(pages[0]);

        StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusSelectedPages"), pages.Count);
    }

    private readonly record struct NormalizeSummary(int Turned, int Upright, int Unsure, int NotChecked, int Failed);

    /// <remarks>
    /// A page counts as turned only if it now has the turn that was found for it, and as upright
    /// only if that turn is the one it already had. A page the user turned by hand meanwhile, or
    /// removed, is neither: what happened to it was their doing. A picture that nothing could
    /// read is "not checked" rather than "no text": it very likely has text.
    /// </remarks>
    private static NormalizeSummary Summarize(Dictionary<PageToCheck, OrientationFinding> handled) => new(
        handled.Count(h => h.Value.Turn is { } turn && turn != h.Key.Rotation && h.Key.Item.Rotation == turn),
        handled.Count(h => h.Value.Turn == h.Key.Rotation),
        handled.Count(h => h.Value.Evidence == OrientationEvidence.Undecided),
        handled.Count(h => h.Value.Evidence == OrientationEvidence.NoRecognizer),
        handled.Count(h => h.Value.Evidence == OrientationEvidence.Failed));

    /// <summary>Turns one page as its finding says, unless the page has moved on since it was checked.</summary>
    private void Apply(PageToCheck page, OrientationFinding finding)
    {
        if (finding.Turn is not { } turn) return;

        var item = page.Item;
        if (item.Rotation != page.Rotation || !_documentItems.Contains(item)) return;

        item.Rotation = turn;
    }

    /// <summary>Stops the check, keeping whatever it has turned so far; one undo puts those back.</summary>
    private void StopNormalize() => _normalizeCts?.Cancel();

    private void NormalizeStop_Click(object sender, RoutedEventArgs e) => StopNormalize();

    /// <summary>"3 turned upright, 37 already upright, 2 unchanged", leaving out the noughts.</summary>
    private string DescribeNormalized(NormalizeSummary summary, int handled, int total, bool stopped)
    {
        var parts = new List<string>();
        void Add(int count, string key)
        {
            if (count > 0) parts.Add(string.Format(_resourceLoader.GetString(key), count));
        }

        Add(summary.Turned, "NormalizeTurned");
        Add(summary.Upright, "NormalizeAlreadyUpright");
        Add(summary.Unsure, "NormalizeLeftAlone");
        Add(summary.NotChecked, "NormalizeNotChecked");
        Add(summary.Failed, "NormalizeFailed");

        // A comma in both languages this app speaks.
        var described = string.Join(", ", parts);
        if (!stopped) return string.Format(_resourceLoader.GetString("StatusNormalized"), described);

        return handled == 0
            ? _resourceLoader.GetString("StatusNormalizeStoppedEarly")
            : string.Format(_resourceLoader.GetString("StatusNormalizeStopped"), handled, total, described);
    }

    /// <summary>
    /// Says why pictures of pages were skipped, when the reason is that Windows has no text
    /// recognizer - and where to get one.
    /// </summary>
    /// <remarks>
    /// Only when text recognition was chosen instead of a model: Windows has none for Indonesian,
    /// so a PC set up in Indonesian alone has none at all. Adding English, or any language that
    /// comes with text recognition, fixes it - or going back to the model, which needs neither.
    /// </remarks>
    private async Task OfferTextRecognitionAsync(int pages)
    {
        var dialog = new ContentDialog
        {
            Title = _resourceLoader.GetString("DialogTitleNoTextRecognition"),
            Content = string.Format(_resourceLoader.GetString("DialogContentNoTextRecognition"), pages),
            PrimaryButtonText = _resourceLoader.GetString("DialogButtonOpenLanguageSettings"),
            CloseButtonText = _resourceLoader.GetString("DialogButtonOK"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };

        if (await Dialogs.DialogHost.ShowAsync(dialog) == ContentDialogResult.Primary)
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:regionlanguage"));
    }

    /// <summary>Keeps every finding, and passes each one on to be applied as it arrives.</summary>
    private sealed class Tee(
        ConcurrentDictionary<PageToCheck, OrientationFinding> results,
        IProgress<(PageToCheck Page, OrientationFinding Finding)> next) : IProgress<(PageToCheck Page, OrientationFinding Finding)>
    {
        public void Report((PageToCheck Page, OrientationFinding Finding) value)
        {
            results[value.Page] = value.Finding;
            next.Report(value);
        }
    }
}

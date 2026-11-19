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

/// <summary>Normalize: turns every page the right way up.</summary>
public sealed partial class MainWindow
{
    /// <summary>The check under way, if there is one.</summary>
    private CancellationTokenSource? _normalizeCts;

    /// <summary>The pages the last check could not judge, for "Select them".</summary>
    private List<DocumentItem> _leftAlone = [];

    private bool _watchingStatus;

    /// <summary>How many operations are using the busy bar; save and normalize can overlap.</summary>
    private int _busyCount;

    private void SetBusy(bool busy)
    {
        _busyCount = Math.Max(0, _busyCount + (busy ? 1 : -1));
        BusyBar.Visibility = _busyCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The pages to normalize.</summary>
    /// <remarks>
    /// The anchored or viewed page; else the selection when it is a real choice (two or more,
    /// or any in select mode); else every page.
    /// </remarks>
    private List<DocumentItem> NormalizeTargets(DocumentItem? anchor)
    {
        if (anchor != null || _viewerItem != null) return TargetItems(anchor);

        var selected = SelectedItemsInOrder();
        return selected.Count >= (_isSelectMode ? 1 : 2) ? selected : [.. Pages];
    }

    /// <summary>Checks each page and turns it as soon as its result arrives.</summary>
    /// <remarks>
    /// The list stays editable: a page removed or turned by hand meanwhile is left alone, and
    /// undo or redo stops the check.
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
            StatusTextBlock.Text = _resourceLoader.GetString(Pages.Count > 0 ? "StatusNormalizeNothing" : "StatusNoPagesYet");
            return;
        }

        // One undo step for the whole run, recorded before it starts.
        RecordUndo();
        var undoStep = _undo[^1];

        var cts = new CancellationTokenSource();
        _normalizeCts = cts;

        // Everything found, and what was actually applied; they differ after a stop.
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
        // The model failed to load: report it once, not per page.
        catch (OrientationModelException ex)
        {
            Log.Warning(ex, "Normalize: the orientation model {Method} could not be used ({Reason})", options.Method, ex.Reason);
            modelFailure = ex;
        }
        finally
        {
            // Check the token, not whether it threw: a late stop throws nothing.
            stopped = cts.IsCancellationRequested;
            NormalizeStopButton.Visibility = Visibility.Collapsed;
            SetBusy(false);
            _normalizeCts = null;
            cts.Dispose();
        }

        // Progress reports may still be queued; take what has not arrived from the full set.
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

    /// <summary>Shows "Select them" when some pages could not be judged.</summary>
    /// <remarks>Not for a single page in the viewer.</remarks>
    private void OfferLeftAlone(IEnumerable<DocumentItem> pages)
    {
        _leftAlone = InListOrder(pages);
        if (_leftAlone.Count == 0 || IsViewerOpen) return;

        // The link belongs to the summary, so hide it once the status changes.
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
    /// Turned or upright only if the page still has the found turn; pages the user changed or
    /// removed meanwhile count as neither. An unreadable picture is "not checked", not "no text".
    /// </remarks>
    private static NormalizeSummary Summarize(Dictionary<PageToCheck, OrientationFinding> handled) => new(
        handled.Count(h => h.Value.Turn is { } turn && turn != h.Key.Rotation && h.Key.Item.Rotation == turn),
        handled.Count(h => h.Value.Turn == h.Key.Rotation),
        handled.Count(h => h.Value.Evidence == OrientationEvidence.Undecided),
        handled.Count(h => h.Value.Evidence == OrientationEvidence.NoRecognizer),
        handled.Count(h => h.Value.Evidence == OrientationEvidence.Failed));

    /// <summary>Turns one page as found, unless it changed since it was checked.</summary>
    private void Apply(PageToCheck page, OrientationFinding finding)
    {
        if (finding.Turn is not { } turn) return;

        var item = page.Item;
        if (item.Rotation != page.Rotation || !_documentItems.Contains(item)) return;

        item.Rotation = turn;
    }

    /// <summary>Stops the check, keeping the turns so far; one undo reverts them.</summary>
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

        var described = string.Join(", ", parts);
        if (!stopped) return string.Format(_resourceLoader.GetString("StatusNormalized"), described);

        return handled == 0
            ? _resourceLoader.GetString("StatusNormalizeStoppedEarly")
            : string.Format(_resourceLoader.GetString("StatusNormalizeStopped"), handled, total, described);
    }

    /// <summary>Explains skipped pictures when Windows has no text recognizer installed.</summary>
    /// <remarks>
    /// Only when text recognition is the chosen method; Windows has none for Indonesian, so
    /// adding a language that has one (or using the model) fixes it.
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

    /// <summary>Records every finding and forwards each to be applied.</summary>
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

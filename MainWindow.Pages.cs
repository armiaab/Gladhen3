using Gladhen3.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Gladhen3;

/// <summary>
/// The quick edits - turning, copying, inserting, reversing and lifting out pages - and the
/// undo history that makes them safe to reach for.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The page commands, named by the Tag of whichever button or menu item asks for one.</summary>
    private enum PageCommand
    {
        RotateRight,
        RotateLeft,
        Rotate180,
        Normalize,
        Duplicate,
        InsertBlank,
        Reverse,
        SplitHere,
        Extract,
        Remove,
        Preview
    }

    /// <summary>The page the shared right-click menu was opened on, until one of its items runs.</summary>
    private DocumentItem? _menuAnchor;

    private ListViewBase ActiveView => _isGridView ? DocumentGridView : DocumentListView;

    private static bool IsPage(DocumentItem item) => item.Type != DocumentType.SectionBreak;

    #region Choosing pages

    /// <summary>The rows a command acts on, in list order.</summary>
    /// <remarks>
    /// In select mode, the selection - unless the menu was opened on a page outside it, which
    /// means that page alone, as a right-click does everywhere else in Windows. Otherwise the
    /// page the menu was opened on, or the one last clicked. Section dividers count only for
    /// removal: turning or copying a divider means nothing. While the viewer is open it is the
    /// page on show, whatever else is selected underneath.
    /// </remarks>
    private List<DocumentItem> TargetItems(DocumentItem? anchor, bool includeSectionBreaks = false)
    {
        bool Wanted(DocumentItem item) => includeSectionBreaks || IsPage(item);

        if (anchor == null && _viewerItem != null) return [_viewerItem];

        if (_isSelectMode)
        {
            var selected = ActiveView.SelectedItems.OfType<DocumentItem>().Where(Wanted).ToList();
            if (selected.Count > 0 && (anchor == null || selected.Contains(anchor)))
                return InListOrder(selected);
        }

        var single = anchor ?? CurrentTargetItem();
        return single != null && Wanted(single) && _documentItems.Contains(single) ? [single] : [];
    }

    private List<DocumentItem> InListOrder(IEnumerable<DocumentItem> items)
        => items
            .Select(item => (Item: item, Index: _documentItems.IndexOf(item)))
            .Where(entry => entry.Index >= 0)
            .OrderBy(entry => entry.Index)
            .Select(entry => entry.Item)
            .ToList();

    private void PageMenu_Opening(object sender, object e)
        => _menuAnchor = sender is FlyoutBase { Target: { } target } ? ItemFromVisual(target) : null;

    private async void PageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var anchor = _menuAnchor;
        _menuAnchor = null;
        await RunPageCommandAsync(sender, anchor);
    }

    private async void PageCommand_Click(object sender, RoutedEventArgs e) => await RunPageCommandAsync(sender, null);

    private async Task RunPageCommandAsync(object sender, DocumentItem? anchor)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<PageCommand>(tag, out var command)) return;

        // Event-handler boundary: every caller is async void, where an escaping exception
        // ends the process. The edits themselves are list operations that are not expected to
        // fail; the save behind Extract reports its own failures.
        try
        {
            await RunPageCommandAsync(command, anchor);

            // The viewer shows the result: the page turned, or the next one if it was removed.
            if (IsViewerOpen && command != PageCommand.Preview) await RefreshViewerAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Page command {Command} failed", command);
            StatusTextBlock.Text = _resourceLoader.GetString("StatusUnexpectedError");
        }
    }

    private async Task RunPageCommandAsync(PageCommand command, DocumentItem? anchor)
    {
        if (command == PageCommand.Reverse)
        {
            ReversePages();
            return;
        }

        if (command == PageCommand.Normalize)
        {
            await NormalizeAsync(NormalizeTargets(anchor));
            return;
        }

        var targets = TargetItems(anchor, includeSectionBreaks: command == PageCommand.Remove);
        if (targets.Count == 0)
        {
            StatusTextBlock.Text = _resourceLoader.GetString(Pages.Any() ? "StatusPickPageFirst" : "StatusNoPagesYet");
            return;
        }

        switch (command)
        {
            case PageCommand.RotateRight:
                Rotate(targets, 90);
                break;
            case PageCommand.RotateLeft:
                Rotate(targets, -90);
                break;
            case PageCommand.Rotate180:
                Rotate(targets, 180);
                break;
            case PageCommand.Duplicate:
                Duplicate(targets);
                break;
            case PageCommand.InsertBlank:
                InsertBlankAfter(targets);
                break;
            case PageCommand.SplitHere:
                SplitBefore(targets[0]);
                break;
            case PageCommand.Extract:
                await SaveItemsAsync(targets, ExtractName(targets));
                break;
            case PageCommand.Preview:
                await OpenViewerAsync(targets[0]);
                break;
            case PageCommand.Remove:
                Remove(targets);
                break;
        }
    }

    #endregion

    #region Edits

    private void Rotate(List<DocumentItem> pages, int degrees)
    {
        RecordUndo();
        foreach (var page in pages) page.Rotation += degrees;

        StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusRotatedPages"), pages.Count);
    }

    /// <summary>Copies the pages in as one run, straight after the last of them.</summary>
    /// <remarks>
    /// As a run rather than each copy beside its original, so that duplicating a chapter gives
    /// the chapter twice, not every page doubled. A single page comes out the same either way.
    /// </remarks>
    private void Duplicate(List<DocumentItem> pages)
    {
        RecordUndo();

        var at = _documentItems.IndexOf(pages[^1]) + 1;
        foreach (var page in pages) _documentItems.Insert(at++, page.Clone());

        UpdateUIState();
        StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusDuplicatedPages"), pages.Count);
    }

    /// <summary>Puts an empty page after each of these, the size of the page before it.</summary>
    private void InsertBlankAfter(List<DocumentItem> pages)
    {
        RecordUndo();

        var name = _resourceLoader.GetString("BlankPageName");
        foreach (var page in pages)
        {
            _documentItems.Insert(_documentItems.IndexOf(page) + 1, new DocumentItem
            {
                Type = DocumentType.BlankPage,
                FileName = name
            });
        }

        UpdateUIState();
        StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusInsertedBlankPages"), pages.Count);
    }

    private void Remove(List<DocumentItem> items)
    {
        RecordUndo();
        foreach (var item in items) _documentItems.Remove(item);

        UpdateUIState();
        StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusRemovedPages"), items.Count(IsPage));
    }

    /// <summary>
    /// Reverses the selected pages among the places they occupy, or with nothing selected,
    /// every page - each split on its own, the way sorting works.
    /// </summary>
    /// <remarks>
    /// The fix for a stack fed into a scanner face down. Within the selection's own slots so
    /// that reversing pages 3, 5 and 9 leaves every other page where it was.
    /// </remarks>
    private void ReversePages()
    {
        var selected = _isSelectMode ? InListOrder(ActiveView.SelectedItems.OfType<DocumentItem>().Where(IsPage)) : [];

        var ordered = _documentItems.ToList();
        int reversed;

        if (selected.Count >= 2)
        {
            var slots = selected.Select(_documentItems.IndexOf).ToList();
            for (var k = 0; k < slots.Count; k++) ordered[slots[k]] = selected[slots.Count - 1 - k];
            reversed = selected.Count;
        }
        else
        {
            var start = 0;
            for (var i = 0; i <= ordered.Count; i++)
            {
                if (i < ordered.Count && IsPage(ordered[i])) continue;
                ordered.Reverse(start, i - start);
                start = i + 1;
            }
            reversed = Pages.Count();
        }

        if (reversed < 2)
        {
            StatusTextBlock.Text = _resourceLoader.GetString("StatusPickPageFirst");
            return;
        }

        RecordUndo();
        ApplyOrder(ordered);
        UpdateSectionSummaries();
        StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusReversedPages"), reversed);
    }

    /// <summary>Moves rows into <paramref name="ordered"/>'s order without removing any.</summary>
    /// <remarks>Moves, not a rebuild, so thumbnails stay and the views animate the change.</remarks>
    private void ApplyOrder(IReadOnlyList<DocumentItem> ordered)
    {
        for (var target = 0; target < ordered.Count; target++)
        {
            var current = _documentItems.IndexOf(ordered[target]);
            if (current != target) _documentItems.Move(current, target);
        }
    }

    private void SelectOddPages_Click(object sender, RoutedEventArgs e) => SelectAlternatePages(odd: true);

    private void SelectEvenPages_Click(object sender, RoutedEventArgs e) => SelectAlternatePages(odd: false);

    /// <summary>Selects every other page, counting from the first page in the list.</summary>
    /// <remarks>
    /// Mostly for duplex scans, where every back side needs the same treatment - turning
    /// 180 degrees, or removing because it is blank.
    /// </remarks>
    private void SelectAlternatePages(bool odd)
    {
        if (!Pages.Any())
        {
            StatusTextBlock.Text = _resourceLoader.GetString("StatusNoPagesYet");
            return;
        }

        if (!_isSelectMode) SelectModeToggle.IsChecked = true;

        var view = ActiveView;
        view.SelectedItems.Clear();

        var position = 0;
        foreach (var item in _documentItems)
        {
            if (!IsPage(item)) continue;
            position++;
            if ((position % 2 == 1) == odd) view.SelectedItems.Add(item);
        }

        StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusSelectedPages"), view.SelectedItems.Count);
    }

    private string? ExtractName(List<DocumentItem> pages)
    {
        var stem = PdfSection.SuggestName(pages);
        return stem.Length == 0 ? null : string.Format(_resourceLoader.GetString("ExtractFileNameFormat"), stem);
    }

    #endregion

    #region Undo

    /// <summary>The list as it stood: which rows, in what order, turned which way, and what each split was called.</summary>
    private sealed record ListState(DocumentItem[] Items, int[] Rotations, string[] SectionNames);

    /// <remarks>
    /// Capped because every step keeps the rows it holds - thumbnails included - alive after
    /// they leave the list. Twenty steps is plenty for what this is for: taking back a slip.
    /// </remarks>
    private const int MaxUndoSteps = 20;

    private readonly List<ListState> _undo = [];
    private readonly List<ListState> _redo = [];

    /// <summary>The list as a drag found it, kept until the drag turns out to have moved something.</summary>
    private ListState? _dragStart;

    private ListState CaptureState() => new(
        [.. _documentItems],
        [.. _documentItems.Select(item => item.Rotation)],
        [.. _documentItems.Select(item => item.SectionName)]);

    /// <summary>Remembers the list as it is now, just before it is changed.</summary>
    private void RecordUndo() => PushUndo(CaptureState());

    private void PushUndo(ListState state)
    {
        _undo.Add(state);
        if (_undo.Count > MaxUndoSteps) _undo.RemoveAt(0);
        _redo.Clear();
        UpdateUndoState();
    }

    private async void Undo_Click(object sender, RoutedEventArgs e)
        => await StepThroughHistoryAsync(_undo, _redo, "StatusUndone", "StatusNothingToUndo");

    private async void Redo_Click(object sender, RoutedEventArgs e)
        => await StepThroughHistoryAsync(_redo, _undo, "StatusRedone", "StatusNothingToRedo");

    private async Task StepThroughHistoryAsync(List<ListState> from, List<ListState> to, string doneKey, string emptyKey)
    {
        // A check still running would go on turning pages after the list had been put back.
        StopNormalize();

        if (from.Count == 0)
        {
            StatusTextBlock.Text = _resourceLoader.GetString(emptyKey);
            return;
        }

        var state = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(CaptureState());

        // Event-handler boundary, reached from async void.
        try
        {
            await RestoreAsync(state);
            await RefreshViewerAsync();
            StatusTextBlock.Text = _resourceLoader.GetString(doneKey);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not restore the page list");
            StatusTextBlock.Text = _resourceLoader.GetString("StatusUnexpectedError");
        }

        UpdateUndoState();
    }

    private async Task RestoreAsync(ListState state)
    {
        for (var i = 0; i < state.Items.Length; i++)
        {
            state.Items[i].Rotation = state.Rotations[i];
            if (state.Items[i].Type == DocumentType.SectionBreak) state.Items[i].SectionName = state.SectionNames[i];
        }

        // The same rows in another order is put back by moving them; anything else - rows
        // added or removed - is rebuilt.
        if (state.Items.Length == _documentItems.Count && new HashSet<DocumentItem>(state.Items).SetEquals(_documentItems))
        {
            ApplyOrder(state.Items);
        }
        else
        {
            _documentItems.Clear();
            await AddItemsInBatchesAsync(state.Items);
        }

        UpdateUIState();
    }

    private void UpdateUndoState()
    {
        if (!_isInitialized) return;

        UndoButton.IsEnabled = _undo.Count > 0;
        UndoMenuItem.IsEnabled = _undo.Count > 0;
        RedoMenuItem.IsEnabled = _redo.Count > 0;
    }

    #endregion

    #region Saving over a source

    /// <summary>Where the copies of overwritten sources are kept, for this session only.</summary>
    private static string OriginalsDirectory
        => System.IO.Path.Combine(Windows.Storage.ApplicationData.Current.TemporaryFolder.Path, "Originals");

    /// <summary>
    /// Keeps the list meaning what it shows when a save is about to overwrite one of the files
    /// its pages are read from.
    /// </summary>
    /// <remarks>
    /// Saving over the original is the quickest edit there is, and it leaves every row from that
    /// file describing a document that has just changed underneath it: the turn a row asks for
    /// is already in the file, and an inserted blank has moved every later page number along by
    /// one. Saving again would quietly apply everything twice, to the wrong pages. So the file is
    /// copied aside first and those rows read from the copy - including rows only the undo
    /// history still holds - and saving the same list again writes the same file again. Rows keep
    /// their FilePath, which is what is shown and what "save over the original" offers.
    /// </remarks>
    private void PreserveSourceBeforeOverwrite(string outputPath)
    {
        var target = System.IO.Path.GetFullPath(outputPath);
        if (!System.IO.File.Exists(target)) return;

        var readers = _documentItems
            .Concat(_undo.SelectMany(state => state.Items))
            .Concat(_redo.SelectMany(state => state.Items))
            .Where(item => item.Type == DocumentType.PdfPage)
            .Distinct()
            .Where(item => string.Equals(System.IO.Path.GetFullPath(item.SourcePdfPath ?? item.FilePath), target, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (readers.Count == 0) return;

        System.IO.Directory.CreateDirectory(OriginalsDirectory);
        var copy = System.IO.Path.Combine(OriginalsDirectory, $"{Guid.NewGuid():N}.pdf");
        System.IO.File.Copy(target, copy);

        foreach (var item in readers) item.SourcePdfPath = copy;
        Log.Information("Saving over a source: {Count} row(s) of {Path} now read from {Copy}", readers.Count, target, copy);
    }

    /// <summary>Removes the copies a previous session left behind.</summary>
    private static void ClearOriginals()
    {
        if (!System.IO.Directory.Exists(OriginalsDirectory)) return;

        try
        {
            System.IO.Directory.Delete(OriginalsDirectory, recursive: true);
        }
        // Housekeeping: a copy still open somewhere is removed next time instead.
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Log.Debug(ex, "Leaving {Path} for next time", OriginalsDirectory);
        }
    }

    #endregion

    #region Typing

    private void SectionName_GotFocus(object sender, RoutedEventArgs e) => SetTextEditingShortcutsEnabled(false);

    /// <summary>
    /// Hands the keys that edit text to the text box while a split's name is being typed.
    /// </summary>
    /// <remarks>
    /// Delete, Ctrl+A, Ctrl+Z and Ctrl+Y all mean something to the page list and something
    /// else to a text box. Left enabled, Delete removed the page behind the name being typed.
    /// Disabling the accelerators, rather than checking focus when one fires, leaves the key
    /// to the text box whichever of the two would otherwise see it first.
    /// </remarks>
    private void SetTextEditingShortcutsEnabled(bool enabled)
    {
        foreach (var item in new[] { UndoMenuItem, RedoMenuItem, SelectAllMenuItem, RemoveMenuItem })
        {
            foreach (var accelerator in item.KeyboardAccelerators) accelerator.IsEnabled = enabled;
        }
    }

    #endregion
}

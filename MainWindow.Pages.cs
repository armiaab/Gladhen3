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

/// <summary>Page commands (turn, duplicate, insert, reverse, extract, remove) and undo.</summary>
public sealed partial class MainWindow
{
    /// <summary>Page commands, named by the Tag of the button or menu item.</summary>
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

    /// <summary>The page the shared context menu was opened on.</summary>
    private DocumentItem? _menuAnchor;

    private ListViewBase ActiveView => _isGridView ? DocumentGridView : DocumentListView;

    private static bool IsPage(DocumentItem item) => item.Type != DocumentType.SectionBreak;

    private static Dictionary<DocumentItem, int> BuildItemIndexMap(IReadOnlyList<DocumentItem> items)
    {
        var indexByItem = new Dictionary<DocumentItem, int>(items.Count);
        for (var i = 0; i < items.Count; i++) indexByItem[items[i]] = i;
        return indexByItem;
    }

    private void ReindexViewerPage()
    {
        if (!IsViewerOpen || _viewerItem == null) return;

        var pages = new List<DocumentItem>();
        var pageIndexByItem = new Dictionary<DocumentItem, int>();
        foreach (var item in _documentItems)
        {
            if (!IsPage(item)) continue;
            pageIndexByItem[item] = pages.Count;
            pages.Add(item);
        }

        if (pages.Count == 0)
        {
            CloseViewer();
            return;
        }

        if (!pageIndexByItem.TryGetValue(_viewerItem, out var index))
        {
            index = Math.Clamp(_viewerPosition, 0, pages.Count - 1);
            _viewerItem = pages[index];
        }

        _viewerPosition = index;
        UpdateViewerChrome(pages);
    }

    private void ReplaceAllDocumentItems(IReadOnlyList<DocumentItem> items, IReadOnlyCollection<DocumentItem>? selectionToRestore = null)
    {
        HashSet<DocumentItem>? selected = null;
        if (selectionToRestore is { Count: > 0 }) selected = [.. selectionToRestore];

        _documentItems.Clear();
        for (var i = 0; i < items.Count; i++) _documentItems.Add(items[i]);

        ReindexViewerPage();

        if (selected == null) return;

        DocumentGridView.SelectedItems.Clear();
        DocumentListView.SelectedItems.Clear();
        foreach (var item in _documentItems)
        {
            if (!selected.Contains(item)) continue;
            DocumentGridView.SelectedItems.Add(item);
            DocumentListView.SelectedItems.Add(item);
        }
    }

    #region Choosing pages

    /// <summary>The rows a command acts on, in list order.</summary>
    /// <remarks>
    /// The page on show while the viewer is open; otherwise the selection, unless the menu was
    /// opened on a page outside it, which then means that page alone. Section dividers count
    /// only for removal.
    /// </remarks>
    private List<DocumentItem> TargetItems(DocumentItem? anchor, bool includeSectionBreaks = false)
    {
        bool Wanted(DocumentItem item) => includeSectionBreaks || IsPage(item);

        if (anchor == null && _viewerItem != null) return [_viewerItem];

        var selected = SelectedItemsInOrder(includeSectionBreaks);
        if (selected.Count > 0)
        {
            var selectedSet = new HashSet<DocumentItem>(selected);
            if (anchor == null || selectedSet.Contains(anchor)) return selected;
        }

        if (anchor != null && Wanted(anchor))
        {
            var indexByItem = BuildItemIndexMap(_documentItems);
            if (indexByItem.ContainsKey(anchor)) return [anchor];
        }

        return [];
    }

    private List<DocumentItem> InListOrder(IEnumerable<DocumentItem> items)
    {
        var indexByItem = BuildItemIndexMap(_documentItems);
        var ordered = new List<(DocumentItem Item, int Index)>();

        foreach (var item in items)
        {
            if (indexByItem.TryGetValue(item, out var index)) ordered.Add((item, index));
        }

        ordered.Sort((left, right) => left.Index.CompareTo(right.Index));
        return ordered.Select(entry => entry.Item).ToList();
    }

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

        // Event-handler boundary: every caller is async void.
        try
        {
            await RunPageCommandAsync(command, anchor);

            // Show the result: the turned page, or the next one if it was removed.
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
            StatusTextBlock.Text = _resourceLoader.GetString(Pages.Count > 0 ? "StatusPickPageFirst" : "StatusNoPagesYet");
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

    /// <summary>Inserts copies as one run after the last page, so a duplicated chapter appears twice in order.</summary>
    private void Duplicate(List<DocumentItem> pages)
    {
        RecordUndo();

        var ordered = _documentItems.ToList();
        var indexByItem = BuildItemIndexMap(ordered);
        var insertAt = indexByItem[pages[^1]] + 1;

        var duplicates = new List<DocumentItem>(pages.Count);
        foreach (var page in pages) duplicates.Add(page.Clone());

        ordered.InsertRange(insertAt, duplicates);

        var selection = new HashSet<DocumentItem>(pages);
        foreach (var duplicate in duplicates) selection.Add(duplicate);

        ReplaceAllDocumentItems(ordered, selection);
        UpdateUIState();
        StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusDuplicatedPages"), pages.Count);
    }

    /// <summary>Inserts an empty page after each of these, sized like the page before it.</summary>
    private void InsertBlankAfter(List<DocumentItem> pages)
    {
        RecordUndo();

        var selected = new HashSet<DocumentItem>(pages);
        var name = _resourceLoader.GetString("BlankPageName");
        var ordered = new List<DocumentItem>(_documentItems.Count + pages.Count);

        foreach (var item in _documentItems)
        {
            ordered.Add(item);
            if (!selected.Contains(item)) continue;

            ordered.Add(new DocumentItem
            {
                Type = DocumentType.BlankPage,
                FileName = name
            });
        }

        ReplaceAllDocumentItems(ordered, selected);
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
    /// Reverses the selected pages within their own slots, or, with nothing chosen, every page
    /// within each section.
    /// </summary>
    /// <remarks>For stacks scanned face down.</remarks>
    private void ReversePages()
    {
        var selected = SelectedItemsInOrder();

        var ordered = _documentItems.ToList();
        int reversed;

        // As in Normalize: one clicked page is just focus, but a tick in select mode is a choice.
        if (selected.Count >= (_isSelectMode ? 1 : 2))
        {
            var indexByItem = BuildItemIndexMap(ordered);
            var slots = new List<int>(selected.Count);
            foreach (var item in selected) slots.Add(indexByItem[item]);

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
            reversed = Pages.Count;
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

    /// <summary>Puts the rows into <paramref name="ordered"/>'s order, keeping the same instances.</summary>
    private void ApplyOrder(IReadOnlyList<DocumentItem> ordered)
    {
        var selection = ActiveView.SelectedItems.OfType<DocumentItem>().ToList();
        ReplaceAllDocumentItems(ordered, selection);
    }

    private void SelectOddPages_Click(object sender, RoutedEventArgs e) => SelectAlternatePages(odd: true);

    private void SelectEvenPages_Click(object sender, RoutedEventArgs e) => SelectAlternatePages(odd: false);

    /// <summary>Selects every other page from the first; useful for duplex scans.</summary>
    private void SelectAlternatePages(bool odd)
    {
        if (Pages.Count == 0)
        {
            StatusTextBlock.Text = _resourceLoader.GetString("StatusNoPagesYet");
            return;
        }

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

    /// <summary>A list snapshot: rows, order, rotations and section names.</summary>
    private sealed record ListState(DocumentItem[] Items, int[] Rotations, string[] SectionNames);

    /// <remarks>Capped because each step keeps its rows, thumbnails included, alive.</remarks>
    private const int MaxUndoSteps = 20;

    private readonly List<ListState> _undo = [];
    private readonly List<ListState> _redo = [];

    /// <summary>The list when a drag started, recorded as an undo step if the drag moved anything.</summary>
    private ListState? _dragStart;

    private ListState CaptureState() => new(
        [.. _documentItems],
        [.. _documentItems.Select(item => item.Rotation)],
        [.. _documentItems.Select(item => item.SectionName)]);

    /// <summary>Records the current list as an undo step.</summary>
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
        // A running check would otherwise turn pages after the list is restored.
        StopNormalize();

        if (from.Count == 0)
        {
            StatusTextBlock.Text = _resourceLoader.GetString(emptyKey);
            return;
        }

        var state = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(CaptureState());

        // Event-handler boundary.
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

        // Same rows in a new order are moved; otherwise the list is rebuilt.
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

    /// <summary>Session-only copies of sources that were saved over.</summary>
    private static string OriginalsDirectory
        => System.IO.Path.Combine(Windows.Storage.ApplicationData.Current.TemporaryFolder.Path, "Originals");

    /// <summary>Copies a source aside before a save overwrites it, and points its rows at the copy.</summary>
    /// <remarks>
    /// After saving over the original, its rows would describe a file that already contains
    /// their edits, so saving again would apply them twice. Rows in the undo history are
    /// repointed too. FilePath is kept for display and for "save over the original".
    /// </remarks>
    /// <param name="pending">Snapshots a running save still has to write; they are repointed as well.</param>
    private void PreserveSourceBeforeOverwrite(string outputPath, IEnumerable<DocumentItem>? pending = null)
    {
        var target = System.IO.Path.GetFullPath(outputPath);
        if (!System.IO.File.Exists(target)) return;

        var readers = _documentItems
            .Concat(pending ?? [])
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

    /// <summary>Deletes copies left by a previous session.</summary>
    private static void ClearOriginals()
    {
        if (!System.IO.Directory.Exists(OriginalsDirectory)) return;

        try
        {
            System.IO.Directory.Delete(OriginalsDirectory, recursive: true);
        }
        // A copy still in use is removed next time.
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Log.Debug(ex, "Leaving {Path} for next time", OriginalsDirectory);
        }
    }

    #endregion

    #region Typing

    private void SectionName_GotFocus(object sender, RoutedEventArgs e)
    {
        SetTextEditingShortcutsEnabled(false);

        // Remember the list before the rename; recorded on lost focus if the name changed.
        _renameStart = sender is FrameworkElement { DataContext: DocumentItem { Type: DocumentType.SectionBreak } band }
            ? (band, band.SectionName, CaptureState())
            : null;
    }

    /// <summary>The section being renamed, its old name, and the list before the edit.</summary>
    private (DocumentItem Band, string Name, ListState State)? _renameStart;

    /// <summary>Disables the list's editing shortcuts while a section name is being typed.</summary>
    /// <remarks>Otherwise Delete, Ctrl+A, Ctrl+Z and Ctrl+Y act on the pages, not the text.</remarks>
    private void SetTextEditingShortcutsEnabled(bool enabled)
    {
        foreach (var item in new[] { UndoMenuItem, RedoMenuItem, SelectAllMenuItem, RemoveMenuItem })
        {
            foreach (var accelerator in item.KeyboardAccelerators) accelerator.IsEnabled = enabled;
        }
    }

    #endregion
}

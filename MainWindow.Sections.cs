using Gladhen3.Models;
using Gladhen3.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;
using WinRT.Interop;

namespace Gladhen3;

/// <summary>
/// Splitting one page list into several PDFs.
/// </summary>
/// <remarks>
/// The page list stays flat and the dividers live in it as ordinary rows, so dragging a
/// divider moves a split point and dragging a page past one re-files it - both handled by
/// the reorder the list already had. Nothing here keeps a second ordering in step with the
/// first, because there is no second ordering.
/// </remarks>
public sealed partial class MainWindow
{
    /// <summary>True once the list is cut into more than one output document.</summary>
    private bool HasSections => _documentItems.Any(d => d.Type == DocumentType.SectionBreak);

    private IEnumerable<DocumentItem> Pages => _documentItems.Where(d => d.Type != DocumentType.SectionBreak);

    /// <summary>The last row that held keyboard focus.</summary>
    /// <remarks>
    /// Opening a menu takes focus away from the list, so by the time a Split command runs
    /// nothing in the list is focused any more and asking the focus manager returns a menu
    /// item. The row the user last put the caret on is what they still mean.
    /// </remarks>
    private DocumentItem? _lastFocusedItem;

    #region Section maintenance

    private static DocumentItem CreateSectionBreak(string name) => new()
    {
        Type = DocumentType.SectionBreak,
        FileName = name,
        SectionName = name
    };

    /// <summary>
    /// Re-derives the bands after a structural change, and may insert a divider.
    /// </summary>
    /// <remarks>
    /// Must not be called from inside <c>CollectionChanged</c>: the views are subscribed to
    /// the same collection, and <see cref="System.Collections.ObjectModel.ObservableCollection{T}"/>
    /// refuses a mutation raised from its own notification once more than one handler is
    /// attached. <see cref="UpdateSectionSummaries"/> is the half that is safe there.
    /// </remarks>
    private void RefreshSections()
    {
        EnsureLeadingBreak();
        NameUnnamedSections();
        UpdateSectionSummaries();
    }

    /// <summary>
    /// Gives the pages above the first divider a band of their own.
    /// </summary>
    /// <remarks>
    /// Dragging the topmost divider down the list is an ordinary thing to do, and it leaves a
    /// run of pages that is still an output file but has nothing to name it. Rather than
    /// inventing a nameless file at save time, the run gets a real divider it can be renamed
    /// on. Nothing happens when the list is not split at all.
    /// </remarks>
    private void EnsureLeadingBreak()
    {
        if (_documentItems.Count == 0) return;
        if (!HasSections) return;
        if (_documentItems[0].Type == DocumentType.SectionBreak) return;

        _documentItems.Insert(0, CreateSectionBreak(string.Empty));
    }

    /// <summary>
    /// Names every band that has not been named yet, in one pass.
    /// </summary>
    /// <remarks>
    /// One pass, and only from <see cref="RefreshSections"/>. Whether a section needs a part
    /// number depends on what the other sections turned out to be, so naming each band as it
    /// is inserted - which is what happens if this runs on every collection change - hands
    /// every one of them the same "report 1". A name the user typed is never touched.
    /// </remarks>
    private void NameUnnamedSections()
    {
        var sections = PdfSection.Split(_documentItems);

        var suggestions = new string[sections.Count];
        var occurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < sections.Count; i++)
        {
            suggestions[i] = PdfSection.SuggestName(sections[i].Items);
            if (suggestions[i].Length == 0) continue;
            occurrences[suggestions[i]] = occurrences.GetValueOrDefault(suggestions[i]) + 1;
        }

        var parts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < sections.Count; i++)
        {
            if (sections[i].BreakItem is not { } band) continue;

            var suggested = suggestions[i];

            var part = 0;
            if (suggested.Length > 0 && occurrences[suggested] > 1)
            {
                part = parts.GetValueOrDefault(suggested) + 1;
                parts[suggested] = part;
            }

            if (!string.IsNullOrWhiteSpace(band.SectionName)) continue;

            band.SectionName = suggested.Length == 0
                ? $"{_resourceLoader.GetString("SectionDefaultNamePrefix")} {i + 1}"
                : part > 0 ? $"{suggested} {part}" : suggested;
        }
    }

    /// <summary>
    /// Refreshes the page count on each band.
    /// </summary>
    /// <remarks>
    /// Touches item properties only, never the collection, so unlike the rest of
    /// <see cref="RefreshSections"/> this is safe to call from a CollectionChanged handler -
    /// which is where it has to run, so the counts follow a drag as it happens.
    /// </remarks>
    private void UpdateSectionSummaries()
    {
        foreach (var section in PdfSection.Split(_documentItems))
        {
            if (section.BreakItem is not { } band) continue;

            band.SectionSummary = section.IsEmpty
                ? _resourceLoader.GetString("SectionEmpty")
                : string.Format(_resourceLoader.GetString("SectionPageCount"), section.Items.Count);

            if (section.IsEmpty) band.SectionEstimate = string.Empty;
        }
    }

    /// <summary>Where the dividers sit, counted in pages rather than rows.</summary>
    private List<int> CurrentBreakPagePositions()
    {
        var positions = new List<int>();
        var pageIndex = 0;

        foreach (var item in _documentItems)
        {
            if (item.Type == DocumentType.SectionBreak) positions.Add(pageIndex);
            else pageIndex++;
        }

        return positions.Distinct().ToList();
    }

    private void RemoveAllBreaks()
    {
        for (var i = _documentItems.Count - 1; i >= 0; i--)
        {
            if (_documentItems[i].Type == DocumentType.SectionBreak)
                _documentItems.RemoveAt(i);
        }
    }

    /// <summary>
    /// Rebuilds the dividers so that one starts at each of <paramref name="pagePositions"/>.
    /// </summary>
    /// <remarks>
    /// Positions are page offsets, not row indices, so callers never have to reason about the
    /// dividers they are themselves inserting. With the old ones gone the two coincide, and
    /// inserting back to front keeps the earlier offsets valid.
    /// </remarks>
    private void ApplyBreakPositions(IEnumerable<int> pagePositions)
    {
        var pageCount = Pages.Count();

        var wanted = pagePositions
            .Where(p => p >= 0 && p < pageCount)
            .Distinct()
            .OrderByDescending(p => p)
            .ToList();

        RemoveAllBreaks();

        foreach (var position in wanted)
            _documentItems.Insert(position, CreateSectionBreak(string.Empty));

        RefreshSections();
        UpdateUIState();
    }

    /// <summary>
    /// Walks up from a visual to the row it belongs to.
    /// </summary>
    /// <remarks>
    /// Uses the visual tree, not <see cref="FrameworkElement.Parent"/>: inside a generated
    /// item container the logical parent is frequently null, so a walk up Parent gives up
    /// before it reaches the ListViewItem that carries the DocumentItem.
    /// </remarks>
    private static DocumentItem? ItemFromVisual(object? source)
    {
        var current = source as DependencyObject;

        while (current != null)
        {
            if (current is FrameworkElement { DataContext: DocumentItem context }) return context;

            if (current is ContentControl { Content: DocumentItem content }) return content;

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    /// <summary>The row the user is acting on: the selection if there is one, else the focus.</summary>
    private DocumentItem? CurrentTargetItem()
    {
        var view = _isGridView ? (ListViewBase)DocumentGridView : DocumentListView;

        if (_isSelectMode && view.SelectedItems.Count > 0)
        {
            DocumentItem? earliest = null;
            var earliestIndex = int.MaxValue;

            foreach (var selected in view.SelectedItems.OfType<DocumentItem>())
            {
                var index = _documentItems.IndexOf(selected);
                if (index >= 0 && index < earliestIndex)
                {
                    earliestIndex = index;
                    earliest = selected;
                }
            }

            if (earliest != null) return earliest;
        }

        var focused = ItemFromVisual(FocusManager.GetFocusedElement(Content.XamlRoot));
        if (focused != null) return focused;

        return _lastFocusedItem != null && _documentItems.Contains(_lastFocusedItem)
            ? _lastFocusedItem
            : null;
    }

    private void DocumentView_GotFocus(object sender, RoutedEventArgs e)
    {
        if (ItemFromVisual(e.OriginalSource) is { } item) _lastFocusedItem = item;
    }

    #endregion

    #region Split commands

    private void SplitHere_Click(object sender, RoutedEventArgs e)
    {
        if (!Pages.Any())
        {
            StatusTextBlock.Text = _resourceLoader.GetString("StatusNoPagesToSplit");
            return;
        }

        var target = CurrentTargetItem();
        if (target == null || target.Type == DocumentType.SectionBreak)
        {
            StatusTextBlock.Text = _resourceLoader.GetString("StatusPickPageToSplit");
            return;
        }

        var position = Pages.ToList().IndexOf(target);
        if (position <= 0)
        {
            StatusTextBlock.Text = _resourceLoader.GetString("StatusPickPageToSplit");
            return;
        }

        var positions = CurrentBreakPagePositions();
        positions.Add(position);
        positions.Add(0);

        ApplyBreakPositions(positions);
        StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusSplitInto"), PdfSection.Split(_documentItems).Count(s => !s.IsEmpty));
    }

    private void SplitBySource_Click(object sender, RoutedEventArgs e)
    {
        var pages = Pages.ToList();
        if (pages.Count == 0)
        {
            StatusTextBlock.Text = _resourceLoader.GetString("StatusNoPagesToSplit");
            return;
        }

        var positions = new List<int> { 0 };
        string? previous = null;

        for (var i = 0; i < pages.Count; i++)
        {
            var source = pages[i].Type == DocumentType.PdfPage
                ? pages[i].SourcePdfPath ?? pages[i].FilePath
                : pages[i].FilePath;

            if (i > 0 && !string.Equals(source, previous, StringComparison.OrdinalIgnoreCase))
                positions.Add(i);

            previous = source;
        }

        ApplyBreakPositions(positions);
        StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusSplitInto"), positions.Distinct().Count());
    }

    private async void SplitEveryN_Click(object sender, RoutedEventArgs e)
    {
        var pageCount = Pages.Count();
        if (pageCount < 2)
        {
            StatusTextBlock.Text = _resourceLoader.GetString("StatusNoPagesToSplit");
            return;
        }

        var perFile = await PromptForCountAsync(
            _resourceLoader.GetString("DialogTitleSplitEveryN"),
            _resourceLoader.GetString("DialogLabelPagesPerFile"),
            1, pageCount, Math.Min(1, pageCount));

        if (perFile == null) return;

        var positions = new List<int>();
        for (var i = 0; i < pageCount; i += perFile.Value) positions.Add(i);

        ApplyBreakPositions(positions);
        StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusSplitInto"), positions.Count);
    }

    private async void SplitIntoN_Click(object sender, RoutedEventArgs e)
    {
        var pageCount = Pages.Count();
        if (pageCount < 2)
        {
            StatusTextBlock.Text = _resourceLoader.GetString("StatusNoPagesToSplit");
            return;
        }

        var fileCount = await PromptForCountAsync(
            _resourceLoader.GetString("DialogTitleSplitIntoN"),
            _resourceLoader.GetString("DialogLabelNumberOfFiles"),
            2, pageCount, Math.Min(2, pageCount));

        if (fileCount == null) return;

        var positions = new List<int>();
        var baseSize = pageCount / fileCount.Value;
        var remainder = pageCount % fileCount.Value;
        var cursor = 0;

        for (var i = 0; i < fileCount.Value; i++)
        {
            positions.Add(cursor);
            cursor += baseSize + (i < remainder ? 1 : 0);
        }

        ApplyBreakPositions(positions);
        StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusSplitInto"), positions.Count);
    }

    private void RemoveAllSplits_Click(object sender, RoutedEventArgs e)
    {
        if (!HasSections)
        {
            StatusTextBlock.Text = _resourceLoader.GetString("StatusNoSplitsToRemove");
            return;
        }

        RemoveAllBreaks();
        UpdateUIState();
        StatusTextBlock.Text = _resourceLoader.GetString("StatusSplitsRemoved");
    }

    private void RemoveSectionBreak_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DocumentItem band }) return;
        if (band.Type != DocumentType.SectionBreak) return;

        _documentItems.Remove(band);
        RefreshSections();
        UpdateUIState();
        StatusTextBlock.Text = _resourceLoader.GetString("StatusSplitRemoved");
    }

    /// <summary>
    /// Puts a name back when the box is left empty.
    /// </summary>
    /// <remarks>
    /// An empty band would be saved as "Document 3", which is not what an empty box suggests
    /// will happen. Filling it in on the way out makes the file name visible before the save.
    /// </remarks>
    private void SectionName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DocumentItem band }) return;
        if (band.Type != DocumentType.SectionBreak) return;
        if (!string.IsNullOrWhiteSpace(band.SectionName)) return;

        band.SectionName = string.Empty;
        NameUnnamedSections();
        UpdateSectionSummaries();
    }

    private async Task<int?> PromptForCountAsync(string title, string label, int minimum, int maximum, int initial)
    {
        var input = new NumberBox
        {
            Header = label,
            Value = Math.Clamp(initial, minimum, maximum),
            Minimum = minimum,
            Maximum = maximum,
            SmallChange = 1,
            LargeChange = 5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
        };

        var dialog = new ContentDialog
        {
            Title = title,
            Content = input,
            PrimaryButtonText = _resourceLoader.GetString("DialogButtonOK"),
            CloseButtonText = _resourceLoader.GetString("DialogButtonCancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };

        if (await Dialogs.DialogHost.ShowAsync(dialog) != ContentDialogResult.Primary) return null;

        if (double.IsNaN(input.Value)) return null;

        var value = (int)Math.Round(input.Value);
        return value < minimum || value > maximum ? null : value;
    }

    #endregion

    #region Keyboard reorder

    /// <summary>
    /// Moves the focused row with Ctrl+Shift+Up/Down.
    /// </summary>
    /// <remarks>
    /// Dragging was the only way to change the order, which made it unreachable without a
    /// mouse and painful over a long list. It matters more now than it did: with the list cut
    /// into several documents, a row position decides which file it ends up in.
    ///
    /// This is the tunnelling PreviewKeyDown rather than KeyDown because a ListViewItem
    /// consumes the arrow keys for focus navigation and marks them handled, so a bubbling
    /// handler on the list never sees them.
    /// </remarks>
    private void DocumentView_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Up && e.Key != VirtualKey.Down) return;

        var keyboard = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        if (!keyboard.HasFlag(CoreVirtualKeyStates.Down) || !shift.HasFlag(CoreVirtualKeyStates.Down)) return;

        var item = ItemFromVisual(e.OriginalSource) ?? CurrentTargetItem();
        if (item == null) return;

        _lastFocusedItem = item;

        var index = _documentItems.IndexOf(item);
        if (index < 0) return;

        var target = e.Key == VirtualKey.Up ? index - 1 : index + 1;
        if (target < 0 || target >= _documentItems.Count) return;

        _documentItems.Move(index, target);
        e.Handled = true;

        RefreshSections();

        var view = sender as ListViewBase ?? (_isGridView ? DocumentGridView : DocumentListView);
        view.UpdateLayout();
        (view.ContainerFromItem(item) as SelectorItem)?.Focus(FocusState.Keyboard);
        view.ScrollIntoView(item);

        StatusTextBlock.Text = _resourceLoader.GetString("StatusReorderComplete");
    }

    #endregion

    #region Saving several documents

    private async Task<string?> PickOutputFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        StorageFolder folder;
        try
        {
            folder = await picker.PickSingleFolderAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Folder picker failed");
            await ShowDialogAsync(
                _resourceLoader.GetString("DialogTitleSaveFailed"),
                _resourceLoader.GetString("DialogContentSaveFailedPicker"));
            StatusTextBlock.Text = _resourceLoader.GetString("StatusSaveCancelled");
            return null;
        }

        if (folder == null)
        {
            StatusTextBlock.Text = _resourceLoader.GetString("StatusCancelled");
            return null;
        }

        return folder.Path;
    }

    /// <summary>
    /// Writes one PDF per section into a folder the user picks.
    /// </summary>
    /// <remarks>
    /// Failures are collected rather than raised as they happen. One unwritable destination
    /// out of ten must not abandon the other nine, and because every dialog in this app is
    /// serialised behind <see cref="Dialogs.DialogHost"/>, reporting each one separately would
    /// make the user dismiss a queue of them. There is exactly one summary at the end.
    /// </remarks>
    private async Task SaveSectionsAsync(List<PdfSection> sections)
    {
        var folder = await PickOutputFolderAsync();
        if (folder == null) return;

        var names = PdfSection.ResolveFileNames(sections, _resourceLoader.GetString("SectionDefaultNamePrefix"));

        var existing = new List<string>();
        for (var i = 0; i < sections.Count; i++)
        {
            if (File.Exists(Path.Combine(folder, names[i]))) existing.Add(names[i]);
        }

        if (existing.Count > 0)
        {
            var overwrite = new ContentDialog
            {
                Title = _resourceLoader.GetString("DialogTitleOverwriteFiles"),
                Content = string.Format(
                    _resourceLoader.GetString("DialogContentOverwriteFiles"),
                    existing.Count,
                    FormatNameList(existing)),
                PrimaryButtonText = _resourceLoader.GetString("DialogButtonReplace"),
                CloseButtonText = _resourceLoader.GetString("DialogButtonCancel"),
                XamlRoot = Content.XamlRoot
            };

            if (await Dialogs.DialogHost.ShowAsync(overwrite) != ContentDialogResult.Primary)
            {
                StatusTextBlock.Text = _resourceLoader.GetString("StatusCancelled");
                return;
            }
        }

        SaveButton.IsEnabled = false;
        BusyBar.Visibility = Visibility.Visible;

        var written = new List<string>();
        var failed = new List<string>();
        var skippedItems = new List<string>();

        try
        {
            for (var i = 0; i < sections.Count; i++)
            {
                var path = Path.Combine(folder, names[i]);
                StatusTextBlock.Text = string.Format(
                    _resourceLoader.GetString("StatusSavingSection"), i + 1, sections.Count, names[i]);

                try
                {
                    var result = await Task.Run(() => _pdfService.CreatePdfFromDocuments(sections[i].Items, path));
                    written.Add(names[i]);
                    foreach (var skipped in result.SkippedItems) skippedItems.Add(skipped);
                }
                catch (PdfOperationException ex)
                {
                    Log.Warning(ex, "Could not write section {Name} ({Reason})", names[i], ex.Reason);
                    failed.Add($"{names[i]} - {DescribeFailure(ex).Title}");
                }
                catch (UnauthorizedAccessException ex)
                {
                    Log.Warning(ex, "Access denied writing section {Name}", names[i]);
                    failed.Add($"{names[i]} - {_resourceLoader.GetString("DialogTitleAccessDenied")}");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Unexpected failure writing section {Name}", names[i]);
                    failed.Add($"{names[i]} - {_resourceLoader.GetString("DialogTitleError")}");
                }
            }
        }
        finally
        {
            SaveButton.IsEnabled = true;
            BusyBar.Visibility = Visibility.Collapsed;
        }

        Log.Information("Split save finished: {Written} written, {Failed} failed, into {Folder}",
            written.Count, failed.Count, folder);

        await ReportSectionOutcomeAsync(folder, written, failed, skippedItems);
    }

    private async Task ReportSectionOutcomeAsync(
        string folder,
        List<string> written,
        List<string> failed,
        List<string> skippedItems)
    {
        if (failed.Count == 0)
        {
            StatusTextBlock.Text = string.Format(_resourceLoader.GetString("StatusSectionsSaved"), written.Count);
            await ShowDialogAsync(
                _resourceLoader.GetString("DialogTitleSuccess"),
                string.Format(_resourceLoader.GetString("DialogContentSectionsSaved"), written.Count, folder));
        }
        else if (written.Count == 0)
        {
            StatusTextBlock.Text = _resourceLoader.GetString("StatusError").Replace("{0}", _resourceLoader.GetString("DialogTitleSaveFailed"));
            await ShowDialogAsync(
                _resourceLoader.GetString("DialogTitleSaveFailed"),
                string.Format(_resourceLoader.GetString("DialogContentSectionsAllFailed"), FormatNameList(failed)));
        }
        else
        {
            StatusTextBlock.Text = string.Format(
                _resourceLoader.GetString("StatusSectionsPartlySaved"), written.Count, failed.Count);
            await ShowDialogAsync(
                _resourceLoader.GetString("DialogTitleSomeFilesSkipped"),
                string.Format(
                    _resourceLoader.GetString("DialogContentSectionsPartlySaved"),
                    written.Count, folder, FormatNameList(failed)));
        }

        if (skippedItems.Count > 0)
        {
            Log.Warning("{Count} item(s) omitted across the split save", skippedItems.Count);
            await ShowDialogAsync(
                _resourceLoader.GetString("DialogTitleSomeFilesSkipped"),
                string.Format(_resourceLoader.GetString("DialogContentPagesSkipped"), FormatNameList(skippedItems)));
        }
    }

    #endregion
}

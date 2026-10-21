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

public sealed partial class MainWindow
{
    private bool HasSections => _documentItems.Any(d => d.Type == DocumentType.SectionBreak);
    private IEnumerable<DocumentItem> Pages => _documentItems.Where(d => d.Type != DocumentType.SectionBreak);
    private DocumentItem? _lastFocusedItem;

    #region Section maintenance

    private static DocumentItem CreateSectionBreak(string name) => new()
    {
        Type = DocumentType.SectionBreak,
        FileName = name,
        SectionName = name
    };

    private void RefreshSections()
    {
        EnsureLeadingBreak();
        NameUnnamedSections();
        UpdateSectionSummaries();
    }

    private void EnsureLeadingBreak()
    {
        if (_documentItems.Count == 0) return;
        if (!HasSections) return;
        if (_documentItems[0].Type == DocumentType.SectionBreak) return;

        _documentItems.Insert(0, CreateSectionBreak(string.Empty));
    }

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

    private void ApplyBreakPositions(IEnumerable<int> pagePositions)
    {
        RecordUndo();

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

    private DocumentItem? CurrentTargetItem()
    {
        var view = _isGridView ? (ListViewBase)DocumentGridView : DocumentListView;

        if (view.SelectedItems.Count > 0)
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

    private void SplitHere_Click(object sender, RoutedEventArgs e) => SplitBefore(CurrentTargetItem());

    private void SplitBefore(DocumentItem? target)
    {
        if (!Pages.Any())
        {
            StatusTextBlock.Text = _resourceLoader.GetString("StatusNoPagesToSplit");
            return;
        }

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
            // An inserted blank page has no file of its own; it stays with the one it follows.
            if (pages[i].Type == DocumentType.BlankPage) continue;

            // FilePath: the file a page belongs to, even once it is read from a saved-over copy.
            var source = pages[i].FilePath;

            if (previous != null && !string.Equals(source, previous, StringComparison.OrdinalIgnoreCase))
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

        RecordUndo();
        RemoveAllBreaks();
        UpdateUIState();
        StatusTextBlock.Text = _resourceLoader.GetString("StatusSplitsRemoved");
    }

    private void RemoveSectionBreak_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DocumentItem band }) return;
        if (band.Type != DocumentType.SectionBreak) return;

        RecordUndo();
        _documentItems.Remove(band);
        RefreshSections();
        UpdateUIState();
        StatusTextBlock.Text = _resourceLoader.GetString("StatusSplitRemoved");
    }

    private void SectionName_LostFocus(object sender, RoutedEventArgs e)
    {
        SetTextEditingShortcutsEnabled(true);

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

    private async void DocumentView_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Enter opens the page in the viewer. Ctrl+Enter is "split here", and Enter typed into
        // a split's name lands on a divider, which is not a page - both are left alone.
        if (e.Key == VirtualKey.Enter &&
            !InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down) &&
            ItemFromVisual(e.OriginalSource) is { } page && IsPage(page))
        {
            e.Handled = true;
            await OpenViewerAsync(page);
            return;
        }

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

        RecordUndo();
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

    private async Task SaveSectionsAsync(List<PdfSection> sections)
    {
        var folder = await PickOutputFolderAsync();
        if (folder == null) return;

        var names = PdfSection.ResolveFileNames(sections, _resourceLoader.GetString("SectionDefaultNamePrefix"));

        var existing = new List<string>();
        for (var i = 0; i < sections.Count; i++)
            if (File.Exists(Path.Combine(folder, names[i]))) existing.Add(names[i]);

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
        SetBusy(true);

        var written = new List<string>();
        var failed = new List<string>();
        var skippedItems = new List<string>();
        var codecFailures = 0;

        try
        {
            for (var i = 0; i < sections.Count; i++)
            {
                var path = Path.Combine(folder, names[i]);
                StatusTextBlock.Text = string.Format(
                    _resourceLoader.GetString("StatusSavingSection"), i + 1, sections.Count, names[i]);

                try
                {
                    // One file of the split may overwrite a source the later ones still read.
                    PreserveSourceBeforeOverwrite(path);
                    var result = await Task.Run(() => _pdfService.CreatePdfFromDocuments(sections[i].Items, path));
                    written.Add(names[i]);
                    foreach (var skipped in result.SkippedItems) skippedItems.Add(skipped);
                    codecFailures += result.CodecFailures;
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
            SetBusy(false);
        }

        Log.Information("Split save finished: {Written} written, {Failed} failed, into {Folder}",
            written.Count, failed.Count, folder);

        await ReportSectionOutcomeAsync(folder, written, failed, skippedItems);
        await ReportCodecFailuresAsync(codecFailures);
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

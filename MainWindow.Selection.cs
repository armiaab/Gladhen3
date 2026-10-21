using Gladhen3.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Collections.Generic;
using System.Linq;
using Windows.System;

namespace Gladhen3;

/// <summary>
/// Letting go of a selection the way Windows does: Esc, or a click on empty space.
/// </summary>
/// <remarks>
/// Choosing pages needs no code: the lists are in Extended mode, which gives click, Ctrl+click,
/// Shift+click, Shift+arrow and Ctrl+A as every Windows list has them. Select mode only swaps
/// that for check boxes.
/// </remarks>
public sealed partial class MainWindow
{
    /// <summary>Clears the selection, and leaves select mode if it was on.</summary>
    private void ClearSelection()
    {
        if (_isSelectMode) SelectModeToggle.IsChecked = false;

        DocumentGridView.SelectedItems.Clear();
        DocumentListView.SelectedItems.Clear();
        StatusTextBlock.Text = _resourceLoader.GetString("StatusDeselectedAll");
    }

    /// <summary>Esc lets go of the selection, wherever the focus is in the main window.</summary>
    /// <remarks>
    /// At the root and on the way down, so that a toolbar button that has just been clicked
    /// does not swallow it. Esc already means something else in two places, which keep it: the
    /// viewer, where it goes back to the list, and a split's name, where it is the text box's.
    /// Dialogs and menus are popups outside this tree and never send it here.
    /// </remarks>
    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || IsViewerOpen) return;
        if (FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox) return;
        if (!_isSelectMode && ActiveView.SelectedItems.Count == 0) return;

        ClearSelection();
        e.Handled = true;
    }

    /// <summary>A click on empty space between or after the pages lets go of the selection.</summary>
    private void DocumentView_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not ListViewBase view || view.SelectedItems.Count == 0) return;
        if (ItemFromVisual(e.OriginalSource) != null) return;

        // The scroll bar is part of the list too, and dragging it is not a click on nothing.
        for (var current = e.OriginalSource as DependencyObject; current != null && current != view; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ScrollBar) return;
        }

        view.SelectedItems.Clear();
        StatusTextBlock.Text = _resourceLoader.GetString("StatusDeselectedAll");
    }

    /// <summary>Shift+F10 or the menu key opens the page menu for the page with the focus.</summary>
    /// <remarks>
    /// The menu hangs on each page's template, inside its list container. A right-click lands on
    /// the template and finds it; the keyboard asks the focused container, which has none, so
    /// the menu could not be reached from the keyboard at all.
    /// </remarks>
    private void DocumentView_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (e.Handled || sender is not ListViewBase view) return;
        if (ItemFromVisual(e.OriginalSource) is not { } item || !IsPage(item)) return;
        if (view.ContainerFromItem(item) is not FrameworkElement container) return;

        var menu = (MenuFlyout)((FrameworkElement)Content).Resources["PageMenu"];
        menu.ShowAt(container, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft });
        e.Handled = true;
    }

    /// <summary>The selected pages, in list order; section dividers only when asked for.</summary>
    private List<DocumentItem> SelectedItemsInOrder(bool includeSectionBreaks = false)
        => InListOrder(ActiveView.SelectedItems.OfType<DocumentItem>().Where(item => includeSectionBreaks || IsPage(item)));
}

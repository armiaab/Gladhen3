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

/// <summary>Clearing the selection: Esc, or a click on empty space.</summary>
/// <remarks>Selecting itself is the lists' built-in Extended mode.</remarks>
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

    /// <summary>Esc clears the selection anywhere in the main window.</summary>
    /// <remarks>
    /// Handled on the way down so a focused toolbar button cannot swallow it. The viewer and a
    /// section-name box keep their own meaning for Esc.
    /// </remarks>
    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || IsViewerOpen) return;
        if (FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox) return;
        if (!_isSelectMode && ActiveView.SelectedItems.Count == 0) return;

        ClearSelection();
        e.Handled = true;
    }

    /// <summary>A click on empty space in a list clears the selection.</summary>
    private void DocumentView_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not ListViewBase view || view.SelectedItems.Count == 0) return;
        if (ItemFromVisual(e.OriginalSource) != null) return;

        // Dragging the scroll bar is not a click on empty space.
        for (var current = e.OriginalSource as DependencyObject; current != null && current != view; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ScrollBar) return;
        }

        view.SelectedItems.Clear();
        StatusTextBlock.Text = _resourceLoader.GetString("StatusDeselectedAll");
    }

    /// <summary>Shift+F10 or the menu key opens the page menu for the focused page.</summary>
    /// <remarks>The menu belongs to the item template, which the focused container does not expose.</remarks>
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

using Gladhen3.Models;
using Gladhen3.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;

namespace Gladhen3;


/// <summary>The page viewer: one page at a time, any zoom, with the page commands.</summary>
/// <remarks>
/// Part of the window rather than a ContentDialog, which WinUI caps at 548 px wide. It
/// re-renders whenever the zoom needs more pixels.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001",
    Justification = "_viewerLoadCts is disposed in CancelViewerLoad each time it is replaced and when the viewer closes; a Window is not IDisposable.")]
public sealed partial class MainWindow
{
    private enum ViewerFit { Page, Width, Free }

    /// <summary>Largest render edge: about 430 dpi on A4, already ~100 MB as a bitmap.</summary>
    private const uint MaxRenderEdge = 5000;

    /// <summary>The margin round the page, in the units the zoom applies to.</summary>
    private const double SheetMargin = 24;

    private const double ZoomStep = 1.25;

    /// <summary>The page on show, or null while the viewer is closed.</summary>
    private DocumentItem? _viewerItem;

    /// <summary>The page's position, so removing it moves to the page that took its place.</summary>
    private int _viewerPosition;

    private ViewerFit _viewerFit = ViewerFit.Page;

    /// <summary>The zoom the viewer last set itself, to tell it from one the user chose.</summary>
    private double _viewerAppliedZoom = 1;

    /// <summary>Bumped on every page shown; a render finishing after that is discarded.</summary>
    private int _viewerVersion;

    /// <summary>UI thread only; disposed by <see cref="CancelViewerLoad"/>.</summary>
    private CancellationTokenSource? _viewerLoadCts;

    /// <summary>The long edge of the bitmap on show, and the most worth rendering.</summary>
    private uint _viewerRenderedEdge;
    private uint _viewerMaxEdge;

    /// <summary>The render in progress: which page version and how large.</summary>
    private (int Version, uint Edge) _viewerInFlight;

    private bool _syncingFilmstrip;

    /// <summary>Source PDFs opened while the viewer is open, so paging does not reload them.</summary>
    private readonly Dictionary<string, Task<Windows.Data.Pdf.PdfDocument>> _viewerDocumentTasks = new(StringComparer.OrdinalIgnoreCase);

    private bool IsViewerOpen => _viewerItem != null;

    #region Opening and closing

    private async Task OpenViewerAsync(DocumentItem item)
    {
        if (!IsPage(item) || !_documentItems.Contains(item)) return;

        Filmstrip.ItemsSource ??= _documentItems;
        ViewerPanel.Visibility = Visibility.Visible;

        // "Remove selected" belongs to the page list.
        SelectionInfoPanel.Visibility = Visibility.Collapsed;

        _viewerFit = ViewerFit.Page;
        ViewerPanel.UpdateLayout();

        await ShowInViewerAsync(item, samePage: false);
        ViewerScroll.Focus(FocusState.Programmatic);
    }

    private void CloseViewer()
    {
        var last = _viewerItem;
        _viewerItem = null;
        _viewerVersion++;
        CancelViewerLoad();

        ViewerPanel.Visibility = Visibility.Collapsed;
        ViewerProgress.IsActive = false;
        ReleaseViewerImage();
        // Windows.Data.Pdf.PdfDocument has no Close/Dispose; dropping the references releases it.
        lock (_viewerDocumentTasks)
        {
            _viewerDocumentTasks.Clear();
        }

        // Return to the page that was on show.
        if (last != null && _documentItems.Contains(last))
        {
            _lastFocusedItem = last;
            var view = ActiveView;

            // Select it too, unless the user had several pages selected.
            if (!_isSelectMode && view.SelectedItems.Count <= 1) view.SelectedItem = last;

            view.ScrollIntoView(last);
            view.UpdateLayout();
            (view.ContainerFromItem(last) as SelectorItem)?.Focus(FocusState.Programmatic);
        }

        UpdateSelectionInfo();
    }

    private void ViewerBack_Click(object sender, RoutedEventArgs e) => CloseViewer();

    /// <summary>Cancels the load in progress and disposes its source.</summary>
    /// <remarks>Cancelled before disposal, so a render still holding the token just sees it cancelled.</remarks>
    private void CancelViewerLoad()
    {
        var cts = _viewerLoadCts;
        if (cts == null) return;

        _viewerLoadCts = null;
        cts.Cancel();
        cts.Dispose();
    }

    private async Task RenderViewerPageSafelyAsync(int version, double zoom)
    {
        var item = _viewerItem;
        if (item == null || version != _viewerVersion || _viewerMaxEdge == 0) return;

        var cancellationToken = _viewerLoadCts?.Token ?? CancellationToken.None;
        try
        {
            await RenderViewerPageAsync(item, version, zoom, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer page or zoom.
        }
        catch (Exception ex)
        {
            if (version == _viewerVersion && ReferenceEquals(_viewerItem, item))
                Log.Warning(ex, "Viewer could not render {Name} page {Page}", item.FileName, item.PageNumber);
        }
    }

    /// <summary>Shows the page again after a command, or its replacement if it was removed.</summary>
    private async Task RefreshViewerAsync()
    {
        if (_viewerItem == null) return;

        var pages = Pages;
        if (pages.Count == 0)
        {
            CloseViewer();
            return;
        }

        var stillThere = pages.Contains(_viewerItem);
        var item = stillThere ? _viewerItem : pages[Math.Clamp(_viewerPosition, 0, pages.Count - 1)];
        await ShowInViewerAsync(item, samePage: stillThere);
    }

    #endregion

    #region Showing a page

    private async Task ShowInViewerAsync(DocumentItem item, bool samePage)
    {
        var version = ++_viewerVersion;
        CancelViewerLoad();
        var loadCts = _viewerLoadCts = new CancellationTokenSource();
        var cancellationToken = loadCts.Token;
        _viewerItem = item;

        var pages = Pages;
        var position = 0;
        while (position < pages.Count && !ReferenceEquals(pages[position], item)) position++;
        _viewerPosition = Math.Min(position, Math.Max(0, pages.Count - 1));
        UpdateViewerChrome(pages);

        ReleaseViewerImage();
        ViewerMessage.Visibility = Visibility.Collapsed;
        ViewerProgress.IsActive = true;

        // A page that will not render says so in place; the viewer keeps working.
        try
        {
            var (width, height, maxEdge) = await MeasureViewerPageAsync(item, cancellationToken);
            if (cancellationToken.IsCancellationRequested || version != _viewerVersion || !ReferenceEquals(_viewerItem, item)) return;

            _viewerRenderedEdge = 0;
            _viewerMaxEdge = maxEdge;
            ViewerSheet.Width = width;
            ViewerSheet.Height = height;
            ViewerScroll.UpdateLayout();

            var zoom = _viewerFit == ViewerFit.Free ? ViewerScroll.ZoomFactor : ApplyViewerFit();
            if (!samePage) ViewerScroll.ChangeView(0, 0, null, disableAnimation: true);

            if (item.Type == DocumentType.BlankPage)
            {
                ShowViewerMessage(_resourceLoader.GetString("BlankPageName"));
                return;
            }

            await RenderViewerPageAsync(item, version, zoom, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Superseded by another page or cancelled.
        }
        catch (Exception ex)
        {
            if (version != _viewerVersion || !ReferenceEquals(_viewerItem, item)) return;

            Log.Warning(ex, "Viewer could not show {Name} page {Page}", item.FileName, item.PageNumber);
            ReleaseViewerImage();
            ShowViewerMessage(_resourceLoader.GetString("ViewerPageNotShown"));
        }
        finally
        {
            if (version == _viewerVersion) ViewerProgress.IsActive = false;
        }
    }

    private void ShowViewerMessage(string text)
    {
        ViewerMessage.Text = text;
        ViewerMessage.Visibility = Visibility.Visible;
    }

    private void UpdateViewerChrome(IReadOnlyList<DocumentItem> pages)
    {
        var item = _viewerItem!;

        ViewerTitle.Text = item.Type == DocumentType.BlankPage ? _resourceLoader.GetString("BlankPageName") : item.FileName;
        ToolTipService.SetToolTip(ViewerTitle, string.IsNullOrEmpty(item.FilePath) ? null : item.FilePath);

        var details = item.Type switch
        {
            DocumentType.PdfPage => string.Format(_resourceLoader.GetString("ViewerDetailsPdfPage"), item.PageNumber, item.TotalPages, item.FileSize),
            DocumentType.BlankPage => _resourceLoader.GetString("ViewerDetailsBlank"),
            _ => item.FileSize
        };
        if (item.Rotation != 0)
            details += " · " + string.Format(_resourceLoader.GetString("ViewerDetailsTurned"), item.Rotation);
        ViewerDetails.Text = details;

        ViewerCounter.Text = string.Format(_resourceLoader.GetString("ViewerCounterFormat"), _viewerPosition + 1, pages.Count);
        ViewerPreviousButton.IsEnabled = _viewerPosition > 0;
        ViewerNextButton.IsEnabled = _viewerPosition < pages.Count - 1;
        ViewerOpenButton.IsEnabled = item.Type != DocumentType.BlankPage && File.Exists(item.FilePath);

        UpdateFitToggles();
        SyncFilmstrip();
    }

    /// <summary>The page's turned size at 100%, and the most pixels worth rendering.</summary>
    private async Task<(double Width, double Height, uint MaxEdge)> MeasureViewerPageAsync(DocumentItem item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        double width, height;
        uint maxEdge;

        switch (item.Type)
        {
            case DocumentType.PdfPage:
            {
                var document = await ViewerDocumentAsync(item, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                using var page = document.GetPage((uint)(item.PageNumber - 1));

                // In DIPs, already turned by the page's own /Rotate.
                (width, height) = (page.Size.Width, page.Size.Height);
                maxEdge = MaxRenderEdge;
                break;
            }

            case DocumentType.Image:
            {
                // Not StorageFile: see OffUiStorage.
                using var stream = await OffUiStorage.OpenReadAsync(item.FilePath, cancellationToken);
                var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
                (width, height) = (decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
                maxEdge = Math.Min(MaxRenderEdge, Math.Max(decoder.PixelWidth, decoder.PixelHeight));
                break;
            }

            default:
                // A4 stands in; when saved, a blank page takes the size of the page before it.
                (width, height) = (210 / 25.4 * 96, 297 / 25.4 * 96);
                maxEdge = 0;
                break;
        }

        return item.Rotation is 90 or 270 ? (height, width, maxEdge) : (width, height, maxEdge);
    }

    private async Task<Windows.Data.Pdf.PdfDocument> ViewerDocumentAsync(DocumentItem item, CancellationToken cancellationToken)
    {
        var path = item.SourcePdfPath ?? item.FilePath;
        Task<Windows.Data.Pdf.PdfDocument> loadTask;
        lock (_viewerDocumentTasks)
        {
            if (!_viewerDocumentTasks.TryGetValue(path, out loadTask!) || loadTask.IsFaulted)
            {
                // Shared by every page of the file, so it is not cancelled with one page.
                loadTask = OffUiStorage.LoadPdfAsync(path, CancellationToken.None);
                _viewerDocumentTasks[path] = loadTask;
            }
        }
        return await loadTask.WaitAsync(cancellationToken);
    }

    /// <summary>Renders the page sharp at <paramref name="zoom"/>, unless the current bitmap already is.</summary>
    private async Task RenderViewerPageAsync(DocumentItem item, int version, double zoom, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || version != _viewerVersion || !ReferenceEquals(_viewerItem, item)) return;
        if (item.Type == DocumentType.BlankPage || _viewerMaxEdge == 0) return;

        // Never below the layout size: the ScrollViewer zooms after layout, so a smaller bitmap
        // would be resampled twice and text would blur.
        var scale = ViewerScroll.XamlRoot?.RasterizationScale ?? 1.0;
        var needed = Math.Max(ViewerSheet.Width, ViewerSheet.Height) * Math.Max(zoom, 1.0) * scale;
        var edge = (uint)Math.Clamp(Math.Ceiling(needed), 64, _viewerMaxEdge);

        // Skip renders that gain only a few pixels, or that one already in progress covers.
        if (_viewerRenderedEdge > 0 && edge <= _viewerRenderedEdge * 1.1) return;
        if (_viewerInFlight.Version == version && _viewerInFlight.Edge >= edge) return;

        _viewerInFlight = (version, edge);
        SoftwareBitmap bitmap;
        try
        {
            bitmap = await RenderPageBitmapAsync(item, edge, cancellationToken);
        }
        finally
        {
            if (_viewerInFlight == (version, edge)) _viewerInFlight = default;
        }

        if (cancellationToken.IsCancellationRequested || version != _viewerVersion || !ReferenceEquals(_viewerItem, item))
        {
            bitmap.Dispose();
            return;
        }

        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(bitmap).AsTask(cancellationToken);
        bitmap.Dispose();

        if (cancellationToken.IsCancellationRequested || version != _viewerVersion || !ReferenceEquals(_viewerItem, item))
        {
            source.Dispose();
            return;
        }

        var previous = ViewerImage.Source as SoftwareBitmapSource;
        ViewerImage.Source = source;
        previous?.Dispose();
        _viewerRenderedEdge = edge;
        Log.Debug("Viewer rendered {Edge}px for a {Width}x{Height} sheet at {Zoom:P0} zoom, scale {Scale}",
            edge, ViewerSheet.Width, ViewerSheet.Height, zoom, scale);
    }

    /// <summary>The page, turned as it will be saved, <paramref name="edge"/> pixels on its long side.</summary>
    private async Task<SoftwareBitmap> RenderPageBitmapAsync(DocumentItem item, uint edge, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var rotation = item.Rotation switch
        {
            90 => BitmapRotation.Clockwise90Degrees,
            180 => BitmapRotation.Clockwise180Degrees,
            270 => BitmapRotation.Clockwise270Degrees,
            _ => BitmapRotation.None
        };

        if (item.Type == DocumentType.PdfPage)
        {
            var document = await ViewerDocumentAsync(item, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            using var page = document.GetPage((uint)(item.PageNumber - 1));
            var scale = edge / Math.Max(page.Size.Width, page.Size.Height);

            using var rendered = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(rendered, new Windows.Data.Pdf.PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, Math.Round(page.Size.Width * scale)),
                DestinationHeight = (uint)Math.Max(1, Math.Round(page.Size.Height * scale)),
                // BMP: no compression cost either way.
                BitmapEncoderId = BitmapEncoder.BmpEncoderId
            }).AsTask(cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            var decoder = await BitmapDecoder.CreateAsync(rendered).AsTask(cancellationToken);
            return await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform { Rotation = rotation },
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);
        }

        using var stream = await OffUiStorage.OpenReadAsync(item.FilePath, cancellationToken);
        var imageDecoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);

        // Scale from the stored size; a uniform scale is unaffected by the EXIF turn.
        var fit = Math.Min(1.0, edge / (double)Math.Max(imageDecoder.PixelWidth, imageDecoder.PixelHeight));
        return await imageDecoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, Math.Round(imageDecoder.PixelWidth * fit)),
                ScaledHeight = (uint)Math.Max(1, Math.Round(imageDecoder.PixelHeight * fit)),
                InterpolationMode = BitmapInterpolationMode.Fant,
                Rotation = rotation
            },
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb).AsTask(cancellationToken);
    }

    /// <remarks>Disposed explicitly: the pixels are unmanaged memory the GC does not see.</remarks>
    private void ReleaseViewerImage()
    {
        if (ViewerImage.Source is SoftwareBitmapSource source)
        {
            ViewerImage.Source = null;
            source.Dispose();
        }
        _viewerRenderedEdge = 0;
        _viewerMaxEdge = 0;
        _viewerInFlight = default;
    }

    #endregion

    #region Moving between pages

    private async Task StepViewerAsync(int delta)
    {
        var pages = Pages;
        if (pages.Count == 0) return;

        var target = pages[Math.Clamp(_viewerPosition + delta, 0, pages.Count - 1)];
        if (target != _viewerItem) await ShowInViewerAsync(target, samePage: false);
    }

    private async void ViewerPrevious_Click(object sender, RoutedEventArgs e) => await StepViewerAsync(-1);

    private async void ViewerNext_Click(object sender, RoutedEventArgs e) => await StepViewerAsync(1);

    private async void Filmstrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingFilmstrip || !IsViewerOpen) return;

        if (Filmstrip.SelectedItem is DocumentItem item && IsPage(item) && item != _viewerItem)
            await ShowInViewerAsync(item, samePage: false);
        else
            SyncFilmstrip(); // a divider was clicked: the selection goes back to the page on show
    }

    private void SyncFilmstrip()
    {
        _syncingFilmstrip = true;
        try
        {
            Filmstrip.SelectedItem = _viewerItem;
            if (_viewerItem != null) Filmstrip.ScrollIntoView(_viewerItem);
        }
        finally
        {
            _syncingFilmstrip = false;
        }
    }

    /// <summary>Page keys, handled before the ScrollViewer can use Left/Right to scroll.</summary>
    private async void ViewerPanel_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!IsViewerOpen) return;

        var control = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        var fitsAcross = ViewerScroll.ScrollableWidth < 1;

        switch (e.Key)
        {
            case VirtualKey.Escape:
                e.Handled = true;
                CloseViewer();
                break;

            case VirtualKey.PageDown:
            case VirtualKey.Right when fitsAcross && !control:
                e.Handled = true;
                await StepViewerAsync(1);
                break;

            case VirtualKey.PageUp:
            case VirtualKey.Left when fitsAcross && !control:
                e.Handled = true;
                await StepViewerAsync(-1);
                break;

            case VirtualKey.Home when !control:
                e.Handled = true;
                await StepViewerAsync(int.MinValue / 2);
                break;

            case VirtualKey.End when !control:
                e.Handled = true;
                await StepViewerAsync(int.MaxValue / 2);
                break;

            case VirtualKey.Add:
            case (VirtualKey)0xBB: // = and +
                e.Handled = true;
                ZoomViewerBy(ZoomStep);
                break;

            case VirtualKey.Subtract:
            case (VirtualKey)0xBD: // - and _
                e.Handled = true;
                ZoomViewerBy(1 / ZoomStep);
                break;

            case VirtualKey.Number0 when control:
            case VirtualKey.NumberPad0 when control:
                e.Handled = true;
                SetViewerFit(ViewerFit.Page);
                break;
        }
    }

    #endregion

    #region Zoom

    /// <summary>Zooms to fit the page or its width, and returns the zoom it chose.</summary>
    private double ApplyViewerFit()
    {
        var viewportWidth = ViewerScroll.ViewportWidth > 0 ? ViewerScroll.ViewportWidth : ViewerScroll.ActualWidth;
        var viewportHeight = ViewerScroll.ViewportHeight > 0 ? ViewerScroll.ViewportHeight : ViewerScroll.ActualHeight;
        if (viewportWidth <= 0 || viewportHeight <= 0 || ViewerSheet.Width <= 0) return ViewerScroll.ZoomFactor;

        var across = viewportWidth / (ViewerSheet.Width + (2 * SheetMargin));
        var down = viewportHeight / (ViewerSheet.Height + (2 * SheetMargin));
        var zoom = _viewerFit == ViewerFit.Width ? across : Math.Min(across, down);
        zoom = Math.Clamp(zoom, ViewerScroll.MinZoomFactor, ViewerScroll.MaxZoomFactor);

        _viewerAppliedZoom = zoom;
        ViewerScroll.ChangeView(0, 0, (float)zoom, disableAnimation: true);
        UpdateZoomText(zoom);
        return zoom;
    }

    private void SetViewerFit(ViewerFit fit)
    {
        _viewerFit = fit;
        UpdateFitToggles();
        var zoom = ApplyViewerFit();
        _ = RenderViewerPageSafelyAsync(_viewerVersion, zoom);
    }

    /// <summary>Zooms about the centre of the view.</summary>
    private void ZoomViewerBy(double factor)
    {
        var current = ViewerScroll.ZoomFactor;
        var zoom = Math.Clamp(current * factor, ViewerScroll.MinZoomFactor, ViewerScroll.MaxZoomFactor);

        var centreX = (ViewerScroll.HorizontalOffset + (ViewerScroll.ViewportWidth / 2)) / current;
        var centreY = (ViewerScroll.VerticalOffset + (ViewerScroll.ViewportHeight / 2)) / current;

        _viewerFit = ViewerFit.Free;
        UpdateFitToggles();
        ViewerScroll.ChangeView(
            Math.Max(0, (centreX * zoom) - (ViewerScroll.ViewportWidth / 2)),
            Math.Max(0, (centreY * zoom) - (ViewerScroll.ViewportHeight / 2)),
            (float)zoom);
    }

    private void ViewerZoomIn_Click(object sender, RoutedEventArgs e) => ZoomViewerBy(ZoomStep);

    private void ViewerZoomOut_Click(object sender, RoutedEventArgs e) => ZoomViewerBy(1 / ZoomStep);

    private void ViewerFitPage_Click(object sender, RoutedEventArgs e) => SetViewerFit(ViewerFit.Page);

    private void ViewerFitWidth_Click(object sender, RoutedEventArgs e) => SetViewerFit(ViewerFit.Width);

    /// <summary>Double-click zooms in on that spot; again fits the page.</summary>
    private void ViewerSheet_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;

        if (_viewerFit == ViewerFit.Free)
        {
            SetViewerFit(ViewerFit.Page);
            return;
        }

        var point = e.GetPosition(ViewerSheet);
        var zoom = Math.Min(ViewerScroll.MaxZoomFactor, ViewerScroll.ZoomFactor * 2.5);

        _viewerFit = ViewerFit.Free;
        UpdateFitToggles();
        ViewerScroll.ChangeView(
            Math.Max(0, ((point.X + SheetMargin) * zoom) - (ViewerScroll.ViewportWidth / 2)),
            Math.Max(0, ((point.Y + SheetMargin) * zoom) - (ViewerScroll.ViewportHeight / 2)),
            (float)zoom);
    }

    private void ViewerScroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!IsViewerOpen) return;

        var zoom = ViewerScroll.ZoomFactor;
        UpdateZoomText(zoom);
        if (e.IsIntermediate) return;

        // Ctrl+wheel or pinch: the user's zoom, so stop fitting.
        if (_viewerFit != ViewerFit.Free && Math.Abs(zoom - _viewerAppliedZoom) > 0.01)
        {
            _viewerFit = ViewerFit.Free;
            UpdateFitToggles();
        }

        _ = RenderViewerPageSafelyAsync(_viewerVersion, zoom);
    }

    private void ViewerScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsViewerOpen || _viewerFit == ViewerFit.Free) return;

        var zoom = ApplyViewerFit();
        _ = RenderViewerPageSafelyAsync(_viewerVersion, zoom);
    }

    private void UpdateZoomText(double zoom) => ViewerZoomText.Text = $"{Math.Round(zoom * 100)}%";

    private void UpdateFitToggles()
    {
        ViewerFitPageToggle.IsChecked = _viewerFit == ViewerFit.Page;
        ViewerFitWidthToggle.IsChecked = _viewerFit == ViewerFit.Width;
    }

    #endregion

    private async void ViewerOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_viewerItem is not { } item || string.IsNullOrEmpty(item.FilePath)) return;

        // Event-handler boundary; report the failure so the button does not seem dead.
        try
        {
            var file = await OffUiStorage.GetFileAsync(item.FilePath);
            await Launcher.LaunchFileAsync(file);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not open {Path} in another application", item.FilePath);
            StatusTextBlock.Text = _resourceLoader.GetString("StatusUnexpectedError");
        }
    }
}

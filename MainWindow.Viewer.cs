using Gladhen3.Models;
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
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;

namespace Gladhen3;

/// <summary>
/// The page viewer: one page at a time, at any zoom, with every page command to hand.
/// </summary>
/// <remarks>
/// It replaced a ContentDialog, which WinUI caps at 548 pixels wide whatever MinWidth says: a
/// landscape page came out as a cropped portrait sliver, the arrow keys went to the scroll
/// viewer instead of the next page, and it rendered at a fixed 800 pixels, so zooming in only
/// made it blurrier. This is part of the window instead, re-renders whenever the zoom needs
/// more pixels than it has, and shows a turned page turned.
/// </remarks>
public sealed partial class MainWindow
{
    private enum ViewerFit { Page, Width, Free }

    /// <summary>
    /// Past this a render costs more memory than the sharpness is worth: 5000 pixels on the
    /// long edge of an A4 page is about 430 dpi, and already 100 MB as a bitmap.
    /// </summary>
    private const uint MaxRenderEdge = 5000;

    /// <summary>The margin round the page, in the units the zoom applies to.</summary>
    private const double SheetMargin = 24;

    private const double ZoomStep = 1.25;

    /// <summary>The page on show, or null while the viewer is closed.</summary>
    private DocumentItem? _viewerItem;

    /// <summary>
    /// Where that page sat among the pages, so that removing it moves on to the page that
    /// took its place rather than back to the start.
    /// </summary>
    private int _viewerPosition;

    private ViewerFit _viewerFit = ViewerFit.Page;

    /// <summary>The zoom the viewer last set itself, to tell it apart from one the user chose.</summary>
    private double _viewerAppliedZoom = 1;

    /// <summary>Bumped on every page shown; a render that finishes after that is thrown away.</summary>
    private int _viewerVersion;

    /// <summary>The long edge, in pixels, of the bitmap on show, and the most it is worth rendering.</summary>
    private uint _viewerRenderedEdge;
    private uint _viewerMaxEdge;

    /// <summary>The render under way, if any: which page showing it is for, and how large.</summary>
    private (int Version, uint Edge) _viewerInFlight;

    private bool _syncingFilmstrip;

    /// <summary>Source PDFs opened for the viewer, kept while it is open so paging through one is not a reload per page.</summary>
    private readonly Dictionary<string, Windows.Data.Pdf.PdfDocument> _viewerDocuments = new(StringComparer.OrdinalIgnoreCase);

    private bool IsViewerOpen => _viewerItem != null;

    #region Opening and closing

    private async Task OpenViewerAsync(DocumentItem item)
    {
        if (!IsPage(item) || !_documentItems.Contains(item)) return;

        Filmstrip.ItemsSource ??= _documentItems;
        ViewerPanel.Visibility = Visibility.Visible;

        // The bottom bar's "remove selected" button belongs to the page list, not to this.
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

        ViewerPanel.Visibility = Visibility.Collapsed;
        SelectionInfoPanel.Visibility = _isSelectMode ? Visibility.Visible : Visibility.Collapsed;
        ViewerProgress.IsActive = false;
        ReleaseViewerImage();
        _viewerDocuments.Clear();

        // Back to the page that was on show, so closing does not lose the place in a long list.
        if (last == null || !_documentItems.Contains(last)) return;

        _lastFocusedItem = last;
        var view = ActiveView;
        view.ScrollIntoView(last);
        view.UpdateLayout();
        (view.ContainerFromItem(last) as SelectorItem)?.Focus(FocusState.Programmatic);
    }

    private void ViewerBack_Click(object sender, RoutedEventArgs e) => CloseViewer();

    /// <summary>Shows the page again after a command, or the one that took its place if it went.</summary>
    private async Task RefreshViewerAsync()
    {
        if (_viewerItem == null) return;

        var pages = Pages.ToList();
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
        _viewerItem = item;

        var pages = Pages.ToList();
        _viewerPosition = Math.Max(0, pages.IndexOf(item));
        UpdateViewerChrome(pages);

        ViewerMessage.Visibility = Visibility.Collapsed;
        ViewerProgress.IsActive = true;

        // Per-item isolation for a view: a page that will not render - a damaged PDF, a file
        // moved away since it was added - says so in place, and the viewer and every other page
        // keep working.
        try
        {
            var (width, height, maxEdge) = await MeasureViewerPageAsync(item);
            if (version != _viewerVersion) return;

            ReleaseViewerImage();
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

            await RenderViewerPageAsync(version, zoom);
        }
        catch (Exception ex)
        {
            if (version != _viewerVersion) return;

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

    private void UpdateViewerChrome(List<DocumentItem> pages)
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

    /// <summary>The page's size at 100% as it will be seen - turned - and the most pixels worth rendering of it.</summary>
    private async Task<(double Width, double Height, uint MaxEdge)> MeasureViewerPageAsync(DocumentItem item)
    {
        double width, height;
        uint maxEdge;

        switch (item.Type)
        {
            case DocumentType.PdfPage:
            {
                var document = await ViewerDocumentAsync(item);
                using var page = document.GetPage((uint)(item.PageNumber - 1));

                // Already in DIPs, and already turned by the page's own /Rotate.
                (width, height) = (page.Size.Width, page.Size.Height);
                maxEdge = MaxRenderEdge;
                break;
            }

            case DocumentType.Image:
            {
                var file = await StorageFile.GetFileFromPathAsync(item.FilePath);
                using var stream = await file.OpenReadAsync();
                var decoder = await BitmapDecoder.CreateAsync(stream);
                (width, height) = (decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
                maxEdge = Math.Min(MaxRenderEdge, Math.Max(decoder.PixelWidth, decoder.PixelHeight));
                break;
            }

            default:
                // A blank page takes the size of the page before it when saved; A4 stands in here.
                (width, height) = (210 / 25.4 * 96, 297 / 25.4 * 96);
                maxEdge = 0;
                break;
        }

        return item.Rotation is 90 or 270 ? (height, width, maxEdge) : (width, height, maxEdge);
    }

    private async Task<Windows.Data.Pdf.PdfDocument> ViewerDocumentAsync(DocumentItem item)
    {
        var path = item.SourcePdfPath ?? item.FilePath;
        if (_viewerDocuments.TryGetValue(path, out var document)) return document;

        var file = await StorageFile.GetFileFromPathAsync(path);
        document = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
        _viewerDocuments[path] = document;
        return document;
    }

    /// <summary>Renders the page sharp at <paramref name="zoom"/>, unless what is on show already is.</summary>
    private async Task RenderViewerPageAsync(int version, double zoom)
    {
        var item = _viewerItem;
        if (item == null || item.Type == DocumentType.BlankPage || _viewerMaxEdge == 0) return;

        // Never fewer pixels than the sheet's own layout size, even zoomed out. The image is laid
        // out at 100% and the scroll viewer's zoom shrinks it afterwards, so a bitmap rendered at
        // the zoomed-out size was first stretched up to 100% and then shrunk back down - two
        // resamples, and small text came out visibly soft. Rendered at 100% or more, it is
        // only ever scaled once.
        var scale = ViewerScroll.XamlRoot?.RasterizationScale ?? 1.0;
        var needed = Math.Max(ViewerSheet.Width, ViewerSheet.Height) * Math.Max(zoom, 1.0) * scale;
        var edge = (uint)Math.Clamp(Math.Ceiling(needed), 64, _viewerMaxEdge);

        // Re-rendering for a few pixels more is wasted work that the eye cannot see. Nor is a
        // second render started while one at least as large is still under way: showing a page
        // and the zoom settling both ask for one, a few milliseconds apart.
        if (_viewerRenderedEdge > 0 && edge <= _viewerRenderedEdge * 1.1) return;
        if (_viewerInFlight.Version == version && _viewerInFlight.Edge >= edge) return;

        _viewerInFlight = (version, edge);
        SoftwareBitmap bitmap;
        try
        {
            bitmap = await RenderPageBitmapAsync(item, edge);
        }
        finally
        {
            if (_viewerInFlight == (version, edge)) _viewerInFlight = default;
        }
        if (version != _viewerVersion)
        {
            bitmap.Dispose();
            return;
        }

        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(bitmap);
        bitmap.Dispose();

        if (version != _viewerVersion)
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

    /// <summary>The page, turned as it will be saved, with <paramref name="edge"/> pixels on its long side.</summary>
    private async Task<SoftwareBitmap> RenderPageBitmapAsync(DocumentItem item, uint edge)
    {
        var rotation = item.Rotation switch
        {
            90 => BitmapRotation.Clockwise90Degrees,
            180 => BitmapRotation.Clockwise180Degrees,
            270 => BitmapRotation.Clockwise270Degrees,
            _ => BitmapRotation.None
        };

        if (item.Type == DocumentType.PdfPage)
        {
            var document = await ViewerDocumentAsync(item);
            using var page = document.GetPage((uint)(item.PageNumber - 1));
            var scale = edge / Math.Max(page.Size.Width, page.Size.Height);

            using var rendered = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(rendered, new Windows.Data.Pdf.PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, Math.Round(page.Size.Width * scale)),
                DestinationHeight = (uint)Math.Max(1, Math.Round(page.Size.Height * scale)),
                // BMP: nothing to compress on the way out and nothing to inflate on the way back.
                BitmapEncoderId = BitmapEncoder.BmpEncoderId
            });

            var decoder = await BitmapDecoder.CreateAsync(rendered);
            return await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform { Rotation = rotation },
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
        }

        var file = await StorageFile.GetFileFromPathAsync(item.FilePath);
        using var stream = await file.OpenReadAsync();
        var imageDecoder = await BitmapDecoder.CreateAsync(stream);

        // Scaled from the stored size, before any EXIF turn - a uniform scale, so the turn does
        // not change it.
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
            ColorManagementMode.ColorManageToSRgb);
    }

    /// <remarks>
    /// Disposed rather than left to the collector: the pixels live in unmanaged memory the GC
    /// cannot see, and paging through a scan would otherwise pile up hundreds of megabytes.
    /// </remarks>
    private void ReleaseViewerImage()
    {
        if (ViewerImage.Source is SoftwareBitmapSource source)
        {
            ViewerImage.Source = null;
            source.Dispose();
        }
        _viewerRenderedEdge = 0;
    }

    #endregion

    #region Moving between pages

    private async Task StepViewerAsync(int delta)
    {
        var pages = Pages.ToList();
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

    /// <summary>
    /// Page keys for the viewer, taken before the scroll viewer sees them - otherwise Left and
    /// Right scroll a page that already fits and never turn it.
    /// </summary>
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
        _ = RenderViewerPageAsync(_viewerVersion, zoom);
    }

    /// <summary>Zooms about the middle of what is on screen, so the part being read stays put.</summary>
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

    /// <summary>Double-click to look closer at that spot, and again to see the whole page.</summary>
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

        // Ctrl+wheel or a pinch: the zoom is the user's now, and fitting stops.
        if (_viewerFit != ViewerFit.Free && Math.Abs(zoom - _viewerAppliedZoom) > 0.01)
        {
            _viewerFit = ViewerFit.Free;
            UpdateFitToggles();
        }

        _ = RenderViewerPageAsync(_viewerVersion, zoom);
    }

    private void ViewerScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsViewerOpen || _viewerFit == ViewerFit.Free) return;

        var zoom = ApplyViewerFit();
        _ = RenderViewerPageAsync(_viewerVersion, zoom);
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

        // Event-handler boundary: nothing visible happens if this fails, so the user is told
        // rather than left wondering whether the button works.
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.FilePath);
            await Launcher.LaunchFileAsync(file);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not open {Path} in another application", item.FilePath);
            StatusTextBlock.Text = _resourceLoader.GetString("StatusUnexpectedError");
        }
    }
}

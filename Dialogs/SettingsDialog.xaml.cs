using Gladhen3.Models;
using Gladhen3.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Gladhen3.Dialogs;

public sealed partial class SettingsDialog : ContentDialog
{
    private readonly Microsoft.Windows.ApplicationModel.Resources.ResourceLoader _resourceLoader = new();
    private readonly nint _ownerWindow;
    private UnitSize _previousUnit;
    private UnitSize _previousMarginUnit;

    /// <param name="ownerWindow">The window the codec file picker belongs to.</param>
    public SettingsDialog(nint ownerWindow)
    {
        _ownerWindow = ownerWindow;
        InitializeComponent();
        LoadSettings();
    }

    private void LoadSettings()
    {
        PaperSizeCombo.SelectedIndex = (int)AppSettings.Current.PaperSize;
        OrientationCombo.SelectedIndex = (int)AppSettings.Current.Orientation;
        MarginCombo.SelectedIndex = (int)AppSettings.Current.Margin;
        CompressionCombo.SelectedIndex = (int)AppSettings.Current.ImageCompression;
        PopulateMethods(AppSettings.Current.ImageCodec);

        // Matched on the language alone: Windows stores "id-ID" back as "id".
        var language = PrimarySubtag(Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride);
        LanguageCombo.SelectedItem = LanguageCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => PrimarySubtag((string)i.Tag) == language)
            ?? LanguageCombo.Items[0];

        UnitCombo.SelectedIndex = (int)AppSettings.Current.CustomSizeUnit;
        _previousUnit = (UnitSize)UnitCombo.SelectedIndex;

        WidthBox.Value = AppSettings.Current.CustomWidth;
        HeightBox.Value = AppSettings.Current.CustomHeight;

        MarginUnitCombo.SelectedIndex = (int)AppSettings.Current.CustomMarginUnit;
        _previousMarginUnit = (UnitSize)MarginUnitCombo.SelectedIndex;

        LeftMarginBox.Value = AppSettings.Current.CustomMarginLeft;
        RightMarginBox.Value = AppSettings.Current.CustomMarginRight;
        TopMarginBox.Value = AppSettings.Current.CustomMarginTop;
        BottomMarginBox.Value = AppSettings.Current.CustomMarginBottom;

        UpdateUnitLabels();
    }

    private void PaperSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomSizePanel != null)
        {
            CustomSizePanel.Visibility = PaperSizeCombo.SelectedIndex == 5 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void MarginCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomMarginPanel != null)
        {
            CustomMarginPanel.Visibility = MarginCombo.SelectedIndex == 5 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void UpdateUnitLabels()
    {
        var unitName = (UnitSize)UnitCombo.SelectedIndex switch
        {
            UnitSize.Millimetre => _resourceLoader.GetString("UnitMillimetre"),
            UnitSize.Inch => _resourceLoader.GetString("UnitInch"),
            UnitSize.Point => _resourceLoader.GetString("UnitPoint"),
            _ => _resourceLoader.GetString("UnitMillimetre")
        };
        if (WidthUnitLabel != null) WidthUnitLabel.Text = unitName;
        if (HeightUnitLabel != null) HeightUnitLabel.Text = unitName;
    }

    private void UnitCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WidthBox == null) return;

        UpdateUnitLabels();

        var newUnitIndex = UnitCombo.SelectedIndex;
        var newUnit = (UnitSize)newUnitIndex;
        if (newUnit != _previousUnit)
        {
            WidthBox.Value = ConvertValue(WidthBox.Value, _previousUnit, newUnit);
            HeightBox.Value = ConvertValue(HeightBox.Value, _previousUnit, newUnit);
            _previousUnit = newUnit;
        }
    }

    private void MarginUnitCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LeftMarginBox == null) return;

        var newUnit = (UnitSize)MarginUnitCombo.SelectedIndex;
        if (newUnit != _previousMarginUnit)
        {
            LeftMarginBox.Value = ConvertValue(LeftMarginBox.Value, _previousMarginUnit, newUnit);
            RightMarginBox.Value = ConvertValue(RightMarginBox.Value, _previousMarginUnit, newUnit);
            TopMarginBox.Value = ConvertValue(TopMarginBox.Value, _previousMarginUnit, newUnit);
            BottomMarginBox.Value = ConvertValue(BottomMarginBox.Value, _previousMarginUnit, newUnit);
            _previousMarginUnit = newUnit;
        }
    }

    private static double ConvertValue(double value, UnitSize fromUnit, UnitSize toUnit)
    {
        if (fromUnit == toUnit) return value;
        const double mmPerInch = 25.4;
        const double ptPerInch = 72.0;

        var valueInMm = fromUnit switch
        {
            UnitSize.Millimetre => value,
            UnitSize.Inch => value * mmPerInch,
            UnitSize.Point => value * (mmPerInch / ptPerInch),
            _ => value
        };

        return toUnit switch
        {
            UnitSize.Millimetre => valueInMm,
            UnitSize.Inch => valueInMm / mmPerInch,
            UnitSize.Point => valueInMm * (ptPerInch / mmPerInch),
            _ => valueInMm
        };
    }

    #region Compression method

    private void CompressionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // With no compression nothing is re-encoded, so there is no method to choose.
        if (MethodCombo != null)
            MethodCombo.IsEnabled = CompressionCombo.SelectedIndex != (int)PdfImageCompression.None;
    }

    /// <summary>Fills the method list: the built-ins, then every installed plugin.</summary>
    private void PopulateMethods(string selectedId)
    {
        MethodCombo.Items.Clear();
        MethodCombo.Items.Add(new ComboBoxItem { Content = _resourceLoader.GetString("SettingsMethodJpeg"), Tag = ImageCodecs.JpegId });
        MethodCombo.Items.Add(new ComboBoxItem { Content = _resourceLoader.GetString("SettingsMethodLossless"), Tag = ImageCodecs.LosslessId });

        var plugins = new List<ImageCodecInfo>();
        try
        {
            plugins.AddRange(ImageCodecs.ListPlugins());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not read the codec folder {Path}", ImageCodecs.PluginDirectory);
        }

        // A plugin chosen earlier but since deleted from outside the app is still shown, marked,
        // so that the setting does not quietly turn into JPEG behind the user's back.
        if (ImageCodecs.IsPlugin(selectedId) && !plugins.Exists(p => p.Id == selectedId))
            plugins.Add(new ImageCodecInfo(selectedId, selectedId[ImageCodecs.PluginPrefix.Length..], null, null, CodecFailureReason.NotFound));

        foreach (var plugin in plugins)
        {
            var item = new ComboBoxItem
            {
                Content = plugin.LoadFailure == null
                    ? string.Format(_resourceLoader.GetString("SettingsMethodPluginFormat"), plugin.Name)
                    : string.Format(_resourceLoader.GetString("SettingsMethodUnavailableFormat"), plugin.FileName),
                Tag = plugin.Id,
                DataContext = plugin
            };
            ToolTipService.SetToolTip(item, plugin.Filter == null ? plugin.FileName : $"{plugin.FileName} ({plugin.Filter})");
            MethodCombo.Items.Add(item);
        }

        MethodCombo.SelectedItem = MethodCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == selectedId)
            ?? MethodCombo.Items[0];
    }

    private ImageCodecInfo? SelectedPlugin => (MethodCombo.SelectedItem as ComboBoxItem)?.DataContext as ImageCodecInfo;

    private void MethodCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var plugin = SelectedPlugin;
        RemoveCodecButton.IsEnabled = plugin != null;

        if (plugin?.LoadFailure is { } failure)
        {
            ShowCodecMessage(InfoBarSeverity.Warning, string.Format(
                _resourceLoader.GetString("SettingsCodecUnusable"), plugin.FileName, CodecText.Reason(_resourceLoader, failure)));
        }
        else if (CodecInfoBar.Severity == InfoBarSeverity.Warning)
        {
            CodecInfoBar.IsOpen = false;
        }
    }

    private async void AddCodec_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.List, SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add(".dll");
        InitializeWithWindow.Initialize(picker, _ownerWindow);

        var fileName = string.Empty;

        // Event-handler boundary: async void, and every outcome is reported in the dialog itself
        // - a second ContentDialog cannot open while this one is up.
        try
        {
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            fileName = file.Name;

            AddCodecButton.IsEnabled = false;
            var installed = await Task.Run(() => ImageCodecs.Install(file.Path));

            PopulateMethods(installed.Id);
            ShowCodecMessage(InfoBarSeverity.Success, string.Format(_resourceLoader.GetString("SettingsCodecAdded"), installed.Name));
        }
        catch (CodecException ex)
        {
            Log.Warning(ex, "Codec plugin {File} refused ({Reason})", fileName, ex.Reason);
            ShowCodecMessage(InfoBarSeverity.Error, string.Format(
                _resourceLoader.GetString("SettingsCodecAddFailed"), fileName, CodecText.Reason(_resourceLoader, ex.Reason)));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not add codec plugin {File}", fileName);
            ShowCodecMessage(InfoBarSeverity.Error, string.Format(
                _resourceLoader.GetString("SettingsCodecAddFailed"), fileName, CodecText.Reason(_resourceLoader, CodecFailureReason.Unknown)));
        }
        finally
        {
            AddCodecButton.IsEnabled = true;
        }
    }

    private async void RemoveCodec_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPlugin is not { } plugin) return;

        var name = plugin.Name ?? plugin.FileName;

        // Event-handler boundary, as above.
        try
        {
            ImageCodecs.Uninstall(plugin.Id);

            // The saved method goes with its DLL, whether or not OK is pressed: left pointing at
            // a file that no longer exists, it would fail the next save.
            if (AppSettings.Current.ImageCodec == plugin.Id)
            {
                AppSettings.Current.ImageCodec = ImageCodecs.JpegId;
                await AppSettings.SaveAsync();
            }

            PopulateMethods(ImageCodecs.JpegId);
            ShowCodecMessage(InfoBarSeverity.Success, string.Format(_resourceLoader.GetString("SettingsCodecRemoved"), name));
        }
        catch (CodecException ex)
        {
            Log.Warning(ex, "Codec plugin {File} not removed ({Reason})", plugin.FileName, ex.Reason);
            ShowCodecMessage(InfoBarSeverity.Error, string.Format(
                _resourceLoader.GetString("SettingsCodecRemoveFailed"), name, CodecText.Reason(_resourceLoader, ex.Reason)));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not remove codec plugin {File}", plugin.FileName);
            ShowCodecMessage(InfoBarSeverity.Error, string.Format(
                _resourceLoader.GetString("SettingsCodecRemoveFailed"), name, CodecText.Reason(_resourceLoader, CodecFailureReason.Unknown)));
        }
    }

    private static string PrimarySubtag(string? tag)
        => string.IsNullOrEmpty(tag) ? string.Empty : tag.Split('-')[0].ToLowerInvariant();

    private void ShowCodecMessage(InfoBarSeverity severity, string message)
    {
        CodecInfoBar.Severity = severity;
        CodecInfoBar.Message = message;
        CodecInfoBar.IsOpen = true;
    }

    #endregion

    private void PresetA4_Click(object sender, RoutedEventArgs e) { UnitCombo.SelectedIndex = 0; WidthBox.Value = 210; HeightBox.Value = 297; }
    private void PresetLetter_Click(object sender, RoutedEventArgs e) { UnitCombo.SelectedIndex = 1; WidthBox.Value = 8.5; HeightBox.Value = 11; }
    private void PresetA5_Click(object sender, RoutedEventArgs e) { UnitCombo.SelectedIndex = 0; WidthBox.Value = 148; HeightBox.Value = 210; }
    private void Preset4x6_Click(object sender, RoutedEventArgs e) { UnitCombo.SelectedIndex = 1; WidthBox.Value = 4; HeightBox.Value = 6; }

    private async void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        AppSettings.Current.PaperSize = (PdfPaperSize)PaperSizeCombo.SelectedIndex;
        AppSettings.Current.Orientation = (PdfPaperOrientation)OrientationCombo.SelectedIndex;
        AppSettings.Current.Margin = (PdfPageMargin)MarginCombo.SelectedIndex;
        AppSettings.Current.ImageCompression = (PdfImageCompression)CompressionCombo.SelectedIndex;
        AppSettings.Current.ImageCodec = (MethodCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? ImageCodecs.JpegId;

        // Kept by Windows for this app, not in the settings file: it has to be in force before
        // the first window is built, which is before the settings file has been read. An empty
        // value goes back to following Windows. It takes effect from the next start.
        //
        // Windows.Globalization, not the App SDK's Microsoft.Windows.Globalization: that one
        // forwards the value here when it is set, but only remembers it for the current process,
        // so after a restart it read back empty and Settings showed "Same as Windows" while the
        // app was in Indonesian - and saving the dialog unchanged would have switched it back.
        Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride =
            (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;

        if (AppSettings.Current.PaperSize == PdfPaperSize.Custom)
        {
            AppSettings.Current.CustomSizeUnit = (UnitSize)UnitCombo.SelectedIndex;
            AppSettings.Current.CustomWidth = WidthBox.Value;
            AppSettings.Current.CustomHeight = HeightBox.Value;
        }
        if (AppSettings.Current.Margin == PdfPageMargin.Custom)
        {
            AppSettings.Current.CustomMarginUnit = (UnitSize)MarginUnitCombo.SelectedIndex;
            AppSettings.Current.CustomMarginLeft = LeftMarginBox.Value;
            AppSettings.Current.CustomMarginRight = RightMarginBox.Value;
            AppSettings.Current.CustomMarginTop = TopMarginBox.Value;
            AppSettings.Current.CustomMarginBottom = BottomMarginBox.Value;
        }

        try
        {
            await AppSettings.SaveAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save settings");
            await ShowSaveFailedAsync();
        }
    }

    private async Task ShowSaveFailedAsync()
    {
        var dialog = new ContentDialog
        {
            Title = _resourceLoader.GetString("DialogTitleError"),
            Content = _resourceLoader.GetString("DialogContentSettingsNotSaved"),
            CloseButtonText = _resourceLoader.GetString("DialogButtonOK"),
            XamlRoot = XamlRoot
        };
        await DialogHost.ShowAsync(dialog);
    }
}

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
        PopulateOrientationMethods(AppSettings.Current.OrientationMethod);
        TextRecognitionFallbackCheck.IsChecked = AppSettings.Current.OrientationTextRecognitionFallback;

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

        // Keep showing a plugin deleted outside the app, marked, rather than silently using JPEG.
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

        // Event-handler boundary; outcomes are shown inside this dialog, since a second one cannot open.
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

            // Saved right away, OK or not: the setting must not point at a deleted DLL.
            if (AppSettings.Current.ImageCodec == plugin.Id)
                await AppSettings.UpdateAsync(settings => settings.ImageCodec = ImageCodecs.JpegId);

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

    #region Normalize

    /// <summary>Fills the list: the built-in model, every added model, then text recognition.</summary>
    private void PopulateOrientationMethods(string selectedId)
    {
        OrientationMethodCombo.Items.Clear();
        OrientationMethodCombo.Items.Add(new ComboBoxItem { Content = _resourceLoader.GetString("SettingsOrientationBuiltIn"), Tag = OrientationModels.BuiltInId });

        var models = new List<OrientationModelInfo>();
        try
        {
            models.AddRange(OrientationModels.List());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not read the model folder {Path}", OrientationModels.ModelDirectory);
        }

        // Keep showing a model deleted outside the app, marked, rather than silently using the built-in one.
        if (OrientationModels.IsModel(selectedId) && !models.Exists(m => m.Id == selectedId))
            models.Add(new OrientationModelInfo(selectedId, selectedId[OrientationModels.ModelPrefix.Length..], null, OrientationModelFailureReason.NotFound));

        foreach (var model in models)
        {
            var item = new ComboBoxItem
            {
                Content = model.LoadFailure == null
                    ? string.Format(_resourceLoader.GetString("SettingsOrientationModelFormat"), model.Name)
                    : string.Format(_resourceLoader.GetString("SettingsOrientationUnavailableFormat"), model.FileName),
                Tag = model.Id,
                DataContext = model
            };
            ToolTipService.SetToolTip(item, model.FileName);
            OrientationMethodCombo.Items.Add(item);
        }

        OrientationMethodCombo.Items.Add(new ComboBoxItem
        {
            Content = _resourceLoader.GetString(TextRecognitionOrientation.IsAvailable ? "SettingsOrientationTextRecognition" : "SettingsOrientationTextRecognitionMissing"),
            Tag = OrientationModels.TextRecognitionId
        });

        OrientationMethodCombo.SelectedItem = OrientationMethodCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == selectedId)
            ?? OrientationMethodCombo.Items[0];
    }

    private OrientationModelInfo? SelectedModel => (OrientationMethodCombo.SelectedItem as ComboBoxItem)?.DataContext as OrientationModelInfo;

    private string SelectedOrientationMethod => (OrientationMethodCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? OrientationModels.BuiltInId;

    private void OrientationMethodCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var model = SelectedModel;
        RemoveModelButton.IsEnabled = model != null;

        // Falling back to text recognition means nothing when text recognition is the method.
        TextRecognitionFallbackCheck.IsEnabled = SelectedOrientationMethod != OrientationModels.TextRecognitionId;

        if (model?.LoadFailure is { } failure)
        {
            ShowModelMessage(InfoBarSeverity.Warning, string.Format(
                _resourceLoader.GetString("SettingsModelUnusable"), model.FileName, OrientationModelText.Reason(_resourceLoader, failure)));
        }
        else if (ModelInfoBar.Severity == InfoBarSeverity.Warning)
        {
            ModelInfoBar.IsOpen = false;
        }
    }

    private async void AddModel_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.List, SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add(".onnx");
        InitializeWithWindow.Initialize(picker, _ownerWindow);

        var fileName = string.Empty;

        // Event-handler boundary; outcomes are shown inside this dialog, since a second one cannot open.
        try
        {
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            fileName = file.Name;

            // Calibrating a model on the sample pages takes a few seconds.
            AddModelButton.IsEnabled = false;
            ModelProgress.IsActive = true;
            ModelProgress.Visibility = Visibility.Visible;
            ShowModelMessage(InfoBarSeverity.Informational, string.Format(_resourceLoader.GetString("SettingsModelChecking"), fileName));

            var installed = await Task.Run(() => OrientationModels.InstallAsync(file.Path));

            PopulateOrientationMethods(installed.Id);
            ShowModelMessage(InfoBarSeverity.Success, string.Format(_resourceLoader.GetString("SettingsModelAdded"), installed.Name));
        }
        catch (OrientationModelException ex)
        {
            Log.Warning(ex, "Orientation model {File} refused ({Reason})", fileName, ex.Reason);
            ShowModelMessage(InfoBarSeverity.Error, string.Format(
                _resourceLoader.GetString("SettingsModelAddFailed"), fileName, OrientationModelText.Reason(_resourceLoader, ex)));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not add orientation model {File}", fileName);
            ShowModelMessage(InfoBarSeverity.Error, string.Format(
                _resourceLoader.GetString("SettingsModelAddFailed"), fileName, OrientationModelText.Reason(_resourceLoader, OrientationModelFailureReason.Unknown)));
        }
        finally
        {
            AddModelButton.IsEnabled = true;
            ModelProgress.IsActive = false;
            ModelProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async void RemoveModel_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedModel is not { } model) return;

        var name = model.Name ?? model.FileName;

        // Event-handler boundary, as above.
        try
        {
            OrientationModels.Uninstall(model.Id);

            // Saved right away, OK or not: the setting must not point at a deleted model.
            if (AppSettings.Current.OrientationMethod == model.Id)
                await AppSettings.UpdateAsync(settings => settings.OrientationMethod = OrientationModels.BuiltInId);

            PopulateOrientationMethods(OrientationModels.BuiltInId);
            ShowModelMessage(InfoBarSeverity.Success, string.Format(_resourceLoader.GetString("SettingsModelRemoved"), name));
        }
        catch (OrientationModelException ex)
        {
            Log.Warning(ex, "Orientation model {File} not removed ({Reason})", model.FileName, ex.Reason);
            ShowModelMessage(InfoBarSeverity.Error, string.Format(
                _resourceLoader.GetString("SettingsModelRemoveFailed"), name, OrientationModelText.Reason(_resourceLoader, ex)));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not remove orientation model {File}", model.FileName);
            ShowModelMessage(InfoBarSeverity.Error, string.Format(
                _resourceLoader.GetString("SettingsModelRemoveFailed"), name, OrientationModelText.Reason(_resourceLoader, OrientationModelFailureReason.Unknown)));
        }
    }

    private void ShowModelMessage(InfoBarSeverity severity, string message)
    {
        ModelInfoBar.Severity = severity;
        ModelInfoBar.Message = message;
        ModelInfoBar.IsOpen = true;
    }

    #endregion

    private void PresetA4_Click(object sender, RoutedEventArgs e) { UnitCombo.SelectedIndex = 0; WidthBox.Value = 210; HeightBox.Value = 297; }
    private void PresetLetter_Click(object sender, RoutedEventArgs e) { UnitCombo.SelectedIndex = 1; WidthBox.Value = 8.5; HeightBox.Value = 11; }
    private void PresetA5_Click(object sender, RoutedEventArgs e) { UnitCombo.SelectedIndex = 0; WidthBox.Value = 148; HeightBox.Value = 210; }
    private void Preset4x6_Click(object sender, RoutedEventArgs e) { UnitCombo.SelectedIndex = 1; WidthBox.Value = 4; HeightBox.Value = 6; }

    /// <summary>Why the last save failed, or null.</summary>
    /// <remarks>The caller reports it after this dialog closes; a second dialog cannot open now.</remarks>
    public Exception? SaveFailure { get; private set; }

    private async void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Keep the dialog open until the write finishes.
        var deferral = args.GetDeferral();
        SaveFailure = null;

        try
        {
            // Edit a copy; it replaces Current only once it is on disk.
            var next = AppSettings.Current.Clone();
            next.PaperSize = (PdfPaperSize)PaperSizeCombo.SelectedIndex;
            next.Orientation = (PdfPaperOrientation)OrientationCombo.SelectedIndex;
            next.Margin = (PdfPageMargin)MarginCombo.SelectedIndex;
            next.ImageCompression = (PdfImageCompression)CompressionCombo.SelectedIndex;
            next.ImageCodec = (MethodCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? ImageCodecs.JpegId;
            next.OrientationMethod = SelectedOrientationMethod;
            next.OrientationTextRecognitionFallback = TextRecognitionFallbackCheck.IsChecked == true;

            if (next.PaperSize == PdfPaperSize.Custom)
            {
                next.CustomSizeUnit = (UnitSize)UnitCombo.SelectedIndex;
                next.CustomWidth = WidthBox.Value;
                next.CustomHeight = HeightBox.Value;
            }
            if (next.Margin == PdfPageMargin.Custom)
            {
                next.CustomMarginUnit = (UnitSize)MarginUnitCombo.SelectedIndex;
                next.CustomMarginLeft = LeftMarginBox.Value;
                next.CustomMarginRight = RightMarginBox.Value;
                next.CustomMarginTop = TopMarginBox.Value;
                next.CustomMarginBottom = BottomMarginBox.Value;
            }

            await AppSettings.SaveAsync(next);

            // Stored by Windows, not in the settings file, because it must apply before the first
            // window is built; takes effect on the next start. Windows.Globalization rather than the
            // App SDK API, which only remembers the value for the current process. Set after the file
            // is written so a failed save changes nothing.
            Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride =
                (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
        }
        // Event-handler boundary.
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save settings");
            SaveFailure = ex;
        }
        finally
        {
            deferral.Complete();
        }
    }
}

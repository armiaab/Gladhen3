using Gladhen3.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gladhen3.Dialogs;

public sealed partial class SettingsDialog : ContentDialog
{
    private readonly Microsoft.Windows.ApplicationModel.Resources.ResourceLoader _resourceLoader = new();
    private int _previousUnitIndex;
    private int _previousMarginUnit;

    public SettingsDialog()
    {
        InitializeComponent();
        LoadSettings();
    }

    private void LoadSettings()
    {
        PaperSizeCombo.SelectedIndex = (int)AppSettings.Current.PaperSize;
        OrientationCombo.SelectedIndex = (int)AppSettings.Current.Orientation;
        MarginCombo.SelectedIndex = (int)AppSettings.Current.Margin;
        CompressionCombo.SelectedIndex = (int)AppSettings.Current.ImageCompression;

        UnitCombo.SelectedIndex = AppSettings.Current.CustomSizeUnit;
        _previousUnitIndex = UnitCombo.SelectedIndex;

        WidthBox.Value = AppSettings.Current.CustomWidth;
        HeightBox.Value = AppSettings.Current.CustomHeight;

        MarginUnitCombo.SelectedIndex = (int)AppSettings.Current.CustomMarginUnit;
        _previousMarginUnit = MarginUnitCombo.SelectedIndex;

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
        if (newUnitIndex != _previousUnitIndex)
        {
            WidthBox.Value = ConvertValue(WidthBox.Value, (UnitSize)_previousUnitIndex, (UnitSize)newUnitIndex);
            HeightBox.Value = ConvertValue(HeightBox.Value, (UnitSize)_previousUnitIndex, (UnitSize)newUnitIndex);
            _previousUnitIndex = newUnitIndex;
        }
    }

    private void MarginUnitCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LeftMarginBox == null) return;

        var newUnit = MarginUnitCombo.SelectedIndex;
        if (newUnit != _previousMarginUnit)
        {
            LeftMarginBox.Value = ConvertValue(LeftMarginBox.Value, (UnitSize)_previousMarginUnit, (UnitSize)newUnit);
            RightMarginBox.Value = ConvertValue(RightMarginBox.Value, (UnitSize)_previousMarginUnit, (UnitSize)newUnit);
            TopMarginBox.Value = ConvertValue(TopMarginBox.Value, (UnitSize)_previousMarginUnit, (UnitSize)newUnit);
            BottomMarginBox.Value = ConvertValue(BottomMarginBox.Value, (UnitSize)_previousMarginUnit, (UnitSize)newUnit);
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

        if (AppSettings.Current.PaperSize == PdfPaperSize.Custom)
        {
            AppSettings.Current.CustomSizeUnit = UnitCombo.SelectedIndex;
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

        await AppSettings.SaveAsync();
    }
}

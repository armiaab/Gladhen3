using Gladhen3.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gladhen3.Dialogs;

public sealed partial class SettingsDialog : ContentDialog
{
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
        var unitName = UnitCombo.SelectedIndex switch
        {
            0 => "mm",
            1 => "in",
            2 => "pt",
            _ => "mm"
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
            WidthBox.Value = ConvertValue(WidthBox.Value, _previousUnitIndex, newUnitIndex);
            HeightBox.Value = ConvertValue(HeightBox.Value, _previousUnitIndex, newUnitIndex);
            _previousUnitIndex = newUnitIndex;
        }
    }

    private void MarginUnitCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LeftMarginBox == null) return;

        var newUnit = MarginUnitCombo.SelectedIndex;
        if (newUnit != _previousMarginUnit)
        {
            LeftMarginBox.Value = ConvertValue(LeftMarginBox.Value, _previousMarginUnit, newUnit);
            RightMarginBox.Value = ConvertValue(RightMarginBox.Value, _previousMarginUnit, newUnit);
            TopMarginBox.Value = ConvertValue(TopMarginBox.Value, _previousMarginUnit, newUnit);
            BottomMarginBox.Value = ConvertValue(BottomMarginBox.Value, _previousMarginUnit, newUnit);
            _previousMarginUnit = newUnit;
        }
    }

    private double ConvertValue(double value, int fromUnit, int toUnit)
    {
        if (fromUnit == toUnit) return value;
        var mmPerInch = 25.4;
        var ptPerInch = 72.0;

        double valueInMm = fromUnit switch
        {
            0 => value, // mm
            1 => value * mmPerInch, // in -> mm
            2 => value * (mmPerInch / ptPerInch), // pt -> mm
            _ => value
        };

        return toUnit switch
        {
            0 => valueInMm, // mm
            1 => valueInMm / mmPerInch, // mm -> in
            2 => valueInMm * (ptPerInch / mmPerInch), // mm -> pt
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
            AppSettings.Current.CustomMarginUnit = (MarginUnit)MarginUnitCombo.SelectedIndex;
            AppSettings.Current.CustomMarginLeft = LeftMarginBox.Value;
            AppSettings.Current.CustomMarginRight = RightMarginBox.Value;
            AppSettings.Current.CustomMarginTop = TopMarginBox.Value;
            AppSettings.Current.CustomMarginBottom = BottomMarginBox.Value;
        }

        await AppSettings.SaveAsync();
    }
}

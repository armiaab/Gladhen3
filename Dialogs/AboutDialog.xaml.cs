using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel;

namespace Gladhen3.Dialogs;

public sealed partial class AboutDialog : ContentDialog
{
    public AboutDialog()
    {
        InitializeComponent();

        // Read from the package so it is never out of date.
        var version = Package.Current.Id.Version;
        VersionText.Text = string.Format(
            new Microsoft.Windows.ApplicationModel.Resources.ResourceLoader().GetString("AboutVersionFormat"),
            $"{version.Major}.{version.Minor}.{version.Build}");
    }
}

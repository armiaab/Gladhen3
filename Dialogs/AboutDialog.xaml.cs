using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel;

namespace Gladhen3.Dialogs;

public sealed partial class AboutDialog : ContentDialog
{
    public AboutDialog()
    {
        InitializeComponent();

        // From the package rather than the resources, where it had to be edited by hand - in
        // every language - for every release, and was always one release out of date somewhere.
        var version = Package.Current.Id.Version;
        VersionText.Text = string.Format(
            new Microsoft.Windows.ApplicationModel.Resources.ResourceLoader().GetString("AboutVersionFormat"),
            $"{version.Major}.{version.Minor}.{version.Build}");
    }
}

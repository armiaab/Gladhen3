using Gladhen3.Services;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Gladhen3.Dialogs;

/// <summary>Explains why an orientation model could not be used.</summary>
internal static class OrientationModelText
{
    public static string Reason(ResourceLoader loader, OrientationModelException failure) => failure.Reason switch
    {
        OrientationModelFailureReason.NotFound => loader.GetString("ModelReason_NotFound"),
        OrientationModelFailureReason.LoadFailed => loader.GetString("ModelReason_LoadFailed"),
        OrientationModelFailureReason.NeedsNewerWindows => string.Format(loader.GetString("ModelReason_NeedsNewerWindows"), failure.Opset),
        OrientationModelFailureReason.WrongShape => loader.GetString("ModelReason_WrongShape"),
        OrientationModelFailureReason.FailedCheck => loader.GetString("ModelReason_FailedCheck"),
        OrientationModelFailureReason.AlreadyInstalled => loader.GetString("ModelReason_AlreadyInstalled"),
        OrientationModelFailureReason.InUse => loader.GetString("ModelReason_InUse"),
        _ => loader.GetString("ModelReason_Unknown")
    };

    /// <summary>For a model listed as unusable, from its reason alone.</summary>
    public static string Reason(ResourceLoader loader, OrientationModelFailureReason reason)
        => Reason(loader, new OrientationModelException(reason, reason.ToString()));
}

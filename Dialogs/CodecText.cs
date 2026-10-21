using Gladhen3.Services;
using Microsoft.Windows.ApplicationModel.Resources;
using System.Runtime.InteropServices;

namespace Gladhen3.Dialogs;

/// <summary>Says why a codec plugin could not be used, in words for the person who installed it.</summary>
internal static class CodecText
{
    public static string Reason(ResourceLoader loader, CodecFailureReason reason) => reason switch
    {
        CodecFailureReason.NotFound => loader.GetString("CodecReason_NotFound"),
        CodecFailureReason.WrongArchitecture => string.Format(
            loader.GetString("CodecReason_WrongArchitecture"),
            RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()),
        CodecFailureReason.LoadFailed => loader.GetString("CodecReason_LoadFailed"),
        CodecFailureReason.NotACodec => loader.GetString("CodecReason_NotACodec"),
        CodecFailureReason.UnsupportedVersion => loader.GetString("CodecReason_UnsupportedVersion"),
        CodecFailureReason.UnsupportedFilter => loader.GetString("CodecReason_UnsupportedFilter"),
        CodecFailureReason.AlreadyInstalled => loader.GetString("CodecReason_AlreadyInstalled"),
        CodecFailureReason.InUse => loader.GetString("CodecReason_InUse"),
        _ => loader.GetString("CodecReason_Unknown")
    };
}

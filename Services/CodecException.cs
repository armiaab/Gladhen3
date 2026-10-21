using System;

namespace Gladhen3.Services;

public enum CodecFailureReason
{
    Unknown = 0,

    /// <summary>The codec's DLL is no longer where it was installed.</summary>
    NotFound,

    /// <summary>The DLL was built for a different processor architecture than this app.</summary>
    WrongArchitecture,

    /// <summary>Windows would not load the DLL, usually because a DLL it depends on is missing.</summary>
    LoadFailed,

    /// <summary>The DLL loaded but does not export the codec entry points.</summary>
    NotACodec,

    /// <summary>The DLL speaks a version of the codec interface this app does not.</summary>
    UnsupportedVersion,

    /// <summary>The DLL produces data no PDF reader can decode.</summary>
    UnsupportedFilter,

    /// <summary>A codec with the same file name is already installed.</summary>
    AlreadyInstalled,

    /// <summary>The DLL could not be removed because something is still using it.</summary>
    InUse,

    /// <summary>The codec failed, or returned data that does not describe the image it was given.</summary>
    EncodeFailed
}

/// <summary>
/// A codec plugin that could not be loaded, installed, removed or run.
/// </summary>
/// <remarks>
/// Like <see cref="PdfOperationException"/> it carries a reason rather than a sentence, so the
/// UI chooses the wording.
/// </remarks>
public class CodecException : Exception
{
    public CodecException()
    {
    }

    public CodecException(string message)
        : base(message)
    {
    }

    public CodecException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public CodecException(CodecFailureReason reason, string message, Exception? innerException = null)
        : base(message, innerException)
        => Reason = reason;

    public CodecFailureReason Reason { get; } = CodecFailureReason.Unknown;

    public string? Path { get; init; }
}

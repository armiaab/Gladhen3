using System;
using System.Collections.Generic;

namespace Gladhen3.Services;

/// <param name="CodecFailures">
/// Images the chosen compression method failed on, which were therefore kept as they were.
/// Only a plugin can fail this way; it is reported so that a broken codec does not pass for one
/// that simply found nothing to shrink.
/// </param>
public sealed record PdfBuildResult(int PagesWritten, IReadOnlyList<string> SkippedItems, int CodecFailures = 0);

public enum PdfFailureReason
{
    Unknown = 0,
    FileInUse,
    AccessDenied,
    DirectoryNotFound,
    NoPages,

    /// <summary>The compression method chosen in Settings is a plugin that will not load.</summary>
    CodecUnavailable
}

public class PdfOperationException : Exception
{
    public PdfOperationException()
    {
    }

    public PdfOperationException(string message)
        : base(message)
    {
    }

    public PdfOperationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PdfOperationException(PdfFailureReason reason, string message, Exception? innerException = null)
        : base(message, innerException)
        => Reason = reason;

    public PdfFailureReason Reason { get; } = PdfFailureReason.Unknown;

    public string? Path { get; init; }

    /// <summary>Set with <see cref="PdfFailureReason.CodecUnavailable"/>: why the plugin would not load.</summary>
    public CodecFailureReason? CodecReason { get; init; }
}

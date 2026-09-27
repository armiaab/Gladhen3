using System;
using System.Collections.Generic;

namespace Gladhen3.Services;

public sealed record PdfBuildResult(int PagesWritten, IReadOnlyList<string> SkippedItems);

public enum PdfFailureReason
{
    Unknown = 0,
    FileInUse,
    AccessDenied,
    DirectoryNotFound,
    NoPages
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
}

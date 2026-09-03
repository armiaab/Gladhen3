using System;
using System.Collections.Generic;

namespace Gladhen3.Services;

/// <summary>
/// The outcome of building a PDF.
/// </summary>
/// <param name="PagesWritten">How many pages ended up in the file.</param>
/// <param name="SkippedItems">
/// Names of items that could not be turned into a page. A partial success is still a success,
/// but the caller has to be told which files did not make it in.
/// </param>
public sealed record PdfBuildResult(int PagesWritten, IReadOnlyList<string> SkippedItems);

/// <summary>
/// Why a PDF operation could not be completed.
/// </summary>
/// <remarks>
/// The reason, not the exception message, is what crosses the service boundary. The service
/// layer has no business deciding what English text the user sees, and the UI needs something
/// stable to switch on that is not a localised string.
/// </remarks>
public enum PdfFailureReason
{
    /// <summary>No more specific reason is known; treat as an unexpected failure.</summary>
    Unknown = 0,

    /// <summary>The destination is open in another application.</summary>
    FileInUse,

    /// <summary>The destination cannot be written to with the current permissions.</summary>
    AccessDenied,

    /// <summary>The destination folder does not exist.</summary>
    DirectoryNotFound,

    /// <summary>Nothing in the selection could be turned into a page.</summary>
    NoPages
}

/// <summary>
/// Thrown when a PDF operation fails for a reason the user can act on.
/// </summary>
/// <remarks>
/// Unexpected failures are deliberately <em>not</em> wrapped in this type: they propagate as
/// themselves so they surface as bugs rather than being dressed up as routine problems.
/// </remarks>
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

    /// <summary>What went wrong, for the UI to map to a localised message.</summary>
    public PdfFailureReason Reason { get; } = PdfFailureReason.Unknown;

    /// <summary>The file being written or read when the failure happened, when known.</summary>
    public string? Path { get; init; }
}

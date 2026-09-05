using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Gladhen3.Models;

/// <summary>
/// One run of pages that will be written as a single PDF.
/// </summary>
/// <remarks>
/// Sections are derived from the flat page list rather than stored: a
/// <see cref="DocumentType.SectionBreak"/> row starts a new one. Deriving them means the
/// built-in drag reorder stays the only thing that edits order, so there is no second
/// ordering to keep in step with the first.
/// </remarks>
public sealed class PdfSection(string requestedName, DocumentItem? breakItem, List<DocumentItem> items)
{
    /// <summary>Windows silently strips these from the end of a name, so a file asked for as
    /// "Report." arrives as "Report" and the two disagree about what was written.</summary>
    private static readonly char[] TrailingJunk = ['.', ' '];

    /// <summary>Still special in Win32 no matter the extension or the directory.</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Long enough for any real title, short enough that folder + name + ".pdf" clears MAX_PATH
    /// from a normally nested folder.
    /// </summary>
    private const int MaxStemLength = 100;

    /// <summary>What the user typed on the band, before it is made safe for a file system.</summary>
    public string RequestedName { get; } = requestedName ?? string.Empty;

    /// <summary>The divider that starts this section, or null for an implicit leading one.</summary>
    public DocumentItem? BreakItem { get; } = breakItem;

    /// <summary>The pages, in order. Never contains a divider.</summary>
    public List<DocumentItem> Items { get; } = items;

    /// <summary>A section with no pages produces no file; it is reported, not written.</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>
    /// Cuts a flat page list into sections at every <see cref="DocumentType.SectionBreak"/>.
    /// </summary>
    /// <remarks>
    /// Pages sitting above the first divider are still a section - dragging a divider down the
    /// list is an ordinary thing to do, and dropping those pages would silently lose them.
    /// </remarks>
    public static List<PdfSection> Split(IReadOnlyList<DocumentItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var sections = new List<PdfSection>();
        var current = new List<DocumentItem>();
        DocumentItem? currentBreak = null;
        var currentName = string.Empty;
        var started = false;

        foreach (var item in items)
        {
            if (item.Type == DocumentType.SectionBreak)
            {
                if (started || current.Count > 0)
                    sections.Add(new PdfSection(currentName, currentBreak, current));

                current = [];
                currentBreak = item;
                currentName = item.SectionName;
                started = true;
                continue;
            }

            current.Add(item);
        }

        if (started || current.Count > 0)
            sections.Add(new PdfSection(currentName, currentBreak, current));

        return sections;
    }

    /// <summary>
    /// Turns section names into file names that can actually coexist in one folder.
    /// </summary>
    /// <remarks>
    /// Returned in step with <paramref name="sections"/>, empty sections included, so callers
    /// can index one against the other. De-duplication is ordinal-ignore-case because that is
    /// what the file system will collide on - two sections named "Report" and "report" would
    /// otherwise quietly overwrite each other and the user would be handed one file having
    /// asked for two.
    /// </remarks>
    public static List<string> ResolveFileNames(IReadOnlyList<PdfSection> sections, string fallbackPrefix)
    {
        ArgumentNullException.ThrowIfNull(sections);

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>(sections.Count);

        for (var i = 0; i < sections.Count; i++)
        {
            var stem = MakeSafeStem(sections[i].RequestedName);
            if (stem.Length == 0) stem = $"{fallbackPrefix} {i + 1}";

            var candidate = stem;
            var suffix = 2;
            while (!used.Add(candidate + ".pdf"))
            {
                candidate = $"{stem} ({suffix})";
                suffix++;
            }

            names.Add(candidate + ".pdf");
        }

        return names;
    }

    /// <summary>Strips everything Win32 will reject, silently rewrite, or treat as a device.</summary>
    public static string MakeSafeStem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray())
            .Trim()
            .TrimEnd(TrailingJunk);

        if (cleaned.Length > MaxStemLength)
            cleaned = cleaned[..MaxStemLength].TrimEnd(TrailingJunk);

        if (cleaned.Length == 0) return string.Empty;

        // "NUL.pdf" is still the null device. Prefixing keeps the user's word visible.
        return ReservedNames.Contains(cleaned) ? "_" + cleaned : cleaned;
    }

    /// <summary>
    /// A sensible starting name for a section: the source it came from if that is unambiguous.
    /// </summary>
    /// <remarks>
    /// Splitting is nearly always "give me these inputs back as separate files", so naming a
    /// section after its only source is right far more often than "Document 3" is.
    /// </remarks>
    public static string SuggestName(IReadOnlyList<DocumentItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) return string.Empty;

        string? single = null;
        foreach (var item in items)
        {
            if (item.Type == DocumentType.SectionBreak) continue;

            var source = item.Type == DocumentType.PdfPage
                ? item.SourcePdfPath ?? item.FilePath
                : item.FilePath;

            if (string.IsNullOrEmpty(source)) return string.Empty;

            if (single == null) single = source;
            else if (!string.Equals(single, source, StringComparison.OrdinalIgnoreCase)) return string.Empty;
        }

        return single == null ? string.Empty : Path.GetFileNameWithoutExtension(single);
    }
}

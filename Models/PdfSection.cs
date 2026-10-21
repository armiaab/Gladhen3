using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Gladhen3.Models;

public sealed class PdfSection(string requestedName, DocumentItem? breakItem, List<DocumentItem> items)
{
    private static readonly char[] TrailingJunk = ['.', ' '];

    // https://help.interfaceware.com/v6/windows-reserved-file-names
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM0",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT0"
    };

    private const int MaxStemLength = 100;

    public string RequestedName { get; } = requestedName ?? string.Empty;

    public DocumentItem? BreakItem { get; } = breakItem;

    public List<DocumentItem> Items { get; } = items;

    public bool IsEmpty => Items.Count == 0;

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

        return ReservedNames.Contains(cleaned) ? "_" + cleaned : cleaned;
    }

    public static string SuggestName(IReadOnlyList<DocumentItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) return string.Empty;

        string? single = null;
        foreach (var item in items)
        {
            // An inserted blank page belongs to no file, so it says nothing about the name.
            if (item.Type is DocumentType.SectionBreak or DocumentType.BlankPage) continue;

            // FilePath rather than SourcePdfPath: after the original has been saved over, a page
            // is read from a copy with a meaningless name, but it still belongs to the original.
            var source = item.FilePath;

            if (string.IsNullOrEmpty(source)) return string.Empty;

            if (single == null) single = source;
            else if (!string.Equals(single, source, StringComparison.OrdinalIgnoreCase)) return string.Empty;
        }

        return single == null ? string.Empty : Path.GetFileNameWithoutExtension(single);
    }
}

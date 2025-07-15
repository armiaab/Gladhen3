using Gladhen3.Models;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Gladhen3.Helpers;

public static class ImageSortingService
{
    public static List<ImageItem> SortByName(IEnumerable<ImageItem> items, bool ascending)
    {
        return ascending
            ? items.OrderBy(item => item.FileName).ToList()
            : items.OrderByDescending(item => item.FileName).ToList();
    }

    public static List<ImageItem> SortByPath(IEnumerable<ImageItem> items, bool ascending)
    {
        return ascending
            ? items.OrderBy(item => item.FilePath).ToList()
            : items.OrderByDescending(item => item.FilePath).ToList();
    }

    public static List<ImageItem> SortByDate(IEnumerable<ImageItem> items, bool ascending)
    {
        return ascending
            ? items.OrderBy(item => File.GetLastWriteTime(item.FilePath!)).ToList()
            : items.OrderByDescending(item => File.GetLastWriteTime(item.FilePath!)).ToList();
    }

    public static List<ImageItem> SortByFileSize(IEnumerable<ImageItem> items, bool ascending)
    {
        return ascending
            ? items.OrderBy(item => new FileInfo(item.FilePath!).Length).ToList()
            : items.OrderByDescending(item => new FileInfo(item.FilePath!).Length).ToList();
    }
}
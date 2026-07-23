namespace Gladhen3.Models;

public enum PdfImageCompression
{
    None = 0, // original format / quality
    Low = 1, // JPEG 85 %
    Medium = 2, // JPEG 65 %
    High = 3  // JPEG 40 %
}

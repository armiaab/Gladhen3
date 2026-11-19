using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Gladhen3.Services;

/// <summary>A page reduced to grey pixels, which is all text recognition needs.</summary>
internal sealed class GrayBitmap(int width, int height, byte[] pixels)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Pixels { get; } = pixels;

    /// <summary>Decodes to grey, long side at most <paramref name="maxEdge"/> pixels.</summary>
    /// <param name="exif">Respected for photos; rendered pages have none.</param>
    public static async Task<GrayBitmap> DecodeAsync(BitmapDecoder decoder, int maxEdge, ExifOrientationMode exif)
    {
        var (w, h) = ((int)decoder.PixelWidth, (int)decoder.PixelHeight);
        var transform = new BitmapTransform();
        var longEdge = Math.Max(w, h);
        if (longEdge > maxEdge)
        {
            var scale = (double)maxEdge / longEdge;
            (w, h) = (Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));
            transform.ScaledWidth = (uint)w;
            transform.ScaledHeight = (uint)h;
            transform.InterpolationMode = BitmapInterpolationMode.Fant;
        }

        // Bgra8, not Gray8: the decoder refuses to convert to grey itself ("the parameter is incorrect").
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform, exif, ColorManagementMode.DoNotColorManage);
        var bgra = data.DetachPixelData();

        // The scale applies to the image as stored; an EXIF quarter turn then swaps the sides.
        if (exif == ExifOrientationMode.RespectExifOrientation && decoder.OrientedPixelWidth != decoder.PixelWidth) (w, h) = (h, w);

        var gray = new byte[w * h];
        for (var i = 0; i < gray.Length; i++)
        {
            var p = i * 4;
            gray[i] = (byte)(((bgra[p + 2] * 299) + (bgra[p + 1] * 587) + (bgra[p] * 114)) / 1000);
        }
        return new GrayBitmap(w, h, gray);
    }

    /// <summary>A copy turned clockwise by <paramref name="degrees"/>, a whole number of quarter turns.</summary>
    public GrayBitmap Turned(int degrees)
    {
        degrees = Models.DocumentItem.NormaliseRotation(degrees);
        if (degrees == 0) return this;

        var (w, h) = (Width, Height);
        var (turnedW, turnedH) = degrees == 180 ? (w, h) : (h, w);
        var turned = new byte[Pixels.Length];

        for (var y = 0; y < h; y++)
        {
            var row = y * w;
            for (var x = 0; x < w; x++)
            {
                var (tx, ty) = degrees switch
                {
                    90 => (h - 1 - y, x),
                    180 => (w - 1 - x, h - 1 - y),
                    _ => (y, w - 1 - x)
                };
                turned[(ty * turnedW) + tx] = Pixels[row + x];
            }
        }

        return new GrayBitmap(turnedW, turnedH, turned);
    }

    public SoftwareBitmap ToSoftwareBitmap()
    {
        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Gray8, Width, Height, BitmapAlphaMode.Ignore);
        bitmap.CopyFromBuffer(Pixels.AsBuffer());
        return bitmap;
    }
}

/// <summary>Scores how word-like the recognised text is, which separates upright text from upside down.</summary>
/// <remarks>
/// The recognizer reads only upright text: a quarter turn yields stray characters, upside
/// down yields text that is not words ("and" becomes "pue").
/// </remarks>
internal readonly record struct ReadingScore(int Words, int Clean, int Common)
{
    /// <summary>Short common words (three letters or more) that do not read as other words upside down.</summary>
    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "that", "with", "for", "this", "from", "are", "was", "which", "have", "not", "been",
        "were", "their", "they", "will", "has", "had", "but", "its", "can", "all", "when", "there", "than",
        "into", "more", "other", "also", "each", "after", "before", "about", "would", "should", "must",
        "these", "those", "only", "such", "what", "where", "between", "through", "most",

        "yang", "dan", "dengan", "untuk", "dalam", "pada", "dari", "ini", "itu", "akan", "tidak", "ada",
        "atau", "oleh", "kami", "kepada", "sebagai", "juga", "tersebut", "bahwa", "telah", "para", "serta",
        "karena", "adalah", "dapat", "bagi", "sudah", "hari", "tahun", "agar", "secara", "antara",
        "setiap", "maka", "harus", "lebih", "bapak", "ibu"
    };

    /// <summary>Letters upside-down digits are read as: 0→O, 1→I/l, 2→Z, 3→E, 4→h, 5→S, 7→L, 8→B.</summary>
    private const string DigitLookalikes = "OoIlZzEhSsLB";

    /// <summary>The fewest points a reading needs before it can win at all.</summary>
    private const int MinimumScore = 8;

    /// <summary>How far ahead of every other turn the winner has to be.</summary>
    private const double Lead = 1.5;

    public static ReadingScore Of(IEnumerable<string> words)
    {
        int count = 0, clean = 0, common = 0, figures = 0;
        foreach (var word in words)
        {
            count++;
            var core = word.Trim('.', ',', ';', ':', '?', '!', '"', '\'', '(', ')', '[', ']');
            if (IsClean(core)) clean++;
            if (CommonWords.Contains(core)) common++;
            if (IsFigure(core)) figures++;
        }
        return new ReadingScore(count, clean, common) { Figures = figures };
    }

    /// <summary>Numbers that only read as numbers the right way up; see <see cref="IsFigure"/>.</summary>
    public int Figures { get; init; }

    /// <summary>Points: 2 per clean word, 6 per common word, 1 per figure.</summary>
    public int Value => (2 * Clean) + (6 * Common) + Figures;

    /// <summary>Common words enough to accept a reading as upright without trying the other turns.</summary>
    public bool IsConclusive => Common >= 6 && Common * 20 >= Words;

    /// <summary>The turn whose reading is clearly the best, or null if none is.</summary>
    /// <param name="byTurn">Scores for 0, 90, 180 and 270 degrees, in that order.</param>
    public static int? Decide(IReadOnlyList<ReadingScore> byTurn)
    {
        var best = 0;
        for (var k = 1; k < byTurn.Count; k++)
        {
            if (byTurn[k].Value > byTurn[best].Value) best = k;
        }

        var runnerUp = byTurn.Where((_, k) => k != best).Max(score => score.Value);
        var winner = byTurn[best].Value;
        return winner >= MinimumScore && winner > runnerUp && winner >= Lead * runnerUp ? best * 90 : null;
    }

    /// <summary>
    /// Three or more letters, cased like a word, not one repeated letter, and not spelt only
    /// with letters that upside-down digits become (2025 reads as "SZOZ").
    /// </summary>
    private static bool IsClean(string word)
    {
        if (word.Length < 3 || !word.All(char.IsLetter)) return false;
        if (word.Skip(1).All(c => c == word[0])) return false;
        if (word.All(DigitLookalikes.Contains)) return false;

        return word.Skip(1).All(char.IsLower) || !word.Any(char.IsLower);
    }

    /// <summary>A figure of three or more digits containing a 2, 3, 4, 5 or 7.</summary>
    /// <remarks>Those digits become letters upside down, so such a figure only reads correctly upright.</remarks>
    private static bool IsFigure(string token)
    {
        if (token.Length < 3 || !char.IsAsciiDigit(token[0]) || !char.IsAsciiDigit(token[^1])) return false;

        int digits = 0;
        var telling = false;
        foreach (var c in token)
        {
            if (char.IsAsciiDigit(c))
            {
                digits++;
                telling |= c is '2' or '3' or '4' or '5' or '7';
            }
            else if (c is not ('.' or ',' or ':' or '/' or '-'))
            {
                return false;
            }
        }
        return digits >= 3 && telling;
    }
}

/// <summary>Finds which way up a picture of a page is by reading it every way round.</summary>
internal static class TextRecognitionOrientation
{
    /// <summary>A recognizer for the user's languages, any installed one, or null.</summary>
    /// <remarks>
    /// Any Latin-script recognizer works, since only orientation matters (Windows has none for
    /// Indonesian). One per thread: a recognizer reads one page at a time.
    /// </remarks>
    public static OcrEngine? CreateEngine()
        => OcrEngine.TryCreateFromUserProfileLanguages()
           ?? OcrEngine.AvailableRecognizerLanguages.Select(OcrEngine.TryCreateFromLanguage).FirstOrDefault(engine => engine != null);

    public static bool IsAvailable => OcrEngine.AvailableRecognizerLanguages.Count > 0;

    /// <summary>Smaller than this and there is nothing worth reading.</summary>
    private const int MinimumEdge = 48;

    /// <summary>The clockwise turn that makes <paramref name="page"/> upright, or null.</summary>
    /// <param name="current">The page's current turn, tried first since it is usually right.</param>
    public static async Task<int?> DetectAsync(OcrEngine engine, GrayBitmap page, int current, CancellationToken cancellationToken)
    {
        if (Math.Min(page.Width, page.Height) < MinimumEdge) return null;

        var scores = new ReadingScore[4];

        // Upside down is the next most common case.
        foreach (var offset in new[] { 0, 180, 90, 270 })
        {
            cancellationToken.ThrowIfCancellationRequested();

            var turn = Models.DocumentItem.NormaliseRotation(current + offset);
            var score = await ReadAsync(engine, page.Turned(turn));
            if (score.IsConclusive) return turn;

            scores[turn / 90] = score;
        }

        return ReadingScore.Decide(scores);
    }

    private static async Task<ReadingScore> ReadAsync(OcrEngine engine, GrayBitmap page)
    {
        using var bitmap = page.ToSoftwareBitmap();
        var result = await engine.RecognizeAsync(bitmap);
        return ReadingScore.Of(result.Lines.SelectMany(line => line.Words).Select(word => word.Text));
    }
}

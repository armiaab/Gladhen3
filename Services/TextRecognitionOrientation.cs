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

    /// <summary>Decodes to grey, scaled down so the long side is at most <paramref name="maxEdge"/> pixels.</summary>
    /// <param name="exif">Respected for a photo, as the viewer shows it; ignored for a rendered page, which has none.</param>
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

/// <summary>How much of what text recognition read looks like words, which is what tells upright text from upside down.</summary>
/// <remarks>
/// Windows' recognizer reads only upright text. Turned a quarter, a page yields a handful of
/// stray characters; upside down it yields plenty of text, but not words - "and" comes out as
/// "pue", "of" as "JO". So each candidate turn is read, and scored on how many clean words and
/// common words it gave.
/// </remarks>
internal readonly record struct ReadingScore(int Words, int Clean, int Common)
{
    /// <summary>
    /// Short, frequent words in the languages this app is used in. Three letters or more, and
    /// none of them reads as another one upside down.
    /// </summary>
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

    /// <summary>
    /// The letters an upside-down digit is read as: 0 as O, 1 as I or l, 2 as Z, 3 as E, 4 as h,
    /// 5 as S, 7 as L, 8 as B.
    /// </summary>
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

    /// <summary>
    /// Points: two for a clean word, six for a common one, one for a figure - figures count for
    /// less because a page of them is rarer evidence than a page of words.
    /// </summary>
    public int Value => (2 * Clean) + (6 * Common) + Figures;

    /// <summary>
    /// So many common words that this cannot be anything but upright text, without reading the
    /// other three ways. Upside-down readings of whole pages never managed more than two.
    /// </summary>
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
    /// Three letters or more, all letters, cased the way words are: lower, Capitalised or upper.
    /// Rules out the "u!", "'6unpueg" and "OJ!I" an upside-down page produces, and a run of one
    /// repeated letter, which reads the same either way up.
    /// </summary>
    /// <remarks>
    /// Nor a word spelt only with letters a digit turns into upside down. A table of figures read
    /// the wrong way up is full of them - 2025 comes out as "SZOZ" - and it was those, against no
    /// words at all the right way up, that turned a page of numbers upside down every time.
    /// </remarks>
    private static bool IsClean(string word)
    {
        if (word.Length < 3 || !word.All(char.IsLetter)) return false;
        if (word.Skip(1).All(c => c == word[0])) return false;
        if (word.All(DigitLookalikes.Contains)) return false;

        return word.Skip(1).All(char.IsLower) || !word.Any(char.IsLower);
    }

    /// <summary>
    /// A figure of three digits or more - "1,245.50", "2025-03" - with a 2, 3, 4, 5 or 7 in it.
    /// </summary>
    /// <remarks>
    /// Those five digits read as letters upside down, so a figure holding one cannot come out of
    /// the flipped reading as a figure too. 0, 1, 6, 8 and 9 read as digits either way.
    /// </remarks>
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
    /// <summary>A recognizer for the user's own languages, or any installed one, or null if Windows has none.</summary>
    /// <remarks>
    /// Any will do: the question is which way up the text is, not what it says, and a Latin-script
    /// recognizer reads Indonesian - for which Windows has none of its own - perfectly well.
    /// One per thread: a recognizer refuses a second page while it is still reading the first.
    /// </remarks>
    public static OcrEngine? CreateEngine()
        => OcrEngine.TryCreateFromUserProfileLanguages()
           ?? OcrEngine.AvailableRecognizerLanguages.Select(OcrEngine.TryCreateFromLanguage).FirstOrDefault(engine => engine != null);

    public static bool IsAvailable => OcrEngine.AvailableRecognizerLanguages.Count > 0;

    /// <summary>Smaller than this and there is nothing worth reading.</summary>
    private const int MinimumEdge = 48;

    /// <summary>The clockwise turn that stands <paramref name="page"/> upright, or null if its text does not say.</summary>
    /// <param name="current">
    /// The turn the page has now. Read first, because it is usually right already, and one
    /// conclusive reading is enough to say so.
    /// </param>
    public static async Task<int?> DetectAsync(OcrEngine engine, GrayBitmap page, int current, CancellationToken cancellationToken)
    {
        if (Math.Min(page.Width, page.Height) < MinimumEdge) return null;

        var scores = new ReadingScore[4];

        // Upside down is the commonest mistake after none at all, so it is tried second.
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

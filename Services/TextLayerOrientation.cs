using PdfSharp.Pdf;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Gladhen3.Services;

/// <summary>Which way a PDF page's text runs, as its content stream draws it.</summary>
internal sealed class TextLayerReading
{
    /// <summary>
    /// Characters by the clockwise turn that would stand them upright on the page as it is shown:
    /// index 0 for text that already reads left to right, 1 for a quarter turn, and so on.
    /// </summary>
    public double[] ByTurn { get; } = new double[4];

    /// <summary>How many of those characters are invisible: an OCR layer laid over a scan.</summary>
    public double Invisible { get; set; }

    /// <summary>How much of the page images cover, as a share of its area. Overlapping images count twice.</summary>
    public double ImageCoverage { get; set; }

    public double Total => ByTurn.Sum();

    /// <summary>The turn that most of the text asks for, if enough of it agrees.</summary>
    /// <returns>Null when there are fewer than <paramref name="minimum"/> characters or no clear majority.</returns>
    public int? Turn(double minimum)
    {
        var total = Total;
        if (total < minimum) return null;

        var best = Array.IndexOf(ByTurn, ByTurn.Max());
        return ByTurn[best] >= total * MajorityShare ? best * 90 : null;
    }

    /// <summary>
    /// Body text agrees with itself; what does not is a rotated table heading, a margin note or
    /// a spine label, and those are never most of a page.
    /// </summary>
    private const double MajorityShare = 0.8;
}

/// <summary>
/// Reads the direction of a page's text from its content stream: each piece of text is drawn
/// through a matrix, and the matrix says which way its baseline points.
/// </summary>
/// <remarks>
/// Exact and cheap where it applies - a born-digital page, or a scan that was run through OCR -
/// and blind where it does not: text drawn as outlines, or a scan with no text layer at all.
/// </remarks>
internal static class TextLayerOrientation
{
    /// <summary>Form XObjects nest; a malformed file can make them nest forever.</summary>
    private const int MaxFormDepth = 12;

    /// <summary>How far off a quarter turn a baseline may lean and still count as one. A watermark at 45 degrees counts as none.</summary>
    private const double QuarterTolerance = 25;

    /// <exception cref="PdfSharpException">The content stream cannot be parsed.</exception>
    public static TextLayerReading Read(PdfPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var reading = new TextLayerReading();
        var rotate = page.Elements.GetInteger("/Rotate");
        var walker = new Walker(reading, rotate % 90 == 0 ? Models.DocumentItem.NormaliseRotation(rotate) : 0, PageArea(page));
        walker.Walk(ContentReader.ReadContent(page), page.Elements.GetDictionary("/Resources") ?? page.Resources, Matrix.Identity, 0);
        return reading;
    }

    private static double PageArea(PdfPage page)
    {
        var area = page.Width.Point * page.Height.Point;
        return area > 0 ? area : 612d * 792d;
    }

    /// <summary>The linear part of a PDF matrix, [a b c d]; translation never changes a direction.</summary>
    /// <remarks>Row vectors, as PDF writes them: a point maps as [x y] × M.</remarks>
    private readonly record struct Matrix(double A, double B, double C, double D)
    {
        public static Matrix Identity => new(1, 0, 0, 1);

        /// <summary>This, then <paramref name="next"/>.</summary>
        public Matrix Then(Matrix next) => new(
            (A * next.A) + (B * next.C), (A * next.B) + (B * next.D),
            (C * next.A) + (D * next.C), (C * next.B) + (D * next.D));

        public double Area => Math.Abs((A * D) - (B * C));
    }

    /// <summary>The graphics state that matters here, saved by q and restored by Q.</summary>
    private struct State
    {
        public Matrix Ctm;
        public double FontSize;
        public double HorizontalScale;
        public int RenderMode;
    }

    private sealed class Walker(TextLayerReading reading, int pageRotate, double pageArea)
    {
        public void Walk(CSequence content, PdfDictionary? resources, Matrix ctm, int depth)
        {
            var state = new State { Ctm = ctm, FontSize = 1, HorizontalScale = 1 };
            var saved = new Stack<State>();
            var textMatrix = Matrix.Identity;

            foreach (var op in Operators(content))
            {
                var operands = op.Operands;
                switch (op.Name)
                {
                    case "q":
                        saved.Push(state);
                        break;
                    case "Q":
                        if (saved.Count > 0) state = saved.Pop();
                        break;
                    case "cm" when operands.Count >= 4:
                        state.Ctm = MatrixOf(operands).Then(state.Ctm);
                        break;
                    case "BT":
                        textMatrix = Matrix.Identity;
                        break;
                    case "Tm" when operands.Count >= 4:
                        textMatrix = MatrixOf(operands);
                        break;
                    case "Tf" when operands.Count >= 2:
                        state.FontSize = Number(operands[1]);
                        break;
                    case "Tz" when operands.Count >= 1:
                        state.HorizontalScale = Number(operands[0]) / 100d;
                        break;
                    case "Tr" when operands.Count >= 1:
                        state.RenderMode = (int)Number(operands[0]);
                        break;
                    case "Tj" or "'" when operands.Count >= 1:
                        Show(Characters(operands[^1]), state, textMatrix);
                        break;
                    case "\"" when operands.Count >= 3:
                        Show(Characters(operands[2]), state, textMatrix);
                        break;
                    case "TJ" when operands.Count >= 1:
                        Show(Characters(operands[0]), state, textMatrix);
                        break;
                    case "Do" when operands.Count >= 1 && operands[0] is CName name:
                        Draw(name.Name, resources, state.Ctm, depth);
                        break;
                    case "EI":
                        // An inline image is drawn into the unit square, like any other.
                        reading.ImageCoverage += state.Ctm.Area / pageArea;
                        break;
                }
            }
        }

        /// <summary>
        /// Counts the characters, filed under the turn that would stand them upright.
        /// </summary>
        /// <remarks>
        /// The text rendering matrix's first row is the direction of the glyphs' own x axis on
        /// the page. That is the baseline for horizontal writing and still the way an upright
        /// glyph faces in vertical writing, so it answers the same question for both. The page's
        /// own /Rotate then turns it once more, clockwise, on its way to the screen.
        /// </remarks>
        private void Show(int characters, State state, Matrix textMatrix)
        {
            if (characters == 0) return;

            var glyph = new Matrix(state.FontSize * state.HorizontalScale, 0, 0, state.FontSize).Then(textMatrix).Then(state.Ctm);
            if (glyph.A == 0 && glyph.B == 0) return;

            var degrees = Math.Atan2(glyph.B, glyph.A) * 180 / Math.PI;
            var quarters = (int)Math.Round(degrees / 90);
            if (Math.Abs(degrees - (quarters * 90)) > QuarterTolerance) return;

            // Counter-clockwise on the page, less the clockwise turn it is shown with.
            var turn = Models.DocumentItem.NormaliseRotation((quarters * 90) - pageRotate);
            reading.ByTurn[turn / 90] += characters;
            if (state.RenderMode is 3 or 7) reading.Invisible += characters;
        }

        private void Draw(string name, PdfDictionary? resources, Matrix ctm, int depth)
        {
            if (resources?.Elements.GetDictionary("/XObject")?.Elements.GetDictionary(name) is not { } xobject) return;

            switch (PdfService.ReadName(xobject, "/Subtype"))
            {
                case "/Image":
                    reading.ImageCoverage += ctm.Area / pageArea;
                    break;

                case "/Form" when depth < MaxFormDepth && xobject.Stream != null:
                    var matrix = xobject.Elements.GetArray("/Matrix") is { Elements.Count: >= 4 } m
                        ? new Matrix(m.Elements.GetReal(0), m.Elements.GetReal(1), m.Elements.GetReal(2), m.Elements.GetReal(3))
                        : Matrix.Identity;

                    // A form without resources of its own uses its parent's.
                    Walk(ContentReader.ReadContent(xobject.Stream.UnfilteredValue),
                        xobject.Elements.GetDictionary("/Resources") ?? resources,
                        matrix.Then(ctm),
                        depth + 1);
                    break;
            }
        }
    }

    /// <summary>Every operator in order, including any the parser grouped into nested sequences.</summary>
    private static IEnumerable<COperator> Operators(CSequence sequence)
    {
        foreach (var item in sequence)
        {
            if (item is COperator op)
            {
                yield return op;
            }
            else if (item is CSequence nested and not CArray)
            {
                foreach (var inner in Operators(nested)) yield return inner;
            }
        }
    }

    private static Matrix MatrixOf(CSequence operands)
        => new(Number(operands[0]), Number(operands[1]), Number(operands[2]), Number(operands[3]));

    private static double Number(CObject value) => value switch
    {
        CInteger integer => integer.Value,
        CReal real => real.Value,
        _ => 0
    };

    /// <summary>How many character codes a string, or a TJ array of strings and kerning, shows.</summary>
    /// <remarks>Bytes, not glyphs: a two-byte CID font counts double, which only scales its page's total.</remarks>
    private static int Characters(CObject shown) => shown switch
    {
        CString text => text.Value.Length,
        CArray parts => parts.OfType<CString>().Sum(part => part.Value.Length),
        _ => 0
    };
}

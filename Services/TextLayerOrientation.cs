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
    /// <summary>Character counts by the clockwise turn that would make them upright (index 0 = already upright).</summary>
    public double[] ByTurn { get; } = new double[4];

    /// <summary>Invisible characters, i.e. an OCR layer over a scan.</summary>
    public double Invisible { get; set; }

    /// <summary>Share of the page area covered by images; overlaps count twice.</summary>
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

    /// <summary>Body text dominates; rotated headings, margin notes and labels never make a majority.</summary>
    private const double MajorityShare = 0.8;
}

/// <summary>Reads text direction from a page's content stream, via each text matrix's baseline.</summary>
/// <remarks>Exact for born-digital or OCR'd pages; blind to outlined text or scans without a text layer.</remarks>
internal static class TextLayerOrientation
{
    /// <summary>Form XObjects nest; a malformed file can make them nest forever.</summary>
    private const int MaxFormDepth = 12;

    /// <summary>How far a baseline may lean from a quarter turn and still count; a 45° watermark counts as none.</summary>
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

        /// <summary>Counts the characters under the turn that would make them upright.</summary>
        /// <remarks>
        /// The text rendering matrix's first row is the glyph x axis, which answers this for both
        /// horizontal and vertical writing; the page's /Rotate is applied on top.
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

    /// <summary>Character codes shown by a string or TJ array.</summary>
    /// <remarks>Counts bytes, so two-byte CID fonts count double; that only scales the page total.</remarks>
    private static int Characters(CObject shown) => shown switch
    {
        CString text => text.Value.Length,
        CArray parts => parts.OfType<CString>().Sum(part => part.Value.Length),
        _ => 0
    };
}

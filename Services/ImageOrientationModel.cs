using Serilog;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.AI.MachineLearning;
using Windows.Graphics.Imaging;

namespace Gladhen3.Services;

/// <summary>A picture in BGRA, scaled roughly to what a model takes.</summary>
internal sealed class BgraPicture(int width, int height, byte[] pixels)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Pixels { get; } = pixels;

    /// <summary>
    /// Decodes at about the size <paramref name="spec"/> needs, so the model's own fitting is
    /// nearly one to one and the downscaling is WIC's.
    /// </summary>
    public static async Task<BgraPicture> DecodeAsync(BitmapDecoder decoder, OrientationModelSpec spec, ExifOrientationMode exif)
    {
        var swapped = exif == ExifOrientationMode.RespectExifOrientation && decoder.OrientedPixelWidth != decoder.PixelWidth;
        var (w, h) = swapped ? ((int)decoder.PixelHeight, (int)decoder.PixelWidth) : ((int)decoder.PixelWidth, (int)decoder.PixelHeight);

        var scale = spec.Resize switch
        {
            ModelResize.Crop => Math.Min(spec.Width, spec.Height) / spec.CropShare / Math.Min(w, h),
            ModelResize.Fit => Math.Max(spec.Width, spec.Height) / (double)Math.Max(w, h),
            _ => Math.Max(spec.Width, spec.Height) / (double)Math.Min(w, h)
        };
        var (sw, sh) = (Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));

        // The transform's size is the stored one; an EXIF quarter turn swaps it afterwards.
        var transform = new BitmapTransform { InterpolationMode = BitmapInterpolationMode.Fant };
        (transform.ScaledWidth, transform.ScaledHeight) = swapped ? ((uint)sh, (uint)sw) : ((uint)sw, (uint)sh);

        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform, exif, ColorManagementMode.ColorManageToSRgb);
        return new BgraPicture(sw, sh, data.DetachPixelData());
    }

    /// <summary>A copy turned clockwise by <paramref name="degrees"/>, a whole number of quarter turns.</summary>
    public BgraPicture Turned(int degrees)
    {
        degrees = Models.DocumentItem.NormaliseRotation(degrees);
        if (degrees == 0) return this;

        var (w, h) = (Width, Height);
        var (turnedW, turnedH) = degrees == 180 ? (w, h) : (h, w);
        var turned = new byte[Pixels.Length];

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var (tx, ty) = degrees switch
                {
                    90 => (h - 1 - y, x),
                    180 => (w - 1 - x, h - 1 - y),
                    _ => (y, w - 1 - x)
                };
                Buffer.BlockCopy(Pixels, ((y * w) + x) * 4, turned, ((ty * turnedW) + tx) * 4, 4);
            }
        }

        return new BgraPicture(turnedW, turnedH, turned);
    }

    /// <summary>
    /// Writes the picture into <paramref name="tensor"/> at <paramref name="offset"/>, fitted
    /// and normalised as <paramref name="spec"/> says.
    /// </summary>
    public void Fill(float[] tensor, int offset, OrientationModelSpec spec, bool channelsLast)
    {
        var (outW, outH) = (spec.Width, spec.Height);
        double scaleX, scaleY;
        switch (spec.Resize)
        {
            case ModelResize.Crop:
                scaleX = scaleY = Math.Min(outW, outH) / spec.CropShare / Math.Min(Width, Height);
                break;
            case ModelResize.Fit:
                scaleX = scaleY = Math.Min(outW / (double)Width, outH / (double)Height);
                break;
            default:
                (scaleX, scaleY) = (outW / (double)Width, outH / (double)Height);
                break;
        }

        var plane = outW * outH;
        var (c0, c2) = spec.IsBgr ? (0, 2) : (2, 0);
        for (var y = 0; y < outH; y++)
        {
            // Centred: the crop is cut from the middle, a fitted picture sits in the middle.
            var sy = ((y + 0.5 - (outH / 2d)) / scaleY) + (Height / 2d) - 0.5;
            for (var x = 0; x < outW; x++)
            {
                var sx = ((x + 0.5 - (outW / 2d)) / scaleX) + (Width / 2d) - 0.5;
                var inside = sx > -1 && sy > -1 && sx < Width && sy < Height;

                for (var c = 0; c < 3; c++)
                {
                    // Channel 0 of the tensor is red for RGB, blue for BGR; BGRA keeps blue at 0.
                    var source = c == 0 ? c0 : c == 2 ? c2 : 1;
                    var value = inside ? Sample(sx, sy, source) : 0f;
                    var normalised = ((value * spec.Scale) - spec.Mean[c]) / spec.Std[c];
                    var index = channelsLast ? offset + (((y * outW) + x) * 3) + c : offset + (c * plane) + (y * outW) + x;
                    tensor[index] = normalised;
                }
            }
        }
    }

    /// <summary>Bilinear, clamped at the edges.</summary>
    private float Sample(double x, double y, int channel)
    {
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var fx = (float)(x - x0);
        var fy = (float)(y - y0);

        float At(int px, int py) => Pixels[((Math.Clamp(py, 0, Height - 1) * Width) + Math.Clamp(px, 0, Width - 1)) * 4 + channel];

        var top = At(x0, y0) + ((At(x0 + 1, y0) - At(x0, y0)) * fx);
        var bottom = At(x0, y0 + 1) + ((At(x0 + 1, y0 + 1) - At(x0, y0 + 1)) * fx);
        return top + ((bottom - top) * fy);
    }
}

/// <summary>What a model made of one picture, read all four ways round.</summary>
/// <param name="Turn">The clockwise turn that stands it upright, or null if the model is not sure.</param>
/// <param name="Confidence">The winning turn's probability, averaged over the four readings.</param>
/// <param name="Agreeing">How many of the four readings, each on its own, chose the same turn.</param>
internal readonly record struct ModelReading(int? Turn, double Confidence, int Agreeing);

/// <summary>An ONNX orientation classifier loaded into Windows ML.</summary>
/// <remarks>
/// Windows ML is part of Windows itself - the same ONNX Runtime, shipped with the OS - so a model
/// costs the package nothing beyond its own file. Which ONNX opsets it runs depends on the
/// Windows version; the built-in model is opset 7, which all of them do.
/// </remarks>
public sealed class ImageOrientationModel : IDisposable
{
    private readonly LearningModel _model;
    private readonly string _input;
    private readonly string _output;
    private readonly bool _channelsLast;
    private readonly bool _batches;

    internal OrientationModelSpec Spec { get; }

    /// <summary>The fixed width and height the model takes, if it says.</summary>
    internal (int Width, int Height)? InputSize { get; }

    private ImageOrientationModel(LearningModel model, OrientationModelSpec spec, string path)
    {
        _model = model;
        Spec = spec;

        if (model.InputFeatures.Count == 0 || model.InputFeatures[0] is not TensorFeatureDescriptor { TensorKind: TensorKind.Float, Shape.Count: 4 } input ||
            model.OutputFeatures.Count == 0 || model.OutputFeatures[0] is not TensorFeatureDescriptor { TensorKind: TensorKind.Float } output)
        {
            model.Dispose();
            throw new OrientationModelException(OrientationModelFailureReason.WrongShape, $"'{System.IO.Path.GetFileName(path)}' does not take a float image tensor.") { Path = path };
        }

        // NCHW, as nearly every export is, unless the channels are plainly last.
        var shape = input.Shape;
        _channelsLast = shape[3] == 3 && shape[1] != 3;
        if (!_channelsLast && shape[1] != 3)
        {
            model.Dispose();
            throw new OrientationModelException(OrientationModelFailureReason.WrongShape, $"'{System.IO.Path.GetFileName(path)}' does not take three colour channels.") { Path = path };
        }

        var (h, w) = _channelsLast ? (shape[1], shape[2]) : (shape[2], shape[3]);
        InputSize = h > 0 && w > 0 ? ((int)w, (int)h) : null;
        _batches = shape[0] == -1;
        _input = input.Name;
        _output = output.Name;
    }

    /// <exception cref="OrientationModelException">The model cannot be loaded or is not an image classifier.</exception>
    internal static ImageOrientationModel Load(string path, OrientationModelSpec spec)
    {
        if (!File.Exists(path))
            throw new OrientationModelException(OrientationModelFailureReason.NotFound, $"'{System.IO.Path.GetFileName(path)}' is missing.") { Path = path };

        LearningModel model;
        try
        {
            model = LearningModel.LoadFromFilePath(path);
        }
        // What Windows ML throws for a file it cannot run: not ONNX at all, or operators this
        // version of Windows does not have.
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            var opset = OnnxHeader.Opset(path);
            Log.Warning(ex, "Windows ML could not load {Path} (opset {Opset})", path, opset);
            throw new OrientationModelException(
                opset > MinimumWindowsOpset ? OrientationModelFailureReason.NeedsNewerWindows : OrientationModelFailureReason.LoadFailed,
                $"Windows could not load '{System.IO.Path.GetFileName(path)}'.", ex) { Path = path, Opset = opset };
        }

        return new ImageOrientationModel(model, spec, path);
    }

    /// <summary>Loads a model before anything is known about how to feed it, to find that out.</summary>
    internal static ImageOrientationModel LoadUnchecked(string path) => Load(path, OrientationModels.BuiltInSpec);

    /// <summary>The newest ONNX opset the oldest supported Windows (10, version 1809) runs.</summary>
    private const long MinimumWindowsOpset = 7;

    /// <summary>One per thread: a session evaluates one picture at a time.</summary>
    internal Session CreateSession() => new(this);

    public void Dispose() => _model.Dispose();

    internal sealed class Session : IDisposable
    {
        private readonly ImageOrientationModel _owner;
        private readonly LearningModelSession _session;

        public Session(ImageOrientationModel owner)
        {
            _owner = owner;
            _session = new LearningModelSession(owner._model, new LearningModelDevice(LearningModelDeviceKind.Cpu));
        }

        public OrientationModelSpec Spec => _owner.Spec;

        /// <summary>The model's four probabilities for one picture, read with <paramref name="spec"/>.</summary>
        public float[] Probabilities(BgraPicture picture, OrientationModelSpec spec) => Evaluate([picture], spec)[0];

        /// <summary>
        /// Reads the picture at all four quarter turns and combines them.
        /// </summary>
        /// <remarks>
        /// Each reading says how the page it saw is turned; turned back by the amount it was
        /// turned for that reading, all four should name the same correction. Averaging them
        /// makes the answer the same whichever way the picture arrived, and requiring all four
        /// to agree is what keeps a model away from pictures it does not understand - a photo
        /// with no text gets four different guesses, a page of text gets one answer four times.
        /// </remarks>
        public ModelReading Read(BgraPicture picture)
        {
            var spec = _owner.Spec;
            var probabilities = Evaluate([picture, picture.Turned(90), picture.Turned(180), picture.Turned(270)], spec);
            var outputFor = spec.OutputForQuarter();

            var mean = new double[4];
            var votes = new int[4];
            for (var r = 0; r < 4; r++)
            {
                // The page shown turned r quarters further: for correction c it should look turned (r - c).
                for (var c = 0; c < 4; c++) mean[c] += probabilities[r][outputFor[(r - c + 4) % 4]] / 4;

                var winner = Array.IndexOf(probabilities[r], probabilities[r].Max());
                var shownTurn = Models.DocumentItem.NormaliseRotation(spec.Labels[winner]) / 90;
                votes[(r - shownTurn + 4) % 4]++;
            }

            var best = Array.IndexOf(mean, mean.Max());
            var sure = mean[best] >= spec.MinimumConfidence && votes[best] == 4;
            return new ModelReading(sure ? best * 90 : null, mean[best], votes[best]);
        }

        private float[][] Evaluate(BgraPicture[] pictures, OrientationModelSpec spec)
        {
            var per = 3 * spec.Width * spec.Height;
            var results = new float[pictures.Length][];

            // All four at once when the model takes a batch, one at a time when it does not.
            var batch = _owner._batches ? pictures.Length : 1;
            for (var start = 0; start < pictures.Length; start += batch)
            {
                var data = new float[batch * per];
                for (var k = 0; k < batch; k++) pictures[start + k].Fill(data, k * per, spec, _owner._channelsLast);

                long[] shape = _owner._channelsLast ? [batch, spec.Height, spec.Width, 3] : [batch, 3, spec.Height, spec.Width];
                using var tensor = TensorFloat.CreateFromArray(shape, data);
                var binding = new LearningModelBinding(_session);
                binding.Bind(_owner._input, tensor);

                var result = _session.Evaluate(binding, string.Empty);
                using var answer = (TensorFloat)result.Outputs[_owner._output];
                var output = answer.GetAsVectorView().ToArray();
                if (output.Length != batch * 4)
                    throw new OrientationModelException(OrientationModelFailureReason.WrongShape, $"The model gave {output.Length / batch} answers, not 4.");

                for (var k = 0; k < batch; k++) results[start + k] = AsProbabilities(output.AsSpan(k * 4, 4));
            }

            return results;
        }

        public void Dispose() => _session.Dispose();
    }

    /// <summary>Softmax, unless the model already ends in one.</summary>
    private static float[] AsProbabilities(ReadOnlySpan<float> values)
    {
        var sum = 0f;
        var already = true;
        foreach (var v in values)
        {
            sum += v;
            already &= v is >= 0f and <= 1f;
        }
        if (already && Math.Abs(sum - 1f) < 0.02f) return values.ToArray();

        var max = values[0];
        foreach (var v in values) max = Math.Max(max, v);
        var e = new float[values.Length];
        var total = 0f;
        for (var i = 0; i < values.Length; i++) total += e[i] = MathF.Exp(values[i] - max);
        for (var i = 0; i < e.Length; i++) e[i] /= total;
        return e;
    }
}

/// <summary>Just enough of the ONNX protobuf to read which opset a model needs.</summary>
internal static class OnnxHeader
{
    /// <summary>The default-domain opset a model imports, or null if the file is not readable as ONNX.</summary>
    public static long? Opset(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var length = (int)Math.Min(stream.Length, 64 * 1024 * 1024);
            var bytes = new byte[length];
            stream.ReadExactly(bytes);
            return Opset(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <remarks>ModelProto field 8 is opset_import: { 1: domain, 2: version }, "" being ai.onnx.</remarks>
    internal static long? Opset(byte[] model)
    {
        long? found = null;
        var pos = 0;
        while (pos < model.Length && TryField(model, ref pos, out var field, out var wire, out var start, out var length))
        {
            if (field != 8 || wire != 2) continue;

            string domain = string.Empty;
            long version = 0;
            var inner = start;
            while (inner < start + length && TryField(model, ref inner, out var f, out var w, out var s, out var l))
            {
                if (f == 1 && w == 2) domain = System.Text.Encoding.UTF8.GetString(model, s, l);
                if (f == 2 && w == 0) version = l;
            }
            if (domain is "" or "ai.onnx") found = version;
        }
        return found;
    }

    /// <summary>Reads one field. For a varint the value comes back in <paramref name="length"/>.</summary>
    private static bool TryField(byte[] b, ref int pos, out int field, out int wire, out int start, out int length)
    {
        (field, wire, start, length) = (0, 0, 0, 0);
        if (!TryVarint(b, ref pos, out var key)) return false;
        field = (int)(key >> 3);
        wire = (int)(key & 7);

        switch (wire)
        {
            case 0:
                if (!TryVarint(b, ref pos, out var value)) return false;
                length = (int)Math.Min(value, int.MaxValue);
                return true;
            case 1:
                pos += 8;
                return pos <= b.Length;
            case 5:
                pos += 4;
                return pos <= b.Length;
            case 2:
                if (!TryVarint(b, ref pos, out var size) || size > (ulong)(b.Length - pos)) return false;
                (start, length) = (pos, (int)size);
                pos += (int)size;
                return true;
            default:
                return false;
        }
    }

    private static bool TryVarint(byte[] b, ref int pos, out ulong value)
    {
        value = 0;
        for (var shift = 0; shift < 64 && pos < b.Length; shift += 7)
        {
            var x = b[pos++];
            value |= (ulong)(x & 0x7F) << shift;
            if ((x & 0x80) == 0) return true;
        }
        return false;
    }
}

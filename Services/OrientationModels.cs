using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Windows.AI.MachineLearning;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Gladhen3.Services;

public enum OrientationModelFailureReason
{
    Unknown = 0,

    /// <summary>The model file is no longer where it was installed.</summary>
    NotFound,

    /// <summary>Windows could not load the file as an ONNX model.</summary>
    LoadFailed,

    /// <summary>
    /// Windows could not load the model, and it uses a newer ONNX opset than the oldest Windows
    /// versions run - most likely the reason.
    /// </summary>
    NeedsNewerWindows,

    /// <summary>The model does not take a colour image or does not give four answers.</summary>
    WrongShape,

    /// <summary>The model could not tell which way up the sample pages were.</summary>
    FailedCheck,

    /// <summary>A model with the same file name is already installed.</summary>
    AlreadyInstalled,

    /// <summary>The model could not be removed because a check is still using it.</summary>
    InUse
}

/// <summary>An orientation model that could not be loaded, installed or removed.</summary>
/// <remarks>Carries a reason rather than a sentence, so the UI chooses the wording.</remarks>
public class OrientationModelException : Exception
{
    public OrientationModelException()
    {
    }

    public OrientationModelException(string message)
        : base(message)
    {
    }

    public OrientationModelException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public OrientationModelException(OrientationModelFailureReason reason, string message, Exception? innerException = null)
        : base(message, innerException)
        => Reason = reason;

    public OrientationModelFailureReason Reason { get; } = OrientationModelFailureReason.Unknown;

    public string? Path { get; init; }

    /// <summary>The model's ONNX opset, when that is what Windows probably objected to.</summary>
    public long? Opset { get; init; }
}

/// <summary>How a picture is fitted to a model's input.</summary>
public enum ModelResize
{
    /// <summary>Scaled so the short side is a little more than the input, then the centre cut out.</summary>
    Crop,

    /// <summary>Scaled so the long side fits, the rest padded with black.</summary>
    Fit,

    /// <summary>Scaled to the input exactly, out of proportion.</summary>
    Stretch
}

/// <summary>How to feed a model a picture, and what its four answers mean.</summary>
/// <remarks>
/// Written beside an installed model as <c>&lt;model&gt;.json</c>. The same file may come with a
/// model the user adds; it is then checked rather than believed.
/// </remarks>
public sealed record OrientationModelSpec
{
    public string Name { get; init; } = string.Empty;

    public int Width { get; init; } = 224;

    public int Height { get; init; } = 224;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ModelResize Resize { get; init; } = ModelResize.Crop;

    /// <summary>For <see cref="ModelResize.Crop"/>: the share of the scaled short side that is kept, as 224 of 256.</summary>
    public double CropShare { get; init; } = 224d / 256d;

    /// <summary>"RGB" or "BGR".</summary>
    public string Channels { get; init; } = "RGB";

    /// <summary>Each channel is taken as (pixel × Scale − Mean) / Std.</summary>
    public float Scale { get; init; } = 1f / 255f;

    public float[] Mean { get; init; } = [0.485f, 0.456f, 0.406f];

    public float[] Std { get; init; } = [0.229f, 0.224f, 0.225f];

    /// <summary>
    /// For each of the model's four outputs, how far the page it saw is turned clockwise, in
    /// degrees. Learned from the sample pages when a model is installed.
    /// </summary>
    public int[] Labels { get; init; } = [0, 90, 180, 270];

    /// <summary>
    /// The averaged probability a reading needs before a page is turned. Below it, and whenever
    /// the four readings disagree, the page is left as it is.
    /// </summary>
    public double MinimumConfidence { get; init; } = 0.7;

    [JsonIgnore]
    public bool IsBgr => string.Equals(Channels, "BGR", StringComparison.OrdinalIgnoreCase);

    /// <summary>The labels as indices: which output says "turned by quarter q".</summary>
    internal int[] OutputForQuarter()
    {
        var outputs = new int[4];
        for (var k = 0; k < Labels.Length; k++) outputs[Models.DocumentItem.NormaliseRotation(Labels[k]) / 90] = k;
        return outputs;
    }

    internal bool HasValidLabels
        => Labels.Length == 4 && Labels.All(l => l % 90 == 0) && Labels.Select(l => Models.DocumentItem.NormaliseRotation(l)).Distinct().Count() == 4;
}

/// <summary>One entry in the list of models a user has added.</summary>
/// <param name="LoadFailure">Why the model cannot be used, or null if it can.</param>
public sealed record OrientationModelInfo(string Id, string FileName, string? Name, OrientationModelFailureReason? LoadFailure);

/// <summary>
/// The ways normalize can tell which way up a picture of a page is: the model that ships with the
/// app, models the user adds, or Windows' own text recognition.
/// </summary>
/// <remarks>
/// A model is any ONNX image classifier with four outputs, one per quarter turn - there are
/// several on Hugging Face. Models run on Windows ML, which is part of Windows, so they cost the
/// package nothing but the model file. Each one a user adds is tried on sample pages turned all
/// four ways before it is accepted, which also works out how it wants to be fed and what its
/// outputs mean: a model that turns pages the wrong way is far worse than none.
/// </remarks>
public static class OrientationModels
{
    /// <summary>PP-LCNet_x1_0_doc_ori, PaddlePaddle's document orientation classifier.</summary>
    public const string BuiltInId = "builtin";

    /// <summary>No model: Windows' text recognition reads the page every way round.</summary>
    public const string TextRecognitionId = "ocr";

    public const string ModelPrefix = "model:";

    public const string BuiltInFileName = "pp-lcnet-doc-ori.onnx";

    /// <summary>
    /// PP-LCNet_x1_0_doc_ori as published by PaddlePaddle: ImageNet normalisation, 224 pixels cut
    /// from the centre of a 256-pixel scale, labels "0", "90", "180", "270" for a page turned
    /// that far clockwise. Its ONNX opset is 7, which every Windows this app runs on supports.
    /// </summary>
    public static OrientationModelSpec BuiltInSpec { get; } = new() { Name = "PP-LCNet document orientation" };

    private static string? _modelDirectory;
    private static string? _assetDirectory;

    /// <summary>Where added models are kept: a folder of the app's own local data.</summary>
    public static string ModelDirectory
    {
        get => _modelDirectory ??= Path.Combine(ApplicationData.Current.LocalFolder.Path, "OrientationModels");
        internal set => _modelDirectory = value;
    }

    /// <summary>The built-in model and the sample pages, installed with the app.</summary>
    public static string AssetDirectory
    {
        get => _assetDirectory ??= Path.Combine(AppContext.BaseDirectory, "Assets", "Models");
        internal set => _assetDirectory = value;
    }

    public static bool IsModel(string? id) => id?.StartsWith(ModelPrefix, StringComparison.Ordinal) == true;

    /// <summary>Opens the model with this id. An unknown id means the built-in one.</summary>
    /// <exception cref="OrientationModelException">The model cannot be loaded.</exception>
    public static ImageOrientationModel Open(string? id)
    {
        if (!IsModel(id)) return ImageOrientationModel.Load(Path.Combine(AssetDirectory, BuiltInFileName), BuiltInSpec);

        var path = ModelPath(id!);
        if (!File.Exists(path))
            throw new OrientationModelException(OrientationModelFailureReason.NotFound, $"'{Path.GetFileName(path)}' is not installed.") { Path = path };

        return ImageOrientationModel.Load(path, ReadSpec(SpecPath(path)) ?? throw new OrientationModelException(
            OrientationModelFailureReason.FailedCheck, $"'{Path.GetFileName(path)}' has no description; add it again.") { Path = path });
    }

    /// <summary>Lists the models in <see cref="ModelDirectory"/>, including ones that are no longer usable.</summary>
    /// <remarks>Reads only the descriptions; nothing is loaded until a check runs.</remarks>
    public static IReadOnlyList<OrientationModelInfo> List()
    {
        var found = new List<OrientationModelInfo>();
        if (!Directory.Exists(ModelDirectory)) return found;

        var files = Directory.GetFiles(ModelDirectory, "*.onnx");
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        foreach (var path in files)
        {
            var fileName = Path.GetFileName(path);
            var spec = ReadSpec(SpecPath(path));
            found.Add(new OrientationModelInfo(ModelPrefix + fileName, fileName, spec?.Name, spec == null ? OrientationModelFailureReason.FailedCheck : null));
        }

        return found;
    }

    /// <summary>Copies a model into <see cref="ModelDirectory"/>, once it has shown it can orient the sample pages.</summary>
    /// <remarks>
    /// A description beside the source (<c>model.json</c> for <c>model.onnx</c>) says how to
    /// feed it; without one the usual conventions are each tried in turn. Either way the labels
    /// are taken from what the model actually does with the sample pages. What was learned is
    /// written beside the copy, so the check is not repeated each time the model is used.
    /// </remarks>
    /// <exception cref="OrientationModelException">The file is not a model this app can use.</exception>
    public static async Task<OrientationModelInfo> InstallAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var fileName = Path.GetFileName(sourcePath);
        var target = Path.Combine(ModelDirectory, fileName);
        if (File.Exists(target))
            throw new OrientationModelException(OrientationModelFailureReason.AlreadyInstalled, $"A model named '{fileName}' is already installed.") { Path = target };

        var declared = ReadSpec(SpecPath(sourcePath));
        var name = declared?.Name is { Length: > 0 } given ? given : Path.GetFileNameWithoutExtension(fileName);

        using var model = ImageOrientationModel.LoadUnchecked(sourcePath);
        IEnumerable<OrientationModelSpec> candidates = declared != null ? [declared with { Name = name }] : Conventions(model, name);
        var spec = await CalibrateAsync(model, candidates, cancellationToken) ?? throw new OrientationModelException(
            OrientationModelFailureReason.FailedCheck, $"'{fileName}' could not tell which way up the sample pages were.") { Path = sourcePath };

        Directory.CreateDirectory(ModelDirectory);
        File.Copy(sourcePath, target);

        // A copy without its description would be listed as broken, so it goes if that cannot be written.
        var described = false;
        try
        {
            await File.WriteAllTextAsync(SpecPath(target), JsonSerializer.Serialize(spec, SpecJson), cancellationToken);
            described = true;
        }
        finally
        {
            if (!described) File.Delete(target);
        }

        Log.Information("Orientation model installed: {File} '{Name}' {Resize} {Channels} labels [{Labels}]",
            fileName, spec.Name, spec.Resize, spec.Channels, string.Join(", ", spec.Labels));
        return new OrientationModelInfo(ModelPrefix + fileName, fileName, spec.Name, null);
    }

    /// <exception cref="OrientationModelException">The model is in use and could not be deleted.</exception>
    public static void Uninstall(string id)
    {
        var path = ModelPath(id);

        try
        {
            File.Delete(path);
            File.Delete(SpecPath(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Loaded by a check that is still running.
            throw new OrientationModelException(OrientationModelFailureReason.InUse, $"'{Path.GetFileName(path)}' is in use.", ex) { Path = path };
        }

        Log.Information("Orientation model removed: {File}", Path.GetFileName(path));
    }

    /// <summary>The model file an id refers to.</summary>
    /// <remarks>
    /// The id comes from the settings file, which anything can edit, so it is held to a bare
    /// file name: an id must not be able to load a file from anywhere but the model folder.
    /// </remarks>
    private static string ModelPath(string id)
    {
        var fileName = id[ModelPrefix.Length..];
        if (fileName.Length == 0 || Path.GetFileName(fileName) != fileName ||
            !fileName.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
        {
            throw new OrientationModelException(OrientationModelFailureReason.NotFound, $"'{id}' does not name a model.");
        }

        return Path.Combine(ModelDirectory, fileName);
    }

    private static string SpecPath(string modelPath) => Path.ChangeExtension(modelPath, ".json");

    private static readonly JsonSerializerOptions SpecJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <returns>Null if there is no description, or it cannot be read as one.</returns>
    private static OrientationModelSpec? ReadSpec(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            var spec = JsonSerializer.Deserialize<OrientationModelSpec>(File.ReadAllText(path), SpecJson);
            return spec is { Width: > 0, Height: > 0, Mean.Length: 3, Std.Length: 3 } && spec.HasValidLabels ? spec : null;
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Model description {Path} is not valid JSON", path);
            return null;
        }
    }

    // ------------------------------------------------------------------------------ calibration

    /// <summary>
    /// The ways image classifiers are usually fed, for a model that came without a description:
    /// three ways of fitting the picture, three normalisations, two channel orders.
    /// </summary>
    private static IEnumerable<OrientationModelSpec> Conventions(ImageOrientationModel model, string name)
    {
        var (width, height) = model.InputSize ?? (224, 224);
        (float[] Mean, float[] Std)[] normalisations =
        [
            ([0.485f, 0.456f, 0.406f], [0.229f, 0.224f, 0.225f]),
            ([0f, 0f, 0f], [1f, 1f, 1f]),
            ([0.5f, 0.5f, 0.5f], [0.5f, 0.5f, 0.5f])
        ];

        foreach (var resize in new[] { ModelResize.Crop, ModelResize.Fit, ModelResize.Stretch })
        foreach (var (mean, std) in normalisations)
        foreach (var channels in new[] { "RGB", "BGR" })
            yield return new OrientationModelSpec { Name = name, Width = width, Height = height, Resize = resize, Mean = mean, Std = std, Channels = channels };
    }

    /// <summary>Sample pages with a known way up, shipped with the app.</summary>
    internal static IEnumerable<string> SamplePages()
        => Directory.Exists(AssetDirectory) ? Directory.GetFiles(AssetDirectory, "sample-page-*.jpg").Order(StringComparer.OrdinalIgnoreCase) : [];

    /// <summary>
    /// Shows the model every sample page at every quarter turn, and keeps the way of feeding it
    /// under which it reads them most confidently - provided it reads them all consistently.
    /// </summary>
    /// <remarks>
    /// Consistent means each turn always gets the same answer, on every page, and the four turns
    /// four different answers. That is also what the labels mean, whatever the model calls them,
    /// so they are taken from here rather than from any description: a model whose "90" means
    /// anticlockwise works as well as one whose "90" means clockwise.
    /// </remarks>
    /// <returns>The description to use, or null if no way of feeding it passes.</returns>
    private static async Task<OrientationModelSpec?> CalibrateAsync(ImageOrientationModel model, IEnumerable<OrientationModelSpec> candidates, CancellationToken cancellationToken)
    {
        var pages = SamplePages().ToList();
        if (pages.Count == 0) throw new InvalidOperationException($"No sample pages in {AssetDirectory}.");

        using var session = model.CreateSession();
        OrientationModelSpec? best = null;
        var bestConfidence = 0d;

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var winners = new int[4];
            Array.Fill(winners, -1);
            var consistent = true;
            var confidence = 0d;

            foreach (var page in pages)
            {
                var picture = await PictureOf(page, candidate);
                for (var quarter = 0; quarter < 4 && consistent; quarter++)
                {
                    var p = session.Probabilities(picture.Turned(quarter * 90), candidate);
                    var winner = Array.IndexOf(p, p.Max());
                    if (winners[quarter] == -1) winners[quarter] = winner;
                    consistent = winners[quarter] == winner;
                    confidence += p[winner];
                }
                if (!consistent) break;
            }

            if (!consistent || winners.Distinct().Count() != 4) continue;

            confidence /= pages.Count * 4;
            var labels = new int[4];
            for (var quarter = 0; quarter < 4; quarter++) labels[winners[quarter]] = quarter * 90;

            Log.Debug("Model calibration: {Resize} {Channels} mean [{Mean}] reads the samples at {Confidence:P0}, labels [{Labels}]",
                candidate.Resize, candidate.Channels, string.Join(", ", candidate.Mean), confidence, string.Join(", ", labels));

            // The candidates come commonest first, and a later one has to be clearly better to
            // replace an earlier one: a model that works several ways is fed the usual way.
            if (confidence >= CalibrationConfidence && confidence > bestConfidence + ClearlyBetter)
            {
                best = candidate with { Labels = labels };
                bestConfidence = confidence;
            }
        }

        return best;
    }

    /// <summary>How sure, on average, a model has to be of the sample pages - all typed, all clear.</summary>
    private const double CalibrationConfidence = 0.6;

    private const double ClearlyBetter = 0.02;

    private static async Task<BgraPicture> PictureOf(string path, OrientationModelSpec spec)
    {
        using var stream = File.OpenRead(path);
        using var ras = stream.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(ras);
        return await BgraPicture.DecodeAsync(decoder, spec, ExifOrientationMode.RespectExifOrientation);
    }
}

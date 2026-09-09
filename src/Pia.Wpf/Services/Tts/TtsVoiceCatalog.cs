using System.IO;
using Pia.Paths;

namespace Pia.Services.Tts;

/// <summary>One curated Piper voice: its sherpa bundle and what the picker shows for it.</summary>
public sealed record TtsVoiceDescriptor(
    string Key,
    string DisplayName,
    string Language,
    string Quality,
    string Gender,
    long BundleBytes);

/// <summary>The three files a sherpa VITS voice loads from.</summary>
public sealed record TtsVoiceFiles(string ModelPath, string TokensPath, string DataDirectory);

/// <summary>
/// The voices offered in the picker and where they come from. sherpa republishes the rhasspy Piper
/// voices with the <c>tokens.txt</c> and <c>espeak-ng-data</c> its phonemizer needs, which a raw
/// rhasspy download does not carry — so the bundle is the unit, not the <c>.onnx</c>.
/// </summary>
public static class TtsVoiceCatalog
{
    internal const string SherpaTtsReleasesBase =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models";

    public const string DefaultVoiceKey = "en_US-lessac-medium";

    /// <summary>BundleBytes is the archive's Content-Length, measured 2026-09-09.</summary>
    public static IReadOnlyList<TtsVoiceDescriptor> Curated { get; } =
    [
        new("en_US-lessac-medium", "Lessac", "English (US)", "Medium", "Male", 67_230_653),
        new("en_US-amy-medium", "Amy", "English (US)", "Medium", "Female", 67_223_746),
        new("en_US-ryan-medium", "Ryan", "English (US)", "Medium", "Male", 67_213_100),
        new("en_GB-alba-medium", "Alba", "English (GB)", "Medium", "Female", 67_212_349),
        new("de_DE-thorsten-medium", "Thorsten", "German", "Medium", "Male", 67_214_254),
        new("de_DE-eva_k-x_low", "Eva", "German", "Low", "Female", 26_521_242),
        new("de_DE-ramona-low", "Ramona", "German", "Low", "Female", 67_084_795),
        new("fr_FR-siwis-medium", "Siwis", "French", "Medium", "Female", 67_207_459),
        new("fr_FR-upmc-medium", "UPMC", "French", "Medium", "Male", 80_422_639),
    ];

    internal static string BundleName(string voiceKey) => $"vits-piper-{voiceKey}";

    internal static string BundleUrl(string voiceKey) =>
        $"{SherpaTtsReleasesBase}/{BundleName(voiceKey)}.tar.bz2";

    public static string VoicesDirectory => Path.Combine(PiaPaths.TtsDirectory, "voices");

    public static string VoiceDirectory(string voiceKey) =>
        Path.Combine(VoicesDirectory, BundleName(voiceKey));

    /// <summary>
    /// Null when anything the engine needs is missing. Callers MUST gate on this: sherpa's C# wrapper
    /// does not check the native handle, so constructing an engine over an incomplete voice takes the
    /// whole process down with an access violation that no <c>catch</c> can see.
    /// </summary>
    public static TtsVoiceFiles? TryResolve(string voiceKey) =>
        TryResolveIn(VoiceDirectory(voiceKey), voiceKey);

    /// <inheritdoc cref="TryResolve"/>
    internal static TtsVoiceFiles? TryResolveIn(string dir, string voiceKey)
    {
        if (!Directory.Exists(dir))
            return null;

        var model = Path.Combine(dir, voiceKey + ".onnx");
        if (!File.Exists(model))
        {
            // The EndsWith filter is not redundant: a "*.onnx" pattern also matches the sidecar
            // "<key>.onnx.json" through its 8.3 short name.
            model = Directory.EnumerateFiles(dir, "*.onnx")
                .FirstOrDefault(f => f.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase));
            if (model is null)
                return null;
        }

        var tokens = Path.Combine(dir, "tokens.txt");
        if (!File.Exists(tokens))
            return null;

        var dataDir = Path.Combine(dir, "espeak-ng-data");

        // phontab rather than the directory: a half-extracted tree has the folder and crashes anyway.
        if (!File.Exists(Path.Combine(dataDir, "phontab")))
            return null;

        return new TtsVoiceFiles(model, tokens, dataDir);
    }

    public static bool IsVoiceInstalled(string voiceKey) => TryResolve(voiceKey) is not null;
}

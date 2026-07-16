using System.Globalization;

namespace Ged.Core.IO.Tex;

/// <summary>ATX animation modes (values match Alpine's <c>AtxSpec::AnimationMode</c>).</summary>
public enum AtxAnimationMode
{
    Static = 0,
    PingPong = 1,
    Loop = 2,
    PlayOnce = 3,
}

/// <summary>A single ATX frame: the referenced texture file and optional per-frame overrides.</summary>
public sealed class AtxFrame
{
    public AtxFrame(string file, int? frameTimeMs, string? material)
    {
        File = file;
        FrameTimeMs = frameTimeMs;
        Material = material;
    }

    /// <summary>The referenced texture file (never empty, never an <c>.atx</c>).</summary>
    public string File { get; }

    /// <summary>Per-frame duration override in milliseconds, or null to inherit the header value.</summary>
    public int? FrameTimeMs { get; }

    /// <summary>Per-frame material override token, or null.</summary>
    public string? Material { get; }
}

/// <summary>
/// A parsed ATX animated-texture descriptor. ATX files are a small TOML subset:
/// an optional <c>[header]</c> table plus one or more <c>[[frame]]</c> tables.
/// This parser reads exactly the schema Alpine's <c>common/atx/parse.h</c> defines
/// (reimplemented from the documented field set; MPL code not copied). It is
/// enough to resolve and preview the frames — it does not load or validate the
/// referenced textures.
/// </summary>
public sealed class AtxDescriptor
{
    public const int MinFrameTimeMs = 1;
    public const int DefaultFrameTimeMs = 100;

    private AtxDescriptor(
        int frameTimeMs,
        bool initiallyOn,
        AtxAnimationMode animationMode,
        string? format,
        string? alphaMask,
        string? material,
        IReadOnlyList<AtxFrame> frames)
    {
        FrameTimeMs = frameTimeMs;
        InitiallyOn = initiallyOn;
        AnimationMode = animationMode;
        Format = format;
        AlphaMask = alphaMask;
        Material = material;
        Frames = frames;
    }

    public int FrameTimeMs { get; }

    public bool InitiallyOn { get; }

    public AtxAnimationMode AnimationMode { get; }

    public string? Format { get; }

    public string? AlphaMask { get; }

    public string? Material { get; }

    /// <summary>Frames in declaration order; always at least one after a successful parse.</summary>
    public IReadOnlyList<AtxFrame> Frames { get; }

    /// <summary>
    /// Parses ATX text. Throws <see cref="TextureFormatException"/> on schema
    /// violations (no frames, a frame missing <c>file</c>, or a nested <c>.atx</c>).
    /// </summary>
    public static AtxDescriptor Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        int headerFrameTime = DefaultFrameTimeMs;
        bool initiallyOn = true;
        var animationMode = AtxAnimationMode.Static;
        string? format = null;
        string? alphaMask = null;
        string? headerMaterial = null;
        var frames = new List<AtxFrame>();

        // Section state: 0 = none, 1 = [header], 2 = a [[frame]].
        int section = 0;
        string? curFile = null;
        int? curFrameTime = null;
        string? curFrameMaterial = null;

        void FlushFrame()
        {
            if (section != 2)
            {
                return;
            }

            if (string.IsNullOrEmpty(curFile))
            {
                throw new TextureFormatException("ATX frame is missing a 'file' key.");
            }

            if (curFile!.EndsWith(".atx", StringComparison.OrdinalIgnoreCase))
            {
                throw new TextureFormatException($"ATX frame references a nested .atx ('{curFile}'), which is not allowed.");
            }

            frames.Add(new AtxFrame(curFile, curFrameTime, curFrameMaterial));
            curFile = null;
            curFrameTime = null;
            curFrameMaterial = null;
        }

        foreach (string rawLine in SplitLines(text))
        {
            string line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("[[", StringComparison.Ordinal) && line.EndsWith("]]", StringComparison.Ordinal))
            {
                string name = line[2..^2].Trim();
                if (!name.Equals("frame", StringComparison.OrdinalIgnoreCase))
                {
                    throw new TextureFormatException($"Unexpected ATX array-of-tables '[[{name}]]'.");
                }

                FlushFrame();
                section = 2;
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                string name = line[1..^1].Trim();
                FlushFrame();
                section = name.Equals("header", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                continue;
            }

            int eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0)
            {
                continue; // ignore lines we don't understand
            }

            string key = line[..eq].Trim().ToLowerInvariant();
            string value = line[(eq + 1)..].Trim();

            switch (section)
            {
                case 1:
                    ApplyHeaderKey(key, value, ref headerFrameTime, ref initiallyOn, ref animationMode,
                        ref format, ref alphaMask, ref headerMaterial);
                    break;
                case 2:
                    ApplyFrameKey(key, value, ref curFile, ref curFrameTime, ref curFrameMaterial);
                    break;
                default:
                    break; // keys outside a known section are ignored
            }
        }

        FlushFrame();

        if (frames.Count == 0)
        {
            throw new TextureFormatException("ATX has no [[frame]] entries.");
        }

        return new AtxDescriptor(headerFrameTime, initiallyOn, animationMode, format, alphaMask, headerMaterial, frames);
    }

    private static void ApplyHeaderKey(
        string key, string value, ref int frameTime, ref bool initiallyOn, ref AtxAnimationMode mode,
        ref string? format, ref string? alphaMask, ref string? material)
    {
        switch (key)
        {
            case "frame_time":
                if (TryParseInt(value, out int ft))
                {
                    frameTime = Math.Max(MinFrameTimeMs, ft);
                }

                break;
            case "initially_on":
                if (TryParseBool(value, out bool on))
                {
                    initiallyOn = on;
                }

                break;
            case "animation_mode":
                if (TryParseInt(value, out int m))
                {
                    mode = m is >= 0 and <= 3 ? (AtxAnimationMode)m : AtxAnimationMode.Static;
                }

                break;
            case "format":
                format = NullIfEmpty(Unquote(value));
                break;
            case "alpha_mask":
                alphaMask = NullIfEmpty(Unquote(value));
                break;
            case "material":
                material = NullIfEmpty(Unquote(value));
                break;
            default:
                break;
        }
    }

    private static void ApplyFrameKey(
        string key, string value, ref string? file, ref int? frameTime, ref string? material)
    {
        switch (key)
        {
            case "file":
                file = Unquote(value);
                break;
            case "frame_time":
                if (TryParseInt(value, out int ft))
                {
                    frameTime = Math.Max(MinFrameTimeMs, ft);
                }

                break;
            case "material":
                material = NullIfEmpty(Unquote(value));
                break;
            default:
                break;
        }
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

    /// <summary>Removes a trailing <c>#</c> comment, respecting double/single-quoted strings.</summary>
    private static string StripComment(string line)
    {
        bool inDouble = false;
        bool inSingle = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
            }
            else if (c == '\'' && !inDouble)
            {
                inSingle = !inSingle;
            }
            else if (c == '#' && !inDouble && !inSingle)
            {
                return line[..i];
            }
        }

        return line;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

    private static bool TryParseInt(string value, out int result) =>
        int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static bool TryParseBool(string value, out bool result)
    {
        string v = value.Trim();
        if (v.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            result = true;
            return true;
        }

        if (v.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            result = false;
            return true;
        }

        result = false;
        return false;
    }
}

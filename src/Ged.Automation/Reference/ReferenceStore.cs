using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Ged.Automation.Reference;

/// <summary>The matched camera stored with a reference (world metres, degrees).</summary>
public sealed class ReferenceCameraRecord
{
    /// <summary>The eye position [x, y, z]. With <see cref="EyeHeight"/>, y is where the floor search starts.</summary>
    public double[] Position { get; set; } = new double[3];

    /// <summary>The look-at point [x, y, z].</summary>
    public double[] Look { get; set; } = new double[3];

    /// <summary>The horizontal field of view in degrees.</summary>
    public double Hfov { get; set; } = ReferenceCamera.DefaultHfov;

    /// <summary>When set, renders stand the eye this many metres above the floor under the camera (keeping the pitch).</summary>
    public double? EyeHeight { get; set; }
}

/// <summary>
/// Cues an agent marked on a reference picture, as fractions of its width and height (0..1, top-left origin): the
/// horizon, a vanishing point, an object of known height. They feed <see cref="ReferenceCamera.Estimate"/>.
/// </summary>
public sealed class ReferenceMarks
{
    /// <summary>The horizon: [y] (level) or [x1, y1, x2, y2] (two points on it; the tilt is the camera roll).</summary>
    public double[]? Horizon { get; set; }

    /// <summary>The vanishing point [x, y] of horizontal lines running away from the camera (a corridor, a road).</summary>
    public double[]? Vp { get; set; }

    /// <summary>The world direction [x, z] those lines run in (turns the vanishing point into the camera's heading).</summary>
    public double[]? VpDir { get; set; }

    /// <summary>A vertical object of known size standing on the floor: [x, baseY, topY, heightMetres].</summary>
    public double[]? Object { get; set; }

    /// <summary>Vertical spans to measure on the same floor: [x, baseY, topY] triples (flattened).</summary>
    public double[]? Measure { get; set; }

    /// <summary>
    /// Rectangles [x, y, w, h] (flattened) the comparison cues leave out: things in the picture with no counterpart in a
    /// level render, such as a first-person weapon, a HUD or a caption.
    /// </summary>
    public double[]? Ignore { get; set; }
}

/// <summary>One registered reference picture.</summary>
public sealed class ReferenceRecord
{
    /// <summary>The id (letters, digits, '-', '_'; unique in its set).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The area or zone of the level it depicts (free text, e.g. "The Yard").</summary>
    public string? Area { get; set; }

    /// <summary>Free notes (what matters in this picture).</summary>
    public string? Notes { get; set; }

    /// <summary>The image it was cut from (absolute path when registered).</summary>
    public string? Source { get; set; }

    /// <summary>The crop rectangle in the source's pixels [x, y, w, h] (null = the whole image).</summary>
    public int[]? Crop { get; set; }

    /// <summary>The stored picture, relative to the set's folder (always a PNG).</summary>
    public string Image { get; set; } = string.Empty;

    /// <summary>The stored picture's width in pixels.</summary>
    public int Width { get; set; }

    /// <summary>The stored picture's height in pixels.</summary>
    public int Height { get; set; }

    /// <summary>The matched camera, once set.</summary>
    public ReferenceCameraRecord? Camera { get; set; }

    /// <summary>Marked cues, once given.</summary>
    public ReferenceMarks? Marks { get; set; }

    /// <summary>The picture's aspect (width / height).</summary>
    [JsonIgnore]
    public double Aspect => Height > 0 ? (double)Width / Height : 1.0;
}

/// <summary>
/// A reference set: a folder holding <c>references.json</c> plus one PNG per reference (the cropped picture). It is
/// sidecar data next to a level's work files: the level file is never touched. Loading a missing set gives an empty one.
/// </summary>
public sealed class ReferenceStore
{
    /// <summary>The index file's name inside the set's folder.</summary>
    public const string IndexFile = "references.json";

    /// <summary>The index file's schema tag.</summary>
    public const string Schema = "glacier.references/1";

    private static readonly Regex IdPattern = new("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new(AutomationJson.Options) { WriteIndented = true };

    private ReferenceStore(string folder, List<ReferenceRecord> references)
    {
        Folder = folder;
        References = references;
    }

    /// <summary>The set's folder (absolute).</summary>
    public string Folder { get; }

    /// <summary>The references, in registration order.</summary>
    public List<ReferenceRecord> References { get; }

    /// <summary>The index file's path.</summary>
    public string IndexPath => Path.Combine(Folder, IndexFile);

    /// <summary>Loads the set in <paramref name="folder"/> (empty when it has no index yet).</summary>
    public static ReferenceStore Load(string folder)
    {
        string full = Path.GetFullPath(folder);
        string index = Path.Combine(full, IndexFile);
        if (!File.Exists(index))
        {
            return new ReferenceStore(full, new List<ReferenceRecord>());
        }

        try
        {
            IndexDto? dto = JsonSerializer.Deserialize<IndexDto>(File.ReadAllText(index, Encoding.UTF8), JsonOptions);
            return new ReferenceStore(full, dto?.References ?? new List<ReferenceRecord>());
        }
        catch (JsonException ex)
        {
            throw new AutomationException(AutomationErrorCodes.Io, $"The reference index '{index}' is not valid JSON: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AutomationException(AutomationErrorCodes.Io, $"Could not read '{index}': {ex.Message}", ex);
        }
    }

    /// <summary>Writes the index (creating the folder).</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string json = JsonSerializer.Serialize(new IndexDto { References = References }, JsonOptions);
            File.WriteAllText(IndexPath, json.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n", new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AutomationException(AutomationErrorCodes.Io, $"Could not write '{IndexPath}': {ex.Message}", ex);
        }
    }

    /// <summary>The reference with <paramref name="id"/>, or a <c>usage</c> error listing the ids there are.</summary>
    public ReferenceRecord Get(string id) =>
        References.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new AutomationException(AutomationErrorCodes.Usage, References.Count == 0
            ? $"No reference '{id}': the set in {Folder} is empty (register one with add)."
            : $"No reference '{id}' in {Folder} (ids: {string.Join(", ", References.Select(r => r.Id))}).");

    /// <summary>The absolute path of a reference's picture.</summary>
    public string ImagePath(ReferenceRecord r) => Path.Combine(Folder, r.Image);

    /// <summary>Throws a <c>usage</c> error unless <paramref name="id"/> is a valid id.</summary>
    public static void CheckId(string id)
    {
        if (!IdPattern.IsMatch(id))
        {
            throw new AutomationException(AutomationErrorCodes.Usage,
                $"Reference id '{id}' must start with a letter or digit and use only letters, digits, '-' and '_' (at most 64).");
        }
    }

    private sealed class IndexDto
    {
        public string Schema { get; set; } = ReferenceStore.Schema;

        public List<ReferenceRecord> References { get; set; } = new();
    }
}

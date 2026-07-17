using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ged.Core.Editing.Graph;
using Ged.Core.Lighting;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// Reads and writes the unified <c>&lt;level&gt;.gedlayout.json</c> sidecar. All writers
/// go through the block helpers (<see cref="SaveGraph"/>, <see cref="SaveLighting"/>,
/// <see cref="SaveAnnotations"/>), each a read-modify-write that preserves the other
/// blocks — so saving graph positions never drops the lighting method or annotations and
/// vice versa. A missing or corrupt file yields an empty sidecar.
/// </summary>
public static class LevelSidecarStore
{
    /// <summary>The sidecar file suffix appended to the level's base name.</summary>
    public const string Suffix = ".gedlayout.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The sidecar path for a level: the RFL path with its extension replaced by <c>.gedlayout.json</c>.</summary>
    public static string SidecarPathFor(string rflPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(rflPath);
        string? dir = Path.GetDirectoryName(rflPath);
        string file = Path.GetFileNameWithoutExtension(rflPath) + Suffix;
        return string.IsNullOrEmpty(dir) ? file : Path.Combine(dir, file);
    }

    /// <summary>Loads the whole sidecar, or an empty one when absent/corrupt.</summary>
    public static LevelSidecar Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new LevelSidecar();
            }

            return FromDto(JsonSerializer.Deserialize<SidecarDto>(File.ReadAllText(path), Options));
        }
        catch (Exception)
        {
            return new LevelSidecar();
        }
    }

    /// <summary>Writes the whole sidecar (creating the directory).</summary>
    public static void Save(LevelSidecar sidecar, string path)
    {
        ArgumentNullException.ThrowIfNull(sidecar);
        ArgumentException.ThrowIfNullOrEmpty(path);
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, Serialize(sidecar));
    }

    /// <summary>Serializes a sidecar to JSON text (exposed for round-trip tests).</summary>
    public static string Serialize(LevelSidecar sidecar)
    {
        ArgumentNullException.ThrowIfNull(sidecar);
        return JsonSerializer.Serialize(ToDto(sidecar), Options);
    }

    /// <summary>Parses a sidecar from JSON text (exposed for round-trip tests).</summary>
    public static LevelSidecar Deserialize(string json) =>
        FromDto(JsonSerializer.Deserialize<SidecarDto>(json, Options));

    // ---- Block read-modify-write helpers --------------------------------------

    public static GraphLayout LoadGraph(string path) => Load(path).Graph;

    public static void SaveGraph(string path, GraphLayout graph)
    {
        LevelSidecar s = Load(path);
        s.Graph = graph;
        Save(s, path);
    }

    public static LightingMethod? LoadLighting(string path) => Load(path).Lighting;

    public static void SaveLighting(string path, LightingMethod? lighting)
    {
        LevelSidecar s = Load(path);
        s.Lighting = lighting;
        Save(s, path);
    }

    public static List<Annotation> LoadAnnotations(string path) => Load(path).Annotations;

    public static void SaveAnnotations(string path, IEnumerable<Annotation> annotations)
    {
        LevelSidecar s = Load(path);
        s.Annotations = new List<Annotation>(annotations);
        Save(s, path);
    }

    // ---- DTO mapping -----------------------------------------------------------

    private static SidecarDto ToDto(LevelSidecar s)
    {
        var dto = new SidecarDto { Version = s.Version <= 0 ? 1 : s.Version };
        foreach (KeyValuePair<int, GraphNodePos> kv in s.Graph.Positions)
        {
            dto.Nodes.Add(new NodeDto { Uid = kv.Key, X = kv.Value.X, Y = kv.Value.Y });
        }

        dto.Nodes.Sort((a, b) => a.Uid.CompareTo(b.Uid));

        if (s.Lighting is { } m)
        {
            dto.Lighting = new LightingDto
            {
                Method = m.Base.ToString(),
                Bounces = m.Bounces,
                AmbientOcclusion = m.AmbientOcclusion,
                SoftShadows = m.SoftShadows,
                HighResLightmaps = m.HighResLightmaps,
                SeamBlend = m.SeamBlend,
                CornerLeakFix = m.CornerLeakFix,
                SmoothGutters = m.SmoothGutters,
                MoverShadows = m.MoverShadows,
            };
        }

        foreach (Annotation a in s.Annotations)
        {
            dto.Annotations.Add(new AnnotationDto
            {
                Id = a.Id,
                Ax = a.A.X, Ay = a.A.Y, Az = a.A.Z,
                Bx = a.B.X, By = a.B.Y, Bz = a.B.Z,
                Label = a.Label,
            });
        }

        return dto;
    }

    private static LevelSidecar FromDto(SidecarDto? dto)
    {
        var s = new LevelSidecar();
        if (dto is null)
        {
            return s;
        }

        s.Version = dto.Version <= 0 ? 1 : dto.Version;
        foreach (NodeDto n in dto.Nodes)
        {
            s.Graph.Set(n.Uid, n.X, n.Y);
        }

        if (dto.Lighting is { } l)
        {
            s.Lighting = new LightingMethod
            {
                Base = string.Equals(l.Method, "Bounced", StringComparison.OrdinalIgnoreCase)
                    ? LightingBase.Bounced : LightingBase.RedClassic,
                Bounces = l.Bounces <= 1 ? 1 : 2,
                AmbientOcclusion = l.AmbientOcclusion,
                SoftShadows = l.SoftShadows,
                HighResLightmaps = l.HighResLightmaps,
                SeamBlend = l.SeamBlend,
                CornerLeakFix = l.CornerLeakFix,
                SmoothGutters = l.SmoothGutters,
                MoverShadows = l.MoverShadows,
            };
        }

        foreach (AnnotationDto a in dto.Annotations)
        {
            s.Annotations.Add(new Annotation
            {
                Id = a.Id,
                A = new Vec3(a.Ax, a.Ay, a.Az),
                B = new Vec3(a.Bx, a.By, a.Bz),
                Label = a.Label,
            });
        }

        return s;
    }

    private sealed class SidecarDto
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("nodes")]
        public List<NodeDto> Nodes { get; set; } = new();

        [JsonPropertyName("lighting")]
        public LightingDto? Lighting { get; set; }

        [JsonPropertyName("annotations")]
        public List<AnnotationDto> Annotations { get; set; } = new();
    }

    private sealed class NodeDto
    {
        [JsonPropertyName("uid")]
        public int Uid { get; set; }

        [JsonPropertyName("x")]
        public double X { get; set; }

        [JsonPropertyName("y")]
        public double Y { get; set; }
    }

    private sealed class LightingDto
    {
        [JsonPropertyName("method")]
        public string Method { get; set; } = "RedClassic";

        [JsonPropertyName("bounces")]
        public int Bounces { get; set; } = 1;

        [JsonPropertyName("ambientOcclusion")]
        public bool HighResLightmaps { get; set; }

        public bool AmbientOcclusion { get; set; }

        [JsonPropertyName("softShadows")]
        public bool SoftShadows { get; set; }

        [JsonPropertyName("seamBlend")]
        public bool SeamBlend { get; set; }

        [JsonPropertyName("cornerLeakFix")]
        public bool CornerLeakFix { get; set; }

        [JsonPropertyName("smoothGutters")]
        public bool SmoothGutters { get; set; }

        // Default true so a sidecar written before this option (no "moverShadows" key) loads with the
        // app default ON, not a spurious OFF; an explicit false in newer sidecars still round-trips.
        [JsonPropertyName("moverShadows")]
        public bool MoverShadows { get; set; } = true;
    }

    private sealed class AnnotationDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("ax")]
        public float Ax { get; set; }

        [JsonPropertyName("ay")]
        public float Ay { get; set; }

        [JsonPropertyName("az")]
        public float Az { get; set; }

        [JsonPropertyName("bx")]
        public float Bx { get; set; }

        [JsonPropertyName("by")]
        public float By { get; set; }

        [JsonPropertyName("bz")]
        public float Bz { get; set; }

        [JsonPropertyName("label")]
        public string? Label { get; set; }
    }
}

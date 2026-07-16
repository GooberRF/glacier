using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ged.Core.Editing.Graph;

/// <summary>
/// Reads and writes the <c>&lt;level&gt;.gedlayout.json</c> sidecar that persists
/// link-graph node positions next to the <c>.rfl</c>. The file is editor-only — it
/// is never written into the RFL and is excluded from the packfile scanner (see
/// <see cref="Packaging.EditorOnlyFiles"/>). A missing or corrupt file yields an
/// empty layout so the graph auto-lays out from scratch.
/// </summary>
public static class GraphLayoutStore
{
    /// <summary>The sidecar file suffix appended to the level's base name.</summary>
    public const string Suffix = ".gedlayout.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The sidecar path for a level: the RFL path with its extension replaced by
    /// <c>.gedlayout.json</c> (e.g. <c>maps\dm01.rfl</c> → <c>maps\dm01.gedlayout.json</c>).
    /// </summary>
    public static string SidecarPathFor(string rflPath) => LevelSidecarStore.SidecarPathFor(rflPath);

    /// <summary>Loads the graph layout at <paramref name="path"/>, or an empty layout if absent/corrupt.</summary>
    public static GraphLayout Load(string path) => LevelSidecarStore.LoadGraph(path);

    /// <summary>
    /// Persists <paramref name="layout"/> into the sidecar at <paramref name="path"/> as a
    /// read-modify-write, preserving the lighting method and annotation blocks.
    /// </summary>
    public static void Save(GraphLayout layout, string path)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrEmpty(path);
        LevelSidecarStore.SaveGraph(path, layout);
    }

    /// <summary>Serializes a layout to its JSON text (exposed for round-trip tests).</summary>
    public static string Serialize(GraphLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var dto = new LayoutDto { Version = layout.Version };
        foreach (KeyValuePair<int, GraphNodePos> kv in layout.Positions)
        {
            dto.Nodes.Add(new NodeDto { Uid = kv.Key, X = kv.Value.X, Y = kv.Value.Y });
        }

        dto.Nodes.Sort((a, b) => a.Uid.CompareTo(b.Uid));
        return JsonSerializer.Serialize(dto, Options);
    }

    /// <summary>Parses a layout from JSON text (exposed for round-trip tests).</summary>
    public static GraphLayout Deserialize(string json) =>
        FromDto(JsonSerializer.Deserialize<LayoutDto>(json, Options));

    private static GraphLayout FromDto(LayoutDto? dto)
    {
        var layout = new GraphLayout();
        if (dto is null)
        {
            return layout;
        }

        layout.Version = dto.Version <= 0 ? 1 : dto.Version;
        foreach (NodeDto n in dto.Nodes)
        {
            layout.Set(n.Uid, n.X, n.Y);
        }

        return layout;
    }

    private sealed class LayoutDto
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("nodes")]
        public List<NodeDto> Nodes { get; set; } = new();
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
}

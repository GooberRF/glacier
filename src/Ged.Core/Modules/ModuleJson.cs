using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Ged.Core.Modules;

/// <summary>
/// The module index format (<c>library/modules/&lt;group&gt;/index.json</c>): written deterministically (fixed property
/// order, numbers rounded to 1/1024 m, '\n' line ends, no timestamps or machine paths) and read back tolerantly.
/// </summary>
public static class ModuleJson
{
    /// <summary>The index schema id.</summary>
    public const string Schema = "glacier.module-index/1";

    /// <summary>Rounds to 1/1024 (and folds -0 to 0) so output never depends on float noise.</summary>
    public static double R(double v)
    {
        double r = Math.Round(v * 1024.0, MidpointRounding.AwayFromZero) / 1024.0;
        return r == 0 ? 0 : r;
    }

    /// <summary>Serializes one group's index.</summary>
    public static string WriteIndex(string group, IReadOnlyList<ModuleInfo> modules)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("schema", Schema);
            w.WriteString("group", group);
            w.WriteNumber("count", modules.Count);
            w.WriteStartArray("modules");
            foreach (ModuleInfo m in modules)
            {
                WriteModule(w, m);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    /// <summary>Writes one module object.</summary>
    public static void WriteModule(Utf8JsonWriter w, ModuleInfo m)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(m);
        w.WriteStartObject();
        w.WriteString("id", m.Id);
        w.WriteString("category", m.Category);
        w.WriteString("role", m.Role);
        w.WriteString("attach", m.Attach);
        w.WriteStartArray("styles");
        foreach (string s in m.Styles)
        {
            w.WriteStringValue(s);
        }

        w.WriteEndArray();
        w.WritePropertyName("size");
        Xyz(w, m.Size);
        w.WriteStartObject("bounds");
        w.WritePropertyName("min");
        Arr(w, m.BoundsMin);
        w.WritePropertyName("max");
        Arr(w, m.BoundsMax);
        w.WriteEndObject();
        w.WriteString("anchor", m.Anchor);
        w.WriteStartObject("stretch");
        for (int a = 0; a < 3; a++)
        {
            if (m.Stretch(a) is { } s)
            {
                w.WriteStartObject(ModuleVocabulary.AxisName(a));
                w.WriteNumber("a", R(s.A));
                w.WriteNumber("b", R(s.B));
                w.WriteEndObject();
            }
        }

        w.WriteEndObject();
        w.WriteStartArray("tiles");
        foreach (ModuleTileFamily t in m.Tiles)
        {
            w.WriteStartObject();
            w.WriteString("axis", t.Axis);
            w.WriteNumber("pitch", R(t.Pitch));
            w.WriteNumber("count", t.Count);
            Ints(w, "first", t.First);
            Ints(w, "replicas", t.Replicas);
            w.WriteNumber("first_center", R(t.FirstCenter));
            w.WriteNumber("margin_start", R(t.MarginStart));
            w.WriteNumber("margin_end", R(t.MarginEnd));
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("textures");
        foreach (ModuleTexture t in m.Textures)
        {
            w.WriteStartObject();
            w.WriteString("name", t.Name);
            w.WriteString("family", t.Family);
            w.WriteBoolean("stock", t.Stock);
            if (t.Source is null)
            {
                w.WriteNull("source");
            }
            else
            {
                w.WriteString("source", t.Source);
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteBoolean("custom_textures", m.CustomTextures);
        w.WriteNumber("brushes", m.Brushes);
        w.WriteNumber("faces", m.Faces);
        w.WriteBoolean("has_air", m.HasAir);
        Num(w, "recess_depth", m.RecessDepth);
        Num(w, "mount_height", m.MountHeight);
        Num(w, "ceiling_gap", m.CeilingGap);
        if (m.Opening is { } o)
        {
            w.WriteStartObject("opening");
            w.WriteNumber("width", R(o.Width));
            w.WriteNumber("height", R(o.Height));
            w.WriteNumber("wall_thickness", R(o.WallThickness));
            w.WriteEndObject();
        }
        else
        {
            w.WriteNull("opening");
        }

        w.WriteNumber("score", Math.Round(m.Score, 4));
        w.WriteNumber("occurrences", m.Occurrences);
        Num(w, "suggested_pitch", m.SuggestedPitch);
        w.WriteStartArray("provenance");
        foreach (ModuleProvenance p in m.Provenance)
        {
            w.WriteStartObject();
            w.WriteString("level", p.Level);
            if (p.Packfile is null)
            {
                w.WriteNull("packfile");
            }
            else
            {
                w.WriteString("packfile", p.Packfile);
            }

            w.WritePropertyName("position");
            Arr(w, p.Position);
            w.WriteNumber("yaw", Math.Round(p.Yaw, 3) == 0 ? 0 : Math.Round(p.Yaw, 3));
            Ints(w, "brush_uids", p.BrushUids);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteString("file", m.File);
        w.WriteEndObject();
    }

    /// <summary>Parses one group's index; <paramref name="groupDir"/> resolves each module's .rfg path.</summary>
    public static List<ModuleInfo> ReadIndex(string json, string? groupDir)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        string group = Str(root, "group") ?? string.Empty;
        var list = new List<ModuleInfo>();
        if (!root.TryGetProperty("modules", out JsonElement mods) || mods.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (JsonElement m in mods.EnumerateArray())
        {
            list.Add(ReadModule(m, group, groupDir));
        }

        return list;
    }

    private static ModuleInfo ReadModule(JsonElement m, string group, string? groupDir)
    {
        ModuleSlice? Slice(string axis) =>
            m.TryGetProperty("stretch", out JsonElement s) && s.ValueKind == JsonValueKind.Object && s.TryGetProperty(axis, out JsonElement z)
                ? new ModuleSlice(Dbl(z, "a") ?? 0, Dbl(z, "b") ?? 0)
                : null;

        string file = Str(m, "file") ?? string.Empty;
        JsonElement bounds = m.TryGetProperty("bounds", out JsonElement b) ? b : default;
        return new ModuleInfo
        {
            Id = Str(m, "id") ?? string.Empty,
            Group = group,
            Category = Str(m, "category") ?? "misc",
            Role = Str(m, "role") ?? "theme_detail",
            Attach = Str(m, "attach") ?? "free",
            Styles = Strings(m, "styles"),
            Size = m.TryGetProperty("size", out JsonElement size) ? new D3(Dbl(size, "x") ?? 0, Dbl(size, "y") ?? 0, Dbl(size, "z") ?? 0) : default,
            BoundsMin = bounds.ValueKind == JsonValueKind.Object ? Vec(bounds, "min") : default,
            BoundsMax = bounds.ValueKind == JsonValueKind.Object ? Vec(bounds, "max") : default,
            StretchX = Slice("x"),
            StretchY = Slice("y"),
            StretchZ = Slice("z"),
            Tiles = m.TryGetProperty("tiles", out JsonElement tiles) && tiles.ValueKind == JsonValueKind.Array
                ? tiles.EnumerateArray().Select(t => new ModuleTileFamily
                {
                    Axis = Str(t, "axis") ?? "x",
                    Pitch = Dbl(t, "pitch") ?? 0,
                    Count = (int)(Dbl(t, "count") ?? 0),
                    First = IntList(t, "first"),
                    Replicas = IntList(t, "replicas"),
                    FirstCenter = Dbl(t, "first_center") ?? 0,
                    MarginStart = Dbl(t, "margin_start") ?? 0,
                    MarginEnd = Dbl(t, "margin_end") ?? 0,
                }).ToList()
                : new List<ModuleTileFamily>(),
            Textures = m.TryGetProperty("textures", out JsonElement texs) && texs.ValueKind == JsonValueKind.Array
                ? texs.EnumerateArray().Select(t => new ModuleTexture(Str(t, "name") ?? string.Empty, Str(t, "family") ?? string.Empty,
                    t.TryGetProperty("stock", out JsonElement st) && st.ValueKind == JsonValueKind.True, Str(t, "source"))).ToList()
                : new List<ModuleTexture>(),
            Brushes = (int)(Dbl(m, "brushes") ?? 0),
            Faces = (int)(Dbl(m, "faces") ?? 0),
            HasAir = m.TryGetProperty("has_air", out JsonElement air) && air.ValueKind == JsonValueKind.True,
            RecessDepth = Dbl(m, "recess_depth"),
            MountHeight = Dbl(m, "mount_height"),
            CeilingGap = Dbl(m, "ceiling_gap"),
            Opening = m.TryGetProperty("opening", out JsonElement o) && o.ValueKind == JsonValueKind.Object
                ? new ModuleOpening(Dbl(o, "width") ?? 0, Dbl(o, "height") ?? 0, Dbl(o, "wall_thickness") ?? 0)
                : null,
            Score = Dbl(m, "score") ?? 0,
            Occurrences = (int)(Dbl(m, "occurrences") ?? 0),
            SuggestedPitch = Dbl(m, "suggested_pitch"),
            Provenance = m.TryGetProperty("provenance", out JsonElement prov) && prov.ValueKind == JsonValueKind.Array
                ? prov.EnumerateArray().Select(p => new ModuleProvenance(Str(p, "level") ?? string.Empty, Str(p, "packfile"),
                    Vec(p, "position"), Dbl(p, "yaw") ?? 0, IntList(p, "brush_uids"))).ToList()
                : new List<ModuleProvenance>(),
            File = file,
            RfgPath = groupDir is null || file.Length == 0 ? null : Path.GetFullPath(Path.Combine(groupDir, file.Replace('/', Path.DirectorySeparatorChar))),
        };
    }

    private static void Xyz(Utf8JsonWriter w, D3 v)
    {
        w.WriteStartObject();
        w.WriteNumber("x", R(v.X));
        w.WriteNumber("y", R(v.Y));
        w.WriteNumber("z", R(v.Z));
        w.WriteEndObject();
    }

    private static void Arr(Utf8JsonWriter w, D3 v)
    {
        w.WriteStartArray();
        w.WriteNumberValue(R(v.X));
        w.WriteNumberValue(R(v.Y));
        w.WriteNumberValue(R(v.Z));
        w.WriteEndArray();
    }

    private static void Ints(Utf8JsonWriter w, string name, IEnumerable<int> values)
    {
        w.WriteStartArray(name);
        foreach (int v in values)
        {
            w.WriteNumberValue(v);
        }

        w.WriteEndArray();
    }

    private static void Num(Utf8JsonWriter w, string name, double? v)
    {
        if (v is double d)
        {
            w.WriteNumber(name, R(d));
        }
        else
        {
            w.WriteNull(name);
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Dbl(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static D3 Vec(JsonElement e, string name)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 3)
        {
            return new D3(v[0].GetDouble(), v[1].GetDouble(), v[2].GetDouble());
        }

        return default;
    }

    private static List<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : new List<string>();

    private static List<int> IntList(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32()).ToList()
            : new List<int>();
}

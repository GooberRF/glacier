using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Linting;
using Ged.Core.Model;
using Ged.Core.Rooms;

namespace Ged.Automation;

/// <summary>How much a <c>summary</c> includes.</summary>
public enum SummaryDetail
{
    /// <summary>Counts only — small enough to paste into a prompt (a few KB at most).</summary>
    Brief,

    /// <summary>Counts plus per-brush, per-object and per-room lists with key fields.</summary>
    Normal,

    /// <summary>Normal plus per-brush face/texture detail, room properties, hidden state.</summary>
    Full,
}

/// <summary>
/// Builds the structured level summary: level info, bounds, brush counts,
/// objects by kind (and, above brief, each object's uid / class / script name / position / key
/// properties / links), the link graph's statistics, the rooms of the current compiled geometry with
/// portal neighbours, and the linter's counts. Output is deterministic — lists are ordered by uid
/// (rooms by index) and numbers are rounded — so two summaries of the same level diff cleanly.
/// </summary>
public static class LevelSummaryBuilder
{
    /// <summary>The summary schema id carried in every summary.</summary>
    public const string Schema = "glacier.summary/1";

    /// <summary>Builds the summary of <paramref name="host"/>'s open level.</summary>
    public static JsonObject Build(IAutomationHost host, SummaryDetail detail)
    {
        ArgumentNullException.ThrowIfNull(host);
        EditorDocument doc = host.Document
            ?? throw new AutomationException(AutomationErrorCodes.NoDocument, "No level is open.");
        bool lists = detail != SummaryDetail.Brief;
        bool full = detail == SummaryDetail.Full;

        IReadOnlyList<Brush> brushes = LevelOperations.Brushes(doc).OrderBy(b => b.Uid).ToList();
        List<LevelObject> objects = doc.Objects.OrderBy(o => o.Uid).ThenBy(o => o.Kind).ToList();
        var uids = new HashSet<int>(objects.Select(o => o.Uid));
        IReadOnlyDictionary<int, Dictionary<string, string>> tags = new GedObjectMetadataService(doc).AllTags();

        var root = new JsonObject
        {
            ["schema"] = Schema,
            ["detail"] = Dto.Camel(detail.ToString()),
            ["level"] = LevelInfo(host, doc),
            ["bounds"] = Bounds(brushes, objects),
            ["brushes"] = BrushBlock(brushes, tags, lists, full),
            ["objects"] = ObjectBlock(objects, tags, lists, full),
            ["links"] = LinkBlock(objects, uids),
            ["rooms"] = RoomBlock(host, doc, lists, full),
            ["lint"] = LintBlock(host),
        };
        return root;
    }

    private static JsonObject LevelInfo(IAutomationHost host, EditorDocument doc)
    {
        int version = doc.Rfl.Header.Version;
        return new JsonObject
        {
            ["name"] = doc.Path is { Length: > 0 } p ? System.IO.Path.GetFileName(p) : LevelTemplates.DefaultLevelName,
            ["path"] = doc.Path,
            ["rflVersion"] = version,
            ["alpine"] = doc.Rfl.Context.IsAlpine,
            ["dirty"] = doc.IsDirty,
            ["mode"] = host.Mode,
        };
    }

    private static JsonNode? Bounds(IReadOnlyList<Brush> brushes, List<LevelObject> objects)
    {
        Vec3? min = null, max = null;
        void Grow(Vec3 p)
        {
            min = min is Vec3 a ? new Vec3(MathF.Min(a.X, p.X), MathF.Min(a.Y, p.Y), MathF.Min(a.Z, p.Z)) : p;
            max = max is Vec3 b ? new Vec3(MathF.Max(b.X, p.X), MathF.Max(b.Y, p.Y), MathF.Max(b.Z, p.Z)) : p;
        }

        foreach (Brush b in brushes)
        {
            foreach (Vec3 v in b.Geometry.Vertices)
            {
                Grow(BrushTransform.WorldVertex(b, v));
            }
        }

        string source = "brushes";
        if (min is null)
        {
            source = "objects";
            foreach (LevelObject o in objects)
            {
                Grow(o.Position);
            }
        }

        if (min is not Vec3 lo || max is not Vec3 hi)
        {
            return null;
        }

        return new JsonObject
        {
            ["min"] = Arr(lo),
            ["max"] = Arr(hi),
            ["size"] = Arr(new Vec3(hi.X - lo.X, hi.Y - lo.Y, hi.Z - lo.Z)),
            ["source"] = source,
        };
    }

    private static JsonObject BrushBlock(IReadOnlyList<Brush> brushes, IReadOnlyDictionary<int, Dictionary<string, string>> tags, bool lists, bool full)
    {
        int air = 0, detail = 0, portal = 0, geoable = 0;
        foreach (Brush b in brushes)
        {
            var f = (BrushFlags)b.Flags;
            air += f.HasFlag(BrushFlags.Air) ? 1 : 0;
            detail += f.HasFlag(BrushFlags.Detail) ? 1 : 0;
            portal += f.HasFlag(BrushFlags.Portal) ? 1 : 0;
            geoable += f.HasFlag(BrushFlags.Geoable) ? 1 : 0;
        }

        var block = new JsonObject
        {
            ["total"] = brushes.Count,
            ["solid"] = brushes.Count - air,
            ["air"] = air,
            ["detail"] = detail,
            ["portal"] = portal,
            ["geoable"] = geoable,
        };
        AddTagKeys(block, brushes.Select(b => b.Uid), tags);

        if (lists)
        {
            var list = new JsonArray();
            foreach (Brush b in brushes)
            {
                JsonObject entry = BrushEntry(b, full);
                AddTags(entry, b.Uid, tags);
                list.Add(entry);
            }

            block["list"] = list;
        }

        return block;
    }

    private static JsonObject BrushEntry(Brush b, bool full)
    {
        Vec3 lo = new(float.MaxValue, float.MaxValue, float.MaxValue);
        Vec3 hi = new(float.MinValue, float.MinValue, float.MinValue);
        foreach (Vec3 v in b.Geometry.Vertices)
        {
            Vec3 w = BrushTransform.WorldVertex(b, v);
            lo = new Vec3(MathF.Min(lo.X, w.X), MathF.Min(lo.Y, w.Y), MathF.Min(lo.Z, w.Z));
            hi = new Vec3(MathF.Max(hi.X, w.X), MathF.Max(hi.Y, w.Y), MathF.Max(hi.Z, w.Z));
        }

        var flags = new JsonArray();
        var bf = (BrushFlags)b.Flags;
        flags.Add(bf.HasFlag(BrushFlags.Air) ? "air" : "solid");
        if (bf.HasFlag(BrushFlags.Detail))
        {
            flags.Add("detail");
        }

        if (bf.HasFlag(BrushFlags.Portal))
        {
            flags.Add("portal");
        }

        if (bf.HasFlag(BrushFlags.Geoable))
        {
            flags.Add("geoable");
        }

        if (bf.HasFlag(BrushFlags.EmitsSteam))
        {
            flags.Add("emitsSteam");
        }

        var textures = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Face f in b.Geometry.Faces)
        {
            if (f.Texture >= 0 && f.Texture < b.Geometry.Textures.Count && !f.IsPortalFace)
            {
                textures.Add(b.Geometry.Textures[f.Texture]);
            }
        }

        var entry = new JsonObject
        {
            ["uid"] = b.Uid,
            ["min"] = b.Geometry.Vertices.Count > 0 ? Arr(lo) : null,
            ["max"] = b.Geometry.Vertices.Count > 0 ? Arr(hi) : null,
            ["flags"] = flags,
            ["textures"] = new JsonArray(textures.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
        };

        if (full)
        {
            entry["pos"] = Arr(b.Position);
            entry["faces"] = b.Geometry.Faces.Count;
            entry["vertices"] = b.Geometry.Vertices.Count;
            if (b.Life != 0)
            {
                entry["life"] = b.Life;
            }
        }

        return entry;
    }

    private static JsonObject ObjectBlock(List<LevelObject> objects, IReadOnlyDictionary<int, Dictionary<string, string>> tags, bool lists, bool full)
    {
        var byKind = new JsonObject();
        foreach (IGrouping<LevelObjectKind, LevelObject> g in objects.GroupBy(o => o.Kind).OrderBy(g => g.Key.ToString(), StringComparer.Ordinal))
        {
            byKind[g.Key.ToString()] = g.Count();
        }

        var spawns = new JsonObject
        {
            ["playerStart"] = objects.Any(o => o.Kind == LevelObjectKind.PlayerStart),
        };
        List<MpRespawnPoint> respawns = objects.Select(o => o.Model).OfType<MpRespawnPoint>().ToList();
        spawns["multiplayer"] = respawns.Count;
        // red and blue each count every spawn that team may use, so a spawn open to both is in both counts.
        spawns["red"] = respawns.Count(r => r.RedTeam != 0);
        spawns["blue"] = respawns.Count(r => r.BlueTeam != 0);
        spawns["bothTeams"] = respawns.Count(r => r.RedTeam != 0 && r.BlueTeam != 0);
        spawns["bot"] = respawns.Count(r => r.Bot != 0);

        var block = new JsonObject
        {
            ["total"] = objects.Count,
            ["byKind"] = byKind,
            ["spawns"] = spawns,
        };
        AddTagKeys(block, objects.Select(o => o.Uid), tags);

        if (lists)
        {
            var list = new JsonArray();
            foreach (LevelObject o in objects)
            {
                JsonObject entry = ObjectEntry(o, full);
                AddTags(entry, o.Uid, tags);
                list.Add(entry);
            }

            block["list"] = list;
        }

        return block;
    }

    /// <summary>
    /// <c>tagKeys</c>: how many of the items carry each provenance-tag key (sorted by key), at every detail
    /// level — e.g. how much of the level an agent made (<c>ai.session</c>) or the design kit built (<c>kit</c>).
    /// Omitted when no item is tagged.
    /// </summary>
    private static void AddTagKeys(JsonObject block, IEnumerable<int> uids, IReadOnlyDictionary<int, Dictionary<string, string>> tags)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (int uid in uids.Distinct())
        {
            if (tags.TryGetValue(uid, out Dictionary<string, string>? t))
            {
                foreach (string key in t.Keys)
                {
                    counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
                }
            }
        }

        if (counts.Count > 0)
        {
            var o = new JsonObject();
            foreach ((string key, int n) in counts)
            {
                o[key] = n;
            }

            block["tagKeys"] = o;
        }
    }

    /// <summary>An item's provenance tags (<c>tags: {key: value}</c>, sorted by key), when it has any.</summary>
    private static void AddTags(JsonObject entry, int uid, IReadOnlyDictionary<int, Dictionary<string, string>> tags)
    {
        if (tags.TryGetValue(uid, out Dictionary<string, string>? t) && t.Count > 0)
        {
            var o = new JsonObject();
            foreach (KeyValuePair<string, string> kv in t.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                o[kv.Key] = kv.Value;
            }

            entry["tags"] = o;
        }
    }

    private static JsonObject ObjectEntry(LevelObject o, bool full)
    {
        var entry = new JsonObject
        {
            ["uid"] = o.Uid,
            ["kind"] = o.Kind.ToString(),
        };

        if (!string.IsNullOrEmpty(o.ClassName))
        {
            entry["class"] = o.ClassName;
        }

        if (!string.IsNullOrEmpty(o.ScriptName))
        {
            entry["script"] = o.ScriptName;
        }

        entry["pos"] = Arr(o.Position);

        JsonObject? props = KeyProperties(o.Model);
        if (props is not null && props.Count > 0)
        {
            entry["props"] = props;
        }

        if (LinkModel.LinksOf(o) is { Count: > 0 } links)
        {
            entry["links"] = new JsonArray(links.OrderBy(u => u).Select(u => (JsonNode?)JsonValue.Create(u)).ToArray());
        }

        if (full && o.Hidden)
        {
            entry["hidden"] = true;
        }

        return entry;
    }

    /// <summary>The handful of per-kind fields that matter for layout/gameplay reasoning.</summary>
    private static JsonObject? KeyProperties(object model) => model switch
    {
        MpRespawnPoint r => new JsonObject
        {
            ["red"] = r.RedTeam != 0,
            ["blue"] = r.BlueTeam != 0,
            ["bot"] = r.Bot != 0,
        },
        Light l => new JsonObject
        {
            ["range"] = Dto.R(l.Range),
            ["color"] = Hex(l.Color),
            ["intensity"] = Dto.R(l.OnIntensity),
        },
        Item i => new JsonObject
        {
            ["count"] = i.Count,
            ["respawnTime"] = i.RespawnTime,
            ["team"] = i.TeamId,
        },
        Trigger t => TriggerProps(t),
        RflEvent e => e.Delay != 0 ? new JsonObject { ["delay"] = Dto.R(e.Delay) } : null,
        _ => null,
    };

    private static JsonObject TriggerProps(Trigger t)
    {
        var p = new JsonObject();
        if (t.SphereRadius is float r)
        {
            p["shape"] = "sphere";
            p["radius"] = Dto.R(r);
        }
        else if (t.BoxWidth is float w)
        {
            p["shape"] = "box";
            p["size"] = Arr(new Vec3(w, t.BoxHeight ?? 0, t.BoxDepth ?? 0));
        }

        if (t.IsUseKeyRequired != 0)
        {
            p["useKey"] = true;
        }

        if (t.IsAuto != 0)
        {
            p["auto"] = true;
        }

        p["resetsTimes"] = t.ResetsTimes;
        return p;
    }

    private static JsonObject LinkBlock(List<LevelObject> objects, HashSet<int> uids)
    {
        int total = 0, sources = 0, broken = 0;
        var targets = new HashSet<int>();
        var linked = new HashSet<int>();
        foreach (LevelObject o in objects)
        {
            if (LinkModel.LinksOf(o) is not { Count: > 0 } links)
            {
                continue;
            }

            sources++;
            linked.Add(o.Uid);
            foreach (int t in links)
            {
                total++;
                targets.Add(t);
                linked.Add(t);
                if (!uids.Contains(t))
                {
                    broken++;
                }
            }
        }

        List<LevelObject> events = objects.Where(o => o.Kind == LevelObjectKind.Event).ToList();
        List<LevelObject> triggers = objects.Where(o => o.Kind == LevelObjectKind.Trigger).ToList();
        return new JsonObject
        {
            ["total"] = total,
            ["sources"] = sources,
            ["targets"] = targets.Count,
            ["broken"] = broken,
            ["events"] = events.Count,
            ["unlinkedEvents"] = events.Count(e => !linked.Contains(e.Uid)),
            ["triggers"] = triggers.Count,
            ["triggersWithoutLinks"] = triggers.Count(t => LinkModel.LinksOf(t) is not { Count: > 0 }),
        };
    }

    private static JsonObject RoomBlock(IAutomationHost host, EditorDocument doc, bool lists, bool full)
    {
        Geometry? geo = LevelOperations.CompiledGeometry(doc);
        if (geo is null || geo.Rooms.Count == 0)
        {
            return new JsonObject { ["status"] = "not built", ["count"] = 0 };
        }

        LevelBuildState state = host.BuildState;
        string status = state.GeometryIsPreview ? "preview" : state.GeometryDirty ? "stale" : "built";
        int sub = geo.Rooms.Count(r => r.IsSubroom != 0);
        var block = new JsonObject
        {
            ["status"] = status,
            ["count"] = geo.Rooms.Count,
            ["main"] = geo.Rooms.Count - sub,
            ["sub"] = sub,
            ["portals"] = geo.Portals.Count,
        };

        if (!lists)
        {
            return block;
        }

        RoomGraph graph = RoomGraph.Build(geo);
        var list = new JsonArray();
        for (int i = 0; i < geo.Rooms.Count; i++)
        {
            Room room = geo.Rooms[i];
            Vec3 lo = room.Aabb.P1, hi = room.Aabb.P2;
            float volume = MathF.Max(hi.X - lo.X, 0) * MathF.Max(hi.Y - lo.Y, 0) * MathF.Max(hi.Z - lo.Z, 0);
            var entry = new JsonObject
            {
                ["index"] = i,
                ["min"] = Arr(lo),
                ["max"] = Arr(hi),
                ["volume"] = Dto.R(volume),
            };

            JsonArray flags = RoomFlags(room);
            if (flags.Count > 0)
            {
                entry["flags"] = flags;
            }

            IEnumerable<IGrouping<int, (int Neighbor, int PortalIndex)>> byNeighbor =
                graph.Neighbors(i).GroupBy(n => n.Neighbor).OrderBy(g => g.Key);
            var neighbors = new JsonArray();
            foreach (IGrouping<int, (int Neighbor, int PortalIndex)> g in byNeighbor)
            {
                if (full)
                {
                    neighbors.Add(new JsonObject
                    {
                        ["room"] = g.Key,
                        ["portals"] = new JsonArray(g.Select(n => n.PortalIndex).OrderBy(p => p).Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
                    });
                }
                else
                {
                    neighbors.Add(g.Key);
                }
            }

            if (neighbors.Count > 0)
            {
                entry["neighbors"] = neighbors;
            }

            if (full)
            {
                if (room.Life != 0)
                {
                    entry["life"] = Dto.R(room.Life);
                }

                if (!string.IsNullOrEmpty(room.EaxEffect))
                {
                    entry["eax"] = room.EaxEffect;
                }

                if (room.AmbientColor is RfColor ambient)
                {
                    entry["ambient"] = Hex(ambient);
                }
            }

            list.Add(entry);
        }

        block["list"] = list;
        return block;
    }

    private static JsonArray RoomFlags(Room r)
    {
        var flags = new JsonArray();
        void Add(byte value, string name)
        {
            if (value != 0)
            {
                flags.Add(name);
            }
        }

        Add(r.IsSubroom, "subroom");
        Add(r.IsSkyroom, "sky");
        Add(r.IsOutside, "outside");
        Add(r.IsCold, "cold");
        Add(r.IsAirlock, "airlock");
        Add(r.IsLiquidRoom, "liquid");
        Add(r.HasAmbientLight, "ambientLight");
        Add(r.HasAlpha, "alpha");
        return flags;
    }

    private static JsonObject LintBlock(IAutomationHost host)
    {
        (LintReport report, _) = LevelLint.Run(host);
        return new JsonObject
        {
            ["errors"] = report.ErrorCount,
            ["warnings"] = report.WarningCount,
            ["info"] = report.InfoCount,
        };
    }

    private static JsonArray Arr(Vec3 v)
    {
        double[] d = Dto.V(v);
        return new JsonArray(d[0], d[1], d[2]);
    }

    private static string Hex(RfColor c) => string.Create(CultureInfo.InvariantCulture, $"#{c.R:X2}{c.G:X2}{c.B:X2}");
}

/// <summary>The automation linter run (shared by <c>lint</c> and <c>summary</c>), configured like the editor's.</summary>
public static class LevelLint
{
    /// <summary>
    /// Lints the open level exactly as the editor's Linter does (save-target budgets, the mounted
    /// library for asset checks, the catalogs for the dependency scan). One deliberate difference: when
    /// the document holds UNSEALED preview geometry the leak check is skipped (it would report thousands
    /// of false open edges) and a note says to run <c>holes</c>, which always measures a sealed build.
    /// <paramref name="checkNorms"/> adds the out-of-norm Info hints (the <c>lint</c> command; skipped with a note on
    /// preview geometry).
    /// </summary>
    public static (LintReport Report, List<string> Notes) Run(IAutomationHost host, bool checkNorms = false)
    {
        EditorDocument doc = host.Document
            ?? throw new AutomationException(AutomationErrorCodes.NoDocument, "No level is open.");
        var notes = new List<string>();
        bool preview = host.BuildState.GeometryIsPreview;
        if (preview)
        {
            notes.Add("Geometry is an unsealed preview build: the leak check was skipped. Run 'holes' (always sealed) for leaks.");
        }

        if (host.Assets is null)
        {
            notes.Add("No RF install is mounted: missing-asset and texture checks were skipped.");
        }

        var options = new LintOptions
        {
            Target = SaveTargets.FromVersion(doc.Rfl.Header.Version),
            Vfs = host.Assets,
            ScanOptions = host.CreateScanOptions(),
            MaxTextureDimension = 1024,
            CheckGeometryLeaks = !preview,
            CheckNorms = checkNorms,
            GeometryIsPreview = preview,
            FileName = doc.Path is { Length: > 0 } path ? System.IO.Path.GetFileName(path) : null,
        };
        LintReport report = LevelLinter.Lint(doc.Rfl, options);
        notes.AddRange(report.Notes);
        return (report, notes);
    }
}

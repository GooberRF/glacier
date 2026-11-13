using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Scripting;

namespace Ged.Automation;

/// <summary>
/// Read-only views of the open level for the <c>inspect</c>, <c>query</c>, <c>catalog</c> and
/// <c>checkpoint</c>/<c>diff</c> commands. Everything is read through the same script facade the Lua
/// globals use (<c>obj:fields()</c>, <c>obj:props()</c>, <c>catalog.describe</c>, …), bound to a
/// throwaway <see cref="ScriptContext"/> that never records a change — so a command and a Lua read of
/// the same object agree field for field.
/// </summary>
internal static class LevelInspector
{
    /// <summary>
    /// A read-only script context over <paramref name="doc"/>. Brushes are read through the host's own
    /// brush editor (a second editor would re-bind the document's undo batching); without an open level
    /// the context runs over a private blank level (catalog lookups need no document).
    /// </summary>
    public static ScriptContext Context(IAutomationHost host, EditorDocument? doc)
    {
        BrushEditor? brushes = doc is not null ? host.Brushes : null;
        if (doc is null)
        {
            doc = LevelTemplates.CreateNew();
            brushes = new BrushEditor(doc);
        }

        var services = new ScriptServices
        {
            Document = doc,
            Brushes = brushes,
            Assets = host.Assets,
            InstallDirectory = host.RfInstallDir,
        };
        return new ScriptContext(services, new ScriptRunOptions { ChunkName = "inspect", DryRun = true }, new ScriptLog());
    }

    /// <summary>The brush with <paramref name="uid"/> when the UID names a brush rather than an object (movers resolve to their brush).</summary>
    public static bool IsBrush(ScriptContext ctx, int uid)
    {
        ScriptObjectHandle? obj = ctx.Level.FindUid(uid);
        ScriptBrushHandle? brush = ctx.Level.FindBrush(uid);
        return brush is not null && (obj is null || obj.Kind == nameof(LevelObjectKind.Mover));
    }

    // ---- inspect ----------------------------------------------------------------------------------

    public static JsonObject Inspect(ScriptContext ctx, int uid)
    {
        if (IsBrush(ctx, uid))
        {
            return InspectBrush(ctx.Level.FindBrush(uid)!);
        }

        ScriptObjectHandle o = ctx.Level.FindUid(uid)
            ?? throw new AutomationException(AutomationErrorCodes.Usage,
                $"No object or brush has UID {uid}. Use query (objects or brushes) to list UIDs.");
        var result = new JsonObject
        {
            ["uid"] = o.Uid,
            ["kind"] = o.Kind,
        };
        if (o.ClassName.Length > 0)
        {
            result["class"] = o.ClassName;
        }

        if (o.Name.Length > 0)
        {
            result["name"] = o.Name;
        }

        result["pos"] = Vec(o.Pos);
        if (o.Rot is { } rot)
        {
            result["rot"] = Rot(rot);
        }

        result["fields"] = Fields(o.Fields(), o.Props());
        result["links"] = new JsonObject
        {
            ["out"] = new JsonArray(o.Links().Select(u => (JsonNode?)JsonValue.Create(u)).ToArray()),
            ["in"] = new JsonArray(o.LinksIn().Select(u => (JsonNode?)JsonValue.Create(u)).ToArray()),
        };
        result["tags"] = Tags(o.Tags());
        return result;
    }

    private static JsonObject InspectBrush(ScriptBrushHandle b)
    {
        var textures = b.Faces.Select(f => f.Texture).Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        return new JsonObject
        {
            ["uid"] = b.Uid,
            ["kind"] = "Brush",
            ["pos"] = Vec(b.Pos),
            ["rot"] = Rot(b.Rot),
            ["fields"] = Fields(b.Fields(), b.Props()),
            ["links"] = new JsonObject { ["out"] = new JsonArray(), ["in"] = new JsonArray() },
            ["tags"] = Tags(b.Tags()),
            ["brush"] = new JsonObject
            {
                ["flags"] = new JsonArray(BrushFlags(b).Select(f => (JsonNode?)f).ToArray()),
                ["faces"] = b.FaceCount,
                ["timeIndex"] = b.TimeIndex,
                ["size"] = Vec(b.Size),
                ["bounds"] = new JsonObject { ["min"] = Vec(b.Bounds.Min), ["max"] = Vec(b.Bounds.Max) },
                ["textures"] = new JsonArray(textures.Select(t => (JsonNode?)t).ToArray()),
            },
        };
    }

    /// <summary>The flag names a brush carries ("air" or "solid", then detail / portal / geoable / steam).</summary>
    public static IEnumerable<string> BrushFlags(ScriptBrushHandle b)
    {
        yield return b.Air ? "air" : "solid";
        if (b.Detail)
        {
            yield return "detail";
        }

        if (b.Portal)
        {
            yield return "portal";
        }

        if (b.Geoable)
        {
            yield return "geoable";
        }

        if (b.EmitsSteam)
        {
            yield return "emits_steam";
        }
    }

    private static JsonArray Fields(ScriptFieldInfo[] infos, Dictionary<string, object?> values)
    {
        var list = new JsonArray();
        foreach (ScriptFieldInfo f in infos)
        {
            var o = new JsonObject { ["name"] = f.Name, ["type"] = f.Type };
            o["value"] = values.TryGetValue(f.Name, out object? v) ? ToJson(v) : null;
            if (f.ReadOnly)
            {
                o["readOnly"] = true;
            }

            if (f.Doc.Length > 0)
            {
                o["doc"] = f.Doc;
            }

            list.Add(o);
        }

        return list;
    }

    // ---- query ------------------------------------------------------------------------------------

    /// <summary>The <c>query</c> filters.</summary>
    public sealed record QuerySpec(string What, string? Kind, string? Class, string? Name, string? Tag, double[]? Region, int Limit, int Offset);

    public static JsonObject Query(ScriptContext ctx, QuerySpec q)
    {
        var items = new List<(int Uid, JsonObject Item)>();
        (Vec3 Min, Vec3 Max)? region = q.Region is { } r
            ? (new Vec3((float)Math.Min(r[0], r[3]), (float)Math.Min(r[1], r[4]), (float)Math.Min(r[2], r[5])),
               new Vec3((float)Math.Max(r[0], r[3]), (float)Math.Max(r[1], r[4]), (float)Math.Max(r[2], r[5])))
            : null;
        (string Key, string? Value)? tag = q.Tag is { } t ? ParseTag(t) : null;
        if (q.What == "brushes")
        {
            if (q.Kind is not null || q.Class is not null || q.Name is not null)
            {
                throw new AutomationException(AutomationErrorCodes.Usage, "kind / class / name filter objects; brushes take tag and region only.");
            }

            IEnumerable<ScriptBrushHandle> brushes = region is { } box
                ? ctx.Level.BrushesIn(BoxArgs(box)).Brushes
                : ctx.Level.Brushes;
            if (tag is { } tg)
            {
                HashSet<int> tagged = ctx.Level.BrushesTagged(tg.Key, tg.Value).Uids.ToHashSet();
                brushes = brushes.Where(b => tagged.Contains(b.Uid));
            }

            foreach (ScriptBrushHandle b in brushes)
            {
                items.Add((b.Uid, new JsonObject
                {
                    ["uid"] = b.Uid,
                    ["kind"] = "Brush",
                    ["pos"] = Vec(b.Pos),
                    ["flags"] = new JsonArray(BrushFlags(b).Select(f => (JsonNode?)f).ToArray()),
                }));
            }
        }
        else if (q.What == "objects")
        {
            IEnumerable<ScriptObjectHandle> objects = q.Kind is { } kind ? ctx.Level.ObjectsOf(kind) : ctx.Level.Objects;
            if (tag is { } tg)
            {
                HashSet<int> tagged = ctx.Level.ObjectsTagged(tg.Key, tg.Value).Uids.ToHashSet();
                objects = objects.Where(o => tagged.Contains(o.Uid));
            }

            Func<string, bool>? cls = q.Class is { } c ? Glob(c) : null;
            Func<string, bool>? name = q.Name is { } n ? Glob(n) : null;
            foreach (ScriptObjectHandle o in objects)
            {
                if ((cls is not null && !cls(o.ClassName)) || (name is not null && !name(o.Name)))
                {
                    continue;
                }

                Vec3 p = o.Pos;
                if (region is { } box && (p.X < box.Min.X || p.X > box.Max.X || p.Y < box.Min.Y || p.Y > box.Max.Y || p.Z < box.Min.Z || p.Z > box.Max.Z))
                {
                    continue;
                }

                var item = new JsonObject { ["uid"] = o.Uid, ["kind"] = o.Kind };
                if (o.ClassName.Length > 0)
                {
                    item["class"] = o.ClassName;
                }

                if (o.Name.Length > 0)
                {
                    item["name"] = o.Name;
                }

                item["pos"] = Vec(p);
                items.Add((o.Uid, item));
            }
        }
        else
        {
            throw new AutomationException(AutomationErrorCodes.Usage, $"Unknown 'what' value '{q.What}' (use objects or brushes).");
        }

        List<JsonObject> sorted = items.OrderBy(i => i.Uid).Select(i => i.Item).ToList();
        List<JsonObject> page = sorted.Skip(q.Offset).Take(q.Limit).ToList();
        bool truncated = q.Offset + page.Count < sorted.Count;
        var result = new JsonObject
        {
            ["what"] = q.What,
            ["total"] = sorted.Count,
            ["offset"] = q.Offset,
            ["returned"] = page.Count,
            ["truncated"] = truncated,
            ["items"] = new JsonArray(page.Select(i => (JsonNode?)i).ToArray()),
        };
        if (truncated)
        {
            result["hint"] = $"More results: repeat with offset {q.Offset + page.Count}, or narrow with kind, class, name, tag or region.";
        }

        return result;
    }

    private static ScriptArgs BoxArgs((Vec3 Min, Vec3 Max) box) => new(new Dictionary<string, object?>
    {
        ["min"] = new ScriptArgs(null, new object?[] { (double)box.Min.X, (double)box.Min.Y, (double)box.Min.Z }),
        ["max"] = new ScriptArgs(null, new object?[] { (double)box.Max.X, (double)box.Max.Y, (double)box.Max.Z }),
    });

    private static (string Key, string? Value) ParseTag(string tag)
    {
        int eq = tag.IndexOf('=');
        return eq < 0 ? (tag.Trim(), null) : (tag[..eq].Trim(), tag[(eq + 1)..]);
    }

    /// <summary>Case-insensitive match: a glob with * and ?, else an exact name.</summary>
    public static Func<string, bool> Glob(string pattern)
    {
        if (pattern.IndexOfAny(new[] { '*', '?' }) < 0)
        {
            return s => string.Equals(s, pattern, StringComparison.OrdinalIgnoreCase);
        }

        var rx = new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return s => rx.IsMatch(s);
    }

    // ---- catalog ----------------------------------------------------------------------------------

    public static JsonObject Catalog(ScriptContext ctx, bool assetsMounted, string op, string? kind, string? cls, string? filter, int? limit, bool stockOnly)
    {
        ScriptCatalog catalog = ctx.Catalog;
        switch (op)
        {
            case "kinds":
                return new JsonObject
                {
                    ["op"] = op,
                    ["kinds"] = new JsonArray(catalog.Kinds().Select(k => (JsonNode?)k).ToArray()),
                    ["tablesLoaded"] = catalog.Available,
                };

            case "classes":
            {
                string k = kind ?? throw new AutomationException(AutomationErrorCodes.Usage, "catalog classes needs a kind (e.g. Item, Entity, Clutter, Event).");
                IEnumerable<string> names = catalog.Classes(k);
                if (filter is { Length: > 0 })
                {
                    Func<string, bool> match = filter.IndexOfAny(new[] { '*', '?' }) >= 0 ? Glob(filter) : n => n.Contains(filter, StringComparison.OrdinalIgnoreCase);
                    names = names.Where(match);
                }

                List<string> all = names.ToList();
                int take = limit ?? 500;
                var result = new JsonObject
                {
                    ["op"] = op,
                    ["kind"] = k,
                    ["tablesLoaded"] = catalog.Available,
                    ["total"] = all.Count,
                    ["returned"] = Math.Min(take, all.Count),
                    ["truncated"] = all.Count > take,
                    ["classes"] = new JsonArray(all.Take(take).Select(n => (JsonNode?)n).ToArray()),
                };
                if (all.Count == 0 && !catalog.Available && k is not "Event")
                {
                    result["note"] = "No RF install is mounted, so the game's class tables are not loaded.";
                }

                return result;
            }

            case "describe":
            {
                string k = kind ?? throw new AutomationException(AutomationErrorCodes.Usage, "catalog describe needs a kind (and optionally a class).");
                ScriptClassInfo info = catalog.Describe(k, cls);
                var o = new JsonObject
                {
                    ["op"] = op,
                    ["kind"] = info.Kind,
                };
                if (info.ClassName.Length > 0)
                {
                    o["class"] = info.ClassName;
                }

                if (info.Category.Length > 0)
                {
                    o["category"] = info.Category;
                }

                if (info.Description.Length > 0)
                {
                    o["description"] = info.Description;
                }

                o["alpine"] = info.Alpine;
                o["hasRotation"] = info.HasRotation;
                o["linkTargets"] = new JsonArray(info.LinkTargets.Select(t => (JsonNode?)t).ToArray());
                var fields = new JsonArray();
                foreach (ScriptFieldInfo f in info.Fields)
                {
                    var fo = new JsonObject { ["name"] = f.Name, ["type"] = f.Type };
                    if (f.Options.Length > 0)
                    {
                        fo["options"] = new JsonArray(f.Options.Select(x => (JsonNode?)x).ToArray());
                    }

                    if (f.Min is double min)
                    {
                        fo["min"] = Dto.R(min);
                    }

                    if (f.Max is double max)
                    {
                        fo["max"] = Dto.R(max);
                    }

                    if (f.ReadOnly)
                    {
                        fo["readOnly"] = true;
                    }

                    if (f.Doc.Length > 0)
                    {
                        fo["doc"] = f.Doc;
                    }

                    fields.Add(fo);
                }

                o["fields"] = fields;
                return o;
            }

            case "textures":
            {
                int take = limit ?? 50;
                var named = new Dictionary<string, object?> { ["limit"] = (double)take };
                if (filter is { Length: > 0 })
                {
                    named["filter"] = filter;
                }

                if (stockOnly)
                {
                    named["stock"] = true;
                }

                ScriptTextureInfo[] textures = catalog.Textures(new ScriptArgs(named));
                var list = new JsonArray();
                foreach (ScriptTextureInfo t in textures)
                {
                    var to = new JsonObject { ["name"] = t.Name, ["stock"] = t.Stock };
                    if (t.Origin is { } origin)
                    {
                        to["origin"] = origin;
                    }

                    if (t.Overridden)
                    {
                        to["overridden"] = true;
                    }

                    list.Add(to);
                }

                var result = new JsonObject
                {
                    ["op"] = op,
                    ["available"] = assetsMounted,
                    ["returned"] = list.Count,
                    ["limit"] = take,
                    ["textures"] = list,
                };
                if (!assetsMounted)
                {
                    result["note"] = "No RF install is mounted, so no textures are available.";
                }
                else if (list.Count == take)
                {
                    result["hint"] = "The list may be capped at 'limit': narrow it with a filter (substring or glob).";
                }

                return result;
            }

            default:
                throw new AutomationException(AutomationErrorCodes.Usage, $"Unknown catalog op '{op}' (use kinds, classes, describe or textures).");
        }
    }

    // ---- checkpoint / diff --------------------------------------------------------------------------

    /// <summary>A structural snapshot: per UID its kind, class and every field value (compact JSON), per brush a few hashes.</summary>
    public sealed class Snapshot
    {
        public required string Name { get; init; }

        public required EditorDocument Document { get; init; }

        public required Dictionary<int, Entry> Entries { get; init; }

        public int Objects { get; init; }

        public int Brushes { get; init; }
    }

    /// <summary>One snapshotted UID: identity plus field name → compact JSON value (brush hashes as opaque strings).</summary>
    public sealed record Entry(string Kind, string? Class, string? Name, Dictionary<string, string> Fields, HashSet<string> Opaque);

    public static Snapshot Take(ScriptContext ctx, string name, EditorDocument doc)
    {
        var entries = new Dictionary<int, Entry>();
        int objects = 0, brushes = 0;
        foreach (ScriptObjectHandle o in ctx.Level.Objects)
        {
            if (IsBrush(ctx, o.Uid))
            {
                continue; // a mover's object twin: tracked as its brush
            }

            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach ((string key, object? value) in o.Props())
            {
                fields[key] = Compact(ToJson(value));
            }

            if (o.Rot is { } rot)
            {
                fields["rot"] = Compact(Rot(rot));
            }

            fields["links"] = Compact(new JsonArray(o.Links().Select(u => (JsonNode?)JsonValue.Create(u)).ToArray()));
            fields["tags"] = Compact(Tags(o.Tags()));
            entries[o.Uid] = new Entry(o.Kind, o.ClassName.Length > 0 ? o.ClassName : null, o.Name.Length > 0 ? o.Name : null, fields, new HashSet<string>());
            objects++;
        }

        foreach (ScriptBrushHandle b in ctx.Level.Brushes)
        {
            Brush? model = ctx.Level.FindBrush(b.Uid) is null ? null : FindBrushModel(doc, b.Uid);
            var fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["position"] = Compact(Vec(b.Pos)),
                ["rotation"] = Compact(Rot(b.Rot)),
                ["flags"] = Compact(new JsonArray(BrushFlags(b).Select(f => (JsonNode?)f).ToArray())),
                ["tags"] = Compact(Tags(b.Tags())),
            };
            if (model is not null)
            {
                fields["geometry"] = GeometryHash(model).ToString(System.Globalization.CultureInfo.InvariantCulture);
                fields["textures"] = TextureHash(model).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            entries[b.Uid] = new Entry("Brush", null, null, fields, new HashSet<string> { "geometry", "textures" });
            brushes++;
        }

        return new Snapshot { Name = name, Document = doc, Entries = entries, Objects = objects, Brushes = brushes };
    }

    public static JsonObject Diff(Snapshot before, Snapshot now, int maxChanges)
    {
        var added = new JsonArray();
        var removed = new JsonArray();
        var changed = new JsonArray();
        int nAdded = 0, nRemoved = 0, nChanged = 0, listed = 0;
        foreach (int uid in now.Entries.Keys.Union(before.Entries.Keys).OrderBy(u => u))
        {
            bool inBefore = before.Entries.TryGetValue(uid, out Entry? b);
            bool inNow = now.Entries.TryGetValue(uid, out Entry? n);
            if (inNow && !inBefore)
            {
                nAdded++;
                if (listed++ < maxChanges)
                {
                    added.Add(Identity(uid, n!));
                }
            }
            else if (inBefore && !inNow)
            {
                nRemoved++;
                if (listed++ < maxChanges)
                {
                    removed.Add(Identity(uid, b!));
                }
            }
            else if (b is not null && n is not null)
            {
                var fields = new JsonArray();
                foreach (string key in n.Fields.Keys.Union(b.Fields.Keys).OrderBy(k => k, StringComparer.Ordinal))
                {
                    b.Fields.TryGetValue(key, out string? bv);
                    n.Fields.TryGetValue(key, out string? nv);
                    if (string.Equals(bv, nv, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var f = new JsonObject { ["name"] = key };
                    if (!n.Opaque.Contains(key))
                    {
                        f["before"] = bv is null ? null : JsonNode.Parse(bv);
                        f["after"] = nv is null ? null : JsonNode.Parse(nv);
                    }

                    fields.Add(f);
                }

                if (b.Kind != n.Kind)
                {
                    fields.Insert(0, new JsonObject { ["name"] = "kind", ["before"] = b.Kind, ["after"] = n.Kind });
                }

                if (fields.Count > 0)
                {
                    nChanged++;
                    if (listed++ < maxChanges)
                    {
                        JsonObject c = Identity(uid, n);
                        c["fields"] = fields;
                        changed.Add(c);
                    }
                }
            }
        }

        bool truncated = listed > maxChanges;
        var result = new JsonObject
        {
            ["since"] = before.Name,
            ["added"] = added,
            ["removed"] = removed,
            ["changed"] = changed,
            ["counts"] = new JsonObject { ["added"] = nAdded, ["removed"] = nRemoved, ["changed"] = nChanged },
            ["truncated"] = truncated,
        };
        if (truncated)
        {
            result["hint"] = $"Only the first {maxChanges} changes are listed (by UID): raise maxChanges or inspect UIDs individually.";
        }

        return result;
    }

    private static JsonObject Identity(int uid, Entry e)
    {
        var o = new JsonObject { ["uid"] = uid, ["kind"] = e.Kind };
        if (e.Class is not null)
        {
            o["class"] = e.Class;
        }

        if (e.Name is not null)
        {
            o["name"] = e.Name;
        }

        return o;
    }

    private static Brush? FindBrushModel(EditorDocument doc, int uid) =>
        LevelOperations.Brushes(doc).FirstOrDefault(b => b.Uid == uid);

    private static int GeometryHash(Brush b)
    {
        var h = new HashCode();
        h.Add(b.Geometry.Vertices.Count);
        foreach (Vec3 v in b.Geometry.Vertices)
        {
            h.Add(v);
        }

        h.Add(b.Geometry.Faces.Count);
        foreach (Face f in b.Geometry.Faces)
        {
            h.Add(f.Flags);
            foreach (FaceVertex fv in f.Vertices)
            {
                h.Add(fv.Index);
            }
        }

        return h.ToHashCode();
    }

    private static int TextureHash(Brush b)
    {
        var h = new HashCode();
        foreach (Face f in b.Geometry.Faces)
        {
            string name = f.Texture >= 0 && f.Texture < b.Geometry.Textures.Count ? b.Geometry.Textures[f.Texture] : string.Empty;
            h.Add(name, StringComparer.OrdinalIgnoreCase);
            foreach (FaceVertex fv in f.Vertices)
            {
                h.Add(fv.TextureCoords);
            }
        }

        return h.ToHashCode();
    }

    // ---- JSON helpers ------------------------------------------------------------------------------

    private static string Compact(JsonNode? n) => n?.ToJsonString(AutomationJson.Options) ?? "null";

    public static JsonArray Vec(Vec3 v) => new(Dto.V(v).Select(d => (JsonNode?)JsonValue.Create(d)).ToArray());

    private static JsonObject Rot(ScriptRotation r) => new()
    {
        ["yaw"] = Dto.R(r.Yaw),
        ["pitch"] = Dto.R(r.Pitch),
        ["roll"] = Dto.R(r.Roll),
    };

    private static JsonObject Tags(Dictionary<string, object?> tags)
    {
        var o = new JsonObject();
        foreach ((string k, object? v) in tags.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            o[k] = v?.ToString();
        }

        return o;
    }

    /// <summary>A script-facade value as JSON (numbers rounded to 3 decimals, vectors as [x, y, z]).</summary>
    public static JsonNode? ToJson(object? v) => v switch
    {
        null => null,
        string s => s,
        bool b => b,
        double d => double.IsFinite(d) ? Dto.R(d) : null,
        float f => float.IsFinite(f) ? Dto.R(f) : null,
        int i => i,
        long l => l,
        Vec3 p => Vec(p),
        ScriptRotation r => Rot(r),
        IDictionary<string, object?> map => new JsonObject(map.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, ToJson(kv.Value)))),
        IEnumerable seq => new JsonArray(seq.Cast<object?>().Select(ToJson).ToArray()),
        _ => v.ToString(),
    };
}

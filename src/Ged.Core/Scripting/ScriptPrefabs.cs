using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfg;
using Ged.Core.Model;
using Ged.Core.Prefabs;

namespace Ged.Core.Scripting;

/// <summary>
/// The <c>prefab</c> global: list the prefab library and place tracked prefab instances.
/// Every file access is confined to the configured library folder
/// (<see cref="ScriptServices.PrefabsDirectory"/>): the library is enumerated from that folder only,
/// a <c>name</c> is matched against the listing (never opened as a path), and a resolved file must
/// lie inside the folder. Placement is the editor's own path — <see cref="PrefabPackage.LoadBased"/>
/// then <see cref="PrefabInstanceService.PlaceInstance"/> (one nested transaction joining the run's
/// undo step) and the brush editor's structural change notification.
/// </summary>
[ScriptDoc("The prefab library: list() the .gedprefab files in the library folder and place{name=, pos=} tracked instances (links inside a prefab are remapped).", Since = 2)]
public sealed class ScriptPrefabs
{
    private readonly ScriptContext _ctx;

    internal ScriptPrefabs(ScriptContext ctx) => _ctx = ctx;

    /// <summary>The library folder (the host's setting, else the default library).</summary>
    internal string LibraryRoot => Path.GetFullPath(
        string.IsNullOrWhiteSpace(_ctx.Services.PrefabsDirectory) ? AppPaths.DefaultPrefabsDirectory : _ctx.Services.PrefabsDirectory!);

    [ScriptDoc("Every prefab in the library folder (recursive), sorted by name: name, file, description, author, brush_count, object_count. Returns prefab_info[].", Example = "for _, p in ipairs(prefab.list()) do print(p.name, p.file) end", Since = 2)]
    public ScriptPrefabInfo[] List() => Scan(warn: true).Select(e => e.Info).ToArray();

    [ScriptDoc("Places a prefab by name (or library file) with its pivot at pos (metres), optional rot or yaw and tags {k = v} on every member. Returns prefab_instance.", Example = "prefab.place{name = \"crate_stack\", pos = {4, 0, 2}, yaw = 90}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.PrefabPlace))]
    public ScriptPrefabInstance Place(ScriptArgs args)
    {
        const string Api = "prefab.place";
        if (args is null)
        {
            throw new ScriptApiException($"{Api}: a table {{name=, pos=}} is required.");
        }

        args.RejectUnknown(ScriptOptionSets.PrefabPlace);
        string name = args.String(Api, "name") ?? throw new ScriptApiException($"{Api}: 'name' is required.", "Use prefab.list() to see the library.");
        Vec3 pos = args.VecOrNull(Api, "pos") ?? throw new ScriptApiException($"{Api}: 'pos' is required.", "The world point the prefab's pivot lands on, e.g. pos = {0, 0, 0}.");
        if (args.Has("rot") && args.Has("yaw"))
        {
            throw new ScriptApiException($"{Api}: give either 'rot' or 'yaw', not both.");
        }

        Mat3 rot = args.Has("rot") ? ScriptRotation.FromArgs(Api, "rot", args.Named["rot"])
            : args.NumberOrNull(Api, "yaw") is double yaw ? ScriptRotation.FromEuler(yaw, 0, 0) : Mat3.Identity;
        rot = rot.Orthonormalize();
        Dictionary<string, string> tags = ScriptTags.Table(Api, args.Table(Api, "tags"));

        string path = Resolve(Api, name);
        (PrefabPackage pkg, RfgFile payload, string hash) loaded;
        try
        {
            loaded = PrefabPackage.LoadBased(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            throw new ScriptApiException($"{Api}: could not read prefab '{name}': {ex.Message}");
        }

        string prefabName = string.IsNullOrWhiteSpace(loaded.pkg.Manifest.Name) ? Path.GetFileNameWithoutExtension(path) : loaded.pkg.Manifest.Name;
        PrefabInstanceRecord record = _ctx.Prefabs.PlaceInstance(loaded.payload, prefabName, loaded.hash, pos, rot);
        if (loaded.payload.Groups.Any(g => g.Brushes.Brushes.Count > 0))
        {
            _ctx.Brushes.NotifyExternalBrushChange(); // imported brushes bypass the brush editor's commands
        }

        foreach (int uid in record.MemberUids)
        {
            _ctx.NoteCreated(uid);
        }

        ScriptTags.Merge(_ctx, Api, record.MemberUids, tags);
        _ctx.Changes.Record("prefab placed");
        return new ScriptPrefabInstance(_ctx, record);
    }

    public override string ToString() => "prefab";

    /// <summary>Resolves a prefab by manifest name, then by library-relative file; confined to the library folder.</summary>
    private string Resolve(string api, string name)
    {
        string n = name.Trim();
        if (n.Length == 0)
        {
            throw new ScriptApiException($"{api}: 'name' must not be empty.", "Use prefab.list() to see the library.");
        }

        if (Path.IsPathRooted(n) || n.Replace('\\', '/').Split('/').Any(seg => seg == ".."))
        {
            throw new ScriptApiException($"{api}: '{name}' is a path outside the prefab library.",
                "Give a prefab name (or its file as listed by prefab.list()); prefabs are only read from the library folder.");
        }

        List<Entry> all = Scan(warn: false);
        List<Entry> byName = all.Where(e => string.Equals(e.Info.Name, n, StringComparison.OrdinalIgnoreCase)).ToList();
        List<Entry> matches = byName.Count > 0
            ? byName
            : all.Where(e => string.Equals(e.Info.File, n.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            string hint = all.Count == 0
                ? $"The prefab library ({LibraryRoot}) has no prefabs."
                : ScriptSuggest.Hint(n, all.Select(e => e.Info.Name).Distinct(StringComparer.OrdinalIgnoreCase), "prefab");
            throw new ScriptApiException($"{api}: no prefab named '{name}'.", hint);
        }

        if (matches.Count > 1)
        {
            throw new ScriptApiException($"{api}: several prefabs are named '{name}': {string.Join(", ", matches.Select(m => m.Info.File))}.",
                "Pass the file instead, e.g. name = \"" + matches[0].Info.File + "\".");
        }

        string full = Path.GetFullPath(matches[0].Path);
        if (!IsInside(full))
        {
            throw new ScriptApiException($"{api}: '{name}' resolves outside the prefab library.");
        }

        return full;
    }

    private bool IsInside(string fullPath)
    {
        string root = LibraryRoot;
        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        StringComparison cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return fullPath.StartsWith(prefix, cmp);
    }

    /// <summary>Every readable prefab in the library, sorted by name then file (deterministic).</summary>
    private List<Entry> Scan(bool warn)
    {
        string root = LibraryRoot;
        var entries = new List<Entry>();
        if (!System.IO.Directory.Exists(root))
        {
            return entries;
        }

        var unreadable = new List<string>();
        IEnumerable<string> files;
        try
        {
            files = System.IO.Directory.EnumerateFiles(root, "*" + PrefabPackage.Extension, SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (warn)
            {
                _ctx.Log.Warn($"prefab.list: the prefab library could not be read ({ex.Message}).");
            }

            return entries;
        }

        foreach (string file in files)
        {
            string full = Path.GetFullPath(file);
            if (!IsInside(full))
            {
                continue;
            }

            string rel = Path.GetRelativePath(root, full).Replace('\\', '/');
            try
            {
                (PrefabManifest m, _) = PrefabPackage.LoadHeader(full);
                string name = string.IsNullOrWhiteSpace(m.Name) ? Path.GetFileNameWithoutExtension(full) : m.Name;
                entries.Add(new Entry(full, new ScriptPrefabInfo(name, rel, m.Description ?? string.Empty, m.Author ?? string.Empty, m.BrushCount, m.ObjectCount)));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
            {
                unreadable.Add(rel);
            }
        }

        if (warn && unreadable.Count > 0)
        {
            _ctx.Log.Warn($"prefab.list: skipped {unreadable.Count} unreadable prefab file(s): {string.Join(", ", unreadable.OrderBy(u => u, StringComparer.OrdinalIgnoreCase))}.");
        }

        return entries
            .OrderBy(e => e.Info.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Info.File, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed record Entry(string Path, ScriptPrefabInfo Info);
}

/// <summary>One prefab in the library (Lua type <c>prefab_info</c>).</summary>
[ScriptDoc("One prefab in the library: name, file (relative to the library, / separators), description, author and member counts.", Since = 2)]
public sealed class ScriptPrefabInfo
{
    internal ScriptPrefabInfo(string name, string file, string description, string author, int brushCount, int objectCount)
    {
        Name = name;
        File = file;
        Description = description;
        Author = author;
        BrushCount = brushCount;
        ObjectCount = objectCount;
    }

    [ScriptDoc("The prefab's name (its manifest name, else the file name).", Since = 2)]
    public string Name { get; }

    [ScriptDoc("The file, relative to the prefab library, with / separators.", Since = 2)]
    public string File { get; }

    [ScriptDoc("The prefab's description (\"\" when none).", Since = 2)]
    public string Description { get; }

    [ScriptDoc("The prefab's author (\"\" when none).", Since = 2)]
    public string Author { get; }

    [ScriptDoc("The number of brushes recorded in the prefab.", Since = 2)]
    public int BrushCount { get; }

    [ScriptDoc("The number of objects recorded in the prefab.", Since = 2)]
    public int ObjectCount { get; }

    public override string ToString() => $"prefab '{Name}' ({File})";
}

/// <summary>A placed prefab instance (Lua type <c>prefab_instance</c>).</summary>
[ScriptDoc("A placed prefab instance: id, prefab name, member UIDs, brushes, objects and pivot position.", Since = 2)]
public sealed class ScriptPrefabInstance
{
    private readonly ScriptContext _ctx;

    internal ScriptPrefabInstance(ScriptContext ctx, PrefabInstanceRecord record)
    {
        _ctx = ctx;
        Id = record.InstanceId;
        Name = record.PrefabName;
        Uids = record.MemberUids.ToArray();
        Pos = record.PivotPosition;
    }

    [ScriptDoc("The instance id (unique within the level).", Since = 2)]
    public int Id { get; }

    [ScriptDoc("The prefab's name.", Since = 2)]
    public string Name { get; }

    [ScriptDoc("Every member UID, brushes and objects (number[]).", Since = 2)]
    public int[] Uids { get; }

    [ScriptDoc("The member brushes (brush[]).", Since = 2)]
    public ScriptBrushHandle[] Brushes =>
        Uids.Where(u => _ctx.Brushes.FindBrush(u) is not null).Select(u => new ScriptBrushHandle(_ctx, u)).ToArray();

    [ScriptDoc("The member objects (object[]).", Since = 2)]
    public ScriptObjectHandle[] Objects =>
        Uids.Where(u => _ctx.Brushes.FindBrush(u) is null)
            .Select(u => _ctx.Document.FindByUid(u)).OfType<LevelObject>()
            .Select(o => new ScriptObjectHandle(_ctx, o)).ToArray();

    [ScriptDoc("The world position of the instance pivot (vec, metres).", Since = 2)]
    public Vec3 Pos { get; }

    public override string ToString() => $"prefab instance #{Id} '{Name}' ({Uids.Length} members)";
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Ged.Automation.Analysis;

/// <summary>The outcome of comparing two corpus snapshots.</summary>
public sealed class SnapshotDiff
{
    public List<string> Changed { get; } = new();

    public List<string> Missing { get; } = new();

    public List<string> Added { get; } = new();

    public List<string> AddedDirectories { get; } = new();

    public List<string> MissingDirectories { get; } = new();

    public int Files { get; set; }

    /// <summary>No file or directory changed, appeared or disappeared.</summary>
    public bool Clean => Changed.Count == 0 && Missing.Count == 0 && Added.Count == 0 && AddedDirectories.Count == 0 && MissingDirectories.Count == 0;

    public JsonObject ToJson() => new()
    {
        ["clean"] = Clean,
        ["files"] = Files,
        ["changed"] = new JsonArray(Changed.Select(s => (JsonNode)s).ToArray()),
        ["missing"] = new JsonArray(Missing.Select(s => (JsonNode)s).ToArray()),
        ["added"] = new JsonArray(Added.Select(s => (JsonNode)s).ToArray()),
        ["addedDirectories"] = new JsonArray(AddedDirectories.Select(s => (JsonNode)s).ToArray()),
        ["missingDirectories"] = new JsonArray(MissingDirectories.Select(s => (JsonNode)s).ToArray()),
    };
}

/// <summary>
/// A read-only fingerprint of the corpus files themselves (CORPUS-NORMS-SPEC §8.1): for every file under every root
/// — and every top-level packfile of a separate RF install — the relative path, length, last-write time (UTC) and
/// SHA-256, plus the directory listing. Taken before a sweep and compared after it, it proves the sweep changed,
/// added and removed nothing. Last-access times are not recorded (NTFS may update them on read).
/// </summary>
public static class CorpusSnapshot
{
    public const string Schema = "glacier.corpus-snapshot/1";

    /// <summary>Takes a snapshot (hashing with up to <paramref name="jobs"/> parallel readers).</summary>
    public static async Task<JsonObject> TakeAsync(IReadOnlyList<string> roots, string? rfInstall, int jobs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var files = new List<CorpusFile>();
        var dirs = new List<string>();
        var skipped = new List<string>();
        for (int i = 0; i < roots.Count; i++)
        {
            var d = new List<string>();
            var s = new List<string>();
            files.AddRange(CorpusReader.EnumerateFiles(roots[i], ct, d, s));
            dirs.AddRange(d.Select(x => Key(i, x)));
            skipped.AddRange(s.Select(x => Key(i, x)));
        }

        int installIndex = -1;
        if (rfInstall is not null && !roots.Any(r => string.Equals(r, rfInstall, StringComparison.OrdinalIgnoreCase) || CorpusOutput.IsInside(rfInstall, r)))
        {
            installIndex = roots.Count;
            files.AddRange(CorpusReader.TopLevelFiles(rfInstall, "*.vpp"));
        }

        var rootIndex = new Dictionary<string, int>(CorpusOutput.PathComparer);
        for (int i = 0; i < roots.Count; i++)
        {
            rootIndex.TryAdd(roots[i], i);
        }

        if (installIndex >= 0)
        {
            rootIndex.TryAdd(rfInstall!, installIndex);
        }

        var hashes = new ConcurrentDictionary<string, (string Sha, string Error)>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, jobs), CancellationToken = ct }, async (f, token) =>
        {
            await Task.Yield();
            try
            {
                using var fs = CorpusReader.OpenRead(f.FullPath);
                byte[] sha = await SHA256.HashDataAsync(fs, token).ConfigureAwait(false);
                hashes[f.FullPath] = (Convert.ToHexString(sha).ToLowerInvariant(), string.Empty);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                hashes[f.FullPath] = (string.Empty, ex.Message);
            }
        }).ConfigureAwait(false);

        var fileRows = files
            .Select(f => (Key: Key(rootIndex[f.Root], f.RelativePath), File: f))
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => (JsonNode)new JsonObject
            {
                ["path"] = x.Key,
                ["length"] = x.File.Length,
                ["lastWriteUtc"] = x.File.LastWriteUtc.ToString("o", CultureInfo.InvariantCulture),
                ["sha256"] = hashes[x.File.FullPath].Sha,
                ["error"] = hashes[x.File.FullPath].Error is { Length: > 0 } e ? e : null,
            })
            .ToArray();

        var rootsJson = new JsonArray(roots.Select(r => (JsonNode)r).ToArray());
        return new JsonObject
        {
            ["schema"] = Schema,
            ["roots"] = rootsJson,
            ["rfInstall"] = installIndex >= 0 ? rfInstall : null,
            ["fileCount"] = fileRows.Length,
            ["files"] = new JsonArray(fileRows),
            ["directories"] = new JsonArray(dirs.OrderBy(d => d, StringComparer.Ordinal).Select(d => (JsonNode)d).ToArray()),
            ["skippedReparsePoints"] = new JsonArray(skipped.OrderBy(d => d, StringComparer.Ordinal).Select(d => (JsonNode)d).ToArray()),
        };
    }

    /// <summary>The roots and install a stored snapshot covered (to re-take it over the same folders).</summary>
    public static (List<string> Roots, string? RfInstall) Coverage(JsonObject snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot["schema"]?.GetValue<string>() != Schema)
        {
            throw new AutomationException(AutomationErrorCodes.Usage, "Not a corpus snapshot (schema mismatch).");
        }

        var roots = (snapshot["roots"] as JsonArray ?? new JsonArray()).Select(n => n!.GetValue<string>()).ToList();
        return (roots, snapshot["rfInstall"]?.GetValue<string>());
    }

    /// <summary>Compares a stored snapshot with a fresh one.</summary>
    public static SnapshotDiff Compare(JsonObject before, JsonObject after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        Dictionary<string, JsonObject> Files(JsonObject s) => (s["files"] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>().ToDictionary(f => f["path"]!.GetValue<string>(), StringComparer.Ordinal);
        HashSet<string> Dirs(JsonObject s) => (s["directories"] as JsonArray ?? new JsonArray())
            .Select(n => n!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);

        Dictionary<string, JsonObject> a = Files(before), b = Files(after);
        var diff = new SnapshotDiff { Files = a.Count };
        foreach ((string path, JsonObject fa) in a.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!b.TryGetValue(path, out JsonObject? fb))
            {
                diff.Missing.Add(path);
                continue;
            }

            if (fa["length"]!.GetValue<long>() != fb["length"]!.GetValue<long>()
                || fa["lastWriteUtc"]!.GetValue<string>() != fb["lastWriteUtc"]!.GetValue<string>()
                || fa["sha256"]!.GetValue<string>() != fb["sha256"]!.GetValue<string>())
            {
                diff.Changed.Add(path);
            }
        }

        diff.Added.AddRange(b.Keys.Where(k => !a.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal));
        HashSet<string> da = Dirs(before), db = Dirs(after);
        diff.AddedDirectories.AddRange(db.Where(d => !da.Contains(d)).OrderBy(d => d, StringComparer.Ordinal));
        diff.MissingDirectories.AddRange(da.Where(d => !db.Contains(d)).OrderBy(d => d, StringComparer.Ordinal));
        return diff;
    }

    private static string Key(int rootIndex, string relative) => $"{rootIndex}:{relative}";
}

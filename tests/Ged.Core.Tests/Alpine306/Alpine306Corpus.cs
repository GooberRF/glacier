using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace Ged.Core.Tests.Alpine306;

/// <summary>
/// The RFL v306 (Alpine Faction 1.5) levels the round-trip tests read, all read-only:
/// <list type="bullet">
///   <item><see cref="Fixtures"/> — the small copied subset in <c>research/fixtures/alpine306</c> (gitignored,
///   retail-community-derived; the fast tier skips without it);</item>
///   <item><see cref="CorpusFiles"/> — every v306 file under the configured corpus roots (<c>GED_CORPUS_DIRS</c>,
///   else <c>research/corpus-dirs.txt</c>), found by header version and de-duplicated by content (the DeepGate
///   tier; skips when no root exists).</item>
/// </list>
/// </summary>
public static class Alpine306Corpus
{
    private static readonly Lazy<IReadOnlyList<string>> CorpusList = new(FindCorpusFiles);

    /// <summary>The copied fixture subset directory, or null.</summary>
    public static string? FixtureDirectory =>
        TestPaths.ResearchFixtures is { } root && Directory.Exists(Path.Combine(root, "alpine306"))
            ? Path.Combine(root, "alpine306")
            : null;

    /// <summary>The fixture files (full paths), sorted; empty without the fixture directory.</summary>
    public static IReadOnlyList<string> Fixtures =>
        FixtureDirectory is { } dir
            ? Directory.EnumerateFiles(dir, "*.rfl").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()
            : Array.Empty<string>();

    /// <summary>Every unique-by-content v306 level under the corpus roots (full paths), sorted.</summary>
    public static IReadOnlyList<string> CorpusFiles => CorpusList.Value;

    /// <summary>xUnit member data over the fixture files (a single null row when absent, so the theory skips).</summary>
    public static IEnumerable<object?[]> FixtureNames()
    {
        IReadOnlyList<string> files = Fixtures;
        if (files.Count == 0)
        {
            yield return new object?[] { null };
            yield break;
        }

        foreach (string f in files)
        {
            yield return new object?[] { Path.GetFileName(f) };
        }
    }

    /// <summary>The configured corpus roots that exist (read-only).</summary>
    public static IReadOnlyList<string> Roots()
    {
        var roots = new List<string>();
        string? env = Environment.GetEnvironmentVariable("GED_CORPUS_DIRS");
        IEnumerable<string> lines = !string.IsNullOrWhiteSpace(env)
            ? env.Split(';')
            : TestPaths.Research is { } research && File.Exists(Path.Combine(research, "corpus-dirs.txt"))
                ? File.ReadAllLines(Path.Combine(research, "corpus-dirs.txt"))
                : Array.Empty<string>();
        foreach (string raw in lines)
        {
            string line = raw.Trim().TrimStart('﻿');
            if (line.Length > 0 && !line.StartsWith('#') && Directory.Exists(line))
            {
                roots.Add(line);
            }
        }

        return roots;
    }

    /// <summary>The RFL header version of a file (0 when it is not an RFL).</summary>
    public static int HeaderVersion(string path)
    {
        try
        {
            using FileStream fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[8];
            if (fs.Read(head) < 8 || BinaryPrimitives.ReadUInt32LittleEndian(head) != 0xD4BADA55u)
            {
                return 0;
            }

            return BinaryPrimitives.ReadInt32LittleEndian(head[4..]);
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static IReadOnlyList<string> FindCorpusFiles()
    {
        var byHash = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string root in Roots())
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.rfl", SearchOption.AllDirectories).ToList();
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string f in files)
            {
                if (HeaderVersion(f) != 306)
                {
                    continue;
                }

                string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)));
                if (!byHash.TryGetValue(hash, out string? existing)
                    || string.CompareOrdinal(f, existing) < 0)
                {
                    byHash[hash] = f;
                }
            }
        }

        return byHash.Values.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

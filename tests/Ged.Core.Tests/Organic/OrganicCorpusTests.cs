using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editing.Organic;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Ged.Core.Tests.Scripting;
using Xunit;
using static Ged.Core.Tests.Scripting.KitTestHarness;

namespace Ged.Core.Tests.Organic;

/// <summary>
/// The organic regression corpus (ORGANIC-GEOMETRY-SPEC §4.3): every fixture in tests/fixtures/organic, for seeds
/// 1–3 here and 4–25 under DeepGate, must pass all six gates — (1) sealed build with 0 holes under Shared BSP AND
/// Incremental, lint clean, exact undo; (2) every generated brush closed/convex/planar, unrotated, on the
/// 1/1024 m grid; (3) no duplicate horizontal coverage on either build; (4) the walkway along every tube: one
/// floor at the expected height and 2 m of open space above it, ±0.9 m to each side, rising at most 45°;
/// (5) determinism (byte-identical reruns; a stored SHA-256 on Windows, with a text dump of the level written
/// on mismatch); (6) budget: ≤ 60 Lua→C# facade calls of the fixture's own, beyond loading the kit (the ≤ 2 s
/// script time is a Perf gate).
/// </summary>
public sealed class OrganicCorpusTests
{
    public static readonly string[] Fixtures = { "canyon-snake", "cave-loop", "cave-climb", "cave-rooms", "rock-walls", "deform-pieces" };

    /// <summary>
    /// Compiler findings: fixture runs whose sealed build still leaks although every brush passes the
    /// generators' rules and the audit (see docs/internal/FEATURES.md, approximations). A ratchet with one
    /// hole ceiling per build method: a listed method must leak at least once and at most its ceiling (when a
    /// fix closes or reduces it, this test fails so the entry is lowered or removed), a method listed as 0 and
    /// every run NOT listed must be exactly watertight. Every other gate runs on listed runs too.
    /// </summary>
    private static readonly Dictionary<string, (int Shared, int Incremental)> KnownCompilerLeaks = new(StringComparer.Ordinal)
    {
    };

    /// <summary>Facade calls an empty script makes through the harness: loading the kit and its modules.</summary>
    private static readonly Lazy<int> KitLoadCalls = new(() =>
    {
        // Straight through the runner: the kit harness requires one undo step, which an empty script does not make.
        ScriptRunResult r = ScriptTestFixture.Runner().Run(
            new ScriptServices { Document = LevelTemplates.CreateNew(KitTestHarness.FixedDate), Confirmation = new AllowAllConfirmation() },
            "local kit = ged.import('kit')\nreturn 0",
            new ScriptRunOptions { ChunkName = "kit-load", Limits = ScriptTestFixture.LoadTolerantLimits },
            new ScriptLog());
        Assert.True(r.Success, r.Error?.ToDisplayString());
        return r.FacadeCalls;
    });

    public static IEnumerable<object[]> FastCases() =>
        Fixtures.SelectMany(f => new[] { 1, 2, 3 }.Select(s => new object[] { f, s }));

    public static IEnumerable<object[]> DeepCases() =>
        Fixtures.SelectMany(f => Enumerable.Range(4, 22).Select(s => new object[] { f, s }));

    [Theory]
    [MemberData(nameof(FastCases))]
    public void Fixture_Passes_Every_Gate(string fixture, int seed) => Gates(fixture, seed, golden: true);

    [Theory]
    [Trait("Category", "DeepGate")]
    [MemberData(nameof(DeepCases))]
    public void Fixture_Passes_Every_Gate_Deep(string fixture, int seed) => Gates(fixture, seed, golden: false);

    /// <summary>Gate 6's wall-clock half: each fixture's script runs within 2 s (Perf: run serially on an idle machine).</summary>
    [Theory]
    [Trait("Category", "Perf")]
    [MemberData(nameof(FastCases))]
    public void Fixture_Script_Runs_Within_Two_Seconds(string fixture, int seed)
    {
        KitRun run = Run(Script(fixture, seed));
        Assert.True(run.Result.ScriptTime <= TimeSpan.FromSeconds(2), $"{fixture} seed {seed}: script time {run.Result.ScriptTime.TotalMilliseconds:0} ms (budget 2 s)");
    }

    private static string Dir() =>
        Path.Combine(ScriptTestFixture.RepoRoot() ?? throw new InvalidOperationException("repo root not found"), "tests", "fixtures", "organic");

    private static string Script(string fixture, int seed) =>
        $"SEED = {seed}\n" + File.ReadAllText(Path.Combine(Dir(), fixture + ".lua"));

    private static void Gates(string fixture, int seed, bool golden)
    {
        KitRun run = Run(Script(fixture, seed));
        string label = $"{fixture} seed {seed}";

        // 6. Budget (calls; the wall-clock half is a Perf gate). The fixture's own calls are counted: the kit's fixed
        // cost of loading itself and its modules (measured on an empty script) grows with every module the kit ships
        // and is not what the budget guards against (a regression to per-vertex callbacks).
        int own = run.Result.FacadeCalls - KitLoadCalls.Value;
        Assert.True(own <= 60, $"{label}: {own} facade calls of its own (budget 60; {run.Result.FacadeCalls} in all, {KitLoadCalls.Value} loading the kit)");

        // 2. Shape rules on every generated brush (rooms are kit boxes, which are rotated-free too).
        List<Brush> brushes = new BrushEditor(run.Doc).Brushes.ToList();
        foreach (Brush b in brushes)
        {
            ScriptGeoEditTests.AssertHull(b);
        }

        // 5. Determinism (the stored hash is checked last, so a changed level still reports every other gate first).
        byte[] bytes = run.Doc.SaveToBytes(updateTimestamp: false);
        KitRun again = Run(Script(fixture, seed));
        Assert.True(bytes.AsSpan().SequenceEqual(again.Doc.SaveToBytes(updateTimestamp: false)), $"{label}: two runs differ");

        // 1. Acceptance (both build methods, lint, undo) — undo runs last.
        (int sharedCeiling, int incrementalCeiling) = KnownCompilerLeaks.GetValueOrDefault($"{fixture}#{seed}");
        CompiledLevel shared = AssertHoles(run.Doc, label, sharedBsp: true, sharedCeiling);
        CompiledLevel incremental = AssertHoles(run.Doc, label, sharedBsp: false, incrementalCeiling);
        AssertLintClean(run);

        // 3. Coverage on both builds.
        foreach ((string method, CompiledLevel c) in new[] { ("Shared BSP", shared), ("Incremental", incremental) })
        {
            List<(float X, float Z, float Y)> dup = FloorCoverage.Duplicates(c.Geometry);
            Assert.True(dup.Count == 0, $"{label} ({method}): duplicate horizontal coverage at {string.Join("; ", dup.Take(4).Select(d => $"({d.X}, {d.Y}, {d.Z})"))}");
        }

        // 4. Walkway along every tube the fixture returned.
        Walkway(label, run, shared);
        AssertUndoRestores(run);

        if (golden && OperatingSystem.IsWindows())
        {
            AssertGolden($"{fixture}#{seed}", Convert.ToHexString(SHA256.HashData(bytes)), again.Doc);
        }
    }

    /// <summary>
    /// A sealed build under one method: exactly watertight when <paramref name="ceiling"/> is 0, else leaking at least once and
    /// at most <paramref name="ceiling"/> times (a known compiler finding).
    /// </summary>
    private static CompiledLevel AssertHoles(Ged.Core.Editor.EditorDocument doc, string label, bool sharedBsp, int ceiling)
    {
        if (ceiling == 0)
        {
            return AssertWatertight(doc, sharedBsp);
        }

        CompiledLevel c = Sealed(doc, sharedBsp);
        int holes = HoleDetector.Detect(c.Geometry).Count;
        string method = sharedBsp ? "Shared BSP" : "Incremental";
        Assert.True(holes >= 1, $"{label} ({method}) is listed as a known compiler leak (at most {ceiling}) but is now watertight: set its ceiling to 0 in KnownCompilerLeaks.");
        Assert.True(holes <= ceiling, $"{label} ({method}) has {holes} holes; its known-leak ceiling is {ceiling}.");
        return c;
    }

    private static void Walkway(string label, KitRun run, CompiledLevel compiled)
    {
        string lines = run.Result.ReturnValue ?? string.Empty;
        if (lines.Length == 0)
        {
            return;
        }

        List<Brush> brushes = new BrushEditor(run.Doc).Brushes.ToList();
        var volumes = brushes.Select((b, i) => BrushVolume.From(i, (b.Flags & (uint)BrushFlags.Air) != 0, BrushWorld.ToOutwardWorldFaces(b))).ToList();
        bool Open(double x, double y, double z)
        {
            bool open = false;
            var p = new Vec3((float)x, (float)y, (float)z);
            foreach (BrushVolume v in volumes)
            {
                if (v.Contains(p))
                {
                    open = v.IsAir;
                }
            }

            return open;
        }

        foreach (string tube in lines.Split('|'))
        {
            Vec3d[] st = tube.Split(';').Select(t => t.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray())
                .Select(a => new Vec3d(a[0], a[1], a[2])).ToArray();
            double? previous = null;
            for (int i = 0; i + 1 < st.Length; i++)
            {
                Vec3d a = st[i], b = st[i + 1];
                double dx = b.X - a.X, dz = b.Z - a.Z, len = Math.Sqrt((dx * dx) + (dz * dz));
                if (len < 1e-9)
                {
                    continue;
                }

                var right = new Vec3d(dz / len, 0, -dx / len);
                for (double s = 0; s < len; s += 0.25)
                {
                    double f = s / len;
                    double y = a.Y + ((b.Y - a.Y) * f);
                    Assert.True(previous is null || Math.Abs(y - previous.Value) <= 0.25 + 1e-6, $"{label}: the walkway rises {y - previous} m in 0.25 m");
                    previous = y;
                    foreach (double side in new[] { -0.9, 0, 0.9 })
                    {
                        double x = a.X + (dx * f) + (right.X * side), z = a.Z + (dz * f) + (right.Z * side);
                        // 2 mm: a ring's lattice direction is within 0.06° of square to the leg, so on a 45° leg a
                        // point 0.9 m to the side may sit up to 1 mm off the station line's height.
                        int floors = FloorCoverage.FloorsAt(compiled.Geometry, x, z).Count(h => Math.Abs(h - y) <= 0.002);
                        if (floors == 0)
                        {
                            // On an edge or corner shared by several floor faces: step 4 mm diagonally off it (the
                            // expected height follows the segment's slope; sideways the floor is level).
                            foreach ((double along, double across) in new[] { (1.0, 1.0), (1.0, -1.0), (-1.0, 1.0), (-1.0, -1.0) })
                            {
                                double nudge = 0.004 * along / len;
                                double nx = x + (dx * nudge) + (right.X * 0.004 * across), nz = z + (dz * nudge) + (right.Z * 0.004 * across);
                                floors = FloorCoverage.FloorsAt(compiled.Geometry, nx, nz).Count(h => Math.Abs(h - (y + ((b.Y - a.Y) * nudge))) <= 0.002);
                                if (floors != 0)
                                {
                                    break;
                                }
                            }
                        }

                        Assert.True(floors == 1, $"{label}: {floors} floor faces at ({x:0.###}, {y:0.###}, {z:0.###}); floors there: {string.Join(", ", FloorCoverage.FloorsAt(compiled.Geometry, x, z, strict: false).Select(h => h.ToString("0.####", CultureInfo.InvariantCulture)))}");
                        Assert.True(Open(x, y + 2.0, z), $"{label}: no 2 m headroom over ({x:0.###}, {y:0.###}, {z:0.###})");
                    }
                }
            }
        }
    }

    private static void AssertGolden(string key, string hash, Ged.Core.Editor.EditorDocument doc)
    {
        string path = Path.Combine(Dir(), "goldens.txt");
        var map = File.Exists(path)
            ? File.ReadAllLines(path).Where(l => l.Contains(' ', StringComparison.Ordinal)).ToDictionary(l => l[..l.IndexOf(' ', StringComparison.Ordinal)], l => l[(l.IndexOf(' ', StringComparison.Ordinal) + 1)..], StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        if (Environment.GetEnvironmentVariable("GED_REGEN_ORGANIC_GOLDENS") == "1")
        {
            lock (GoldenLock)
            {
                map = File.Exists(path)
                    ? File.ReadAllLines(path).Where(l => l.Contains(' ', StringComparison.Ordinal)).ToDictionary(l => l[..l.IndexOf(' ', StringComparison.Ordinal)], l => l[(l.IndexOf(' ', StringComparison.Ordinal) + 1)..], StringComparer.Ordinal)
                    : new Dictionary<string, string>(StringComparer.Ordinal);
                map[key] = hash;
                File.WriteAllLines(path, map.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} {kv.Value}"));
            }

            return;
        }

        Assert.True(map.TryGetValue(key, out string? expected), $"no golden for {key}; regenerate with GED_REGEN_ORGANIC_GOLDENS=1");
        if (!string.Equals(expected, hash, StringComparison.Ordinal))
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "organic-golden-mismatch");
            Directory.CreateDirectory(dir);
            string dump = Path.Combine(dir, key.Replace('#', '-') + ".actual.txt");
            File.WriteAllText(dump, Dump(doc));
            Assert.Fail($"{key}: the level's SHA-256 is {hash}, the golden is {expected}. A text dump of the actual level (every brush's flags, tags and world faces) is at {dump}; " +
                        "dump a run of the previous build the same way to diff them, and regenerate with GED_REGEN_ORGANIC_GOLDENS=1 when the change is intended.");
        }
    }

    /// <summary>A diff-friendly text form of the level's brushes: one header line per brush, then one line per face of world corners.</summary>
    internal static string Dump(Ged.Core.Editor.EditorDocument doc)
    {
        var sb = new System.Text.StringBuilder();
        IReadOnlyDictionary<int, Dictionary<string, string>> tags = Tags(doc);
        foreach (Brush b in new BrushEditor(doc).Brushes)
        {
            string t = tags.TryGetValue(b.Uid, out Dictionary<string, string>? kv)
                ? string.Join(",", kv.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"))
                : string.Empty;
            sb.Append(CultureInfo.InvariantCulture, $"brush {b.Uid} flags 0x{b.Flags:X} faces {b.Geometry.Faces.Count} tags [{t}]\n");
            foreach (Face f in b.Geometry.Faces)
            {
                sb.Append("  ").AppendJoin(" ", f.Vertices.Select(v => HullDeform.World(b, b.Geometry.Vertices[v.Index]))
                    .Select(p => string.Create(CultureInfo.InvariantCulture, $"({p.X:0.######},{p.Y:0.######},{p.Z:0.######})"))).Append('\n');
            }
        }

        return sb.ToString();
    }

    private static readonly object GoldenLock = new();
}

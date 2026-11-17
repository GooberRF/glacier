using System.IO;
using Ged.Core.IO;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Alpine306;

/// <summary>
/// RFL v306 (Alpine Faction 1.5) read + round-trip over real levels (ALPINE-COMPAT-SPEC §8, chunk C3): every file
/// loads, every parsed section re-serializes byte-exactly, and the deliverable save (<see cref="RflFile.UpgradeToAlpine"/>
/// + <see cref="RflFile.Save(bool)"/>) is byte-identical to the file — except the one documented rewrite: a props chunk in
/// the transient pre-release version 7 layout is rewritten as version 6 on any save (owner decision Q3), so for those
/// files only that chunk may differ, and it must re-read with the same values. A second save is always a no-op.
/// </summary>
public sealed class Alpine306RoundTripTests
{
    private readonly ITestOutputHelper _output;

    public Alpine306RoundTripTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [MemberData(nameof(Alpine306Corpus.FixtureNames), MemberType = typeof(Alpine306Corpus))]
    public void Fixture_Round_Trips_Byte_Identically(string? fileName)
    {
        if (fileName is null)
        {
            return; // research/fixtures/alpine306 absent
        }

        string path = Path.Combine(Alpine306Corpus.FixtureDirectory!, fileName);
        List<string> failures = Check(File.ReadAllBytes(path), fileName);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    [Trait("Category", "DeepGate")] // reads every v306 level under the corpus roots (~1.5 GB)
    public void Every_Corpus_V306_Level_Round_Trips()
    {
        IReadOnlyList<string> files = Alpine306Corpus.CorpusFiles;
        if (files.Count == 0)
        {
            return; // no corpus root configured
        }

        var failures = new List<string>();
        foreach (string f in files)
        {
            failures.AddRange(Check(File.ReadAllBytes(f), f));
        }

        _output.WriteLine($"{files.Count} unique v306 levels, {failures.Count} failures.");
        Assert.True(failures.Count == 0, $"{failures.Count} of {files.Count} levels failed:{Environment.NewLine}" + string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// The corpus holds pre-release layouts of some 1.5 chunks (rope emitters without the decoration block or with a
    /// "ZIPL" block, an older waypoint helper record, a liquid emission chunk with a leading version, terrain records
    /// with an older decoration layout or a blob longer than their header implies) that Alpine 1.5 itself cannot read
    /// cleanly. Those chunks are kept raw and read-only (§10 rule 3); this ratchet keeps their number from growing, i.e. a
    /// current-layout chunk must always parse typed. The terrain reader validates whole records, blob included, as
    /// Alpine's does, which finds 4 such terrain chunks (a structure-only pass found 2).
    /// </summary>
    private const int MaxPreReleaseRawChunks = 21;

    [Fact]
    [Trait("Category", "DeepGate")]
    public void Every_Corpus_V306_Chunk_Is_Typed_Not_Raw()
    {
        IReadOnlyList<string> files = Alpine306Corpus.CorpusFiles;
        if (files.Count == 0)
        {
            return;
        }

        var problems = new List<string>();
        var preRelease = new List<string>();
        var stats = new SortedDictionary<string, int>(StringComparer.Ordinal);
        void Count(string key) => stats[key] = stats.GetValueOrDefault(key) + 1;
        foreach (string f in files)
        {
            RflFile file = RflFile.Load(File.ReadAllBytes(f));
            file.ParseAllKnownSections();
            foreach (RflSection s in file.Sections)
            {
                switch (s.Content)
                {
                    case AlpineLevelPropertiesSection p:
                        Count($"props v{p.Version}{(p.IsShortLayout ? " short" : string.Empty)}{(p.UnparsedTail.Length > 0 ? " +tail" : string.Empty)}");
                        break;
                    case AlpineMeshObjectsSection m:
                        Count($"mesh blocks {m.Blocks}{(m.UnparsedTail.Length > 0 ? " +tail" : string.Empty)}");
                        break;
                    case AlpineLightmapsSection lm:
                        Count($"lightmaps v{lm.SectionVersion}{(lm.IsUsable ? string.Empty : " unusable")}");
                        if (lm.SectionVersion == AlpineLightmapsSection.CurrentSectionVersion && !lm.IsUsable)
                        {
                            problems.Add($"{f}: lightmaps: {lm.StructureProblem}");
                        }

                        break;
                    case AlpineTerrainSection t:
                        Count("terrain");
                        if (t.StructureProblem is not null)
                        {
                            preRelease.Add($"{f}: terrain: {t.StructureProblem}");
                        }

                        break;
                    case AlpineMoverPathsSection mp:
                        Count("mover path");
                        if (mp.IsReadOnly)
                        {
                            preRelease.Add($"{f}: mover path: {mp.ReadOnlyReason}");
                        }

                        break;
                    case IRflSectionContent c when ReadOnlyReason(c) is { } reason:
                        preRelease.Add($"{f}: {SectionIds.Describe(s.TypeId)}: {reason}");
                        break;
                    case IRflSectionContent c when SectionIds.IsAlpineSpace(s.TypeId):
                        Count(SectionIds.Describe(s.TypeId));
                        break;
                }

                if (s.Content is null && !s.IsEnd)
                {
                    Count($"raw 0x{s.TypeId:X8}");
                }
            }
        }

        foreach ((string key, int n) in stats)
        {
            _output.WriteLine($"{key}: {n}");
        }

        _output.WriteLine($"pre-release chunks kept raw ({preRelease.Count}):");
        preRelease.ForEach(_output.WriteLine);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.True(preRelease.Count <= MaxPreReleaseRawChunks,
            $"{preRelease.Count} chunks kept raw (ratchet {MaxPreReleaseRawChunks}):{Environment.NewLine}" + string.Join(Environment.NewLine, preRelease));
    }

    private static string? ReadOnlyReason(IRflSectionContent c) => c switch
    {
        AlpineWeatherRegionsSection s => s.ReadOnlyReason,
        AlpineVehicleFactoriesSection s => s.ReadOnlyReason,
        AlpineProjectionCamerasSection s => s.ReadOnlyReason,
        AlpineRopeEmittersSection s => s.ReadOnlyReason,
        AlpineDirectionalLightsSection s => s.ReadOnlyReason,
        AlpineWaypointHelpersSection s => s.ReadOnlyReason,
        AlpineLiquidEmissionSection s => s.ReadOnlyReason,
        _ => null,
    };

    /// <summary>The round-trip checks for one v306 file; returns one line per failure.</summary>
    internal static List<string> Check(byte[] original, string name)
    {
        var failures = new List<string>();
        RflFile file;
        try
        {
            file = RflFile.Load(original);
            file.ParseAllKnownSections();
        }
        catch (Exception ex)
        {
            failures.Add($"{name}: load failed: {ex.Message}");
            return failures;
        }

        if (file.Header.Version != RflFile.AlpineSaveVersion)
        {
            failures.Add($"{name}: header version {file.Header.Version}, expected {RflFile.AlpineSaveVersion}.");
        }

        RflContext ctx = file.Context;
        foreach (RflSection section in file.Sections.Where(s => s.Content is not null))
        {
            var w = new RfWriter(section.RawBytes.Length);
            section.Content!.Write(w, ctx);
            if (!w.ToArray().AsSpan().SequenceEqual(section.RawBytes))
            {
                failures.Add($"{name}: section 0x{section.TypeId:X8} does not re-serialize byte-exactly.");
            }
        }

        bool v7Props = file.Sections.Any(s => s.Content is AlpineLevelPropertiesSection { Version: > AlpineLevelPropertiesSection.CurrentVersion });
        RflFile saved = RflFile.Load(original);
        saved.UpgradeToAlpine();
        byte[] bytes = saved.Save(updateTimestamp: false);
        if (!v7Props)
        {
            if (!bytes.AsSpan().SequenceEqual(original))
            {
                failures.Add($"{name}: the save is not byte-identical (first difference at {FirstDifference(original, bytes)}).");
            }
        }
        else
        {
            failures.AddRange(CheckV7Rewrite(original, bytes, name));
        }

        RflFile again = RflFile.Load(bytes);
        IReadOnlyList<string> secondNotes = again.UpgradeToAlpine();
        if (secondNotes.Count != 0 || !again.Save(updateTimestamp: false).AsSpan().SequenceEqual(bytes))
        {
            failures.Add($"{name}: a second save is not a no-op.");
        }

        return failures;
    }

    /// <summary>Only the props chunk may differ, and it must now be version 6 holding the same values.</summary>
    private static IEnumerable<string> CheckV7Rewrite(byte[] original, byte[] saved, string name)
    {
        RflFile before = RflFile.Load(original);
        RflFile after = RflFile.Load(saved);
        if (before.Sections.Count != after.Sections.Count)
        {
            yield return $"{name}: the v7 props rewrite changed the section count.";
            yield break;
        }

        for (int i = 0; i < before.Sections.Count; i++)
        {
            RflSection a = before.Sections[i];
            RflSection b = after.Sections[i];
            if (a.TypeId != b.TypeId)
            {
                yield return $"{name}: the v7 props rewrite reordered section {i}.";
                continue;
            }

            if (a.TypeId != (uint)SectionType.AlpineLevelProperties)
            {
                if (!a.RawBytes.AsSpan().SequenceEqual(b.RawBytes))
                {
                    yield return $"{name}: section 0x{a.TypeId:X8} changed although only the props chunk is rewritten.";
                }

                continue;
            }

            var pa = (AlpineLevelPropertiesSection)AlpineLevelPropertiesSection.Parse(new RfReader(a.RawBytes), before.Context);
            var pb = (AlpineLevelPropertiesSection)AlpineLevelPropertiesSection.Parse(new RfReader(b.RawBytes), after.Context);
            if (pb.Version != AlpineLevelPropertiesSection.CurrentVersion || pb.IsShortLayout || pb.UnparsedTail.Length != 0)
            {
                yield return $"{name}: the rewritten props chunk is not the full version 6 layout.";
            }

            // Every field the v7 chunk stored keeps its value; the fields it lacked take Alpine's defaults.
            var w1 = new RfWriter();
            var w2 = new RfWriter();
            pa.RewriteAsCurrentLayout();
            pa.Write(w1, after.Context);
            pb.Write(w2, after.Context);
            if (!w1.ToArray().AsSpan().SequenceEqual(w2.ToArray()))
            {
                yield return $"{name}: the rewritten props chunk does not hold the original values.";
            }
        }
    }

    internal static int FirstDifference(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            if (a[i] != b[i])
            {
                return i;
            }
        }

        return a.Length == b.Length ? -1 : n;
    }
}

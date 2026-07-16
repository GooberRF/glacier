using System.Collections.Generic;
using System.IO;
using Ged.Core.IO;
using Ged.Core.IO.Rfl;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Test A (reworked for GED's Alpine-only save policy): saving a corpus level always
/// produces a valid Alpine v305 file — GED never writes below v305, exactly as Alpine
/// RED, which stamps MAXIMUM_RFL_VERSION (305) on every save. The byte-identity
/// invariant now splits by SOURCE version:
/// <list type="bullet">
///   <item>A v305 source re-saves byte-identically (the strict no-op invariant).</item>
///   <item>A pre-305 source is UPGRADED on save: the output is a valid v305 file,
///   saving that output again is byte-identical (the FIXPOINT invariant), and every
///   section of the upgraded file re-serializes exactly (full model parse-back
///   equality — proof the v305 output is understood losslessly).</item>
/// </list>
/// </summary>
public sealed class NoOpRoundTripTests
{
    [Theory]
    [MemberData(nameof(Corpus.RflFileNames), MemberType = typeof(Corpus))]
    public void Save_Upgrades_To_Valid_V305_With_Fixpoint(string? fileName)
    {
        if (fileName is null)
        {
            return; // corpus unavailable
        }

        string path = Path.Combine(Corpus.Directory!, fileName);
        byte[] original = File.ReadAllBytes(path);
        int sourceVersion = RflFile.Load(original).Header.Version;

        // The deliverable save GED performs: upgrade to Alpine v305, then serialize.
        RflFile file = RflFile.Load(original);
        file.UpgradeToAlpine();
        byte[] saved1 = file.Save(updateTimestamp: false);

        // (1) The output is a valid v305 file.
        RflFile reloaded = RflFile.Load(saved1);
        Assert.Equal(RflFile.AlpineSaveVersion, reloaded.Header.Version);

        // (2) FIXPOINT: re-saving the upgraded file is byte-identical.
        reloaded.UpgradeToAlpine(); // no-op — already v305
        byte[] saved2 = reloaded.Save(updateTimestamp: false);
        Assert.Equal(saved1.Length, saved2.Length);
        Assert.True(saved1.AsSpan().SequenceEqual(saved2),
            $"{fileName}: fixpoint failed — re-save differs at offset {FirstDifference(saved1, saved2)}.");

        // (3) STRICT byte-identity for v305 sources.
        if (sourceVersion == RflFile.AlpineSaveVersion)
        {
            Assert.Equal(original.Length, saved1.Length);
            Assert.True(original.AsSpan().SequenceEqual(saved1),
                $"{fileName}: v305 source not byte-identical at offset {FirstDifference(original, saved1)}.");
        }
        else
        {
            // A real upgrade must have happened.
            Assert.NotEqual(RflFile.AlpineSaveVersion, sourceVersion);
        }

        // (4) Full model parse-back equality: every parsed section of the upgraded
        // v305 file re-serializes byte-exactly, proving lossless v305 understanding.
        RflContext ctx = reloaded.Context;
        var failures = new List<string>();
        for (int i = 0; i < reloaded.Sections.Count; i++)
        {
            RflSection section = reloaded.Sections[i];
            if (!RflSectionRegistry.HasParser(section.TypeId))
            {
                continue;
            }

            RflSectionRegistry.TryParse(section, ctx, out IRflSectionContent? parsed);
            var w = new RfWriter(section.RawBytes.Length);
            parsed!.Write(w, ctx);
            if (!w.ToArray().AsSpan().SequenceEqual(section.RawBytes))
            {
                failures.Add($"section[{i}] 0x{section.TypeId:X8} ({section.Type})");
            }
        }

        Assert.True(failures.Count == 0,
            $"{fileName}: {failures.Count} v305 section(s) did not reserialize losslessly: {string.Join(", ", failures)}");
    }

    private static int FirstDifference(byte[] a, byte[] b)
    {
        int n = System.Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            if (a[i] != b[i])
            {
                return i;
            }
        }

        return n;
    }
}

using System.IO;
using System.Text;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Test C: after marking one section dirty and re-saving with a size change,
/// reloading must yield a self-consistent header (offsets / counts / total
/// size), and every untouched section must still byte-match the original.
///
/// The check runs on the canonical Alpine v305 form of each level: GED always saves
/// v305, so we first upgrade (a no-op for a v305 source) and take that as the
/// baseline. The header-recompute + untouched-verbatim invariant then applies to a
/// v305 dirty-save, which is the only save GED performs.
/// </summary>
public sealed class HeaderIntegrityTests
{
    private const string Suffix = "_ged_header_integrity_test";

    [Theory]
    [MemberData(nameof(Corpus.RflFileNames), MemberType = typeof(Corpus))]
    public void Dirty_Save_Recomputes_Header_And_Preserves_Other_Sections(string? fileName)
    {
        if (fileName is null)
        {
            return; // corpus unavailable
        }

        string path = Path.Combine(Corpus.Directory!, fileName);

        // Canonical v305 baseline (GED never writes below v305). For a v305 source this
        // is byte-identical to the file on disk; for a pre-305 source it is the upgraded
        // form. All the invariants below apply to this v305 form.
        RflFile upgraded = RflFile.Load(File.ReadAllBytes(path));
        upgraded.UpgradeToAlpine();
        byte[] originalBytes = upgraded.Save(updateTimestamp: false);

        RflFile original = RflFile.Load(originalBytes);
        RflFile work = RflFile.Load(originalBytes);
        work.ParseAllKnownSections();

        int idx = work.Sections.FindIndex(s => s.TypeId == (uint)SectionType.LevelProperties);
        Assert.True(idx >= 0, $"{fileName}: no level_properties section to modify.");

        var lp = (LevelPropertiesSection)work.Sections[idx].Content!;
        lp.GeomodTexture += Suffix;
        work.Sections[idx].Dirty = true;

        byte[] saved = work.Save(updateTimestamp: false);
        RflFile reloaded = RflFile.Load(saved);

        // Header self-consistency: recompute from the reloaded layout.
        var (numSections, totalSize, psOffset, liOffset) = RecomputeHeader(reloaded);
        Assert.Equal(numSections, reloaded.Header.NumSections);
        Assert.Equal(totalSize, reloaded.Header.SectionsTotalSize);
        Assert.Equal(psOffset, reloaded.Header.PlayerStartOffset);
        Assert.Equal(liOffset, reloaded.Header.LevelInfoOffset);

        // Same section set, in order.
        Assert.Equal(original.Sections.Count, reloaded.Sections.Count);

        // The modified section grew by the suffix length; all others are verbatim.
        for (int i = 0; i < original.Sections.Count; i++)
        {
            Assert.Equal(original.Sections[i].TypeId, reloaded.Sections[i].TypeId);
            if (i == idx)
            {
                Assert.Equal(
                    original.Sections[i].RawBytes.Length + Encoding.Latin1.GetByteCount(Suffix),
                    reloaded.Sections[i].RawBytes.Length);
            }
            else
            {
                Assert.True(
                    original.Sections[i].RawBytes.AsSpan().SequenceEqual(reloaded.Sections[i].RawBytes),
                    $"{fileName}: untouched section[{i}] 0x{original.Sections[i].TypeId:X8} changed after dirty save.");
            }
        }

        // Re-saving the reloaded (now-clean) file is stable.
        Assert.True(saved.AsSpan().SequenceEqual(reloaded.Save(updateTimestamp: false)),
            $"{fileName}: clean re-save of modified file was not byte-stable.");
    }

    /// <summary>
    /// Independently recomputes the header offsets/counts from a file's section
    /// layout (mirrors, but does not call, the production Save logic).
    /// </summary>
    private static (int numSections, int totalSize, int psOffset, int liOffset) RecomputeHeader(RflFile file)
    {
        RflContext ctx = file.Context;
        int headerSize = 28
            + 2 + Encoding.Latin1.GetByteCount(file.Header.LevelName)
            + (ctx.HasModName ? 2 + Encoding.Latin1.GetByteCount(file.Header.ModName ?? string.Empty) : 0);

        int numSections = 0;
        int totalSize = 0;
        int psOffset = file.Header.PlayerStartOffset;
        int liOffset = file.Header.LevelInfoOffset;
        int cursor = headerSize;

        foreach (RflSection s in file.Sections)
        {
            if (!s.IsEnd)
            {
                numSections++;
                totalSize += s.RawBytes.Length;
            }

            if (s.TypeId == (uint)SectionType.PlayerStart)
            {
                psOffset = cursor;
            }
            else if (s.TypeId == (uint)SectionType.LevelInfo)
            {
                liOffset = cursor;
            }

            cursor += 8 + s.RawBytes.Length;
        }

        return (numSections, totalSize, psOffset, liOffset);
    }
}

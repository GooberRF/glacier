using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Item 6: the RFL header timestamp is stamped to "now" on a real save that carries a
/// change (dirty document), while a no-op save of an untouched file stays byte-identical
/// (the byte-identity invariant). Also covers the read-only <see cref="RflHeader.TimestampUtc"/>
/// accessor the Level Properties panel exposes.
/// </summary>
public sealed class RflTimestampTests
{
    [Fact]
    public void TimestampUtc_Reflects_Unix_Seconds()
    {
        var header = new RflHeader { Timestamp = 1_700_000_000u };
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000L), header.TimestampUtc);
    }

    [Fact]
    public void Zero_Timestamp_Is_The_Unix_Epoch()
    {
        Assert.Equal(DateTimeOffset.UnixEpoch, new RflHeader { Timestamp = 0 }.TimestampUtc);
    }

    [Fact]
    public void Dirty_Save_Bumps_The_Header_Timestamp_To_Now()
    {
        if (FirstCorpusFile() is not { } path)
        {
            return; // corpus unavailable
        }

        EditorDocument doc = EditorDocument.Open(path);
        uint original = doc.Rfl.Header.Timestamp;
        doc.MarkDirty();
        Assert.True(doc.IsDirty);

        uint before = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string temp = Path.Combine(Path.GetTempPath(), $"ged_ts_{Guid.NewGuid():N}.rfl");
        try
        {
            // Mirrors MainWindow's "real save stamps only when modified" gate.
            doc.Save(temp, updateTimestamp: doc.IsDirty);
            uint saved = RflFile.Load(temp).Header.Timestamp;
            uint after = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            Assert.InRange(saved, before - 2u, after + 2u);
            Assert.NotEqual(original, saved); // never keeps the stale corpus timestamp
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void Clean_Save_Preserves_The_Timestamp_And_Stays_Byte_Identical()
    {
        if (FirstCorpusFile() is not { } path)
        {
            return; // corpus unavailable
        }

        byte[] originalBytes = File.ReadAllBytes(path);
        EditorDocument doc = EditorDocument.Open(path);
        Assert.False(doc.IsDirty);

        // The gate resolves to updateTimestamp:false for a clean (untouched) document.
        byte[] resaved = doc.SaveToBytes(updateTimestamp: doc.IsDirty);

        Assert.Equal(RflFile.Load(originalBytes).Header.Timestamp, RflFile.Load(resaved).Header.Timestamp);
        Assert.True(originalBytes.AsSpan().SequenceEqual(resaved), "clean save must be byte-identical");
    }

    [Fact]
    public void Dirty_Save_Restamps_The_LevelInfo_Date_In_Reds_Format()
    {
        var info = LevelInfoSection.CreateDefault(new DateTime(2001, 8, 24, 16, 48, 1));
        string oldDate = info.Date;
        Assert.Equal("Friday, August 24, 2001 16:48:01", oldDate);

        var rfl = new RflFile();
        rfl.Header.Version = 0xC8;
        rfl.Sections.Add(new RflSection((uint)SectionType.LevelInfo, Array.Empty<byte>()) { Content = info, Dirty = true });
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));

        byte[] bytes = rfl.Save(updateTimestamp: true);
        RflFile reloaded = RflFile.Load(bytes);
        reloaded.ParseAllKnownSections();
        LevelInfoSection info2 = reloaded.Sections.Select(s => s.Content).OfType<LevelInfoSection>().Single();

        Assert.NotEqual(oldDate, info2.Date);
        Assert.True(
            DateTime.TryParseExact(info2.Date, LevelInfoSection.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed),
            $"date '{info2.Date}' is not in RED's format");
        Assert.True(Math.Abs((DateTime.Now - parsed).TotalMinutes) < 5, "date should be ~now");
    }

    [Fact]
    public void Clean_Save_Leaves_The_LevelInfo_Date_Unchanged()
    {
        if (FirstCorpusFile() is not { } path)
        {
            return;
        }

        RflFile loaded = RflFile.Load(File.ReadAllBytes(path));
        loaded.ParseAllKnownSections();
        LevelInfoSection? info = loaded.Sections.Select(s => s.Content).OfType<LevelInfoSection>().FirstOrDefault();
        if (info is null)
        {
            return; // level has no level_info
        }

        string originalDate = info.Date;
        byte[] resaved = loaded.Save(updateTimestamp: false); // clean no-op save
        RflFile reloaded = RflFile.Load(resaved);
        reloaded.ParseAllKnownSections();
        LevelInfoSection info2 = reloaded.Sections.Select(s => s.Content).OfType<LevelInfoSection>().Single();

        Assert.Equal(originalDate, info2.Date);
    }

    private static string? FirstCorpusFile() =>
        Corpus.Available && Corpus.RflFiles.Count > 0 ? Corpus.RflFiles[0] : null;
}

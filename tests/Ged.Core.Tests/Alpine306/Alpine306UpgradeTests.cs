using System.IO;
using Ged.Core.Editor;
using Ged.Core.IO;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests.Alpine306;

/// <summary>
/// The RFL 306 save policy (ALPINE-COMPAT-SPEC §2, chunk C4): a pre-306 level is upgraded with exactly the delta
/// Alpine RED's own load → save produces, and a second save is a no-op; a level newer than 306 opens read-only and
/// every save refuses it (owner decision Q4).
/// </summary>
public sealed class Alpine306UpgradeTests
{
    private static readonly RflContext V305 = new(305);

    /// <summary>A v305 level: props v4 (legacy timers/movers set), an Alpine mesh without the 306 blocks, a fog
    /// near clip, and Looping set on a Play_Sound and a Music_Start event.</summary>
    private static byte[] V305Level(float fogNear = 12.5f)
    {
        RflFile rfl = LevelTemplates.CreateNewRfl(Alpine306Synthetic.FixedDate);
        rfl.Header.Version = 305;
        var lp = (LevelPropertiesSection)rfl.Sections.First(s => s.Content is LevelPropertiesSection).Content!;
        lp.FogNearPlane = fogNear;

        rfl.Sections.RemoveAll(s => s.TypeId == (uint)SectionType.AlpineLevelProperties); // the template's v6 chunk
        var props = new AlpineLevelPropertiesSection
        {
            Version = 4, LegacyCyclicTimers = 1, LegacyMovers = 1, StartsWithHeadlamp = 0, StaticMeshAmbientLightModifier = 2f,
            Rf2StyleGeomod = 1, GeoableEntries = { new AlpineGeoableEntry { BrushUid = 5, RoomUid = 6 } },
        };
        rfl.InsertSection(new RflSection((uint)SectionType.AlpineLevelProperties, Array.Empty<byte>()) { Content = props, Dirty = true });

        var mesh = new AlpineMeshObjectsSection();
        mesh.Meshes.Add(new AlpineMeshObject { Uid = 70, MeshFilename = "crate.v3m", CollisionMode = 2, Orientation = Mat3.Identity });
        rfl.InsertSection(new RflSection((uint)SectionType.AlpineMeshObjects, Array.Empty<byte>()) { Content = mesh, Dirty = true });

        var events = new EventsSection();
        events.Events.Add(new RflEvent { Uid = 80, ClassName = "Play_Sound", Bool1 = 1, Str1 = "loop.wav", Color = new RfColor(255, 255, 255, 255) });
        events.Events.Add(new RflEvent { Uid = 81, ClassName = "Music_Start", Bool1 = 1, Str1 = "music.wav", Color = new RfColor(255, 255, 255, 255) });
        events.Events.Add(new RflEvent { Uid = 82, ClassName = "Play_Sound", Bool1 = 0, Str1 = "once.wav", Color = new RfColor(255, 255, 255, 255) });
        rfl.InsertSection(new RflSection((uint)SectionType.Events, Array.Empty<byte>()) { Content = events, Dirty = true });
        return rfl.Save();
    }

    [Fact]
    public void V305_To_V306_Applies_Exactly_The_Spec_Delta_And_Logs_It_Once()
    {
        byte[] original = V305Level();
        RflFile before = RflFile.Load(original);
        RflFile file = RflFile.Load(original);
        IReadOnlyList<string> notes = file.UpgradeToAlpine();
        byte[] saved = file.Save();
        RflFile after = RflFile.Load(saved);

        Assert.Equal(306, after.Header.Version);
        Assert.Equal(2, notes.Count);
        Assert.Contains(notes, n => n.Contains("Fog near clip 12.5 was reset to 0", StringComparison.Ordinal));
        Assert.Contains(notes, n => n.StartsWith("2 Play_Sound / Music_Start events have Looping set", StringComparison.Ordinal));

        Assert.Equal(before.Sections.Select(s => s.TypeId), after.Sections.Select(s => s.TypeId)); // no section added, removed or moved
        for (int i = 0; i < before.Sections.Count; i++)
        {
            RflSection a = before.Sections[i];
            RflSection b = after.Sections[i];
            switch ((SectionType)a.TypeId)
            {
                case SectionType.AlpineLevelProperties:
                    var pa = (AlpineLevelPropertiesSection)AlpineLevelPropertiesSection.Parse(new RfReader(a.RawBytes), V305);
                    var pb = (AlpineLevelPropertiesSection)AlpineLevelPropertiesSection.Parse(new RfReader(b.RawBytes), after.Context);
                    Assert.Equal(4u, pa.Version);
                    Assert.Equal(6u, pb.Version);
                    Assert.False(pb.IsShortLayout);
                    Assert.Equal(a.RawBytes[4..], b.RawBytes[4..a.RawBytes.Length]); // v1–v4 fields carried byte for byte
                    Assert.Equal(0, pb.LegacyLighting); // Q6: pre-306 levels switch to Alpine lighting
                    Assert.Equal(90f, pb.SunPitch);
                    Assert.Equal(AlpineLevelPropertiesSection.DefaultPayloadRules, pb.PayloadRules);
                    break;

                case SectionType.AlpineMeshObjects:
                    Assert.Equal(a.RawBytes, b.RawBytes[..a.RawBytes.Length]);
                    Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 0x80, 0x3F }, b.RawBytes[a.RawBytes.Length..]);
                    break;

                case SectionType.LevelProperties:
                    var la = (LevelPropertiesSection)LevelPropertiesSection.Parse(new RfReader(a.RawBytes), V305);
                    var lb = (LevelPropertiesSection)LevelPropertiesSection.Parse(new RfReader(b.RawBytes), after.Context);
                    Assert.Equal(12.5f, la.FogNearPlane);
                    Assert.Equal(0f, lb.FogNearPlane);
                    la.FogNearPlane = 0f;
                    var w = new RfWriter();
                    la.Write(w, after.Context);
                    Assert.Equal(w.ToArray(), b.RawBytes); // nothing else in the section changed
                    break;

                default:
                    Assert.Equal(a.RawBytes, b.RawBytes); // events (Looping honoured silently), player start, level info, ...
                    break;
            }
        }

        // The second save is a no-op.
        RflFile again = RflFile.Load(saved);
        Assert.Empty(again.UpgradeToAlpine());
        Assert.Equal(saved, again.Save());
    }

    [Fact]
    public void A_Zero_Fog_Near_Clip_And_No_Looping_Upgrade_Silently()
    {
        RflFile rfl = LevelTemplates.CreateNewRfl(Alpine306Synthetic.FixedDate);
        rfl.Header.Version = 304;
        byte[] original = rfl.Save();
        RflFile file = RflFile.Load(original);
        Assert.Empty(file.UpgradeToAlpine());
        byte[] saved = file.Save();
        Assert.Equal(original.Length, saved.Length);
        Assert.Equal(306, BitConverter.ToInt32(saved, 4));
        Assert.Equal(original[8..], saved[8..]); // only the header version differs: no props chunk is synthesised
    }

    [Fact]
    public void Editor_Save_Reports_The_Upgrade_Notes()
    {
        string dir = Directory.CreateTempSubdirectory("glacier-c4-").FullName;
        try
        {
            var doc = new EditorDocument(RflFile.Load(V305Level()));
            string path = Path.Combine(dir, "up.rfl");
            doc.Save(path, updateTimestamp: false);
            Assert.Equal(2, doc.LastSaveNotes.Count);
            Assert.Equal(306, RflFile.Load(path).Header.Version);

            doc.Save(path, updateTimestamp: false);
            Assert.Empty(doc.LastSaveNotes);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Every pre-306 corpus level upgrades with only the §2 sections touched: the header version, the props chunk,
    /// the Alpine mesh chunk, level_properties when its fog near clip was non-zero, plus the pre-existing v180
    /// geometry and v300 event migrations. Everything else is byte-identical and nothing moves.
    /// </summary>
    [Theory]
    [MemberData(nameof(Corpus.RflFileNames), MemberType = typeof(Corpus))]
    public void Corpus_Upgrade_Touches_Only_The_Spec_Sections(string? fileName)
    {
        if (fileName is null)
        {
            return;
        }

        byte[] original = File.ReadAllBytes(Path.Combine(Corpus.Directory!, fileName));
        RflFile before = RflFile.Load(original);
        int source = before.Header.Version;
        if (source >= 306)
        {
            return;
        }

        if (source >= 0x12C)
        {
            // Without the upgrade an Alpine source still re-serializes byte for byte (the strict no-op invariant
            // these files had while 305 was the save version).
            Assert.True(original.AsSpan().SequenceEqual(RflFile.Load(original).Save()), $"{fileName}: raw re-save differs");
        }

        RflFile file = RflFile.Load(original);
        file.UpgradeToAlpine();
        RflFile after = RflFile.Load(file.Save());
        Assert.Equal(before.Sections.Select(s => s.TypeId), after.Sections.Select(s => s.TypeId));

        var allowed = new HashSet<uint> { (uint)SectionType.AlpineLevelProperties, (uint)SectionType.AlpineMeshObjects, (uint)SectionType.LevelProperties };
        if (source < 0xC8)
        {
            allowed.UnionWith(new[] { (uint)SectionType.StaticGeometry, (uint)SectionType.Brushes, (uint)SectionType.Movers });
        }

        if (source == 0x12C)
        {
            allowed.Add((uint)SectionType.Events);
        }

        for (int i = 0; i < before.Sections.Count; i++)
        {
            RflSection a = before.Sections[i];
            if (!allowed.Contains(a.TypeId))
            {
                Assert.True(a.RawBytes.AsSpan().SequenceEqual(after.Sections[i].RawBytes),
                    $"{fileName}: section 0x{a.TypeId:X8} changed in the v306 upgrade");
            }
        }
    }

    // ---- files newer than 306 -------------------------------------------------------------------

    private static byte[] V307Level()
    {
        byte[] bytes = Alpine306Synthetic.Level();
        BitConverter.GetBytes(307).CopyTo(bytes, 4);
        return bytes;
    }

    [Fact]
    public void A_Level_Newer_Than_306_Opens_Read_Only_With_A_Note()
    {
        RflFile rfl = RflFile.Load(V307Level());
        rfl.ParseAllKnownSections();
        Assert.True(rfl.IsSaveBlocked);
        string note = Assert.Single(rfl.LoadNotes());
        Assert.Contains("newer version of Alpine Faction (RFL version 307", note, StringComparison.Ordinal);
        Assert.Contains("read-only", note, StringComparison.Ordinal);
        Assert.Contains("RFL version 307", rfl.SaveBlockedReason, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => rfl.UpgradeToAlpine());

        // Its known chunks still load for inspection.
        Assert.NotEmpty(rfl.Sections.Select(s => s.Content).OfType<AlpineWeatherRegionsSection>().Single().Records);
    }

    [Fact]
    public void Every_Document_Save_Of_A_Newer_Level_Is_Refused_And_Writes_Nothing()
    {
        string dir = Directory.CreateTempSubdirectory("glacier-c4-").FullName;
        try
        {
            var doc = new EditorDocument(RflFile.Load(V307Level()));
            Assert.True(doc.IsSaveBlocked);
            string path = Path.Combine(dir, "newer.rfl");
            Assert.Throws<LevelSaveBlockedException>(() => doc.Save(path));
            Assert.Throws<LevelSaveBlockedException>(() => doc.SaveAs(path));
            Assert.Throws<LevelSaveBlockedException>(() => LevelSavePolicy.Write(doc, path, saveAs: false, stampTimestamp: false, levelLighting: null));
            Assert.False(File.Exists(path));
            Assert.Equal(307, doc.Rfl.Header.Version);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_306_Level_Is_Not_Blocked()
    {
        RflFile rfl = RflFile.Load(Alpine306Synthetic.Level());
        Assert.False(rfl.IsSaveBlocked);
        Assert.Null(rfl.SaveBlockedReason);
    }
}

using System.IO;
using System.Text;
using Ged.Core.IO;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Reader coverage for section layouts the engine (or Alpine Faction) accepts that the
/// field-by-field reading used to reject: the geo-region shape code (a shallow-geomod sphere
/// stores an orientation), the face lightmap-UV test (a signed 16-bit surface index), the
/// Alpine level-properties and mesh chunks written by older Alpine editors, and the
/// orientation-less v300 Alpine events. Every case is built from synthetic section bytes and
/// must parse and re-serialize byte-for-byte.
/// </summary>
public sealed class RflReaderLayoutVariantTests
{
    private const int V200 = 0xC8;
    private const int V300 = 0x12C;
    private const int V304 = 0x130;

    // ---------------------------------------------------------------- geo regions

    [Fact]
    public void GeoRegion_Shallow_Sphere_Stores_Orientation_Between_Position_And_Radius()
    {
        byte[] body = Bytes(w =>
        {
            w.Write(1);
            w.Write(6078); // uid
            w.Write((ushort)(GeoRegion.FlagIsSphere | GeoRegion.FlagHiddenInEditor | GeoRegion.FlagUseShallowGeomods));
            w.Write((ushort)0); // hardness
            w.Write(0.3f); // shallow depth
            Vec(w, -31f, 4.7f, 12.1f);
            DiskIdentity(w);
            w.Write(5f); // radius
        });

        var s = (GeoRegionsSection)RoundTrip(SectionType.GeoRegions, body, V200);
        GeoRegion g = Assert.Single(s.Regions);
        Assert.True(g.IsSphere);
        Assert.True(g.HasRotation);
        Assert.Equal(5f, g.Radius);
        Assert.Equal(0.3f, g.ShallowGeomodDepth);
        Assert.Equal(Mat3.Identity, g.Rotation);
    }

    [Fact]
    public void GeoRegion_Shape_Code_Selects_Payload_For_Every_Shape()
    {
        byte[] body = Bytes(w =>
        {
            w.Write(5);

            // Plain sphere: position + radius.
            Header(w, 1, GeoRegion.FlagIsSphere, 100);
            Vec(w, 1f, 2f, 3f);
            w.Write(4f);

            // Box: position + orientation + width/height/depth.
            Header(w, 2, GeoRegion.FlagIsBox | GeoRegion.FlagHiddenInEditor, 100);
            Vec(w, 1f, 2f, 3f);
            DiskIdentity(w);
            Vec(w, 10f, 10f, 30f);

            // Shallow box: depth first, then the box payload.
            Header(w, 3, GeoRegion.FlagIsBox | GeoRegion.FlagUseShallowGeomods, 50);
            w.Write(0.5f);
            Vec(w, 1f, 2f, 3f);
            DiskIdentity(w);
            Vec(w, 1f, 1f, 1f);

            // Shape code 0: no payload at all.
            Header(w, 4, 0, 0);

            // Shape code 3: two points and a radius.
            Header(w, 5, 0x03, 0);
            Vec(w, 1f, 2f, 3f);
            Vec(w, 4f, 5f, 6f);
            w.Write(2f);
        });

        var s = (GeoRegionsSection)RoundTrip(SectionType.GeoRegions, body, V304);
        Assert.Equal(5, s.Regions.Count);
        Assert.Null(s.Regions[0].Rotation);
        Assert.Equal(30f, s.Regions[1].Depth);
        Assert.Equal(0.5f, s.Regions[2].ShallowGeomodDepth);
        Assert.Equal(GeoRegion.ShapeTwoPoint, s.Regions[4].Shape);
        Assert.Equal(new Vec3(4f, 5f, 6f), s.Regions[4].EndPoint);
    }

    [Fact]
    public void GeoRegion_Shallow_Sphere_Created_In_Memory_Writes_Orientation()
    {
        var section = new GeoRegionsSection();
        section.Regions.Add(new GeoRegion
        {
            Uid = 7,
            Flags = GeoRegion.FlagIsSphere | GeoRegion.FlagUseShallowGeomods,
            Hardness = 50,
            ShallowGeomodDepth = 1f,
            Radius = 4f,
        });

        var w = new RfWriter(64);
        section.Write(w, new RflContext(RflFile.AlpineSaveVersion));
        byte[] bytes = w.ToArray();

        // count + uid + flags/hardness + depth + position + orientation + radius
        Assert.Equal(4 + 4 + 4 + 4 + 12 + 36 + 4, bytes.Length);
        var back = (GeoRegionsSection)GeoRegionsSection.Parse(new RfReader(bytes), new RflContext(RflFile.AlpineSaveVersion));
        Assert.Equal(Mat3.Identity, back.Regions[0].Rotation);
        Assert.Equal(4f, back.Regions[0].Radius);
    }

    // ---------------------------------------------------------------- face lightmap UVs

    [Theory]
    [InlineData(0, true)]
    [InlineData(0x7FFF, true)]
    [InlineData(0x8000, false)]
    [InlineData(0xBEEF, false)]
    [InlineData(0xFFFF, false)]
    [InlineData(-1, false)]
    public void Face_Lightmap_Uvs_Follow_The_Signed_16_Bit_Surface_Index(int surfaceIndex, bool hasLightmapUvs)
    {
        var face = new Face { SurfaceIndex = surfaceIndex };
        Assert.Equal(hasLightmapUvs, Geometry.FaceHasLightmapUvs(face));
    }

    [Fact]
    public void Geometry_Face_Past_Surface_32767_Has_No_Lightmap_Uvs_On_Disk()
    {
        var ctx = new RflContext(RflFile.AlpineSaveVersion);
        var g = new Geometry();
        g.Vertices.Add(new Vec3(0, 0, 0));
        g.Vertices.Add(new Vec3(1, 0, 0));
        g.Vertices.Add(new Vec3(0, 1, 0));
        g.Faces.Add(Triangle(surfaceIndex: 0x7FFF, withLightmap: true));
        g.Faces.Add(Triangle(surfaceIndex: 0x8000, withLightmap: false));

        var w = new RfWriter(512);
        g.Write(w, ctx);
        byte[] bytes = w.ToArray();

        var section = (GeometrySection)RoundTrip(SectionType.StaticGeometry, bytes, RflFile.AlpineSaveVersion);
        Assert.False(section.Geometry.UsesWideSurfaceLightmapUvs);
        Geometry back = section.Geometry;
        Assert.All(back.Faces[0].Vertices, v => Assert.NotNull(v.LightmapCoords));
        Assert.All(back.Faces[1].Vertices, v => Assert.Null(v.LightmapCoords));

        var again = new RfWriter(512);
        back.Write(again, ctx);
        Assert.Equal(bytes, again.ToArray());
    }

    [Fact]
    public void Static_Geometry_With_Wide_Surface_Lightmap_Uvs_Still_Opens_And_Round_Trips()
    {
        // Lightmap UVs stored on a face whose surface index is 0x8000 (a layout the engine
        // misreads, but which earlier Glacier versions read and wrote): built by writing a face
        // that does carry UVs and patching its surface index.
        var ctx = new RflContext(RflFile.AlpineSaveVersion);
        var g = new Geometry();
        g.Vertices.Add(new Vec3(0, 0, 0));
        g.Vertices.Add(new Vec3(1, 0, 0));
        g.Vertices.Add(new Vec3(0, 1, 0));
        g.Faces.Add(Triangle(surfaceIndex: 0x12345, withLightmap: true));
        var w = new RfWriter(512);
        g.Write(w, ctx);
        byte[] body = w.ToArray();
        int at = body.AsSpan().IndexOf(new byte[] { 0x45, 0x23, 0x01, 0x00 });
        Assert.True(at > 0);
        body[at] = 0x00;
        body[at + 1] = 0x80;
        body[at + 2] = 0x00;

        var s = (GeometrySection)RoundTrip(SectionType.StaticGeometry, body, RflFile.AlpineSaveVersion);
        Assert.True(s.Geometry.UsesWideSurfaceLightmapUvs);
        Assert.All(s.Geometry.Faces[0].Vertices, v => Assert.NotNull(v.LightmapCoords));
    }

    // ---------------------------------------------------------------- Alpine level properties

    public static TheoryData<string, string> AlpinePropsLayouts => new()
    {
        { "v1", "01000000 01" },
        { "v2 without headlamp", "02000000 00 01" },
        { "v2 with a legacy extra byte", "02000000 00 00 00 01" },
        { "v4 ending after rf2 geomod", "04000000 00 00 01 00 00000040 01" },
        { "v4 without the hold-open table", "04000000 00 00 01 00 00000040 00 00000000 00000000" },
        { "v4 complete", "04000000 01 01 01 01 0000c03f 01 01000000 0a000000 f0ffff7f 01000000 0b000000 efffff7f 02 01000000 0c000000" },
        { "v5 fields kept raw", "05000000 01 01 01 00 00000040 00 00000000 00000000 00000000 01 00004842 0000f041 c5a06cff" },
        { "chunk without a version", "00" },
        { "implausible version, no fields", "01010101" },
        { "v4 with a partial table", "04000000 00 00 01 00 00000040 00 02000000 01000000" },
    };

    [Theory]
    [MemberData(nameof(AlpinePropsLayouts))]
    public void AlpineProps_Every_Layout_Round_Trips(string name, string hex)
    {
        byte[] body = Hex(hex);
        var s = (AlpineLevelPropertiesSection)RoundTrip(SectionType.AlpineLevelProperties, body, V304);
        Assert.NotNull(name);
        Assert.NotNull(s);
    }

    [Fact]
    public void AlpineProps_Missing_Fields_Read_As_Alpine_Defaults()
    {
        var s = (AlpineLevelPropertiesSection)RoundTrip(SectionType.AlpineLevelProperties, Hex("02000000 00 00"), V304);
        Assert.Equal(0, s.LegacyCyclicTimers);
        Assert.Equal(0, s.LegacyMovers);
        Assert.Equal(1, s.StartsWithHeadlamp); // Alpine default for a chunk that ends before it
        Assert.Equal(2.0f, s.StaticMeshAmbientLightModifier);
        Assert.True(s.IsShortLayout);
    }

    [Fact]
    public void AlpineProps_Short_Chunk_Grows_Only_As_Far_As_An_Edit_Needs()
    {
        byte[] body = Hex("04000000 00 00 01 00 00000040 00 00000000 00000000"); // no hold-open table
        var s = (AlpineLevelPropertiesSection)Parse(SectionType.AlpineLevelProperties, body, V304);

        // Unchanged: identical bytes.
        Assert.Equal(body, Write(s, V304));

        // A hold-open keyframe needs the table: exactly the table is appended.
        s.HoldOpenKeyframeUids.Add(77);
        Assert.Equal(Hex("04000000 00 00 01 00 00000040 00 00000000 00000000 01000000 4d000000"), Write(s, V304));
    }

    [Fact]
    public void AlpineProps_Version_Raise_Writes_The_Full_Layout_And_Drops_Old_Extra_Bytes()
    {
        // v2 from an older editor: cyclic, movers, and one more legacy byte.
        var s = (AlpineLevelPropertiesSection)Parse(SectionType.AlpineLevelProperties, Hex("02000000 00 00 00 01"), V304);
        Assert.Equal(new byte[] { 0x01 }, s.UnparsedTail);

        s.Version = 4;
        byte[] written = Write(s, V304);
        Assert.Equal(Hex("04000000 00 00 00 00 00000040 00 00000000 00000000 00000000"), written);
    }

    [Fact]
    public void AlpineProps_Fresh_Section_Writes_Full_Layout_As_Before()
    {
        // A section created in memory starts at Alpine's existing-level defaults (legacy timers and movers on,
        // headlamp on, static-mesh ambient 2.0 — editor_patch/level.h:1133-1140), not zeros.
        var s = new AlpineLevelPropertiesSection { Version = 4 };
        Assert.Equal(Hex("04000000 01 01 01 00 00000040 00 00000000 00000000 00000000"), Write(s, V304));
    }

    // ---------------------------------------------------------------- Alpine mesh objects

    [Fact]
    public void AlpineMesh_Last_Record_Without_Material_Round_Trips_And_Reads_Defaults()
    {
        byte[] body = Bytes(w =>
        {
            w.Write(1u);
            MeshRecordHead(w, 2, "miner.v3c");
        });

        var s = (AlpineMeshObjectsSection)RoundTrip(SectionType.AlpineMeshObjects, body, V304);
        AlpineMeshObject m = Assert.Single(s.Meshes);
        Assert.Equal(0, m.Material);
        Assert.Equal(0, m.IsClutter);
    }

    [Fact]
    public void AlpineMesh_Bytes_Alpine_Skips_After_The_Records_Round_Trip()
    {
        // Fewer than the 4 material bytes after a short last record.
        byte[] stub = Bytes(w =>
        {
            w.Write(1u);
            MeshRecordHead(w, 1, "miner.v3c");
            w.Write(new byte[] { 0, 0, 0 });
        });
        RoundTrip(SectionType.AlpineMeshObjects, stub, V304);

        // The per-object flag block (one byte per mesh) after full records.
        byte[] flags = Bytes(w =>
        {
            w.Write(2u);
            MeshRecordHead(w, 1, "a.v3m");
            w.Write(3); // material
            w.Write((byte)0); // not clutter
            MeshRecordHead(w, 2, "b.v3m");
            w.Write(0);
            w.Write((byte)0);
            w.Write(new byte[] { 0, 1 });
        });
        var s = (AlpineMeshObjectsSection)RoundTrip(SectionType.AlpineMeshObjects, flags, V304);
        Assert.Equal(new byte[] { 0, 1 }, s.UnparsedTail);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AlpineMesh_V306_Trailing_Blocks_Round_Trip(bool withDrawScale)
    {
        byte[] body = Bytes(w =>
        {
            w.Write(2u);
            for (int uid = 1; uid <= 2; uid++)
            {
                MeshRecordHead(w, uid, "a.v3m");
                w.Write(0);
                w.Write((byte)0);
            }

            w.Write(new byte[] { 0, 1 }); // per-object flags
            for (int i = 0; i < 2; i++)
            {
                w.Write((byte)1); // geometry source
                VStr(w, "col.v3m");
                if (withDrawScale)
                {
                    w.Write(1.5f);
                }
            }
        });

        RoundTrip(SectionType.AlpineMeshObjects, body, 306);
        Assert.Throws<RflFormatException>(() => Parse(SectionType.AlpineMeshObjects, body, V304));
    }

    [Fact]
    public void AlpineMesh_Clutter_Record_With_Partial_Corpse_Fields_Round_Trips()
    {
        byte[] body = Bytes(w =>
        {
            w.Write(1u);
            MeshRecordHead(w, 9, "barrel.v3m");
            w.Write(2); // material
            w.Write((byte)1); // clutter
            w.Write(150f);
            VStr(w, string.Empty);
            VStr(w, "yellboom");
            w.Write(1f);
            w.Write(10f);
            for (int k = 0; k < 11; k++)
            {
                w.Write(1f);
            }

            VStr(w, "barrel_corpse.v3m"); // corpse file + anim, no collision/material bytes
            VStr(w, string.Empty);
        });

        var s = (AlpineMeshObjectsSection)RoundTrip(SectionType.AlpineMeshObjects, body, V304);
        AlpineMeshClutterInfo c = s.Meshes[0].Clutter!;
        Assert.Equal("barrel_corpse.v3m", c.CorpseFilename);
        Assert.Equal(2, c.CorpseCollision); // Alpine default
        Assert.Equal(-1, c.CorpseMaterial); // Alpine default
    }

    [Fact]
    public void AlpineMesh_Edited_Short_Record_Writes_The_Fields_It_Needs()
    {
        byte[] body = Bytes(w =>
        {
            w.Write(1u);
            MeshRecordHead(w, 2, "miner.v3c");
        });
        var s = (AlpineMeshObjectsSection)Parse(SectionType.AlpineMeshObjects, body, V304);
        s.Meshes[0].Material = 4;

        byte[] expected = Bytes(w =>
        {
            w.Write(1u);
            MeshRecordHead(w, 2, "miner.v3c");
            w.Write(4);
        });
        Assert.Equal(expected, Write(s, V304));

        // A mesh appended after it forces the full layout on the formerly-last record.
        s.Meshes.Add(new AlpineMeshObject { Uid = 3, MeshFilename = "b.v3m", Orientation = Mat3.Identity });
        var reparsed = (AlpineMeshObjectsSection)Parse(SectionType.AlpineMeshObjects, Write(s, V304), V304);
        Assert.Equal(2, reparsed.Meshes.Count);
        Assert.Equal(4, reparsed.Meshes[0].Material);
        Assert.Equal("b.v3m", reparsed.Meshes[1].MeshFilename);
    }

    [Fact]
    public void AlpineMesh_Unknown_Header_Layout_Is_Still_Rejected()
    {
        // A record-count field Alpine would clamp and then misread (a pre-release layout).
        byte[] body = Bytes(w =>
        {
            w.Write(0xAF000003u);
            w.Write(1u);
            MeshRecordHead(w, 2, "barrel.v3m");
        });

        Assert.Throws<RflFormatException>(() => Parse(SectionType.AlpineMeshObjects, body, V304));
    }

    [Fact]
    public void AlpineMesh_Unexplained_Leftover_Is_Still_Rejected()
    {
        byte[] body = Bytes(w =>
        {
            w.Write(1u);
            MeshRecordHead(w, 2, "a.v3m");
            w.Write(0);
            w.Write((byte)0);
            w.Write(new byte[] { 1, 2, 3, 4, 5 });
        });

        Assert.Throws<RflFormatException>(() => Parse(SectionType.AlpineMeshObjects, body, V304));
    }

    // ---------------------------------------------------------------- v300 Alpine events

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Events_V300_Without_Alpine_Orientation_Parse_And_Round_Trip(bool teleportOmitted, bool cloneOmitted)
    {
        byte[] body = Bytes(w =>
        {
            w.Write(3);
            EventRecord(w, 1, "Difficulty_Gate", rotation: false);
            EventRecord(w, 2, "Clone_Entity", rotation: !cloneOmitted);
            EventRecord(w, 3, "AF_Teleport_Player", rotation: !teleportOmitted);
        });

        var s = (EventsSection)RoundTrip(SectionType.Events, body, V300);
        Assert.Equal(teleportOmitted, s.OmitsAfTeleportPlayerOrientation);
        Assert.Equal(cloneOmitted, s.OmitsCloneEntityOrientation);
        Assert.Equal(3, s.Events.Count);
    }

    [Fact]
    public void Events_V300_Standard_Layout_Is_Not_Flagged()
    {
        byte[] body = Bytes(w =>
        {
            w.Write(1);
            EventRecord(w, 1, "AF_Teleport_Player", rotation: true);
        });

        var s = (EventsSection)RoundTrip(SectionType.Events, body, V300);
        Assert.False(s.OmitsAlpineEventOrientation);
    }

    [Fact]
    public void Events_Orientationless_Variant_Is_Only_Accepted_At_V300()
    {
        byte[] body = Bytes(w =>
        {
            w.Write(1);
            EventRecord(w, 1, "AF_Teleport_Player", rotation: false);
        });

        Assert.Throws<RflFormatException>(() => Parse(SectionType.Events, body, 0x12D));
    }

    [Fact]
    public void Upgrade_Writes_The_Orientation_The_Game_Reads()
    {
        byte[] events = Bytes(w =>
        {
            w.Write(2);
            EventRecord(w, 1, "AF_Teleport_Player", rotation: false);
            EventRecord(w, 2, "Difficulty_Gate", rotation: false);
        });

        var file = new RflFile();
        file.Header.Version = V300;
        file.Header.LevelName = "t";
        file.Header.ModName = string.Empty;
        file.Sections.Add(new RflSection((uint)SectionType.Events, events));
        file.Sections.Add(new RflSection((uint)SectionType.End, System.Array.Empty<byte>()));

        // Unmodified at v300: verbatim.
        RflFile reread = RflFile.Load(file.Save());
        Assert.Equal(events, reread.Sections[0].RawBytes);

        reread.ParseAllKnownSections();
        Assert.Single(reread.LoadNotes());
        reread.UpgradeToAlpine();
        RflFile v305 = RflFile.Load(reread.Save());
        v305.ParseAllKnownSections();
        var upgraded = (EventsSection)v305.Sections[0].Content!;
        Assert.False(upgraded.OmitsAlpineEventOrientation);
        Assert.Equal(Mat3.Identity, upgraded.Events[0].Rotation);
        Assert.Equal(events.Length + 36, v305.Sections[0].RawBytes.Length);
        Assert.Empty(v305.LoadNotes());
    }

    // ---------------------------------------------------------------- load notes

    [Fact]
    public void Load_Notes_Flag_A_Version_Glacier_Does_Not_Read_Without_Changing_The_Load()
    {
        // An old/beta-format file: the table that follows the header is not one Glacier reads.
        byte[] file = RawFile(45, numSections: 13, w => w.Write(new byte[] { 1, 2, 3, 4, 0xFF, 0xFF, 0xFF, 0x7F, 9 }));
        RflFile rfl = RflFile.Load(file);
        Assert.Empty(rfl.Sections);
        string note = Assert.Single(rfl.LoadNotes());
        Assert.Contains("version (45)", note);
        Assert.Contains("empty level", note);
    }

    [Fact]
    public void Load_Notes_Flag_An_Interrupted_Save_Without_Changing_The_Load()
    {
        // Header counts never patched and no End marker.
        byte[] file = RawFile(V304, numSections: 0, w =>
        {
            w.Write((uint)SectionType.TgaFiles);
            w.Write(4);
            w.Write(0);
        });
        RflFile rfl = RflFile.Load(file);
        Assert.Single(rfl.Sections);
        Assert.True(rfl.SectionTableIncomplete);
        Assert.Contains("interrupted save", Assert.Single(rfl.LoadNotes()));
    }

    [Fact]
    public void Load_Notes_Are_Empty_For_A_Normal_Level()
    {
        byte[] file = RawFile(V304, numSections: 1, w =>
        {
            w.Write((uint)SectionType.TgaFiles);
            w.Write(4);
            w.Write(0);
            w.Write((uint)SectionType.End);
            w.Write(0);
        });
        RflFile rfl = RflFile.Load(file);
        Assert.False(rfl.SectionTableIncomplete);
        Assert.Empty(rfl.LoadNotes());
        Assert.Equal(file, rfl.Save());
    }

    private static byte[] RawFile(int version, int numSections, System.Action<BinaryWriter> sections) => Bytes(w =>
    {
        w.Write(0xD4BADA55u);
        w.Write(version);
        w.Write(0u);
        w.Write(0);
        w.Write(0);
        w.Write(numSections);
        w.Write(numSections == 0 ? 0 : 4);
        VStr(w, "t");
        if (version >= 0xB2)
        {
            VStr(w, string.Empty);
        }

        sections(w);
    });

    // ---------------------------------------------------------------- diagnostics

    [Fact]
    public void Truncated_Section_Reports_The_Section()
    {
        byte[] body = Bytes(w =>
        {
            w.Write(2);
            EventRecord(w, 1, "Difficulty_Gate", rotation: false);
        });

        var ex = Assert.Throws<RflFormatException>(() => Parse(SectionType.Events, body, V304));
        Assert.Contains("0x00000600", ex.Message);
        Assert.Contains("Events", ex.Message);
    }

    // ---------------------------------------------------------------- retail fixture (optional)

    /// <summary>
    /// A stock single-player level whose geo regions include a shallow-geomod sphere. The level
    /// is retail-derived, so it lives only in the gitignored research/fixtures tree; the test
    /// returns early on a checkout without it.
    /// </summary>
    [Fact]
    public void Stock_Level_With_Shallow_Sphere_Region_Parses_And_Round_Trips()
    {
        string? path = TestPaths.FixtureFile("levels", "L9S1.rfl");
        if (path is null)
        {
            return;
        }

        byte[] original = File.ReadAllBytes(path);
        RflFile rfl = RflFile.Load(original);
        rfl.ParseAllKnownSections();
        foreach (RflSection section in rfl.Sections)
        {
            section.Dirty = section.Content is not null;
        }

        Assert.Equal(original, rfl.Save());
        GeoRegionsSection geo = rfl.Sections.Select(s => s.Content).OfType<GeoRegionsSection>().Single();
        Assert.Contains(geo.Regions, g => g.IsSphere && g.UsesShallowGeomods && g.Rotation is not null);
    }

    // ---------------------------------------------------------------- helpers

    private static IRflSectionContent Parse(SectionType type, byte[] body, int version)
    {
        var section = new RflSection((uint)type, body);
        Assert.True(RflSectionRegistry.TryParse(section, new RflContext(version), out IRflSectionContent? content));
        return content!;
    }

    private static byte[] Write(IRflSectionContent content, int version)
    {
        var w = new RfWriter(256);
        content.Write(w, new RflContext(version));
        return w.ToArray();
    }

    private static IRflSectionContent RoundTrip(SectionType type, byte[] body, int version)
    {
        IRflSectionContent content = Parse(type, body, version);
        Assert.Equal(body, Write(content, version));
        return content;
    }

    private static byte[] Bytes(System.Action<BinaryWriter> build)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.Latin1, leaveOpen: true))
        {
            build(w);
        }

        return ms.ToArray();
    }

    private static byte[] Hex(string hex) => System.Convert.FromHexString(hex.Replace(" ", string.Empty));

    private static void Vec(BinaryWriter w, float x, float y, float z)
    {
        w.Write(x);
        w.Write(y);
        w.Write(z);
    }

    /// <summary>Identity orientation in the on-disk row order (forward, right, up).</summary>
    private static void DiskIdentity(BinaryWriter w)
    {
        Vec(w, 0f, 0f, 1f);
        Vec(w, 1f, 0f, 0f);
        Vec(w, 0f, 1f, 0f);
    }

    private static void Header(BinaryWriter w, int uid, int flags, int hardness)
    {
        w.Write(uid);
        w.Write((ushort)flags);
        w.Write((ushort)hardness);
    }

    private static void VStr(BinaryWriter w, string s)
    {
        w.Write((ushort)s.Length);
        w.Write(Encoding.Latin1.GetBytes(s));
    }

    private static void MeshRecordHead(BinaryWriter w, int uid, string mesh)
    {
        w.Write(uid);
        Vec(w, 0f, 0f, -15f);
        DiskIdentity(w);
        VStr(w, "Mesh");
        VStr(w, mesh);
        VStr(w, string.Empty); // state anim
        w.Write((byte)2); // collision
        w.Write((byte)0); // no texture overrides
    }

    private static void EventRecord(BinaryWriter w, int uid, string className, bool rotation)
    {
        w.Write(uid);
        VStr(w, className);
        Vec(w, 0f, 0f, -15f);
        VStr(w, className);
        w.Write((byte)0); // hidden
        w.Write(0f); // delay
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write(0);
        w.Write(0);
        w.Write(0f);
        w.Write(0f);
        VStr(w, string.Empty);
        VStr(w, string.Empty);
        w.Write(0); // no links
        if (rotation)
        {
            DiskIdentity(w);
        }

        w.Write(new byte[] { 0xEB, 0xD7, 0x00, 0xFF }); // color
    }

    private static Face Triangle(int surfaceIndex, bool withLightmap)
    {
        var f = new Face { SurfaceIndex = surfaceIndex, Texture = 0 };
        for (int i = 0; i < 3; i++)
        {
            f.Vertices.Add(new FaceVertex
            {
                Index = i,
                TextureCoords = new Uv(i, 0),
                LightmapCoords = withLightmap ? new Uv(0, i) : null,
            });
        }

        return f;
    }
}

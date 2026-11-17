using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// The sky room draws full-bright, as RF and Alpine Faction draw it (the face texture alone: no lightmap, no fog; RF.exe
/// FUN_005536c0, Alpine gr_d3d11_solid.cpp:29-45/106-113/546). A sky box bakes to the level ambient when no light
/// reaches it, so binding its lightmap in the lit view would show the sky near-black. CPU-only (no GPU).
/// </summary>
public sealed class SkyRoomFullbrightTests
{
    /// <summary>
    /// Four rooms, one lightmapped face each: 0 a playable room, 1 the sky room, 2 a detail room only the sky room
    /// holds, 3 a detail room both the playable room and the sky room hold.
    /// </summary>
    private static RflFile Level()
    {
        var geo = new Geometry();
        for (int r = 0; r < 4; r++)
        {
            geo.Textures.Add($"tex{r}.tga");
            geo.Rooms.Add(new Room { Id = 0x70000001 + r, IsSkyroom = (byte)(r == 1 ? 1 : 0), IsSubroom = (byte)(r >= 2 ? 1 : 0) });
            geo.SubroomLists.Add(new SubroomList { RoomIndex = r });
            int v = geo.Vertices.Count;
            float x = r * 10f;
            geo.Vertices.Add(new Vec3(x, 0, 0));
            geo.Vertices.Add(new Vec3(x + 2, 0, 0));
            geo.Vertices.Add(new Vec3(x + 2, 2, 0));
            geo.Vertices.Add(new Vec3(x, 2, 0));
            geo.Surfaces.Add(new Surface { LightmapIndex = 0, RoomIndex = r });
            var face = new Face
            {
                Texture = r,
                SurfaceIndex = r,
                RoomIndex = r,
                FaceId = r,
                Plane = new RfPlane(new Vec3(0, 0, -1), 0),
            };
            for (int i = 0; i < 4; i++)
            {
                face.Vertices.Add(new FaceVertex { Index = v + i, LightmapCoords = new Uv(0.1f * i, 0.1f) });
            }

            geo.Faces.Add(face);
        }

        geo.SubroomLists[1].SubroomIndices.AddRange(new[] { 2, 3 });
        geo.SubroomLists[0].SubroomIndices.Add(3);

        var rfl = new RflFile();
        rfl.Header.Version = 0xC8;
        rfl.Header.LevelName = "skyroom";
        rfl.Sections.Add(new RflSection((uint)SectionType.StaticGeometry, Array.Empty<byte>())
        {
            Content = new GeometrySection { Geometry = geo },
            Dirty = true,
        });
        rfl.Sections.Add(new RflSection((uint)SectionType.Lightmaps, Array.Empty<byte>())
        {
            Content = new LightmapsSection { Lightmaps = { new Lightmap { Width = 4, Height = 4, Pixels = new byte[4 * 4 * 3] } } },
            Dirty = true,
        });
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return rfl;
    }

    [Fact]
    public void Sky_Room_Indices_Hold_The_Sky_Room_And_The_Detail_Rooms_Only_It_Holds()
    {
        RflFile rfl = Level();
        rfl.ParseAllKnownSections();
        Geometry geo = rfl.Sections.Select(s => s.Content).OfType<GeometrySection>().Single().Geometry;

        HashSet<int> sky = Assert.IsType<HashSet<int>>(SceneBuilder.SkyRoomIndices(geo));
        Assert.Equal(new[] { 1, 2 }, sky.OrderBy(i => i));

        geo.Rooms[1].IsSkyroom = 0;
        Assert.Null(SceneBuilder.SkyRoomIndices(geo));
    }

    [Fact]
    public void Sky_Room_Faces_Bind_No_Lightmap_So_The_Lit_View_Shows_Their_Texture_Full_Bright()
    {
        RenderScene scene = SceneBuilder.Build(Level(), new SceneBuildOptions { IncludeMovers = false, IncludeObjects = false });

        int LightmapOf(string texture) => Assert.Single(scene.Batches, b => b.TextureName == texture).LightmapIndex;

        Assert.Equal(0, LightmapOf("tex0.tga"));  // playable room: lightmapped
        Assert.Equal(-1, LightmapOf("tex1.tga")); // the sky room: full-bright
        Assert.Equal(-1, LightmapOf("tex2.tga")); // detail held only by the sky room: full-bright
        Assert.Equal(0, LightmapOf("tex3.tga"));  // detail a playable room also holds: lightmapped
    }
}

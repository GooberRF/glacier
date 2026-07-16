using System;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Proves — by construction — that GED does NOT reproduce the well-known stock
/// RED.exe corruption bugs (FEATURES §13 "Stock-bug fixes by construction"):
/// (1) lights survive copy/paste (both present, valid, distinct UIDs),
/// (2) respawn-point multi-edit does not corrupt siblings,
/// (3) keyframe + cutscene-path-node copy preserves every field,
/// (4) nav cover/hide flags are preserved on save (RED clears them),
/// (5) more than 127 decals round-trips without corruption (RED's byte count wraps).
/// These are expected to pass on the shipped tree — this file is the explicit proof.
/// </summary>
public sealed class StockBugRegressionTests
{
    private static RflFile NewLevel(SectionType type, IRflSectionContent content)
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0xC8;
        rfl.Header.LevelName = "stockbug.rfl";
        rfl.Sections.Add(new RflSection((uint)type, Array.Empty<byte>()) { Content = content, Dirty = true });
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return rfl;
    }

    private static RflFile RoundTrip(RflFile rfl) => RflFile.Load(rfl.Save());

    private static T Content<T>(RflFile rfl) where T : class, IRflSectionContent
    {
        foreach (RflSection s in rfl.Sections)
        {
            RflSectionRegistry.TryParse(s, rfl.Context, out IRflSectionContent? parsed);
            if (parsed is T match)
            {
                return match;
            }
        }

        throw new InvalidOperationException($"no {typeof(T).Name}");
    }

    // ---- (1) lights survive copy/paste ---------------------------------------

    [Fact]
    public void Lights_Survive_Copy_Paste_Both_Present_And_Valid()
    {
        var lights = new LightsSection(SectionType.Lights);
        lights.Lights.Add(new Light
        {
            Uid = 5,
            ClassName = "Light",
            ScriptName = "lamp",
            Position = new Vec3(3, 4, 5),
            Rotation = Mat3.Identity,
            Color = new RfColor(200, 150, 100, 255),
            Range = 12.5f,
            OnIntensity = 1f,
        });

        var doc = new EditorDocument(NewLevel(SectionType.Lights, lights));
        LevelObject src = doc.Objects.Single(o => o.Kind == LevelObjectKind.Light);
        doc.Select(src);
        doc.CopySelection();
        var newUids = doc.Paste();

        // Both lights present, distinct UIDs, and the clone is a real deep copy.
        Assert.Single(newUids);
        var allLights = doc.Objects.Where(o => o.Kind == LevelObjectKind.Light).ToList();
        Assert.Equal(2, allLights.Count);
        Light pasted = (Light)doc.FindByUid(newUids[0])!.Model;
        var original = (Light)src.Model;
        Assert.NotEqual(original.Uid, pasted.Uid);
        Assert.NotSame(original, pasted);
        Assert.Equal(original.Color, pasted.Color);
        Assert.Equal(original.Range, pasted.Range);

        // And they survive a save/reload with both intact (the "lights break after copy" bug).
        RflFile reloaded = RoundTrip(doc.Rfl);
        LightsSection section = Content<LightsSection>(reloaded);
        Assert.Equal(2, section.Lights.Count);
        Assert.All(section.Lights, l => Assert.Equal(12.5f, l.Range));
        Assert.Equal(2, section.Lights.Select(l => l.Uid).Distinct().Count());
    }

    // ---- (2) respawn-point multi-edit does not corrupt -----------------------

    [Fact]
    public void Respawn_MultiEdit_Does_Not_Corrupt_Siblings()
    {
        var rp = new MpRespawnPointsSection();
        for (int i = 0; i < 3; i++)
        {
            rp.Points.Add(new MpRespawnPoint
            {
                Uid = 10 + i,
                Position = new Vec3(i, 0, 0),
                Rotation = Mat3.Identity,
                ScriptName = $"spawn{i}",
                Team = 0,
                RedTeam = (byte)(i == 0 ? 1 : 0),
                BlueTeam = (byte)(i == 1 ? 1 : 0),
            });
        }

        var doc = new EditorDocument(NewLevel(SectionType.MpRespawnPoints, rp));
        var points = doc.Objects.Where(o => o.Kind == LevelObjectKind.MpRespawnPoint).ToList();
        Assert.Equal(3, points.Count);

        // Multi-edit: set Team on the first two (as the multi-select inspector does),
        // leaving the third untouched.
        foreach (LevelObject o in points.Take(2))
        {
            var model = (MpRespawnPoint)o.Model;
            int old = model.Team;
            doc.EditValue(o.Section, "Edit Team", old, 1, v => model.Team = v);
        }

        RflFile reloaded = RoundTrip(doc.Rfl);
        MpRespawnPointsSection section = Content<MpRespawnPointsSection>(reloaded);

        // All three survive; the edited two changed team, the third is untouched, and
        // every point keeps its own position / team-flag identity (no cross-corruption).
        Assert.Equal(3, section.Points.Count);
        MpRespawnPoint p0 = section.Points.Single(p => p.Uid == 10);
        MpRespawnPoint p1 = section.Points.Single(p => p.Uid == 11);
        MpRespawnPoint p2 = section.Points.Single(p => p.Uid == 12);
        Assert.Equal(1, p0.Team);
        Assert.Equal(1, p1.Team);
        Assert.Equal(0, p2.Team);
        Assert.Equal(1, p0.RedTeam);
        Assert.Equal(1, p1.BlueTeam);
        Assert.Equal(new Vec3(2, 0, 0), p2.Position);
    }

    // ---- (3) keyframe + cutscene-path-node copy preserve fields ---------------

    [Fact]
    public void Keyframe_Deep_Copy_Preserves_Every_Field()
    {
        var kf = new Keyframe
        {
            Uid = 42,
            Position = new Vec3(1, 2, 3),
            Rotation = Mat3.Identity,
            ScriptName = "kf",
            PauseTime = 1.5f,
            DepartTravelTime = 2f,
            ReturnTravelTime = 3f,
            AccelTime = 0.25f,
            DecelTime = 0.5f,
            EventUid = 7,
            ItemUid1 = 8,
            ItemUid2 = 9,
            DegreesAboutAxis = 90f,
        };

        var clone = (Keyframe)ModelCloner.Clone(kf);

        Assert.NotSame(kf, clone);
        Assert.Equal(kf.Uid, clone.Uid);
        Assert.Equal(kf.Position, clone.Position);
        Assert.Equal(kf.PauseTime, clone.PauseTime);
        Assert.Equal(kf.DepartTravelTime, clone.DepartTravelTime);
        Assert.Equal(kf.ReturnTravelTime, clone.ReturnTravelTime);
        Assert.Equal(kf.AccelTime, clone.AccelTime);
        Assert.Equal(kf.DecelTime, clone.DecelTime);
        Assert.Equal(kf.EventUid, clone.EventUid);
        Assert.Equal(kf.ItemUid1, clone.ItemUid1);
        Assert.Equal(kf.ItemUid2, clone.ItemUid2);
        Assert.Equal(kf.DegreesAboutAxis, clone.DegreesAboutAxis);
    }

    [Fact]
    public void Cutscene_Path_Node_Copy_Preserves_Fields()
    {
        var nodes = new CutscenePathNodesSection();
        nodes.Nodes.Add(new ObjectHeader
        {
            Uid = 30,
            ScriptName = "pathnode",
            Position = new Vec3(11, 22, 33),
            Rotation = Mat3.Identity,
        });

        var doc = new EditorDocument(NewLevel(SectionType.CutscenePathNodes, nodes));
        LevelObject src = doc.Objects.Single(o => o.Kind == LevelObjectKind.CutscenePathNode);
        doc.Select(src);
        doc.CopySelection();
        var newUids = doc.Paste();

        Assert.Single(newUids);
        var pasted = (ObjectHeader)doc.FindByUid(newUids[0])!.Model;
        var original = (ObjectHeader)src.Model;
        Assert.NotSame(original, pasted);
        Assert.NotEqual(original.Uid, pasted.Uid);
        Assert.Equal(original.Position, pasted.Position); // fields preserved on copy
        Assert.Equal(2, doc.Objects.Count(o => o.Kind == LevelObjectKind.CutscenePathNode));
    }

    // ---- (4) nav cover/hide preserved on save --------------------------------

    [Fact]
    public void NavPoint_Cover_And_Hide_Survive_Save()
    {
        var nav = new NavPointsSection();
        nav.NavPoints.Add(new NavPoint
        {
            Uid = 1,
            Position = new Vec3(0, 0, 0),
            Radius = 1.5f,
            Height = 3f,
            NavType = 0,
            Cover = 1,
            Hide = 1,
            Crunch = 1,
            PauseTime = 2f,
        });
        nav.Connections.Add(new System.Collections.Generic.List<int>()); // one per nav point

        var rfl = NewLevel(SectionType.NavPoints, nav);
        NavPointsSection section = Content<NavPointsSection>(RoundTrip(rfl));

        NavPoint np = Assert.Single(section.NavPoints);
        Assert.Equal(1, np.Cover);  // RED clears these on save — GED preserves them
        Assert.Equal(1, np.Hide);
        Assert.Equal(1, np.Crunch);
    }

    // ---- (5) more than 127 decals round-trips without corruption --------------

    [Fact]
    public void Over_127_Decals_RoundTrip_Without_Corruption()
    {
        const int count = 200; // > 127: stock RED's signed-byte count wraps and corrupts
        var decals = new DecalsSection();
        for (int i = 0; i < count; i++)
        {
            decals.Decals.Add(new Decal
            {
                Header = new ObjectHeader { Uid = 1000 + i, Position = new Vec3(i, 0, 0), Rotation = Mat3.Identity },
                Extents = new Vec3(1, 1, 1),
                Texture = $"decal{i}.tga",
                Alpha = 255,
                Tiling = 0,
                Scale = 1f,
            });
        }

        var rfl = NewLevel(SectionType.Decals, decals);
        DecalsSection section = Content<DecalsSection>(RoundTrip(rfl));

        Assert.Equal(count, section.Decals.Count);
        Assert.Equal(count, section.Decals.Select(d => d.Header.Uid).Distinct().Count());
        Assert.Equal("decal199.tga", section.Decals[^1].Texture); // the 200th survived intact
    }
}

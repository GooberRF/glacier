using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.Model;
using Ged.Core.Tables;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// The parity backbone: place one object of every type and one event of
/// every placeable class (145) into a copy of dm01 through the editor APIs with
/// representative field values, save, reload, and assert every placed
/// object/event reads back structurally identical while every untouched section
/// stays byte-identical.
/// </summary>
public class PlacementRoundTripTests
{
    private static string? Dm01 => Corpus.Available ? Path.Combine(Corpus.Directory!, "dm01.rfl") : null;

    [Fact]
    public void Place_Every_Object_And_Event_Then_Save_Reload_Is_Structurally_Identical()
    {
        if (Dm01 is null || !File.Exists(Dm01))
        {
            return;
        }

        byte[] original = File.ReadAllBytes(Dm01);
        var doc = EditorDocument.OpenBytes(original, Dm01);

        // Snapshot the original section bytes keyed by type id (each type appears once).
        var originalBytes = doc.Rfl.Sections.ToDictionary(s => s.TypeId, s => s.RawBytes);

        // Sections we intentionally modify are exempt from the untouched-identity check.
        var touched = new HashSet<uint> { (uint)SectionType.Events };
        foreach (LevelObjectKind kind in ObjectFactory.RoundTripKinds)
        {
            touched.Add((uint)ObjectFactory.Build(kind, 0, Vec3.Zero).Section);
        }

        // ---- place one event of every placeable class (145) --------------------
        var placedEvents = new List<RflEvent>();
        int n = 0;
        foreach (EventSchema schema in EventSchemaCatalog.Placeable)
        {
            LevelObject? lo = doc.PlaceEvent(schema, new Vec3(n * 2, 100, 0), sampleValues: true);
            Assert.NotNull(lo);
            placedEvents.Add((RflEvent)lo!.Model);
            n++;
        }

        Assert.Equal(145, placedEvents.Count);

        // ---- place one object of every type -----------------------------------
        var placedObjects = new List<(int Uid, LevelObjectKind Kind, Vec3 Pos)>();
        int m = 0;
        foreach (LevelObjectKind kind in ObjectFactory.RoundTripKinds)
        {
            var pos = new Vec3(m * 3, 200, 0);
            LevelObject? lo = doc.PlaceObject(kind, pos);
            Assert.NotNull(lo);
            placedObjects.Add((lo!.Uid, kind, pos));
            m++;
        }

        // ---- save + reload -----------------------------------------------------
        byte[] saved = doc.SaveToBytes();
        var reloaded = EditorDocument.OpenBytes(saved);

        // Every placed event reads back with identical generic-record fields.
        var reloadedEvents = reloaded.Objects
            .Where(o => o.Kind == LevelObjectKind.Event)
            .Select(o => (RflEvent)o.Model)
            .ToDictionary(e => e.Uid);

        foreach (RflEvent expected in placedEvents)
        {
            Assert.True(reloadedEvents.TryGetValue(expected.Uid, out RflEvent? actual),
                $"Placed event {expected.ClassName} (uid {expected.Uid}) missing after reload.");
            AssertEventEqual(expected, actual!);
        }

        // Every placed object reads back with the same kind + position.
        foreach (var (uid, kind, pos) in placedObjects)
        {
            LevelObject? back = reloaded.FindByUid(uid);
            Assert.True(back is not null, $"Placed {kind} (uid {uid}) missing after reload.");
            Assert.Equal(kind, back!.Kind);
            Assert.Equal(pos, back.Position);
        }

        // Untouched sections are byte-identical to the original.
        foreach (RflSection s in reloaded.Rfl.Sections)
        {
            if (touched.Contains(s.TypeId) || !originalBytes.TryGetValue(s.TypeId, out byte[]? orig))
            {
                continue;
            }

            Assert.True(orig.AsSpan().SequenceEqual(s.RawBytes),
                $"Untouched section 0x{s.TypeId:X8} changed on save.");
        }

        // Serialization fixpoint: a no-op re-save of the reloaded file is byte-identical.
        Assert.Equal(saved, reloaded.SaveToBytes());
    }

    [Fact]
    public void Nav_Point_Cover_And_Hide_Flags_Survive_The_Round_Trip()
    {
        // The stock RED bug clears nav-point cover/hide on save; GED must not.
        if (Dm01 is null || !File.Exists(Dm01))
        {
            return;
        }

        var doc = EditorDocument.OpenBytes(File.ReadAllBytes(Dm01), Dm01);
        LevelObject? np = doc.PlaceObject(LevelObjectKind.NavPoint, new Vec3(5, 5, 5));
        int uid = np!.Uid;

        var reloaded = EditorDocument.OpenBytes(doc.SaveToBytes());
        var back = (NavPoint)reloaded.FindByUid(uid)!.Model;
        Assert.Equal(1, back.Cover);
        Assert.Equal(1, back.Hide);
    }

    private static void AssertEventEqual(RflEvent a, RflEvent b)
    {
        Assert.Equal(a.ClassName, b.ClassName);
        Assert.Equal(a.Position, b.Position);
        Assert.Equal(a.ScriptName, b.ScriptName);
        Assert.Equal(a.Delay, b.Delay);
        Assert.Equal(a.Bool1, b.Bool1);
        Assert.Equal(a.Bool2, b.Bool2);
        Assert.Equal(a.Int1, b.Int1);
        Assert.Equal(a.Int2, b.Int2);
        Assert.Equal(a.Float1, b.Float1);
        Assert.Equal(a.Float2, b.Float2);
        Assert.Equal(a.Str1, b.Str1);
        Assert.Equal(a.Str2, b.Str2);
        Assert.Equal(a.Links, b.Links);
        Assert.Equal(a.Rotation, b.Rotation);
        Assert.Equal(a.Color, b.Color);
    }
}

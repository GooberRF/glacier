using System;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Face-plane sign parity for authored brush/mover geometry. RF's stored-plane convention is
/// <c>Normal·X + Offset == 0</c> (i.e. <c>Offset = -(Normal·vertex)</c>), and RF builds a mover's
/// collision hull from these authored face planes — so an inverted offset animates fine but
/// collides wrong ("collision doesn't follow the mover"). These gates pin the correct sign on every
/// authoring path and verify the load-time repair that heals levels saved by the earlier build.
/// </summary>
public sealed class MoverPlaneSignTests
{
    private static EditorDocument NewLevel()
    {
        var rfl = new RflFile();
        rfl.Header.Version = SaveTargets.AlpineVersion;
        rfl.Header.LevelName = "sign.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return new EditorDocument(rfl);
    }

    /// <summary>An air room plus a solid box (placed off-origin) turned into a mover.</summary>
    private static (EditorDocument Doc, int MoverUid) RoomWithMover()
    {
        EditorDocument doc = NewLevel();
        var be = new BrushEditor(doc);
        be.CreateBrush(
            new BrushCreateParams { Shape = BrushShape.Box, Air = true, Width = 20, Height = 12, Depth = 20 },
            new Vec3(0, 0, 0), Mat3.Identity);
        int box = be.CreateBrush(
            new BrushCreateParams { Shape = BrushShape.Box, Width = 3, Height = 3, Depth = 3 },
            new Vec3(2, -3, 0), Mat3.Identity);
        var mv = new MoverService(doc);
        mv.CreateMover(new[] { box }, Array.Empty<int>(), "Lift");
        return (doc, box);
    }

    private static Brush MoverFrom(RflFile rfl, int uid) =>
        rfl.Sections.Select(s => s.Content).OfType<MoversSection>().First().Movers.Single(m => m.Uid == uid);

    private static Vec3 Centroid(Geometry g, Face f)
    {
        var sum = new Vec3(0, 0, 0);
        foreach (FaceVertex fv in f.Vertices)
        {
            sum = sum.Add(g.Vertices[fv.Index]);
        }

        return sum.Scale(1f / f.Vertices.Count);
    }

    // ---- (a) from-scratch authoring ----------------------------------------------------------

    [Fact]
    public void FromScratch_Brush_Face_Planes_Lie_On_Every_Corner()
    {
        Geometry g = BrushFactory.Box(3, 4, 5, 0, 0, 0, "rck_default.tga");
        Assert.NotEmpty(g.Faces);

        foreach (Face f in g.Faces)
        {
            Vec3 n = f.Plane.Normal;
            foreach (FaceVertex fv in f.Vertices)
            {
                Vec3 v = g.Vertices[fv.Index];
                float signed = n.Dot(v) + f.Plane.Offset; // RF convention: == 0 on the plane
                Assert.True(MathF.Abs(signed) < 1e-3f,
                    $"authored face vertex is off its own plane by {signed} (offset has the wrong sign)");
            }
        }
    }

    // ---- (b) mover save → parse --------------------------------------------------------------

    [Fact]
    public void Saved_Mover_Face_Plane_Offsets_Are_Negative_NdotV()
    {
        var (doc, box) = RoomWithMover();

        byte[] bytes = doc.SaveToBytes();
        RflFile reloaded = RflFile.Load(bytes);
        reloaded.ParseAllKnownSections();

        Brush mover = MoverFrom(reloaded, box);
        Assert.NotEmpty(mover.Geometry.Faces);

        foreach (Face f in mover.Geometry.Faces)
        {
            Vec3 n = f.Plane.Normal;
            Vec3 c = Centroid(mover.Geometry, f);
            Assert.True(MathF.Abs(f.Plane.Offset - (-n.Dot(c))) < 1e-3f,
                $"mover face offset {f.Plane.Offset} != -(n·c) {-n.Dot(c)}");

            foreach (FaceVertex fv in f.Vertices)
            {
                float signed = n.Dot(mover.Geometry.Vertices[fv.Index]) + f.Plane.Offset;
                Assert.True(MathF.Abs(signed) < 1e-3f, $"mover face vertex off-plane by {signed}");
            }
        }
    }

    // ---- (c) load-time repair of a sign-inverted level ---------------------------------------

    [Fact]
    public void Load_Repairs_Sign_Inverted_Mover_Face_Planes()
    {
        var (doc, box) = RoomWithMover();

        // Reproduce the earlier build's defect: invert every mover face-plane offset before saving.
        Brush corrupt = MoverFrom(doc.Rfl, box);
        int flipped = 0;
        foreach (Face f in corrupt.Geometry.Faces)
        {
            f.Plane = new RfPlane(f.Plane.Normal, -f.Plane.Offset);
            flipped++;
        }

        Assert.True(flipped > 0, "test must actually flip some planes to be meaningful");
        byte[] corruptBytes = doc.SaveToBytes();

        // The movers-section parse must heal the inverted planes on load.
        RflFile reloaded = RflFile.Load(corruptBytes);
        reloaded.ParseAllKnownSections();

        Brush healed = MoverFrom(reloaded, box);
        foreach (Face f in healed.Geometry.Faces)
        {
            Vec3 n = f.Plane.Normal;
            foreach (FaceVertex fv in f.Vertices)
            {
                float signed = n.Dot(healed.Geometry.Vertices[fv.Index]) + f.Plane.Offset;
                Assert.True(MathF.Abs(signed) < 1e-3f,
                    $"inverted plane was not repaired on load: off by {signed}");
            }
        }
    }

    // ---- (d) correct planes are left bit-exact ------------------------------------------------

    [Fact]
    public void Correct_Convention_Planes_Round_Trip_Bit_Exact()
    {
        var (doc, box) = RoomWithMover();

        // The correct, authored (RF-convention) offsets before any save/load round-trip.
        float[] authored = MoverFrom(doc.Rfl, box).Geometry.Faces.Select(f => f.Plane.Offset).ToArray();

        byte[] first = doc.SaveToBytes();

        // Load + reparse runs the repair pass over already-correct planes: it must be a no-op.
        RflFile reloaded = RflFile.Load(first);
        reloaded.ParseAllKnownSections();

        // Every mover face offset survives the load-time repair pass bit-exact.
        float[] roundTripped = MoverFrom(reloaded, box).Geometry.Faces.Select(f => f.Plane.Offset).ToArray();
        Assert.Equal(authored, roundTripped);

        // And the reparsed model re-serializes byte-for-byte (the repair changed nothing on disk).
        byte[] second = reloaded.Save(false);
        Assert.Equal(first, second);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Core.Analysis;
using Ged.Core.Assets;
using Ged.Core.Editing;
using Ged.Core.IO.Tex;
using Ged.Core.Model;
using Xunit;
using Vec3d = Ged.Core.Editing.Organic.Vec3d;

namespace Ged.Core.Tests;

/// <summary>
/// Thin structural members (<see cref="UvMembers"/>) and texture grain (<see cref="TextureGrains"/>): a girder or trim
/// texture runs along every beam, post and pilaster (stock levels: 98 % of strip-texture area on thin members, 100 % on
/// vertical ones), never repeats across a narrow face, wraps continuously round the member and reads the right way
/// round; uv_align lays members that way and keeps members already laid that way (library modules' source mapping);
/// walls and grainless textures are aligned exactly as before.
/// </summary>
public sealed class UvMembersTests
{
    private static Brush Box(float w, float h, float d, Vec3 pos, string texture, bool air = false)
    {
        Brush b = BrushFactory.Create(new BrushCreateParams { Shape = BrushShape.Box, Width = w, Height = h, Depth = d, Texture = texture }, 1);
        b.Position = pos;
        b.Rotation = Mat3.Identity;
        if (air)
        {
            b.Flags |= (uint)BrushFlags.Air;
            BrushOrientation.Reverse(b.Geometry);
        }

        return b;
    }

    // girder.tga: a 128 × 64 strip (grain U); post.tga: 64 × 256 (grain V); panel.tga: 256 × 256 without grain;
    // sqgirder.tga: 128 × 128 whose pixels show a vertical grain.
    private static (int W, int H) Size(string t) => t switch
    {
        "girder.tga" => (128, 64),
        "post.tga" => (64, 256),
        "sqgirder.tga" => (128, 128),
        _ => (256, 256),
    };

    private static TextureGrain Measured(string t) => t == "sqgirder.tga" ? TextureGrain.V : TextureGrain.None;

    private static readonly UvAutoAlignOptions Options = new() { TextureSize = Size, MeasuredGrain = Measured };

    private static Uv[] Uvs(Face f) => f.Vertices.Select(v => v.TextureCoords).ToArray();

    private static void BoxMap(Brush b)
    {
        for (int fi = 0; fi < b.Geometry.Faces.Count; fi++)
        {
            (int w, int h) = Size(b.Geometry.Textures[b.Geometry.Faces[fi].Texture]);
            UvOps.BoxMap(b.Geometry, b.Geometry.Faces[fi], b.Rotation, b.Position, 64, w, h);
        }
    }

    private static UvAutoAlignOptions With(UvMemberMode members = UvMemberMode.Auto, bool keepFitted = true) =>
        new() { TextureSize = Size, MeasuredGrain = Measured, Members = members, KeepFitted = keepFitted };

    private static IEnumerable<(UvMembers.MemberFace Side, (bool GrainAlong, double AcrossRepeats, double AlongPx, bool Readable) R)> Readings(Brush b, string texture)
    {
        UvMembers.Member m = UvMembers.Detect(b) ?? throw new InvalidOperationException("not a member");
        (int w, int h) = Size(texture);
        TextureGrain g = TextureGrains.Of(w, h, Measured(texture));
        foreach (UvMembers.MemberFace s in m.Sides)
        {
            yield return (s, UvMembers.Read(s.Poly, s.Normal, Uvs(b.Geometry.Faces[s.FaceIndex]), m.Axis, s.Width, g, w, h)!.Value);
        }
    }

    // ---- grain ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(128, 64, TextureGrain.None, TextureGrain.U)]
    [InlineData(256, 64, TextureGrain.V, TextureGrain.U)]
    [InlineData(64, 256, TextureGrain.None, TextureGrain.V)]
    [InlineData(128, 128, TextureGrain.V, TextureGrain.V)]
    [InlineData(256, 256, TextureGrain.None, TextureGrain.None)]
    [InlineData(256, 128, TextureGrain.None, TextureGrain.U)]
    [InlineData(96, 128, TextureGrain.U, TextureGrain.U)]
    [InlineData(256, 192, TextureGrain.U, TextureGrain.None)] // a 256 px panel stays upright whatever its pixels show
    [InlineData(0, 0, TextureGrain.U, TextureGrain.None)]
    public void Strips_Run_Along_Their_Long_Side_Else_What_Their_Pixels_Show(int w, int h, TextureGrain measured, TextureGrain expected) =>
        Assert.Equal(expected, TextureGrains.Of(w, h, measured));

    private static TextureImage Image(int w, int h, Func<int, int, byte> lum)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int k = ((y * w) + x) * 4;
                byte v = lum(x, y);
                px[k] = px[k + 1] = px[k + 2] = v;
                px[k + 3] = 255;
            }
        }

        return new TextureImage(w, h, px);
    }

    [Fact]
    public void Pixels_Show_Horizontal_Bands_As_Grain_U_Vertical_Rails_As_V_And_A_Checker_As_None()
    {
        Assert.Equal(TextureGrain.U, TextureGrains.Measure(Image(64, 64, (x, y) => (byte)(y % 16 < 4 ? 220 : 60))));
        Assert.Equal(TextureGrain.V, TextureGrains.Measure(Image(64, 64, (x, y) => (byte)(x % 32 < 6 ? 200 : 90))));
        Assert.Equal(TextureGrain.None, TextureGrains.Measure(Image(64, 64, (x, y) => (byte)(((x / 8) + (y / 8)) % 2 == 0 ? 200 : 50))));
        Assert.Equal(TextureGrain.None, TextureGrains.Measure(Image(64, 64, (x, y) => (byte)(((x + y) % 16) < 8 ? 200 : 50)))); // diagonal stripes
        Assert.Equal(TextureGrain.None, TextureGrains.Measure(Image(32, 32, (x, y) => 128)));
    }

    [Fact]
    public void Stock_Girder_Post_And_Panel_Textures_Have_The_Grain_Stock_Mappers_Lay_Along_Members()
    {
        if (TestPaths.RfInstall is not { } install)
        {
            return; // no RF install: skipped
        }

        using AssetVfs vfs = GameMount.Mount(install);
        var sizes = new TextureSizeLookup(vfs);

        // Strips by proportion, and the square girder and support textures stock levels lay V along their posts and
        // beams (girder07 / girder08 / merc_support02: 100 % V along on dm10, ctf01, dm16); panels and hazard stripes none.
        var expected = new Dictionary<string, TextureGrain>
        {
            ["mtl_girder06.tga"] = TextureGrain.U, ["mtl_chromegirder02.tga"] = TextureGrain.U, ["mtl_techtrim01.tga"] = TextureGrain.U,
            ["sld_woodpost01.tga"] = TextureGrain.V, ["mtl_rustygrdr01.tga"] = TextureGrain.V, ["mtl_doortrim.tga"] = TextureGrain.V,
            ["mtl_girder07.tga"] = TextureGrain.V, ["mtl_girder08.tga"] = TextureGrain.V, ["mtl_merc_support02.tga"] = TextureGrain.V,
            ["mtl_supportbeam02.tga"] = TextureGrain.V, ["mtl_merc_support01.tga"] = TextureGrain.V,
            ["mtl_hazardstripe01.tga"] = TextureGrain.None, ["cem_greypanel01.tga"] = TextureGrain.None, ["mtl_cleanpanel01.tga"] = TextureGrain.None,
            ["mtl_girder05.tga"] = TextureGrain.None,
        };
        foreach ((string name, TextureGrain g) in expected)
        {
            Assert.True(sizes.GetGrain(name) is not null, name + " does not resolve");
            Assert.True(g == sizes.GetGrain(name), $"{name}: expected {g}, got {sizes.GetGrain(name)}");
        }
    }

    // ---- detection -----------------------------------------------------------------------------------------------

    [Fact]
    public void Posts_Beams_And_Bands_Are_Members_Walls_Slabs_Stubs_And_Air_Are_Not()
    {
        UvMembers.Member post = UvMembers.Detect(Box(0.4f, 6, 0.3f, new Vec3(0, 3, 0), "girder.tga"))!;
        Assert.NotNull(post);
        Assert.Equal(1, post.Axis.Y, 6);
        Assert.Equal(6, post.Length, 4);
        Assert.Equal(0.4, post.Width, 4);
        Assert.Equal(4, post.Sides.Count);

        UvMembers.Member beam = UvMembers.Detect(Box(0.35f, 0.45f, 9, new Vec3(0, 5, 0), "girder.tga"))!;
        Assert.Equal(1, beam.Axis.Z, 6);
        Assert.NotNull(UvMembers.Detect(Box(10, 0.75f, 0.1f, new Vec3(0, 0.4f, 0), "girder.tga"))); // a base band

        Assert.Null(UvMembers.Detect(Box(0.25f, 3, 12, new Vec3(0, 1.5f, 0), "girder.tga"))); // a thin wall: 3 m wide
        Assert.Null(UvMembers.Detect(Box(4, 0.5f, 4, new Vec3(0, 0, 0), "girder.tga"))); // a slab
        Assert.Null(UvMembers.Detect(Box(0.5f, 1.2f, 0.5f, new Vec3(0, 0, 0), "girder.tga"))); // a stub
        Assert.Null(UvMembers.Detect(Box(0.4f, 6, 0.3f, new Vec3(0, 3, 0), "girder.tga", air: true)));
    }

    [Fact]
    public void A_Rotated_Diagonal_Brace_Is_A_Member_Along_Its_Own_Axis()
    {
        Brush b = Box(0.3f, 0.3f, 8, new Vec3(0, 4, 0), "girder.tga");
        b.Rotation = Mat3Math.Compose(Mat3Math.RotationY(0.5f), Mat3Math.RotationX(0.7f));
        UvMembers.Member m = UvMembers.Detect(b)!;
        Assert.NotNull(m);
        Vec3 fwd = b.Rotation.Transform(new Vec3(0, 0, 1));
        Assert.Equal(1, Math.Abs((m.Axis.X * fwd.X) + (m.Axis.Y * fwd.Y) + (m.Axis.Z * fwd.Z)), 4);
        Assert.Equal(8, m.Length, 3);
    }

    // ---- the mapping ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_Box_Mapped_Girder_Post_Stacks_The_Girder_Across_It_And_Alignment_Turns_It_Up_The_Post()
    {
        Brush post = Box(0.5f, 6, 0.25f, new Vec3(0, 3, 0), "girder.tga");
        BoxMap(post);

        // Box mapping: U across the post, the 64 px girder repeating every metre up it (the zebra post).
        Assert.All(Readings(post, "girder.tga"), x => Assert.False(x.R.GrainAlong));

        UvAutoAlignStats st = UvAutoAlign.Apply(new[] { post }, Options);
        Assert.Equal(4, st.MemberFaces);
        foreach (var (side, r) in Readings(post, "girder.tga"))
        {
            Assert.True(r.GrainAlong, $"face {side.FaceIndex}");
            Assert.True(r.Readable, $"face {side.FaceIndex}");
            Assert.InRange(r.AcrossRepeats, 0, 1.0001);
        }

        // The 0.5 m front shows half the 64 px girder at 64 px/m, so one repeat is fitted across it exactly (128 px/m).
        var front = Readings(post, "girder.tga").First(x => Math.Abs(x.Side.Width - 0.5) < 1e-6);
        Assert.Equal(1, front.R.AcrossRepeats, 3);
        Assert.Equal(128, front.R.AlongPx, 1);
    }

    [Fact]
    public void Narrow_Members_Keep_The_Stock_Density_And_Show_A_Slice_Of_The_Texture()
    {
        Brush rail = Box(0.12f, 0.12f, 6, new Vec3(0, 1, 0), "girder.tga");
        UvAutoAlign.Apply(new[] { rail }, Options);
        foreach (var (_, r) in Readings(rail, "girder.tga"))
        {
            Assert.True(r.GrainAlong);
            Assert.Equal(64, r.AlongPx, 1);
            Assert.Equal(0.12 * 64 / 64, r.AcrossRepeats, 3);
        }
    }

    [Fact]
    public void A_Wide_Column_Never_Shows_The_Texture_Repeating_Across_It()
    {
        Brush col = Box(1.2f, 9, 1.2f, new Vec3(0, 4.5f, 0), "girder.tga");
        UvAutoAlign.Apply(new[] { col }, Options);
        Assert.All(Readings(col, "girder.tga"), x =>
        {
            Assert.True(x.R.GrainAlong);
            Assert.Equal(1, x.R.AcrossRepeats, 3); // 1.2 m would show 1.2 repeats at 64 px/m: fitted to one
        });
    }

    [Fact]
    public void Grain_V_Textures_Stand_Upright_On_Posts_And_Turn_Along_Beams()
    {
        Brush post = Box(0.4f, 5, 0.4f, new Vec3(0, 2.5f, 0), "post.tga");
        Brush beam = Box(0.4f, 0.5f, 8, new Vec3(3, 5, 0), "post.tga");
        Brush sq = Box(0.3f, 0.3f, 7, new Vec3(-3, 5, 0), "sqgirder.tga");
        UvAutoAlign.Apply(new[] { post, beam, sq }, Options);
        Assert.All(Readings(post, "post.tga"), x => Assert.True(x.R.GrainAlong && x.R.Readable && x.R.AcrossRepeats <= 1.0001));
        Assert.All(Readings(beam, "post.tga"), x => Assert.True(x.R.GrainAlong && x.R.Readable && x.R.AcrossRepeats <= 1.0001));
        Assert.All(Readings(sq, "sqgirder.tga"), x => Assert.True(x.R.GrainAlong && x.R.Readable && x.R.AcrossRepeats <= 1.0001));

        // Upright on the post: V grows downward.
        foreach (UvMembers.MemberFace s in UvMembers.Detect(post)!.Sides)
        {
            (Vec3d gu, Vec3d gv) = UvQuality.Gradients(s.Poly, Uvs(post.Geometry.Faces[s.FaceIndex]), s.Normal)!.Value;
            Assert.True(gv.Y < 0, "V must point down the post");
            Assert.Equal(0, gu.Y, 6);
        }
    }

    [Fact]
    public void The_Texture_Wraps_Round_The_Member_Without_A_Jump_And_Runs_On_Along_Collinear_Pieces()
    {
        Brush a = Box(0.5f, 0.3f, 4, new Vec3(0, 3, 2), "girder.tga");
        Brush b = Box(0.5f, 0.3f, 4, new Vec3(0, 3, 6), "girder.tga");
        UvAutoAlign.Apply(new[] { a, b }, Options);
        List<UvQualityFace> faces = UvQuality.FacesOf(new[] { a, b }, Array.Empty<string>());
        JsonObject rep = UvQuality.Measure(faces.Where(f => Math.Abs(f.Normal.Z) < 0.5).ToList(), t => Size(t));

        // Every long edge of each member (four corners) meets its neighbour with the texture's features continuous.
        Assert.Equal(1.0, rep["seams"]!["fold"]!["aligned"]?.GetValue<double>() ?? rep["seams"]!["wall_corner"]!["aligned"]!.GetValue<double>(), 3);

        // Along: the two collinear pieces continue (world-anchored U along the axis).
        Assert.Equal(1.0, rep["seams"]!["coplanar"]!["aligned"]?.GetValue<double>() ?? 1.0, 3);
    }

    [Fact]
    public void A_Member_Already_Laid_Like_A_Mapper_Lays_It_Is_Kept_As_It_Is()
    {
        // A library pilaster as its source level had it: the girder turned up the post, its front fitted across.
        Brush post = Box(0.5f, 6, 0.25f, new Vec3(0, 3, 0), "girder.tga");
        UvAutoAlign.Apply(new[] { post }, Options);
        foreach (Face f in post.Geometry.Faces)
        {
            UvOps.Offset(f, 0.37f, 0.11f);
        }

        Uv[][] before = post.Geometry.Faces.Select(Uvs).ToArray();
        UvAutoAlignStats st = UvAutoAlign.Apply(new[] { post }, With(keepFitted: false));
        Assert.Equal(1, st.MembersKept);
        Assert.Equal(0, st.MemberFaces);
        foreach (int fi in UvMembers.Detect(post)!.Sides.Select(s => s.FaceIndex))
        {
            Assert.Equal(before[fi], Uvs(post.Geometry.Faces[fi]));
        }
    }

    [Fact]
    public void Walls_And_Grainless_Members_Align_Exactly_As_Without_Member_Mapping()
    {
        Brush[] Make() => new[]
        {
            Box(12, 6, 12, new Vec3(0, 3, 0), "panel.tga", air: true),
            Box(0.4f, 6, 0.4f, new Vec3(2, 3, 2), "panel.tga"),
            Box(0.4f, 0.5f, 10, new Vec3(-2, 5.5f, 0), "panel.tga"),
        };
        Brush[] on = Make(), off = Make();
        foreach (Brush b in on.Concat(off))
        {
            BoxMap(b);
        }

        UvAutoAlign.Apply(on, Options);
        UvAutoAlign.Apply(off, With(UvMemberMode.Off));
        for (int i = 0; i < on.Length; i++)
        {
            for (int fi = 0; fi < on[i].Geometry.Faces.Count; fi++)
            {
                Assert.Equal(Uvs(off[i].Geometry.Faces[fi]), Uvs(on[i].Geometry.Faces[fi]));
            }
        }
    }

    [Fact]
    public void Members_Only_Leaves_Every_Other_Face_Untouched()
    {
        Brush room = Box(12, 6, 12, new Vec3(0, 3, 0), "girder.tga", air: true);
        Brush post = Box(0.4f, 6, 0.4f, new Vec3(2, 3, 2), "girder.tga");
        BoxMap(room);
        BoxMap(post);
        Uv[][] roomBefore = room.Geometry.Faces.Select(Uvs).ToArray();
        Uv[][] postBefore = post.Geometry.Faces.Select(Uvs).ToArray();
        UvAutoAlign.Apply(new[] { room, post }, With(UvMemberMode.Only));
        for (int fi = 0; fi < room.Geometry.Faces.Count; fi++)
        {
            Assert.Equal(roomBefore[fi], Uvs(room.Geometry.Faces[fi]));
        }

        var sides = UvMembers.Detect(post)!.Sides.Select(s => s.FaceIndex).ToHashSet();
        for (int fi = 0; fi < post.Geometry.Faces.Count; fi++)
        {
            if (sides.Contains(fi))
            {
                Assert.NotEqual(postBefore[fi], Uvs(post.Geometry.Faces[fi]));
            }
            else
            {
                Assert.Equal(postBefore[fi], Uvs(post.Geometry.Faces[fi])); // the end caps
            }
        }
    }

    // ---- the measurement -----------------------------------------------------------------------------------------

    [Fact]
    public void The_Report_Tells_Zebra_Posts_From_Posts_Laid_Like_A_Mapper()
    {
        Brush post = Box(0.5f, 6, 0.25f, new Vec3(0, 3, 0), "girder.tga");
        BoxMap(post);
        var issues = new List<UvQualityIssue>();
        JsonObject bad = UvMembers.Measure(new[] { post }, t => Size(t), Measured, issues)!;
        Assert.Equal(1, bad["members"]!.GetValue<int>());
        Assert.Equal(0, bad["grain_along"]!.GetValue<double>(), 3);
        Assert.Equal(0, bad["mapped"]!.GetValue<double>(), 3);
        Assert.Contains(issues, i => i.Kind == "member_grain");

        UvAutoAlign.Apply(new[] { post }, Options);
        JsonObject good = UvMembers.Measure(new[] { post }, t => Size(t), Measured, null)!;
        Assert.Equal(1, good["grain_along"]!.GetValue<double>(), 3);
        Assert.Equal(1, good["no_repeats_across"]!.GetValue<double>(), 3);
        Assert.Equal(1, good["mapped"]!.GetValue<double>(), 3);

        // Nothing grained on a member: no reading.
        Assert.Null(UvMembers.Measure(new[] { Box(0.5f, 6, 0.25f, new Vec3(0, 3, 0), "panel.tga") }, t => Size(t), Measured, null));
    }
}

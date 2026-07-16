using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// DEFECT 1 GATE — the in-game geomod cap triangulator (Alpine <c>ear_clip_triangulate</c>, reimplemented
/// in <see cref="CapFaceEarClip"/>) must COMPLETE on every geoable/breakable output face GED builds, with
/// no repeated-vertex faces, exactly as it does on RED's own compiled geometry. Goober saw
/// "[CapFace] Ear clip stuck: remaining=10 of 18" digging GED-built dmabrupt; the cause was authored /
/// near-coincident duplicate corners surviving into the compiled detail faces (RED strips them in
/// BuildFinalRenderSolid, GED did not). <see cref="OutputFaceCleanup"/> restores RED's clean output.
/// </summary>
[Trait("Category", "DeepGate")] // heavy corpus compile/bake; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
public sealed class EarClipCapGateTests
{
    private readonly ITestOutputHelper _out;

    public EarClipCapGateTests(ITestOutputHelper output) => _out = output;

    /// <summary>dmabrupt's geoable/breakable rooms — the dug surfaces — must ear-clip with ZERO stalls
    /// and ZERO repeated vertices, matching RED's baseline (also asserted 0/0 so the oracle is honest).</summary>
    [Fact]
    public void Dmabrupt_Geoable_Breakable_Faces_EarClip_Like_Red()
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, "dmabruptdecayrc2a27.rfl");
        if (!File.Exists(path))
        {
            return;
        }

        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        Geometry red = rfl.Sections.Select(s => s.Content).OfType<GeometrySection>().First().Geometry;
        AlpineLevelPropertiesSection alp =
            rfl.Sections.Select(s => s.Content).OfType<AlpineLevelPropertiesSection>().First();

        CompiledLevel result = GeometryBuildService.Build(
            rfl, new CompileOptions { Alpine = true, BuildSurfaces = false });
        Geometry ged = result.Geometry;

        HashSet<int> redRooms = EarClipCapDiag.GeoableRoomIndices(red, alp, uidToRoomUid: null);
        HashSet<int> gedRooms = EarClipCapDiag.GeoableRoomIndices(ged, alp, result.BrushRoomUid);

        (int redStuck, int redRepeated, _) = Probe(red, redRooms);
        (int gedStuck, int gedRepeated, int gedFaces) = Probe(ged, gedRooms);

        _out.WriteLine($"RED geo: stuck={redStuck} repeated={redRepeated}");
        _out.WriteLine($"GED geo ({gedFaces} faces): stuck={gedStuck} repeated={gedRepeated}");

        Assert.Equal(0, redStuck);
        Assert.Equal(0, redRepeated);
        Assert.True(gedStuck == 0,
            $"{gedStuck} GED geoable/breakable faces stall the in-game ear clip (RED: {redStuck}) — geomod would leave them uncapped");
        Assert.True(gedRepeated == 0,
            $"{gedRepeated} GED geoable/breakable faces carry a repeated vertex (RED: {redRepeated})");
    }

    /// <summary>Corpus-wide no-regression: for each level, GED's DETAIL faces must ear-clip no worse than
    /// RED's (same or fewer stalls and repeats). RED leaves some world faces self-touching, so the invariant
    /// is "no worse than RED", pinned on the geomod-relevant detail set.</summary>
    [Theory]
    [InlineData("dmabruptdecayrc2a27.rfl")]
    [InlineData("glass_house.rfl")]
    [InlineData("dm04.rfl")]
    [InlineData("ctf01.rfl")]
    public void Corpus_Detail_Faces_EarClip_No_Worse_Than_Red(string fileName)
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, fileName);
        if (!File.Exists(path))
        {
            return;
        }

        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        Geometry? red = rfl.Sections.Select(s => s.Content).OfType<GeometrySection>().FirstOrDefault()?.Geometry;
        if (red is null)
        {
            return;
        }

        bool hasAlpine = rfl.Sections.Select(s => s.Content).OfType<AlpineLevelPropertiesSection>().Any();
        Geometry ged = GeometryBuildService
            .Build(rfl, new CompileOptions { Alpine = hasAlpine, BuildSurfaces = false }).Geometry;

        (int redStuck, int redRepeated, int redFaces) = ProbeDetail(red);
        (int gedStuck, int gedRepeated, int gedFaces) = ProbeDetail(ged);
        _out.WriteLine($"{fileName}: RED detail faces={redFaces} stuck={redStuck} repeated={redRepeated}; " +
            $"GED detail faces={gedFaces} stuck={gedStuck} repeated={gedRepeated}");

        Assert.True(gedStuck <= redStuck,
            $"{fileName}: GED detail-face ear-clip stalls {gedStuck} exceed RED's {redStuck}");
        Assert.True(gedRepeated <= redRepeated,
            $"{fileName}: GED detail-face repeated-vertex faces {gedRepeated} exceed RED's {redRepeated}");
    }

    private static (int Stuck, int Repeated, int Faces) Probe(Geometry g, HashSet<int> rooms)
    {
        int stuck = 0, repeated = 0, faces = 0;
        foreach (Face f in g.Faces)
        {
            if (f.IsPortalFace || f.Vertices.Count < 3 || !rooms.Contains(f.RoomIndex))
            {
                continue;
            }

            faces++;
            CapFaceEarClip.Probe p = CapFaceEarClip.ProbeLoop(CapFaceEarClip.LoopOf(g, f));
            if (p.Outcome == CapFaceEarClip.Outcome.Stuck)
            {
                stuck++;
            }

            if (p.RepeatedVertices > 0)
            {
                repeated++;
            }
        }

        return (stuck, repeated, faces);
    }

    private static (int Stuck, int Repeated, int Faces) ProbeDetail(Geometry g)
    {
        int stuck = 0, repeated = 0, faces = 0;
        foreach (Face f in g.Faces)
        {
            if (f.IsPortalFace || f.Vertices.Count < 3 || (f.Flags & (ushort)FaceFlags.IsDetail) == 0)
            {
                continue;
            }

            faces++;
            CapFaceEarClip.Probe p = CapFaceEarClip.ProbeLoop(CapFaceEarClip.LoopOf(g, f));
            if (p.Outcome == CapFaceEarClip.Outcome.Stuck)
            {
                stuck++;
            }

            if (p.RepeatedVertices > 0)
            {
                repeated++;
            }
        }

        return (stuck, repeated, faces);
    }
}

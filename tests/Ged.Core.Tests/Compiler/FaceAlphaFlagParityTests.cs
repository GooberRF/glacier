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
/// Flagship 25 — DEFECT 1 (sign faces render transparent). RED honors a texture's alpha channel
/// only on DETAIL faces: the compile-time trait pass (RED.exe FlagFaceTextureTraits FUN_0041d3c0)
/// gates the has_alpha (0x40) and has_holes (0x80) setters behind the detail bit
/// (<c>if ((flags &gt;&gt; 3) &amp; 1)</c>). On a structural (non-detail) brush the alpha channel is
/// ignored and the face draws opaque. GED used to set 0x40/0x80 from texture content on every face,
/// so a 32-bit sign TGA on a normal wall drew fully transparent in-game. This gate pins RED's rule.
/// </summary>
[Trait("Category", "DeepGate")] // heavy corpus compile/bake; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
public sealed class FaceAlphaFlagParityTests
{
    private readonly ITestOutputHelper _out;

    public FaceAlphaFlagParityTests(ITestOutputHelper output) => _out = output;

    private const ushort Detail = 0x0008;
    private const ushort HasAlpha = 0x0040;
    private const ushort HasHoles = 0x0080;

    // The three sign textures Goober reported as transparent in GED-built dmabrupt.
    private static readonly string[] SignTextures =
    {
        "mtl_gbrapc01dirty.tga", "mtl_gbrUltorProp001.tga", "mtl_gbrtheplace01.tga",
    };

    /// <summary>Ground truth: no RED original carries the alpha/holes bits on a non-detail face.</summary>
    [Theory]
    [MemberData(nameof(Corpus.RflFileNames), MemberType = typeof(Corpus))]
    public void Red_Originals_Never_Alpha_Flag_Non_Detail_Faces(string? file)
    {
        // The corpus also holds GED rebuilds / backups / autosaves (which may carry the pre-fix
        // bug); the ground-truth premise only applies to shipped RED-authored originals.
        if (file is null || !Corpus.Available || !IsRedOriginal(file))
        {
            return;
        }

        Geometry? red = LoadGeometry(Path.Combine(Corpus.Directory!, file));
        if (red is null)
        {
            return;
        }

        int nonDetailAlpha = red.Faces.Count(f => (f.Flags & Detail) == 0 && (f.Flags & HasAlpha) != 0);
        int nonDetailHoles = red.Faces.Count(f => (f.Flags & Detail) == 0 && (f.Flags & HasHoles) != 0);
        Assert.True(nonDetailAlpha == 0,
            $"{file}: RED has {nonDetailAlpha} non-detail faces with 0x40 — the detail-gating premise is wrong for this level");
        Assert.True(nonDetailHoles == 0, $"{file}: RED has {nonDetailHoles} non-detail faces with 0x80");
    }

    /// <summary>
    /// GED must reproduce that rule: even when EVERY texture reports an alpha channel, only detail
    /// faces get 0x40/0x80 after a compile. Forcing all textures alpha exercises the gate on the
    /// exact sign faces (their textures do have alpha) without needing a mounted texture VFS.
    /// </summary>
    [Theory]
    [MemberData(nameof(Corpus.RflFileNames), MemberType = typeof(Corpus))]
    public void Ged_Honors_Texture_Alpha_Only_On_Detail_Faces(string? file)
    {
        if (file is null || !Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, file);
        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        List<Brush> brushes = MoverBrushes.StaticWorldBrushes(rfl);
        if (brushes.Count == 0)
        {
            return;
        }

        List<RoomEffect> effects = rfl.Sections.Select(s => s.Content).OfType<RoomEffectsSection>()
            .FirstOrDefault()?.Effects ?? new List<RoomEffect>();

        var options = new CompileOptions
        {
            Alpine = rfl.Context.IsAlpine,
            BuildSurfaces = false,
            TextureTraits = _ => new TextureTraits(false, true, true), // every texture "has alpha"
        };

        Geometry ged = GeometryCompiler.Compile(brushes, effects, options).Geometry;

        int nonDetailAlpha = ged.Faces.Count(f => (f.Flags & Detail) == 0 && (f.Flags & HasAlpha) != 0);
        int nonDetailHoles = ged.Faces.Count(f => (f.Flags & Detail) == 0 && (f.Flags & HasHoles) != 0);
        _out.WriteLine($"{file}: non-detail 0x40={nonDetailAlpha} 0x80={nonDetailHoles} " +
                       $"(detail-alpha faces={ged.Faces.Count(f => (f.Flags & Detail) != 0 && (f.Flags & HasAlpha) != 0)})");

        Assert.True(nonDetailAlpha == 0, $"{file}: GED set 0x40 on {nonDetailAlpha} non-detail faces (defect 1)");
        Assert.True(nonDetailHoles == 0, $"{file}: GED set 0x80 on {nonDetailHoles} non-detail faces (defect 1)");
    }

    /// <summary>The three reported sign faces on dmabrupt draw opaque (no 0x40) in a GED build.</summary>
    [Fact]
    public void Reported_Sign_Faces_Are_Opaque_In_Ged()
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
        List<Brush> brushes = MoverBrushes.StaticWorldBrushes(rfl);
        List<RoomEffect> effects = rfl.Sections.Select(s => s.Content).OfType<RoomEffectsSection>()
            .FirstOrDefault()?.Effects ?? new List<RoomEffect>();

        var options = new CompileOptions
        {
            Alpine = true,
            BuildSurfaces = false,
            TextureTraits = _ => new TextureTraits(false, true, true),
        };

        Geometry ged = GeometryCompiler.Compile(brushes, effects, options).Geometry;

        int checkedSigns = 0;
        foreach (string tex in SignTextures)
        {
            int idx = ged.Textures.FindIndex(t => string.Equals(t, tex, System.StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
            {
                continue;
            }

            foreach (Face f in ged.Faces.Where(f => f.Texture == idx))
            {
                checkedSigns++;
                Assert.True((f.Flags & Detail) == 0, $"{tex}: expected a structural (non-detail) face");
                Assert.True((f.Flags & HasAlpha) == 0, $"{tex}: sign face still carries 0x40 (would render transparent)");
                Assert.True((f.Flags & HasHoles) == 0, $"{tex}: sign face still carries 0x80");
            }
        }

        Assert.True(checkedSigns > 0, "none of the reported sign textures were found in the compiled geometry");
    }

    /// <summary>A shipped RED-authored original (not a GED rebuild, editor backup, or autosave).</summary>
    private static bool IsRedOriginal(string file)
    {
        string n = file.ToLowerInvariant();
        return !n.StartsWith("ged") && !n.Contains('~') && !n.Contains(".autosave");
    }

    private static Geometry? LoadGeometry(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        return rfl.Sections.Select(s => s.Content).OfType<GeometrySection>().FirstOrDefault()?.Geometry;
    }
}

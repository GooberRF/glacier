using System.Runtime.InteropServices;
using Ged.Rendering.Graphics;
using Ged.Rendering.Rhi;
using Ged.Rendering.Rhi.Gl;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Rendering.Tests;

/// <summary>
/// The std140 layout-assert gate: the two shader constant buffers are uploaded as
/// raw <c>System.Numerics</c> struct bytes to BOTH the D3D11 cbuffers (b0/b1) and
/// the GL std140 UBOs (binding 0/1), so the field offsets must be identical or the
/// backends read different bytes. This test pins the C# struct offsets/sizes to the
/// documented std140 layout (always runs, no GPU), and — when an OpenGL 3.3 device
/// is present — cross-checks that the GL driver assigns the SAME std140 offsets to
/// the real shader blocks.
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class UboLayoutParityTests
{
    private readonly ITestOutputHelper _out;

    public UboLayoutParityTests(ITestOutputHelper output) => _out = output;

    // Expected std140 offsets (== D3D11 cbuffer offsets == uploaded struct offsets).
    private static readonly (string Member, int Offset)[] FrameLayout =
    {
        ("ViewProj", 0),
        ("CameraRight", 64),
        ("CameraUp", 80),
        ("CameraPos", 96),
        ("Params", 112),
        ("FogColor", 128),
        ("FogParams", 144),
    };

    private static readonly (string Member, int Offset)[] DrawLayout =
    {
        ("World", 0),
        ("Tint", 64),
        ("PickId", 80),
        ("HasLightmap", 84),
        ("Scroll", 88),
    };

    [Fact]
    public void CSharpStructOffsets_MatchStd140()
    {
        Assert.Equal(160, Marshal.SizeOf<FrameConstants>());
        Assert.Equal(96, Marshal.SizeOf<DrawConstants>());

        foreach ((string member, int offset) in FrameLayout)
        {
            Assert.Equal(offset, (int)Marshal.OffsetOf<FrameConstants>(member));
        }

        foreach ((string member, int offset) in DrawLayout)
        {
            Assert.Equal(offset, (int)Marshal.OffsetOf<DrawConstants>(member));
        }
    }

    [Fact]
    public void GlDriverStd140Offsets_MatchExpected()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(GraphicsBackend.OpenGl, out string reason);
        if (gd is null)
        {
            _out.WriteLine($"OpenGL device unavailable, skipping GL std140 cross-check: {reason}");
            return;
        }

        var glDevice = (GlRenderDevice)gd.Rhi;

        // Query each block member across every real program/stage; the std140 offset is
        // deterministic, so the first stage that keeps a member active gives the answer.
        (IShaderProgram Program, bool Pick)[] stages =
        {
            (gd.Programs.World, false), (gd.Programs.World, true),
            (gd.Programs.Mesh, false), (gd.Programs.Mesh, true),
            (gd.Programs.Billboard, false), (gd.Programs.Billboard, true),
            (gd.Programs.Line, false),
        };

        AssertBlock(glDevice, stages, FrameLayout, "FrameConstants");
        AssertBlock(glDevice, stages, DrawLayout, "DrawConstants");
    }

    private void AssertBlock(
        GlRenderDevice device,
        (IShaderProgram Program, bool Pick)[] stages,
        (string Member, int Offset)[] layout,
        string blockName)
    {
        foreach ((string member, int expected) in layout)
        {
            int found = -1;
            foreach ((IShaderProgram program, bool pick) in stages)
            {
                if (device.TryGetUniformOffset(program, pick, member, out int offset))
                {
                    found = offset;
                    break;
                }
            }

            Assert.True(found >= 0, $"{blockName}.{member} was not active in any program (cannot verify offset)");
            _out.WriteLine($"{blockName}.{member}: GL offset {found}, expected {expected}");
            Assert.Equal(expected, found);
        }
    }
}

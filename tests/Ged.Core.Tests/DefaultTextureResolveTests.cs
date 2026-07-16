using Ged.Core.Assets;
using Ged.Core.Editing;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// The default brush texture must actually RESOLVE through the VFS
/// on a real install — otherwise every newly-created brush renders white. This is the
/// resolution half of the rendered-level texture regression (the render half lives in
/// Ged.Rendering.Tests.RenderInvalidationTests).
/// </summary>
public sealed class DefaultTextureResolveTests
{
    [Fact]
    public void Default_Brush_Texture_Resolves_In_The_Install()
    {
        if (!TestPaths.HasRfInstall)
        {
            return;
        }

        using AssetVfs vfs = GameMount.Mount(TestPaths.RfInstall!);
        Assert.NotNull(vfs.ResolveTexture(BrushCreateParams.DefaultTexture));
        Assert.NotNull(vfs.LoadTexture(BrushCreateParams.DefaultTexture));
    }
}

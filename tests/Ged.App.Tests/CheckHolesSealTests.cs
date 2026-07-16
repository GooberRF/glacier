using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Ged.App;
using Ged.Core.Editing;
using Ged.Core.Model;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Regression for the "Check for Holes reports THOUSANDS of leaks" hotfix.
///
/// The live-CSG / merged-stash PREVIEW build (<c>BuildAsync(interactive: false)</c>) applies
/// UNSEALED geometry to the document — it skips the t-joint seal (FixTJoints), so on a real level
/// it carries thousands of open t-joint edges. Check-for-Holes (and Save) must never treat that
/// preview geometry as authoritative: they re-seal it with a full interactive build first. These
/// tests pin the <see cref="GeometryBuildController.GeometryIsPreview"/> quality flag and the
/// Check-for-Holes re-seal path.
/// </summary>
public sealed class CheckHolesSealTests
{
    private static GeometryBuildController BoxControllerWith(out EditorSession session)
    {
        session = new EditorSession();
        session.NewLevel();
        var controller = new GeometryBuildController(session, _ => { }, () => { }, (_, _) => { });
        controller.Attach();
        session.BrushEditor!.CreateBrush(
            new BrushCreateParams { Shape = BrushShape.Box, Width = 4f, Height = 4f, Depth = 4f },
            default, Mat3.Identity);
        return controller;
    }

    [AvaloniaFact]
    public async Task Preview_Build_Marks_Geometry_As_Preview_Quality()
    {
        GeometryBuildController controller = BoxControllerWith(out _);

        await controller.BuildAsync(interactive: false);
        Assert.True(controller.GeometryIsPreview, "a preview (interactive == false) build is unsealed");

        await controller.BuildAsync(); // interactive == true → sealed
        Assert.False(controller.GeometryIsPreview, "an interactive build seals the geometry");
    }

    [AvaloniaFact]
    public async Task CheckHoles_ReSeals_Preview_Geometry_Before_Reporting()
    {
        GeometryBuildController controller = BoxControllerWith(out _);

        // Leave preview-quality (unsealed) geometry in the document, as the merged-stash / live-CSG
        // preview does. On dmabrupt this geometry reports ~13k false "holes".
        await controller.BuildAsync(interactive: false);
        Assert.True(controller.GeometryIsPreview);

        // Check-for-Holes must force a full interactive (sealed) build first, so the reported count
        // is the real residual — not the preview t-joint explosion.
        await controller.CheckHolesAsync();
        Assert.False(controller.GeometryIsPreview);
        Assert.False(controller.GeometryDirty);
    }
}

using System.Collections.Generic;
using Avalonia.Headless.XUnit;
using Ged.App;
using Ged.Core.Editing;
using Ged.Core.Model;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Defect 2 — after a prefab placement / propagation (which deletes + re-imports member brushes
/// DIRECTLY, bypassing <see cref="BrushEditor"/> and its BrushesChanged invalidation), the compiled
/// geometry must be invalidated exactly like a structural brush edit: geometry marked dirty and the
/// merged-brush stash / fragment overlay cleared wholesale, so the live-CSG preview cannot show stale
/// geometry.
/// </summary>
public sealed class PrefabPropagationInvalidationTests
{
    [AvaloniaFact]
    public void InvalidateBrushGeometry_Marks_Dirty_And_Clears_The_Merged_Brush_Stash()
    {
        var session = new EditorSession();
        session.NewLevel();
        var controller = new GeometryBuildController(session, _ => { }, () => { }, (_, _) => { });
        controller.Attach();

        BrushEditor be = session.BrushEditor!;
        be.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2f, Height = 2f, Depth = 2f }, new Vec3(0, 0, 0), Mat3.Identity);

        // Simulate the state right after a build: a populated merged-brush stash + fragments.
        session.BrushFaceSurvival = new Dictionary<int, bool[]>();
        session.BrushFragments = BrushFragmentIndex.Build(new Geometry(), new Dictionary<int, int>(), new Dictionary<int, bool[]>());
        session.StaleFragmentBrushUids.Add(7);

        // The post-propagation invalidation (what MainWindow calls after Propagate / Place).
        controller.InvalidateBrushGeometry();

        Assert.True(controller.GeometryDirty);
        Assert.Null(session.BrushFragments);
        Assert.Null(session.BrushFaceSurvival);
        Assert.Empty(session.StaleFragmentBrushUids);
    }
}

using Ged.App;
using Ged.Core.Editing;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Owner decision: the shared brush-type template (the Brush panel's "Air (else Solid)"
/// checkbox and the Draw Brush tool both create from it) must default to Air on every
/// application launch. The template is a fresh instance seeded from
/// <see cref="MainWindow.NewDefaultBrushParams"/> and is never persisted to settings.cfg,
/// so each launch resets to Air; in-session the checkbox toggles it as before. These tests
/// pin the launch default so a future edit can't silently flip it back to Solid.
/// </summary>
public sealed class DrawBrushLaunchDefaultTests
{
    [Fact]
    public void Launch_Default_Brush_Type_Is_Air()
    {
        BrushCreateParams p = MainWindow.NewDefaultBrushParams();

        Assert.True(p.Air, "the brush-type launch default (Draw Brush + Brush panel) must be Air");
    }

    [Fact]
    public void Core_Default_Stays_Solid_So_Only_The_App_Launch_Default_Is_Air()
    {
        // The App overrides only its own launch template; the Core BrushCreateParams default
        // (used by scripting, the factory and byte-identity round-trips) stays Solid.
        Assert.False(new BrushCreateParams().Air);
        Assert.True(MainWindow.NewDefaultBrushParams().Air);
    }
}

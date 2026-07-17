using Ged.App;
using Ged.Core.Lighting;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Feature 1 wiring: the Preview-Lighting method gate on the build controller — real
/// bakes use the selected method, preview bakes stay on RED Classic (null) unless the
/// last full bake was fast (&lt; ~1.5 s).
/// </summary>
public sealed class LightingMethodWiringTests
{
    private static GeometryBuildController Controller()
    {
        var session = new EditorSession();
        session.NewLevel();
        return new GeometryBuildController(session, _ => { }, () => { }, (_, _) => { });
    }

    [Fact]
    public void Real_Bake_Always_Uses_The_Selected_Method()
    {
        GeometryBuildController c = Controller();
        c.Method = new LightingMethod { Base = LightingBase.Bounced, Bounces = 2 };

        Assert.NotNull(c.MethodForBake(preview: false));
        Assert.Equal(LightingBase.Bounced, c.MethodForBake(preview: false)!.Base);
    }

    [Fact]
    public void Preview_Uses_Red_Classic_When_The_Last_Full_Bake_Was_Slow()
    {
        GeometryBuildController c = Controller();
        c.Method = new LightingMethod { Base = LightingBase.Bounced };
        c.LastFullBakeMs = 4000; // slow level

        Assert.Null(c.MethodForBake(preview: true)); // → stock RED Classic
    }

    [Fact]
    public void Preview_May_Use_The_Full_Method_When_The_Last_Full_Bake_Was_Fast()
    {
        GeometryBuildController c = Controller();
        c.Method = new LightingMethod { Base = LightingBase.Bounced, AmbientOcclusion = true };
        c.LastFullBakeMs = 800; // under the ~1.5 s gate

        LightingMethod? m = c.MethodForBake(preview: true);
        Assert.NotNull(m);
        Assert.True(m!.AmbientOcclusion);
    }

    [Fact]
    public void No_Prior_Bake_Keeps_Preview_On_Red_Classic()
    {
        GeometryBuildController c = Controller();
        c.Method = new LightingMethod { Base = LightingBase.Bounced };
        Assert.Equal(0, c.LastFullBakeMs);
        Assert.Null(c.MethodForBake(preview: true));
    }

    [Fact]
    public void WithMethod_Maps_Onto_LightingOptions()
    {
        var opts = new LightingOptions();
        Assert.True(opts.IsRedClassicMethod);

        Assert.False(opts.MoverShadows); // raw LightingOptions default is the RED-matching / byte-safe OFF

        opts.WithMethod(new LightingMethod { Base = LightingBase.Bounced, Bounces = 2, AmbientOcclusion = true, SoftShadows = true, SeamBlend = true, CornerLeakFix = true, SmoothGutters = true, MoverShadows = true });
        Assert.Equal(2, opts.LightBounces);
        Assert.True(opts.AmbientOcclusion);
        Assert.True(opts.SoftShadows);
        Assert.True(opts.CrossRoomBlend);
        Assert.True(opts.CornerLeakFix);
        Assert.True(opts.SmoothGutterNormals);
        Assert.True(opts.AngleWeightedNormals);
        Assert.True(opts.MoverShadows);
        Assert.False(opts.IsRedClassicMethod);

        // "Movers cast shadows" defaults ON in a LightingMethod (owner quality default); WithMethod carries it.
        Assert.True(new LightingMethod().MoverShadows);
        var mm = new LightingOptions();
        mm.WithMethod(new LightingMethod { Base = LightingBase.RedClassic, MoverShadows = false });
        Assert.False(mm.MoverShadows);

        // A null method (preview → RED Classic) leaves the defaults untouched (seam blend off).
        var d = new LightingOptions();
        d.WithMethod(null);
        Assert.True(d.IsRedClassicMethod);
        Assert.False(d.CrossRoomBlend);

        // Seam Blend is orthogonal to the base: RED Classic + Seam Blend still maps CrossRoomBlend on.
        var c = new LightingOptions();
        c.WithMethod(new LightingMethod { Base = LightingBase.RedClassic, SeamBlend = true });
        Assert.True(c.CrossRoomBlend);
        Assert.True(c.IsRedClassicMethod); // kernel stays RED-Classic; only the seam is post-blended
    }
}

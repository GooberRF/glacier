using System.Collections.Generic;
using System.IO;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Lighting;
using Xunit;

namespace Ged.Automation.Tests;

/// <summary>The read-only editor-settings reader, the RF-install resolution order, and the path allow-list.</summary>
public sealed class SettingsAndPathTests
{
    [Fact]
    public void Missing_Settings_File_Gives_Fresh_Install_Defaults()
    {
        using var dir = new TempDir();
        EditorSettingsSnapshot s = EditorSettingsReader.Read(dir.File("nope.cfg"));
        Assert.Same(EditorSettingsSnapshot.Defaults, s);
        Assert.True(s.UseSharedBspBuild);
        Assert.Null(s.RfInstallDir);
        Assert.True(s.EnableAutomationBridge);
        Assert.Equal(LightingBase.RedClassic, s.LightingMethod.Base);
        Assert.True(s.LightingMethod.MoverShadows);
        Assert.Equal(BrushCreateParams.StockFloorTexture, s.DefaultFloorTexture);
    }

    [Fact]
    public void Partial_Settings_Keep_Defaults_For_Missing_Fields_And_Ignore_Unknown_Ones()
    {
        EditorSettingsSnapshot s = EditorSettingsReader.Parse("""
            { "RfInstallDir": "X:\\RF", "UseSharedBspBuild": false, "LightingMethodBase": 1,
              "LightingMethodBounces": 5, "LightingHighRes": true, "SomethingNew": [1,2,3] }
            """);
        Assert.Equal("X:\\RF", s.RfInstallDir);
        Assert.False(s.UseSharedBspBuild);
        Assert.Equal(LightingBase.Bounced, s.LightingMethod.Base);
        Assert.Equal(2, s.LightingMethod.Bounces); // clamped like the editor
        Assert.True(s.LightingMethod.HighResLightmaps);
        Assert.True(s.LightingMethod.MoverShadows); // absent → default on
        Assert.False(s.LightingMethod.AmbientOcclusion);
        Assert.True(s.EnableAutomationBridge);

        BuildSettings b = s.ToBuildSettings();
        Assert.False(b.UseSharedBsp);
        Assert.True(b.LightingMethod!.HighResLightmaps);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json {")]
    [InlineData("[1,2]")]
    [InlineData("{ \"UseSharedBspBuild\": \"yes\", \"LightingMethodBase\": \"bounced\", \"RfInstallDir\": 7 }")]
    public void Malformed_Or_Wrongly_Typed_Settings_Fall_Back_To_Defaults(string json)
    {
        EditorSettingsSnapshot s = EditorSettingsReader.Parse(json);
        Assert.True(s.UseSharedBspBuild);
        Assert.Equal(LightingBase.RedClassic, s.LightingMethod.Base);
        Assert.Null(s.RfInstallDir);
    }

    [Fact]
    public void The_Dead_Default_Texture_Is_Migrated_Like_The_Editor_Does()
    {
        EditorSettingsSnapshot s = EditorSettingsReader.Parse("{ \"DefaultWallTexture\": \"Rck_Default01.tga\", \"DefaultFloorTexture\": \"my_floor.tga\" }");
        Assert.Equal(BrushCreateParams.StockWallTexture, s.DefaultWallTexture);
        Assert.Equal("my_floor.tga", s.DefaultFloorTexture);
    }

    [Fact]
    public void The_Settings_Locator_Tries_Beside_The_Tool_Then_The_Profile_Then_A_Checkout_Editor_Build()
    {
        using var dir = new TempDir();
        string active = dir.File("cli-bin/settings.cfg");
        string profile = dir.File("profile/settings.cfg");
        string checkout = Directory.CreateDirectory(dir.File("checkout")).FullName;
        string debug = Path.Combine(checkout, "src", "Ged.App", "bin", "Debug", "net8.0", "settings.cfg");
        string release = Path.Combine(checkout, "src", "Ged.App", "bin", "Release", "net8.0", "settings.cfg");
        foreach (string f in new[] { debug, release })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, "{}");
        }

        File.SetLastWriteTimeUtc(debug, new System.DateTime(2026, 1, 2, 0, 0, 0, System.DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(release, new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc));

        // Order: beside the tool, the profile, then the checkout's editor builds (newest first).
        Assert.Equal(new[] { active, profile, debug, release }, EditorSettingsLocator.Candidates(active, profile, checkout));
        Assert.Equal(new[] { active }, EditorSettingsLocator.Candidates(active, active, null));

        // First existing wins.
        Assert.Equal(debug, EditorSettingsLocator.Find(EditorSettingsLocator.Candidates(active, profile, checkout)));
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        File.WriteAllText(profile, "{}");
        Assert.Equal(profile, EditorSettingsLocator.Find(EditorSettingsLocator.Candidates(active, profile, checkout)));
        Directory.CreateDirectory(Path.GetDirectoryName(active)!);
        File.WriteAllText(active, "{}");
        Assert.Equal(active, EditorSettingsLocator.Find(EditorSettingsLocator.Candidates(active, profile, checkout)));
        Assert.Null(EditorSettingsLocator.Find(new[] { dir.File("nope.cfg") }));
    }

    [Fact]
    public void Rf_Install_Choice_Names_Its_Source_And_The_Settings_File()
    {
        using var dir = new TempDir();
        string path = dir.File("settings.cfg");
        File.WriteAllText(path, "{ \"RfInstallDir\": \"S\" }");
        EditorSettingsSnapshot settings = EditorSettingsReader.Read(path);
        Assert.Equal(path, settings.SettingsFile);
        Assert.Null(EditorSettingsReader.Read(dir.File("missing.cfg")).SettingsFile);

        RfInstallChoice flag = RfInstallResolver.ResolveWithSource("F", settings, _ => "E");
        Assert.Equal(("F", RfInstallResolver.FromFlag), (flag.Dir, flag.Source));
        Assert.Equal("F (from --rf-dir)", flag.Describe());

        RfInstallChoice env = RfInstallResolver.ResolveWithSource(null, settings, _ => "E");
        Assert.Equal(("E", RfInstallResolver.FromEnvironment), (env.Dir, env.Source));
        Assert.Equal("E (from GED_RF_DIR)", env.Describe());

        RfInstallChoice fromSettings = RfInstallResolver.ResolveWithSource(null, settings, _ => null);
        Assert.Equal(("S", RfInstallResolver.FromSettings), (fromSettings.Dir, fromSettings.Source));
        Assert.Equal($"S (from editor settings: {path})", fromSettings.Describe());

        RfInstallChoice none = RfInstallResolver.ResolveWithSource(null, EditorSettingsSnapshot.Defaults, _ => null);
        Assert.Null(none.Dir);
        Assert.Equal(RfInstallResolver.FromNothing, none.Source);
        Assert.Contains("no editor settings file was found", none.Describe());
    }

    [Fact]
    public void Rf_Install_Resolution_Order_Is_Flag_Then_Environment_Then_Settings()
    {
        var settings = new EditorSettingsSnapshot { RfInstallDir = "S" };
        var env = new Dictionary<string, string?> { ["GED_RF_DIR"] = "E" };
        Assert.Equal("F", RfInstallResolver.Resolve("F", settings, k => env.GetValueOrDefault(k)));
        Assert.Equal("E", RfInstallResolver.Resolve(null, settings, k => env.GetValueOrDefault(k)));
        Assert.Equal("S", RfInstallResolver.Resolve(null, settings, _ => null));
        Assert.Null(RfInstallResolver.Resolve(" ", EditorSettingsSnapshot.Defaults, _ => ""));
    }

    [Fact]
    public void Allow_List_Accepts_Paths_Under_Roots_And_Explicit_Files_Only()
    {
        using var dir = new TempDir();
        string root = Directory.CreateDirectory(dir.File("maps")).FullName;
        var policy = new PathAllowList(() => new[] { root, null });

        Assert.True(policy.IsAllowed(Path.Combine(root, "a.rfl")));
        Assert.True(policy.IsAllowed(Path.Combine(root, "sub", "b.rfl")));
        Assert.False(policy.IsAllowed(dir.File("outside.rfl")));
        Assert.False(policy.IsAllowed(Path.Combine(root, "..", "escape.rfl"))); // '..' cannot escape
        Assert.False(policy.IsAllowed(root + "-sibling" + Path.DirectorySeparatorChar + "c.rfl")); // prefix trick

        policy.AllowExplicit(dir.File("outside.rfl"));
        Assert.True(policy.IsAllowed(dir.File("outside.rfl")));
        Assert.False(policy.IsAllowed(dir.File("other.rfl")));

        AutomationException ex = Assert.Throws<AutomationException>(() => policy.Require(dir.File("other.rfl"), "write"));
        Assert.Equal(AutomationErrorCodes.PathNotAllowed, ex.Code);
        Assert.Contains(root, ex.Message);
    }
}

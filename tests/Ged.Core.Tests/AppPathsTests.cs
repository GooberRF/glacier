using System;
using System.IO;
using System.Text.Json;
using Ged.Core;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Portable file-layout resolution: by default everything the app writes lives next to
/// the executable (settings.cfg, keymap.cfg, logs\, cache\, prefabs\, recovery\); when
/// the exe directory is not writable it all falls back to the per-user profile.
/// </summary>
public class AppPathsTests
{
    [Fact]
    public void Portable_Layout_Puts_Everything_Beside_The_Exe()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "ged-portable-" + Guid.NewGuid().ToString("N"));

        AppPaths.ResolvedPaths p = AppPaths.Resolve(baseDir, baseWritable: true);

        Assert.Equal(Path.Combine(baseDir, "settings.cfg"), p.SettingsFile);
        Assert.Equal(Path.Combine(baseDir, "keymap.cfg"), p.KeymapFile);
        Assert.Equal(Path.Combine(baseDir, "logs"), p.LogsDirectory);
        Assert.Equal(Path.Combine(baseDir, "cache"), p.CacheDirectory);
        Assert.Equal(Path.Combine(baseDir, "prefabs"), p.PrefabsDirectory);
        Assert.Equal(Path.Combine(baseDir, "recovery"), p.RecoveryDirectory);
    }

    [Fact]
    public void Fallback_Uses_The_User_Profile_When_The_Exe_Dir_Is_Not_Writable()
    {
        AppPaths.ResolvedPaths p = AppPaths.Resolve(@"C:\Program Files\GED", baseWritable: false);

        string appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Glacier");
        string localAppData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glacier");

        // Settings / keymap / prefabs -> %APPDATA%; logs / cache / recovery -> %LOCALAPPDATA%.
        Assert.Equal(Path.Combine(appData, "settings.cfg"), p.SettingsFile);
        Assert.Equal(Path.Combine(appData, "keymap.cfg"), p.KeymapFile);
        Assert.Equal(Path.Combine(appData, "prefabs"), p.PrefabsDirectory);
        Assert.Equal(Path.Combine(localAppData, "logs"), p.LogsDirectory);
        Assert.Equal(Path.Combine(localAppData, "cache"), p.CacheDirectory);
        Assert.Equal(Path.Combine(localAppData, "recovery"), p.RecoveryDirectory);

        // The fallback must never point back at the read-only exe directory.
        Assert.DoesNotContain(@"C:\Program Files\GED", p.SettingsFile, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Settings_And_Keymap_Use_The_Cfg_Extension_Not_Json()
    {
        Assert.EndsWith("settings.cfg", AppPaths.SettingsFile);
        Assert.EndsWith("keymap.cfg", AppPaths.KeymapFile);
        Assert.DoesNotContain("settings.json", AppPaths.SettingsFile);
        Assert.DoesNotContain("keymap.json", AppPaths.KeymapFile);
    }

    [Fact]
    public void Live_Paths_Sit_Under_The_Base_Directory_When_Writable()
    {
        // The test host's base dir is writable, so the live properties are the portable ones.
        if (!AppPaths.BaseDirectoryWritable)
        {
            return;
        }

        Assert.Equal(Path.Combine(AppPaths.BaseDirectory, "settings.cfg"), AppPaths.SettingsFile);
        Assert.Equal(Path.Combine(AppPaths.BaseDirectory, "logs"), AppPaths.LogsDirectory);
        Assert.Equal(Path.Combine(AppPaths.BaseDirectory, "cache"), AppPaths.CacheDirectory);
        Assert.False(AppPaths.UsingProfileFallback);
    }

    [Fact]
    public void ProbeWritable_True_For_A_Writable_Dir_And_Leaves_No_Trace()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ged-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(AppPaths.ProbeWritable(dir));
            // The probe file must be cleaned up; only the (created) directory remains.
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void ProbeWritable_False_When_The_Directory_Cannot_Be_Created()
    {
        // A path whose parent is a file can neither be created nor written to.
        string file = Path.Combine(Path.GetTempPath(), "ged-probe-file-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "x");
        try
        {
            Assert.False(AppPaths.ProbeWritable(Path.Combine(file, "sub")));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void SeedScriptsIfAbsent_Populates_An_Empty_Fallback_From_The_Bundle()
    {
        string root = Path.Combine(Path.GetTempPath(), "ged-seed-" + Guid.NewGuid().ToString("N"));
        string bundle = Path.Combine(root, "bundle");
        string target = Path.Combine(root, "fallback");
        try
        {
            // Bundle mirrors the shipped layout: examples/ + api/ subfolders.
            Directory.CreateDirectory(Path.Combine(bundle, "examples"));
            Directory.CreateDirectory(Path.Combine(bundle, "api"));
            File.WriteAllText(Path.Combine(bundle, "examples", "hello.lua"), "-- hello");
            File.WriteAllText(Path.Combine(bundle, "examples", "spiral.lua"), "-- spiral");
            File.WriteAllText(Path.Combine(bundle, "api", "ged.lua"), "-- stub");

            int seeded = AppPaths.SeedScriptsIfAbsent(bundle, target);

            Assert.Equal(3, seeded);
            Assert.True(File.Exists(Path.Combine(target, "examples", "hello.lua")));
            Assert.True(File.Exists(Path.Combine(target, "examples", "spiral.lua")));
            Assert.True(File.Exists(Path.Combine(target, "api", "ged.lua")));
            Assert.Equal("-- stub", File.ReadAllText(Path.Combine(target, "api", "ged.lua")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void SeedScriptsIfAbsent_Never_Overwrites_A_User_Modified_File()
    {
        string root = Path.Combine(Path.GetTempPath(), "ged-seed-keep-" + Guid.NewGuid().ToString("N"));
        string bundle = Path.Combine(root, "bundle");
        string target = Path.Combine(root, "fallback");
        try
        {
            Directory.CreateDirectory(Path.Combine(bundle, "examples"));
            File.WriteAllText(Path.Combine(bundle, "examples", "hello.lua"), "-- bundled");
            File.WriteAllText(Path.Combine(bundle, "examples", "new.lua"), "-- new");

            // The user already edited hello.lua in the fallback; it must survive untouched.
            Directory.CreateDirectory(Path.Combine(target, "examples"));
            File.WriteAllText(Path.Combine(target, "examples", "hello.lua"), "-- MY EDIT");

            int seeded = AppPaths.SeedScriptsIfAbsent(bundle, target);

            Assert.Equal(1, seeded); // only the absent new.lua is copied
            Assert.Equal("-- MY EDIT", File.ReadAllText(Path.Combine(target, "examples", "hello.lua")));
            Assert.True(File.Exists(Path.Combine(target, "examples", "new.lua")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void SeedScriptsIfAbsent_Is_A_Noop_When_Source_Equals_Target_Or_Is_Missing()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ged-seed-noop-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "a.lua"), "x");

            // Same source and target (portable install: the bundle IS the active dir) copies nothing.
            Assert.Equal(0, AppPaths.SeedScriptsIfAbsent(dir, dir));
            // A missing source is a harmless no-op.
            Assert.Equal(0, AppPaths.SeedScriptsIfAbsent(Path.Combine(dir, "does-not-exist"), dir));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Settings_Cfg_Round_Trips_Through_The_Resolved_Portable_Path()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "ged-roundtrip-" + Guid.NewGuid().ToString("N"));
        try
        {
            AppPaths.ResolvedPaths p = AppPaths.Resolve(baseDir, baseWritable: true);
            Directory.CreateDirectory(Path.GetDirectoryName(p.SettingsFile)!);

            var original = new Sample("C:\\Games\\RedFaction", true, 3);
            File.WriteAllText(p.SettingsFile, JsonSerializer.Serialize(original));

            // The file is literally settings.cfg beside the (base) exe dir, and reloads intact.
            Assert.True(File.Exists(p.SettingsFile));
            Assert.Equal("settings.cfg", Path.GetFileName(p.SettingsFile));
            Sample? reloaded = JsonSerializer.Deserialize<Sample>(File.ReadAllText(p.SettingsFile));
            Assert.Equal(original, reloaded);
        }
        finally
        {
            if (Directory.Exists(baseDir))
            {
                Directory.Delete(baseDir, recursive: true);
            }
        }
    }

    private sealed record Sample(string RfInstallDir, bool DarkTheme, int GridSize);
}

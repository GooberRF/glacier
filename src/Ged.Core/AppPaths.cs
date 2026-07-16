using System;
using System.IO;

namespace Ged.Core;

/// <summary>
/// Resolves where the editor keeps everything it writes. GED is a portable app: by
/// default all app-generated files live <b>next to the executable</b>
/// (<see cref="AppContext.BaseDirectory"/>, never the process working directory):
/// <list type="bullet">
/// <item><c>settings.cfg</c> — user settings (JSON payload, <c>.cfg</c> extension)</item>
/// <item><c>keymap.cfg</c> — key bindings</item>
/// <item><c>logs\</c> — crash logs + the rolling <c>session.log</c></item>
/// <item><c>cache\</c> — texture/mesh thumbnail cache</item>
/// <item><c>prefabs\</c> — the default prefab library (still a user-changeable setting)</item>
/// <item><c>recovery\</c> — emergency autosaves written during a crash</item>
/// </list>
///
/// If the executable's directory is not writable (e.g. installed under Program Files,
/// or on read-only media) the whole set falls back to the per-user profile — settings/
/// keymap/prefabs under <c>%APPDATA%\Glacier</c>, logs/cache/recovery under
/// <c>%LOCALAPPDATA%\Glacier</c> — so settings are never silently lost. The
/// shell surfaces <see cref="UsingProfileFallback"/> as a one-time warning. Writability
/// is decided once, up front, with a real write probe.
/// </summary>
public static class AppPaths
{
    private const string AppFolder = "Glacier";

    /// <summary>The executable's directory — the portable-app base (NOT the working directory).</summary>
    public static string BaseDirectory { get; } = AppContext.BaseDirectory;

    /// <summary>True when the executable's directory is writable (decided once via a write probe).</summary>
    public static bool BaseDirectoryWritable { get; } = ProbeWritable(BaseDirectory);

    /// <summary>True when the exe directory was not writable and the profile fallback is in use.</summary>
    public static bool UsingProfileFallback => !BaseDirectoryWritable;

    private static readonly ResolvedPaths Paths = Resolve(BaseDirectory, BaseDirectoryWritable);

    /// <summary>The settings file (<c>settings.cfg</c>).</summary>
    public static string SettingsFile => Paths.SettingsFile;

    /// <summary>The keymap file (<c>keymap.cfg</c>).</summary>
    public static string KeymapFile => Paths.KeymapFile;

    /// <summary>The crash/session log directory (<c>logs\</c>).</summary>
    public static string LogsDirectory => Paths.LogsDirectory;

    /// <summary>The thumbnail cache directory (<c>cache\</c>).</summary>
    public static string CacheDirectory => Paths.CacheDirectory;

    /// <summary>The default prefab-library directory (<c>prefabs\</c>); overridable via a setting.</summary>
    public static string DefaultPrefabsDirectory => Paths.PrefabsDirectory;

    /// <summary>The emergency-autosave (crash recovery) directory (<c>recovery\</c>).</summary>
    public static string RecoveryDirectory => Paths.RecoveryDirectory;

    /// <summary>
    /// The portable scripts library (<c>scripts\</c>) beside the exe, holding bundled examples
    /// (<c>scripts\examples\</c>) and user scripts. Follows the same writable-probe / profile
    /// fallback as the other portable folders: under <c>%APPDATA%\Glacier\scripts</c> when the
    /// exe directory is read-only. Bundled examples ship next to the exe regardless.
    /// </summary>
    public static string ScriptsDirectory { get; } = ResolveScriptsDirectory(BaseDirectory, BaseDirectoryWritable);

    private static string ResolveScriptsDirectory(string baseDir, bool baseWritable) => baseWritable
        ? Path.Combine(baseDir, "scripts")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolder, "scripts");

    /// <summary>
    /// The bundled scripts folder shipped BESIDE the binary (<c>&lt;exe-dir&gt;/scripts</c>), holding
    /// the example library (<c>scripts/examples/</c>) and the Lua completion stub (<c>scripts/api/ged.lua</c>).
    /// This is always readable — even inside a read-only Linux AppImage mount (at <c>usr/bin/scripts</c>) —
    /// but it is only the ACTIVE <see cref="ScriptsDirectory"/> when the exe dir is writable.
    /// </summary>
    public static string BundledScriptsDirectory { get; } = Path.Combine(BaseDirectory, "scripts");

    /// <summary>
    /// Seeds the bundled example scripts + Lua api stub into the active <see cref="ScriptsDirectory"/> when
    /// it resolved to the profile fallback (the exe dir is read-only, e.g. inside a Linux AppImage) and so
    /// starts EMPTY — otherwise those files, shipped only beside the read-only binary, would be invisible and
    /// the scripting tour would break. Copy-if-absent: an existing file is NEVER overwritten, so a user-modified
    /// script survives. No-op for a writable/portable install (the bundle already IS the active dir) or when the
    /// bundle is missing. Best-effort; returns the number of files seeded. Call once at startup.
    /// </summary>
    public static int SeedBundledScriptsToFallback()
    {
        // Only the read-only-exe (profile-fallback) case needs seeding; a writable install already has the
        // bundle as its active scripts dir.
        if (BaseDirectoryWritable)
        {
            return 0;
        }

        return SeedScriptsIfAbsent(BundledScriptsDirectory, ScriptsDirectory);
    }

    /// <summary>
    /// Copies every file under <paramref name="sourceDir"/> (recursively, preserving the <c>examples/</c> and
    /// <c>api/</c> subfolders) into <paramref name="targetDir"/>, skipping any file that already exists so a
    /// user-modified copy is never clobbered. Pure and side-effect-free on the source; exposed for tests.
    /// Returns the number of files actually copied; best-effort (a copy failure is swallowed, not fatal).
    /// </summary>
    public static int SeedScriptsIfAbsent(string sourceDir, string targetDir)
    {
        if (string.IsNullOrEmpty(sourceDir) || !Directory.Exists(sourceDir) ||
            string.Equals(Path.GetFullPath(sourceDir), Path.GetFullPath(targetDir), StringComparison.Ordinal))
        {
            return 0;
        }

        int seeded = 0;
        try
        {
            foreach (string src in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string dst = Path.Combine(targetDir, Path.GetRelativePath(sourceDir, src));
                if (File.Exists(dst))
                {
                    continue; // never overwrite a user-modified file
                }

                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst);
                seeded++;
            }
        }
        catch (Exception)
        {
            // Best-effort seeding — a read-only target or race is non-fatal.
        }

        return seeded;
    }

    /// <summary>The resolved portable-vs-fallback location for every app-written file.</summary>
    public readonly record struct ResolvedPaths(
        string SettingsFile,
        string KeymapFile,
        string LogsDirectory,
        string CacheDirectory,
        string PrefabsDirectory,
        string RecoveryDirectory);

    /// <summary>
    /// Pure path resolution (exposed for tests): when <paramref name="baseWritable"/> the
    /// files sit directly under <paramref name="baseDir"/>; otherwise they fall back to the
    /// per-user profile (settings/keymap/prefabs → <c>%APPDATA%\Glacier</c>,
    /// logs/cache/recovery → <c>%LOCALAPPDATA%\Glacier</c>).
    /// </summary>
    public static ResolvedPaths Resolve(string baseDir, bool baseWritable)
    {
        if (baseWritable)
        {
            return new ResolvedPaths(
                Path.Combine(baseDir, "settings.cfg"),
                Path.Combine(baseDir, "keymap.cfg"),
                Path.Combine(baseDir, "logs"),
                Path.Combine(baseDir, "cache"),
                Path.Combine(baseDir, "prefabs"),
                Path.Combine(baseDir, "recovery"));
        }

        string appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolder);
        string localAppData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolder);

        return new ResolvedPaths(
            Path.Combine(appData, "settings.cfg"),
            Path.Combine(appData, "keymap.cfg"),
            Path.Combine(localAppData, "logs"),
            Path.Combine(localAppData, "cache"),
            Path.Combine(appData, "prefabs"),
            Path.Combine(localAppData, "recovery"));
    }

    /// <summary>
    /// Returns true when <paramref name="dir"/> exists (or can be created) and a file can be
    /// written and deleted in it. Best-effort and side-effect-free on success.
    /// </summary>
    public static bool ProbeWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, $".ged-write-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

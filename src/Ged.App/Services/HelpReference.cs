using System;
using System.Diagnostics;
using System.IO;

namespace Ged.App.Services;

/// <summary>
/// Resolves and opens the offline HTML help reference (<c>help.html</c>) and other external
/// links (the community Discord). The reference ships beside the executable
/// (<see cref="AppContext.BaseDirectory"/>); in the dev tree it falls back to
/// <c>docs/help.html</c> found by walking up from the base directory. Opening uses the OS
/// shell (default browser) so it works on Windows and Linux (xdg-open) alike.
/// </summary>
public static class HelpReference
{
    /// <summary>The community Discord invite opened from Help ▸ Join the Community Discord.</summary>
    public const string DiscordUrl = "https://discord.gg/factionfiles";

    /// <summary>The issue tracker opened from the About box's "Report Bug" button.</summary>
    public const string IssuesUrl = "https://github.com/GooberRF/glacier/issues";

    /// <summary>
    /// Locates <c>help.html</c>: beside the executable first (the shipped layout), then a
    /// dev-tree fallback to <c>docs/help.html</c> discovered by walking up from the base
    /// directory. Returns <c>null</c> when neither exists.
    /// </summary>
    public static string? ResolvePath()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, "help.html");
        if (File.Exists(beside))
        {
            return beside;
        }

        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "docs", "help.html");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Opens a local file path or URL in the OS default handler (browser). Uses
    /// <see cref="ProcessStartInfo.UseShellExecute"/> so a bare path/URL resolves through the
    /// shell on Windows and through xdg-open on Linux. Throws on failure — callers surface a toast.
    /// </summary>
    public static void OpenExternal(string target) =>
        Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
}

using System.Reflection;

namespace Ged.App;

/// <summary>
/// The editor's version strings, read from the entry assembly's attributes (set in
/// Ged.App.csproj: Version = 1.0.0, InformationalVersion = 1.0.0+&lt;git-sha&gt;).
/// Consumed by the About dialog and the crash log.
/// </summary>
internal static class AppVersion
{
    /// <summary>Numeric product version, e.g. <c>1.0.0</c>.</summary>
    public static string Version =>
        typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>Informational version with the git short-sha, e.g. <c>1.0.0+abc1234</c>.</summary>
    public static string Informational =>
        typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Version;
}

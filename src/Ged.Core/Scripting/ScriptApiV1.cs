namespace Ged.Core.Scripting;

/// <summary>
/// The versioned, additive-only script API surface contract (plan §5.8). Scripts pin the
/// major version they were authored against — either with a <c>--@api N</c> header or a
/// runtime <c>ged.require_api(N)</c> call — and the runner refuses a higher-major request on
/// an older build with a clear message. Within a major version the surface is additive-only;
/// the API-surface snapshot test pins every exposed global so an accidental breaking change
/// fails CI (same discipline the round-trip byte-identity invariant enforces for the format).
/// </summary>
public static class ScriptApiV1
{
    /// <summary>The current API major version advertised as <c>ged.api_version</c>.</summary>
    public const int Version = 1;

    /// <summary>The top-level Lua globals the engine binds. Pinned by the API-surface test.</summary>
    public static readonly IReadOnlyList<string> Globals = new[]
    {
        "ged",        // meta: api_version, require_api, group, dry_run, allow_destructive
        "level",      // document bindings (brushes, objects, place, find_uid, …)
        "selection",  // selection query + mutate
        "assets",     // texture/asset lookup + where-used
        "ops",        // build / light / check_holes / save / package / playtest
        "lint",       // run the linter + contribute findings
        "log",        // info / warn / error
        "rng",        // seeded, deterministic RNG
        "print",      // Lua print → script log output
    };
}

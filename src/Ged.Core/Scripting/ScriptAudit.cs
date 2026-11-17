using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Linting;
using Ged.Core.Spatial;

namespace Ged.Core.Scripting;

// level.audit{}: the craft audit (Linting/CraftAudit) from Lua — the gate a level must pass before it is reported done.
public sealed partial class ScriptLevel
{
    [ScriptDoc("Craft audit: sunk/floating/random props, stairs and bridges leading nowhere, perches, openings into solid or void, purpose-off pieces, flat rock, basic areas. {clean, total, counts, findings}.",
        Example = "local a = level.audit{build = \"sealed\"} for _, f in ipairs(a.findings) do log.warn(f.message .. \" Fix: \" .. f.fix) end", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.LevelAudit))]
    public Dictionary<string, object?> Audit(ScriptArgs? args = null)
    {
        const string Api = "level.audit";
        args?.RejectUnknown(ScriptOptionSets.LevelAudit);
        SpatialLevel level = _ctx.Spatial.Level(Api, args);
        string[]? checks = null;
        if (args is not null && args.Named.TryGetValue("checks", out object? c) && c is not null)
        {
            checks = c switch
            {
                string s => new[] { s },
                ScriptArgs t => t.Items.Select(x => x?.ToString() ?? string.Empty).ToArray(),
                _ => throw new ScriptApiException($"{Api}: checks must be a check name or a list of them.", $"Checks: {string.Join(", ", CraftAudit.Checks)}."),
            };
            foreach (string name in checks.Where(n => !CraftAudit.Checks.Contains(n, StringComparer.OrdinalIgnoreCase)))
            {
                throw new ScriptApiException($"{Api}: unknown check '{name}'.", $"Checks: {string.Join(", ", CraftAudit.Checks)}.");
            }
        }

        var options = new CraftAuditOptions
        {
            Checks = checks,
            MaxPerCheck = args?.Int(Api, "max", 40) ?? 40,
            ObjectBounds = _ctx.Assets is { } vfs ? CraftAudit.MeshBoundsFrom(vfs) : null,
            Budget = new SpatialBudget
            {
                MaxSamples = args?.Int(Api, "max_samples", SpatialBudget.DefaultMaxSamples) ?? SpatialBudget.DefaultMaxSamples,
                TimeLimit = TimeSpan.FromSeconds(args?.Number(Api, "timeout_s", 120) ?? 120),
            },
        };
        using (_ctx.EnterHostOperation())
        {
            try
            {
                System.Text.Json.Nodes.JsonObject json = CraftAudit.Run(level, options, _ctx.Cancellation).ToJson();
                json["geometry"] = level.Freshness.ToJson();
                return ScriptSpatial.ToLua(json);
            }
            catch (SpatialException ex)
            {
                throw new ScriptApiException($"{Api}: {ex.Message}", ex.Hint);
            }
        }
    }
}

public static partial class ScriptOptionSets
{
    public static readonly ScriptOptionSet LevelAudit = new("level.audit",
        new("checks", "string | list of strings", "all", "Only these checks: props, stairs, perches, openings, purpose, rock, density."),
        new("max", "integer", "40", "At most this many findings listed per check (counts stay complete)."),
        new("build", "string", "\"none\"", "\"sealed\" runs ops.build() first so the current brushes are audited; \"none\" audits the stored geometry."),
        new("max_samples", "integer", "600000", "Floor-lattice sample budget; above it the 0.5 m pitch doubles (coarser, faster)."),
        new("timeout_s", "number", "120", "Measurement time limit in seconds."));
}

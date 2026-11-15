using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Modules;
using Xunit;

namespace Ged.Core.Tests.Modules;

/// <summary>
/// The mapper's <c>curation.json</c> on the fixture library (<see cref="ModuleTestLevel"/>): rejects hide modules,
/// relabels change category / role / styles, keeps rank higher, bad entries are ignored with a warning, the Lua
/// <c>modules.list{include_rejected=}</c> option, and a harvest leaving the file untouched while reporting stale ids.
/// </summary>
public sealed class ModuleCurationTests
{
    private static string WriteLibrary()
    {
        string dir = ModuleTestLevel.TempDir();
        ModuleLibraryBuilder.Write(dir, ModuleTestLevel.Harvest(ModuleTestLevel.Build()).Modules, new[] { "stock" });
        return dir;
    }

    private static void WriteCuration(string dir, string json) => File.WriteAllText(Path.Combine(dir, ModuleCuration.FileName), json);

    private static string Id(ModuleLibrary lib, string category) => lib.Query(new ModuleQuery { Category = category }).Single().Id;

    [Fact]
    public void Reject_Hides_Relabel_Changes_And_Keep_Boosts()
    {
        string dir = WriteLibrary();
        try
        {
            ModuleLibrary plain = ModuleLibrary.Load(dir);
            string pilaster = Id(plain, "pilaster"), crate = Id(plain, "crate_stack"), beam = Id(plain, "beam"), railing = Id(plain, "railing");
            int total = plain.Modules.Count;
            double railingScore = plain.Find(railing)!.Score;
            Assert.Equal(0, plain.Stats().Curation.Kept);
            Assert.Equal(total, plain.Stats().Curation.Undecided);

            WriteCuration(dir, $$"""
                {
                  "schema": "glacier.module-curation/1",
                  "modules": {
                    "{{pilaster}}": { "verdict": "reject", "note": "too plain" },
                    "{{crate}}": { "verdict": "keep", "category": "machinery", "role": "landmark", "styles": ["bunker", "industrial"] },
                    "{{beam}}": { "category": "truss" },
                    "{{railing}}": { "verdict": "keep" }
                  }
                }
                """);
            ModuleLibrary lib = ModuleLibrary.Load(dir);
            Assert.Empty(lib.Warnings);

            // Reject: gone from Modules, Find, Query and the stats counts; still reachable when asked for.
            Assert.Equal(total - 1, lib.Modules.Count);
            Assert.Equal(total, lib.AllModules.Count);
            Assert.Null(lib.Find(pilaster));
            Assert.Empty(lib.Query(new ModuleQuery { Category = "pilaster" }));
            ModuleInfo rejected = Assert.Single(lib.Query(new ModuleQuery { Category = "pilaster", IncludeRejected = true }));
            Assert.Equal("reject", rejected.Verdict);
            Assert.Equal("too plain", rejected.CurationNote);
            Assert.NotNull(lib.Find(pilaster, includeRejected: true));
            Assert.False(lib.Stats().ByCategory.ContainsKey("pilaster"));

            // Relabel: category, role and styles replaced; the harvested values kept for reference.
            ModuleInfo relabelled = lib.Find(crate)!;
            Assert.Equal("machinery", relabelled.Category);
            Assert.Equal("landmark", relabelled.Role);
            Assert.Equal(new[] { "bunker", "industrial" }, relabelled.Styles);
            Assert.Equal("bunker", relabelled.PrimaryStyle);
            Assert.True(relabelled.Relabelled);
            Assert.Equal("crate_stack", relabelled.Harvested!.Value.Category);
            Assert.Contains(lib.Query(new ModuleQuery { Category = "machinery" }), m => m.Id == crate);
            Assert.Contains(lib.Query(new ModuleQuery { Style = "bunker" }), m => m.Id == crate);
            Assert.Empty(lib.Query(new ModuleQuery { Category = "crate_stack" }));
            Assert.Equal("truss", lib.Find(beam)!.Category);
            Assert.Null(lib.Find(beam)!.Verdict);

            // Keep: the selection score rises by the documented bonus and the module leads an unfiltered query.
            ModuleInfo kept = lib.Find(railing)!;
            Assert.Equal(railingScore, kept.Score);
            Assert.Equal(railingScore + ModuleCuration.KeepBonus, kept.SelectionScore, 9);
            Assert.True(lib.Query().Take(2).All(m => m.Kept));

            ModuleCurationCounts counts = lib.Stats().Curation;
            Assert.Equal(2, counts.Kept);
            Assert.Equal(1, counts.Rejected);
            Assert.Equal(2, counts.Relabelled);
            Assert.Equal(total - 3, counts.Undecided);
            Assert.Equal(0, counts.UnknownIds);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Bad_Entries_And_Unknown_Ids_Are_Ignored_With_Warnings()
    {
        string dir = WriteLibrary();
        try
        {
            ModuleLibrary plain = ModuleLibrary.Load(dir);
            string pilaster = Id(plain, "pilaster"), crate = Id(plain, "crate_stack"), beam = Id(plain, "beam"), railing = Id(plain, "railing");
            WriteCuration(dir, $$"""
                {
                  "schema": "glacier.module-curation/1",
                  "modules": {
                    "{{pilaster}}": { "verdict": "maybe" },
                    "{{crate}}": { "category": "spaceship" },
                    "{{beam}}": { "styles": ["industrial", "baroque"] },
                    "{{railing}}": "keep",
                    "pilaster_deadbeef": { "verdict": "reject" },
                    "": { "verdict": "keep" }
                  }
                }
                """);
            ModuleLibrary lib = ModuleLibrary.Load(dir);
            Assert.True(lib.Available);
            Assert.Equal(plain.Modules.Count, lib.Modules.Count);
            Assert.All(lib.AllModules, m => Assert.Null(m.Verdict));
            Assert.All(lib.AllModules, m => Assert.False(m.Relabelled));
            Assert.Equal(new[] { "pilaster_deadbeef" }, lib.UnknownCurationIds);
            Assert.Equal(4, lib.IgnoredCurationEntries);
            Assert.Contains(lib.Warnings, w => w.Contains("unknown verdict 'maybe'", StringComparison.Ordinal));
            Assert.Contains(lib.Warnings, w => w.Contains("unknown category 'spaceship'", StringComparison.Ordinal));
            Assert.Contains(lib.Warnings, w => w.Contains("unknown style 'baroque'", StringComparison.Ordinal));
            Assert.Contains(lib.Warnings, w => w.Contains("pilaster_deadbeef", StringComparison.Ordinal));

            // A broken file, a wrong top level and a missing schema never throw.
            foreach (string bad in new[] { "{ not json", "[1, 2]", "{\"modules\": []}", "{\"modules\": {\"" + railing + "\": {\"verdict\": \"KEEP\"}}}" })
            {
                WriteCuration(dir, bad);
                ModuleLibrary again = ModuleLibrary.Load(dir);
                Assert.Equal(plain.Modules.Count, again.AllModules.Count);
                Assert.NotEmpty(again.Warnings);
            }

            // Verdicts are case-insensitive: the last file kept the railing (with a schema warning).
            Assert.True(ModuleLibrary.Load(dir).Find(railing)!.Kept);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Lua_List_Hides_Rejected_Unless_Asked_And_Place_Refuses_Them()
    {
        string dir = WriteLibrary();
        try
        {
            ModuleLibrary plain = ModuleLibrary.Load(dir);
            string pilaster = Id(plain, "pilaster"), railing = Id(plain, "railing");
            WriteCuration(dir, "{\"schema\": \"glacier.module-curation/1\", \"modules\": {\"" + pilaster + "\": {\"verdict\": \"reject\"}, \"" + railing + "\": {\"verdict\": \"keep\"}}}");
            Ged.Core.Tests.Scripting.KitModulesRun.Run run = Ged.Core.Tests.Scripting.KitModulesRun.Exec(dir, $$$"""
                local plain = modules.list{category = "pilaster", resolvable = false}
                local all = modules.list{category = "pilaster", resolvable = false, include_rejected = true}
                local got = modules.get("{{{pilaster}}}")
                local kept = modules.get("{{{railing}}}")
                return tostring(#plain) .. "|" .. tostring(#all) .. "|" .. all[1].verdict .. "|" .. got.verdict .. "|" .. kept.verdict
                  .. "|" .. tostring(kept.score > kept.harvest_score)
                """);
            Assert.Equal(new[] { "0", "1", "reject", "reject", "keep", "true" }, run.Result.ReturnValue!.Split('|'));

            Ged.Core.Tests.Scripting.KitModulesRun.Run refused = Ged.Core.Tests.Scripting.KitModulesRun.Exec(dir,
                "modules.place{id = '" + pilaster + "', pos = {0, 0, 0}}", expectSuccess: false);
            Assert.False(refused.Result.Success);
            Assert.Contains("rejected by the library's curation", refused.Result.Error!.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Harvest_Leaves_Curation_Untouched_And_Reports_Stale_Ids()
    {
        string root = ModuleTestLevel.TempDir();
        try
        {
            string levels = Path.Combine(root, "levels");
            Directory.CreateDirectory(levels);
            File.WriteAllBytes(Path.Combine(levels, "fixture.rfl"), ModuleTestLevel.Build().SaveToBytes());
            string lib = Path.Combine(root, "library", "modules");
            Directory.CreateDirectory(lib);
            const string Curation = "{\n  \"schema\": \"glacier.module-curation/1\",\n  \"modules\": {\n    \"pilaster_00000000\": { \"verdict\": \"reject\", \"note\": \"from an older harvest\" }\n  }\n}\n";
            WriteCuration(lib, Curation);
            byte[] before = File.ReadAllBytes(Path.Combine(lib, ModuleCuration.FileName));

            ModuleHarvestReport report = ModuleHarvestRunner.Run(new ModuleHarvestRequest
            {
                Manifest = HarvestManifest.Single("fixture.rfl", "stock"),
                Root = levels,
                OutputDir = lib,
                Jobs = 1,
            });
            Assert.True(report.Modules.Count > 0);
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(lib, ModuleCuration.FileName)));
            Assert.Equal(1, report.CurationEntries);
            Assert.Equal(new[] { "pilaster_00000000" }, report.CurationUnknownIds);
            Assert.Contains("pilaster_00000000", report.ToText(), StringComparison.Ordinal);
            Assert.Contains("\"curation_unknown_ids\"", report.ToJson(), StringComparison.Ordinal);

            // A second harvest over the first keeps it too.
            ModuleHarvestRunner.Run(new ModuleHarvestRequest { Manifest = HarvestManifest.Single("fixture.rfl", "stock"), Root = levels, OutputDir = lib, Jobs = 1 });
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(lib, ModuleCuration.FileName)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

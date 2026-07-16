using System;
using System.IO;
using System.Linq;
using Ged.Core.Scripting;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// The API-surface guard (plan §5.8): pins every exposed script global + member. A removed or
/// renamed member changes the generated snapshot and fails CI — the same discipline the
/// round-trip byte-identity invariant enforces for the file format. Also regenerates the shipped
/// reference (<c>docs/internal/SCRIPTING-API.md</c>) + Lua stub (<c>scripts/api/ged.lua</c>) and asserts
/// they are committed and current, so the docs cannot drift from the surface.
///
/// To update after an intentional, additive API change, run once with the environment variable
/// <c>GED_REGEN_SCRIPT_DOCS=1</c> and commit the regenerated files.
/// </summary>
public sealed class ScriptApiSurfaceTests
{
    private static readonly bool Regen =
        Environment.GetEnvironmentVariable("GED_REGEN_SCRIPT_DOCS") == "1";

    [Fact]
    public void Bound_Globals_Match_The_Pinned_Contract()
    {
        // The facade binds exactly these globals; print is provided by the Lua Basic module.
        string[] facadeGlobals = ScriptApiV1.Globals.Where(g => g != "print").OrderBy(g => g).ToArray();

        EditorDocumentGlobals(out string[] boundGlobals);

        Assert.Equal(facadeGlobals, boundGlobals);
    }

    [Fact]
    public void Surface_Snapshot_Is_Committed_And_Current()
    {
        string snapshot = ScriptApiReference.GenerateSurfaceSnapshot();
        string path = Path.Combine(RepoRoot(), "tests", "Ged.Core.Tests", "Scripting", "api-surface.snapshot.txt");

        if (Regen)
        {
            File.WriteAllText(path, snapshot);
            return;
        }

        Assert.True(File.Exists(path),
            "api-surface.snapshot.txt is missing. Regenerate with GED_REGEN_SCRIPT_DOCS=1.");
        Assert.Equal(Normalize(File.ReadAllText(path)), Normalize(snapshot));
    }

    [Fact]
    public void Generated_Reference_And_Stub_Are_Committed_And_Current()
    {
        string md = ScriptApiReference.GenerateMarkdown();
        string stub = ScriptApiReference.GenerateLuaStub();
        string mdPath = Path.Combine(RepoRoot(), "docs", "internal", "SCRIPTING-API.md");
        string stubPath = Path.Combine(RepoRoot(), "scripts", "api", "ged.lua");

        if (Regen)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(stubPath)!);
            File.WriteAllText(mdPath, md);
            File.WriteAllText(stubPath, stub);
            return;
        }

        Assert.True(File.Exists(mdPath), "docs/internal/SCRIPTING-API.md is missing. Regenerate with GED_REGEN_SCRIPT_DOCS=1.");
        Assert.True(File.Exists(stubPath), "scripts/api/ged.lua is missing. Regenerate with GED_REGEN_SCRIPT_DOCS=1.");
        Assert.Equal(Normalize(File.ReadAllText(mdPath)), Normalize(md));
        Assert.Equal(Normalize(File.ReadAllText(stubPath)), Normalize(stub));
    }

    // The facade's bound global names (mirrors ScriptContext.Globals) resolved via a throwaway doc.
    private static void EditorDocumentGlobals(out string[] names)
    {
        Editor.EditorDocument doc = ScriptTestFixture.NewDoc(entities: 0);
        var log = new ScriptLog();
        var ctx = new ScriptContext(ScriptTestFixture.Services(doc), new ScriptRunOptions(), log);
        names = ctx.Globals.Keys.OrderBy(k => k).ToArray();
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd('\n') + "\n";

    private static string RepoRoot()
    {
        // Override (used when tests run from an isolated output dir outside the repo tree).
        string? env = Environment.GetEnvironmentVariable("GED_REPO_ROOT");
        if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "Glacier.sln")))
        {
            return env;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Glacier.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repo root (Glacier.sln).");
    }
}

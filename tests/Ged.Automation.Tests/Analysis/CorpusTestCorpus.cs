using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Ged.Cli;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.IO.Vpp;
using Ged.Core.Model;

namespace Ged.Automation.Tests.Analysis;

/// <summary>
/// A synthetic corpus in a temp folder (CORPUS-NORMS-SPEC §8.1): generated levels, one packfile, an autosave, a
/// truncated file, a non-level, an AI-tagged level, an unsupported version and a two-version series — every file
/// marked read-only. No real corpus is ever touched by these tests.
/// </summary>
internal sealed class CorpusTestCorpus : IDisposable
{
    public CorpusTestCorpus(bool readOnly = true)
    {
        Root = Directory.CreateTempSubdirectory("ged-corpus-root-").FullName;
        Out = Directory.CreateTempSubdirectory("ged-corpus-out-").FullName;
        string maps = Directory.CreateDirectory(Path.Combine(Root, "maps")).FullName;
        Directory.CreateDirectory(Path.Combine(Root, "empty"));

        byte[] alpha = Compiled(TestLevels.RoomDocument());
        EditorDocument betaDoc = TestLevels.RoomDocument();
        betaDoc.PlaceObject(LevelObjectKind.Light, new Vec3(3, 3, 3));
        byte[] beta = betaDoc.SaveToBytes(updateTimestamp: false);
        EditorDocument packedDoc = TestLevels.RoomDocument();
        packedDoc.PlaceObject(LevelObjectKind.MpRespawnPoint, new Vec3(-3, 0.5f, -3));
        byte[] packed = Compiled(packedDoc);

        File.WriteAllBytes(Path.Combine(maps, "alpha.rfl"), alpha);
        File.WriteAllBytes(Path.Combine(maps, "beta.rfl"), beta);
        File.WriteAllBytes(Path.Combine(maps, "alpha_autosave.rfl"), alpha);
        File.WriteAllBytes(Path.Combine(maps, "broken.rfl"), alpha[..200]);
        File.WriteAllBytes(Path.Combine(maps, "notalevel.rfl"), "this is not a level, just text padding it out."u8.ToArray());
        File.WriteAllBytes(Path.Combine(maps, "tagged.rfl"), Tagged());
        byte[] future = (byte[])beta.Clone();
        BitConverter.GetBytes(307).CopyTo(future, 4); // 306 is read since Alpine 1.5; 307 is the next, unknown version
        File.WriteAllBytes(Path.Combine(maps, "future.rfl"), future);
        File.WriteAllBytes(Path.Combine(maps, "series_v1.rfl"), Authored("Tester", 1_000_000));
        File.WriteAllBytes(Path.Combine(maps, "series_v2.rfl"), Authored("Tester", 2_000_000));
        File.WriteAllText(Path.Combine(Root, "readme.txt"), "not a level");
        new VppBuilder().Add("packed.rfl", packed).Add("alpha_copy.rfl", alpha).Add("notes.txt", "x"u8.ToArray()).Write(Path.Combine(Root, "pack.vpp"));

        if (readOnly)
        {
            foreach (string f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, File.GetAttributes(f) | FileAttributes.ReadOnly);
            }
        }
    }

    /// <summary>The corpus root (read-only files).</summary>
    public string Root { get; }

    /// <summary>A separate, empty output folder.</summary>
    public string Out { get; }

    /// <summary>A level with a sealed build applied.</summary>
    public static byte[] Compiled(EditorDocument doc)
    {
        CompileOptions options = CompileOptionsFactory.CreateForDocument(doc, null, new BuildSettings { UseSharedBsp = true }, new CompileRequest { Sealed = true });
        GeometryBuildService.BuildAndApply(doc.Rfl, options);
        return doc.SaveToBytes(updateTimestamp: false);
    }

    private static byte[] Tagged()
    {
        EditorDocument doc = TestLevels.RoomDocument();
        int uid = doc.Objects.First(o => o.Kind == LevelObjectKind.Light).Uid;
        new GedObjectMetadataService(doc).SetBlock(uid, GedMetadataType.Tags,
            ObjectTags.Encode(new Dictionary<string, string> { ["ai.script"] = "gen.lua" }));
        return doc.SaveToBytes(updateTimestamp: false);
    }

    private static byte[] Authored(string author, uint timestamp)
    {
        EditorDocument doc = TestLevels.RoomDocument();
        foreach (var s in doc.Rfl.Sections)
        {
            if (s.Content is LevelInfoSection info)
            {
                info.Author = author;
                info.LevelName = "Series";
                s.Dirty = true;
            }
        }

        doc.Rfl.Header.Timestamp = timestamp;
        return doc.SaveToBytes(updateTimestamp: false);
    }

    /// <summary>
    /// An independent snapshot of the root: every file's (relative path, length, last-write UTC, SHA-256, attributes)
    /// and every directory. Last-access times are deliberately not compared.
    /// </summary>
    public static List<string> Snapshot(string root)
    {
        var rows = new List<string>();
        foreach (string d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            rows.Add("D " + Path.GetRelativePath(root, d));
        }

        foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var fi = new FileInfo(f);
            string sha;
            using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                sha = Convert.ToHexString(SHA256.HashData(fs));
            }

            rows.Add($"F {Path.GetRelativePath(root, f)}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{sha}|{fi.Attributes}");
        }

        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    /// <summary>An isolated CLI environment: no settings, no RF install, no repository configuration.</summary>
    public static CliEnvironment Env() => new()
    {
        PipeName = "glacier-test-" + Guid.NewGuid().ToString("N"),
        SettingsPath = Path.Combine(Path.GetTempPath(), "ged-no-such-settings-" + Guid.NewGuid().ToString("N") + ".cfg"),
        GetEnvironmentVariable = _ => null,
        ConnectTimeout = TimeSpan.FromMilliseconds(300),
        Clock = () => TestLevels.Fixed,
        RepoRoot = () => null,
    };

    public void Dispose()
    {
        foreach (string dir in new[] { Root, Out })
        {
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                }

                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // best-effort
            }
        }
    }
}

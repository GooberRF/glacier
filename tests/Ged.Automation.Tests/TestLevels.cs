using System;
using System.IO;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;

namespace Ged.Automation.Tests;

/// <summary>A temp directory deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir() => Path = Directory.CreateTempSubdirectory("ged-automation-").FullName;

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // best-effort
        }
    }
}

/// <summary>Levels built in-test (no corpus needed) and the optional corpus.</summary>
internal static class TestLevels
{
    public static readonly DateTime Fixed = new(2026, 1, 2, 3, 4, 5);

    /// <summary>A sealed two-room level: two overlapping air boxes, a light, a respawn point.</summary>
    public static EditorDocument RoomDocument()
    {
        EditorDocument doc = LevelTemplates.CreateNew(Fixed);
        var ed = new BrushEditor(doc);
        ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 12, Height = 4, Depth = 12, Air = true },
            new Vec3(0, 2, 0), Mat3.Identity);
        ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 8, Height = 3, Depth = 3, Air = true },
            new Vec3(9, 1.5f, 0), Mat3.Identity);
        doc.PlaceObject(LevelObjectKind.Light, new Vec3(0, 3, 0));
        doc.PlaceObject(LevelObjectKind.MpRespawnPoint, new Vec3(2, 0.5f, 2));
        return doc;
    }

    /// <summary>Writes <see cref="RoomDocument"/> to <paramref name="path"/> (as a never-modified v305 file).</summary>
    public static string WriteRoomLevel(string path)
    {
        RoomDocument().Save(path, updateTimestamp: false); // v306, like every Glacier save
        return path;
    }

    /// <summary>
    /// A level whose sealed build leaks: an air box with one face removed (an open brush), so the
    /// compiled room boundary has open edges.
    /// </summary>
    public static string WriteLeakyLevel(string path)
    {
        EditorDocument doc = LevelTemplates.CreateNew(Fixed);
        var ed = new BrushEditor(doc);
        int uid = ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 8, Height = 4, Depth = 8, Air = true },
            new Vec3(0, 2, 0), Mat3.Identity);
        Brush b = ed.FindBrush(uid)!;
        b.Geometry.Faces.RemoveAt(0);
        File.WriteAllBytes(path, doc.SaveToBytes());
        return path;
    }

    /// <summary>The repository root (located by Glacier.sln), or null.</summary>
    public static string? RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(System.IO.Path.Combine(dir.FullName, "Glacier.sln")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            return null;
        }
    }

    /// <summary>A corpus level (research/example_rfls), or null when the corpus is absent.</summary>
    public static string? Corpus(string name)
    {
        string? env = Environment.GetEnvironmentVariable("GED_CORPUS_DIR");
        string? dir = !string.IsNullOrEmpty(env) ? env : RepoRoot is { } root ? System.IO.Path.Combine(root, "research", "example_rfls") : null;
        string? p = dir is null ? null : System.IO.Path.Combine(dir, name);
        return p is not null && File.Exists(p) ? p : null;
    }
}

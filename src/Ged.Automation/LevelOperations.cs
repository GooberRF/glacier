using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Ged.Core.Assets;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Packaging;
using Ged.Core.Tables;

namespace Ged.Automation;

/// <summary>
/// Host-independent level operations the automation hosts share: the compile (always through
/// <see cref="CompileOptionsFactory"/>, so it is identical to the editor's build with the same
/// settings), applying a result, the brush fingerprint used to track "geometry changed since the last
/// build", and the dependency-scan options (catalogs) the linter needs.
/// </summary>
public static class LevelOperations
{
    /// <summary>
    /// Compiles the document. <paramref name="sealedBuild"/> = the full build (surfaces + t-joint
    /// seal); false = the editor's fast unsealed live preview. Lighting bakes always run sealed.
    /// Cookie warnings are appended to the report. <paramref name="progress"/> receives the compiler's
    /// stage progress (on the compiling thread).
    /// </summary>
    public static CompiledLevel Compile(
        EditorDocument doc,
        AssetVfs? vfs,
        BuildSettings settings,
        bool sealedBuild,
        bool bakeLighting,
        bool shadows,
        TextureTraitsCache? traits,
        CancellationToken cancellation,
        IProgress<AutomationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var warnings = new List<string>();
        var request = new CompileRequest
        {
            Sealed = sealedBuild || bakeLighting,
            BakeLighting = bakeLighting,
            CastShadows = shadows,
            Cancellation = cancellation,
            Progress = progress is null ? null : p => progress.Report(new AutomationProgress(p.Stage, p.Current, p.Total)),
            BakeProgress = progress is null ? null : p => progress.Report(new AutomationProgress(p.Stage, p.Current, p.Total)),
        };
        CompileOptions options = CompileOptionsFactory.CreateForDocument(doc, vfs, settings, request, traits, warnings);
        CompiledLevel result = GeometryBuildService.Build(doc.Rfl, options);
        foreach (string w in warnings)
        {
            result.Report.Add(BuildSeverity.Warning, w);
        }

        return result;
    }

    /// <summary>Applies a compiled result into the document (the compiled sections) and marks it dirty.</summary>
    public static void Apply(EditorDocument doc, CompiledLevel result)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(result);
        GeometryBuildService.Apply(doc.Rfl, result);
        doc.MarkDirty();
    }

    /// <summary>The document's current compiled geometry (static_geometry), or null when never built.</summary>
    public static Geometry? CompiledGeometry(EditorDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        foreach (RflSection section in doc.Rfl.Sections)
        {
            if (section.TypeId == (uint)SectionType.StaticGeometry)
            {
                if (section.Content is null)
                {
                    doc.Rfl.ParseAllKnownSections();
                }

                return (section.Content as GeometrySection)?.Geometry;
            }
        }

        return null;
    }

    /// <summary>
    /// A cheap fingerprint of every brush's shape, placement and flags. Two equal fingerprints mean no
    /// brush changed in a way a build would see; hosts compare it to decide "geometry dirty".
    /// </summary>
    public static long BrushFingerprint(EditorDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var h = new HashCode();
        foreach (Brush b in Brushes(doc))
        {
            h.Add(b.Uid);
            h.Add(b.Flags);
            h.Add(b.Life);
            h.Add(b.Position);
            h.Add(b.Rotation);
            h.Add(b.Geometry.Vertices.Count);
            foreach (Vec3 v in b.Geometry.Vertices)
            {
                h.Add(v);
            }

            h.Add(b.Geometry.Faces.Count);
            foreach (Face f in b.Geometry.Faces)
            {
                h.Add(f.Texture);
                h.Add(f.Flags);
                h.Add(f.Vertices.Count);
            }
        }

        return h.ToHashCode();
    }

    /// <summary>The document's brushes (empty when the level has no brushes section).</summary>
    public static IReadOnlyList<Brush> Brushes(EditorDocument doc)
    {
        foreach (RflSection section in doc.Rfl.Sections)
        {
            if (section.TypeId == (uint)SectionType.Brushes)
            {
                if (section.Content is null)
                {
                    doc.Rfl.ParseAllKnownSections();
                }

                return (section.Content as BrushesSection)?.Brushes ?? (IReadOnlyList<Brush>)Array.Empty<Brush>();
            }
        }

        return Array.Empty<Brush>();
    }

    /// <summary>
    /// The linter's dependency-scan options, as the editor builds them: the game catalogs read from
    /// the mounted library plus the level's companion dialogue text file when one exists.
    /// </summary>
    public static DependencyScanOptions ScanOptions(AssetVfs? vfs, string? levelPath)
    {
        var options = new DependencyScanOptions
        {
            ClutterCatalog = TryLoad(vfs, "clutter.tbl", ClutterCatalog.Load),
            EntityCatalog = TryLoad(vfs, "entity.tbl", EntityCatalog.Load),
            ItemCatalog = TryLoad(vfs, "items.tbl", ItemCatalog.Load),
        };

        if (levelPath is { Length: > 0 })
        {
            string txt = Path.ChangeExtension(levelPath, ".txt");
            if (File.Exists(txt))
            {
                options.DialogueTextFile = Path.GetFileName(txt);
            }
        }

        return options;
    }

    /// <summary>Total number of links across all objects.</summary>
    public static int LinkCount(EditorDocument doc) =>
        doc.Objects.Sum(o => LinkModel.LinksOf(o)?.Count ?? 0);

    private static T? TryLoad<T>(AssetVfs? vfs, string name, Func<byte[], T> parse)
        where T : class
    {
        try
        {
            byte[]? data = vfs?.ReadFile(name);
            return data is null ? null : parse(data);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

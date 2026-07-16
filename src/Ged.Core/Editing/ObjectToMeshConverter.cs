using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Tables;

namespace Ged.Core.Editing;

/// <summary>The mesh object + child objects produced by converting one clutter/entity source.</summary>
public sealed class MeshConversionPlan
{
    public required AlpineMeshObject Mesh { get; init; }

    /// <summary>Thruster VFX meshes spawned from the source mesh's <c>thruster_N</c> tag points.</summary>
    public List<AlpineMeshObject> ThrusterMeshes { get; } = new();

    /// <summary>Coronas spawned from the source mesh's <c>corona_N</c> tag points.</summary>
    public List<AlpineCoronaObject> Coronas { get; } = new();

    /// <summary>True when the destructibility (clutter) block was inherited (Life &gt; -1).</summary>
    public bool InheritedClutter { get; init; }

    /// <summary>Every new object the plan creates (mesh + thruster meshes + coronas).</summary>
    public IEnumerable<object> AllNewObjects =>
        new object[] { Mesh }.Concat(ThrusterMeshes).Concat(Coronas);
}

/// <summary>
/// Converts a placed clutter/entity object into an Alpine Mesh object, inheriting the class's
/// destructibility and spawning the child coronas / thruster meshes the class defines — a pure,
/// UID-free build (the document assigns UIDs and applies the result as one undo transaction).
///
/// Mirrors Alpine's "To Mesh Object" (editor_patch/alpine_obj.cpp:1483-1637):
/// mesh filename from the class table (v3d→v3m / vcm→v3c fixup); collision mode + impact material
/// from the class; the clutter block (life/debris/explosion/velocity + 11 damage-type factors +
/// corpse mesh/material/collision) when Life &gt; -1; child coronas from <c>corona_N</c> tags paired
/// with the class's glare list (corona.cpp:668-706); and — entity only — thruster VFX meshes from
/// <c>thruster_N</c> tags plus the stand-animation idle pose.
/// </summary>
public static class ObjectToMeshConverter
{
    /// <summary>Whether a level object is a convertible clutter/entity.</summary>
    public static bool CanConvert(LevelObject o) =>
        o is not null && o.Kind is LevelObjectKind.Clutter or LevelObjectKind.Entity;

    /// <summary>
    /// Builds the conversion plan for one source object, or null when it is not a clutter/entity or
    /// no mesh filename can be resolved from the catalogs (Alpine skips such objects). All new
    /// objects have UID 0 — the caller assigns fresh UIDs.
    /// </summary>
    public static MeshConversionPlan? BuildPlan(
        LevelObject source,
        ClutterCatalog? clutter,
        EntityCatalog? entities,
        IMeshTagSource? tagSource = null,
        Func<string, GlareDef?>? glareLookup = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!CanConvert(source))
        {
            return null;
        }

        ClutterDef? ci = source.Kind == LevelObjectKind.Clutter ? clutter?.Find(source.ClassName) : null;
        EntityDef? ei = source.Kind == LevelObjectKind.Entity ? entities?.Find(source.ClassName) : null;

        string filename = FixMeshExt(ci?.V3dFilename ?? ei?.V3dFilename ?? string.Empty);
        if (string.IsNullOrWhiteSpace(filename))
        {
            return null; // no mesh to point at (Alpine `continue`s)
        }

        Mat3 orient = OrientationOf(source);
        var mesh = new AlpineMeshObject
        {
            Position = source.Position,
            Orientation = orient,
            ScriptName = source.ScriptName,
            MeshFilename = filename,
            CollisionMode = 2, // default All, overridden below when the class is known
        };

        var glareNames = new List<string>();
        bool inheritedClutter = false;

        if (ci is not null)
        {
            mesh.CollisionMode = (byte)ci.CollisionMode;
            mesh.Material = ci.MaterialIndex;
            var cp = new AlpineMeshClutterInfo
            {
                Life = ci.LifeValue,
                DebrisFilename = FixMeshExt(ci.DebrisFilename ?? string.Empty),
                DebrisVelocity = ci.DebrisVelocity,
                ExplosionVclip = ci.ExplodeVclip ?? string.Empty,
                ExplosionRadius = ci.ExplodeRadius,
                DamageTypeFactors = (float[])ci.DamageTypeFactors.Clone(),
            };
            if (!string.IsNullOrEmpty(ci.CorpseClassName) && clutter?.Find(ci.CorpseClassName) is { } corpse)
            {
                cp.CorpseFilename = FixMeshExt(corpse.V3dFilename ?? string.Empty);
                cp.CorpseMaterial = (sbyte)corpse.MaterialIndex;
                cp.CorpseCollision = 2;
            }

            inheritedClutter = ci.LifeValue > -1f;
            mesh.Clutter = cp;                              // set before the flag: the setter keeps a non-null block
            mesh.IsClutter = (byte)(inheritedClutter ? 1 : 0);
            if (!string.IsNullOrEmpty(ci.GlareName))
            {
                glareNames.Add(ci.GlareName);
            }
        }
        else if (ei is not null)
        {
            mesh.CollisionMode = (byte)ei.CollisionMode;
            mesh.Material = ei.MaterialIndex;
            var cp = new AlpineMeshClutterInfo
            {
                DebrisFilename = FixMeshExt(ei.DebrisFilename ?? string.Empty),
                ExplosionVclip = ei.ExplodeVclip ?? string.Empty,
                ExplosionRadius = ei.ExplodeRadius,
                DamageTypeFactors = (float[])ei.DamageTypeFactors.Clone(),
            };
            inheritedClutter = ei.LifeValue > -1f;
            if (inheritedClutter)
            {
                cp.Life = ei.LifeValue;
            }

            if (!string.IsNullOrEmpty(ei.CorpseV3dFilename))
            {
                cp.CorpseFilename = FixMeshExt(ei.CorpseV3dFilename);
                cp.CorpseMaterial = -1; // inherit from base
                cp.CorpseCollision = 2;
            }

            mesh.Clutter = cp;
            mesh.IsClutter = (byte)(inheritedClutter ? 1 : 0);
            glareNames.AddRange(ei.CoronaGlareNames);
            if (!string.IsNullOrEmpty(ei.StandAnim))
            {
                mesh.StateAnim = FixAnimExt(ei.StandAnim);
            }
        }

        var plan = new MeshConversionPlan { Mesh = mesh, InheritedClutter = inheritedClutter };

        // Child objects from the mesh's tag points.
        IReadOnlyList<MeshTag> tags = tagSource?.ReadTags(filename) ?? Array.Empty<MeshTag>();
        if (tags.Count > 0)
        {
            SpawnCoronas(plan, tags, glareNames, glareLookup, source.Position, orient);
            if (ei is not null)
            {
                SpawnThrusters(plan, tags, ei.ThrusterVfxNames, source.Position, orient);
            }
        }

        return plan;
    }

    private static void SpawnCoronas(
        MeshConversionPlan plan, IReadOnlyList<MeshTag> tags, IReadOnlyList<string> glareNames,
        Func<string, GlareDef?>? glareLookup, Vec3 objPos, Mat3 objOrient)
    {
        if (glareNames.Count == 0 || glareLookup is null)
        {
            return;
        }

        for (int i = 0; ; i++)
        {
            if (FindTag(tags, $"corona_{i + 1}") is not { } tag)
            {
                break;
            }

            string gname = i < glareNames.Count ? glareNames[i] : glareNames[0];
            GlareDef? gi = glareLookup(gname);
            if (gi is null || string.IsNullOrEmpty(gi.CoronaBitmap))
            {
                continue;
            }

            Vec3 worldPos = objPos.Add(objOrient.Transform(tag.Position));
            Mat3 worldOrient = Mat3Math.Compose(objOrient, tag.Orientation);
            plan.Coronas.Add(CreateCorona(gi, worldPos, worldOrient));
        }
    }

    private static void SpawnThrusters(
        MeshConversionPlan plan, IReadOnlyList<MeshTag> tags, IReadOnlyList<string> vfxNames,
        Vec3 objPos, Mat3 objOrient)
    {
        if (vfxNames.Count == 0)
        {
            return;
        }

        for (int i = 0; ; i++)
        {
            if (FindTag(tags, $"thruster_{i + 1}") is not { } tag)
            {
                break;
            }

            string vfx = i < vfxNames.Count ? vfxNames[i] : vfxNames[^1];
            plan.ThrusterMeshes.Add(new AlpineMeshObject
            {
                Position = objPos.Add(objOrient.Transform(tag.Position)),
                Orientation = Mat3Math.Compose(objOrient, tag.Orientation),
                ScriptName = "Thruster",
                MeshFilename = vfx,
                CollisionMode = 0, // a VFX billboard mesh never collides
            });
        }
    }

    private static AlpineCoronaObject CreateCorona(GlareDef gi, Vec3 pos, Mat3 orient) => new()
    {
        Position = pos,
        Orientation = orient,
        ScriptName = "Corona",
        ColorR = gi.ColorR,
        ColorG = gi.ColorG,
        ColorB = gi.ColorB,
        ColorA = 255,
        CoronaBitmap = gi.CoronaBitmap,
        ConeAngle = gi.ConeAngle,
        Intensity = gi.Intensity,
        RadiusDistance = gi.RadiusDistance,
        RadiusScale = gi.RadiusScale,
        DiminishDistance = gi.DiminishDistance,
        VolumetricBitmap = gi.VolumetricBitmap,
        VolumetricHeight = string.IsNullOrEmpty(gi.VolumetricBitmap) ? null : gi.VolumetricHeight,
        VolumetricLength = string.IsNullOrEmpty(gi.VolumetricBitmap) ? null : gi.VolumetricLength,
    };

    private static MeshTag? FindTag(IReadOnlyList<MeshTag> tags, string name)
    {
        foreach (MeshTag t in tags)
        {
            if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return t;
            }
        }

        return null;
    }

    private static Mat3 OrientationOf(LevelObject source) => source.Model switch
    {
        Entity e => e.Rotation,
        Clutter c => c.Header.Rotation,
        _ => Mat3.Identity,
    };

    // Alpine's conversion fixups (alpine_obj.cpp:1513-1514,1534-1535,1549-1550,1577-1578,1588).
    private static string FixMeshExt(string s) => ReplaceExt(ReplaceExt(s, ".v3d", ".v3m"), ".vcm", ".v3c");

    private static string FixAnimExt(string s) => ReplaceExt(s, ".mvf", ".rfa");

    private static string ReplaceExt(string s, string from, string to) =>
        s.EndsWith(from, StringComparison.OrdinalIgnoreCase) ? s[..^from.Length] + to : s;
}

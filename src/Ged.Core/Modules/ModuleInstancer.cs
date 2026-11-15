using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfg;
using Ged.Core.Model;

namespace Ged.Core.Modules;

/// <summary>How to place a module.</summary>
public sealed class ModulePlacement
{
    /// <summary>World position of the module's local origin (see <see cref="ModuleInfo.Anchor"/>).</summary>
    public Vec3 Position { get; init; }

    /// <summary>Yaw in degrees (any angle; 0 keeps local +Z on world +Z, 90 turns it onto world +X).</summary>
    public double YawDegrees { get; init; }

    /// <summary>Target size on local X (null = natural).</summary>
    public double? Length { get; init; }

    /// <summary>Target size on local Y (null = natural).</summary>
    public double? Height { get; init; }

    /// <summary>Target size on local Z (null = natural).</summary>
    public double? Depth { get; init; }

    /// <summary>Flip local X (faces stay outward, textures stay readable).</summary>
    public bool Mirror { get; init; }

    /// <summary>Every solid member becomes a detail brush (default), so a placed module can never unseal a level; air members stay air.</summary>
    public bool ForceDetail { get; init; } = true;

    /// <summary>Texture replacements (source name → new name, case-insensitive).</summary>
    public IReadOnlyDictionary<string, string>? TextureMap { get; init; }

    /// <summary>Tags set on every created brush.</summary>
    public IReadOnlyDictionary<string, string>? Tags { get; init; }
}

/// <summary>The outcome of a placement; failures are results, not exceptions.</summary>
/// <param name="Ok">True when the brushes were created.</param>
/// <param name="Error">Why it failed (null on success).</param>
/// <param name="BrushUids">The created brush UIDs, in time order (empty on failure or for <see cref="ModuleInstancer.BuildBrushes"/>).</param>
/// <param name="Size">The placed size on local X, Y, Z.</param>
/// <param name="TileCounts">The unit count of each tile family after the placement (module order of <see cref="ModuleInfo.Tiles"/>).</param>
public sealed record ModulePlaceResult(bool Ok, string? Error, IReadOnlyList<int> BrushUids, D3 Size, IReadOnlyList<int> TileCounts)
{
    public static ModulePlaceResult Fail(string error) => new(false, error, Array.Empty<int>(), default, Array.Empty<int>());
}

/// <summary>World-space brushes of a placed module before they are added to a level.</summary>
public sealed record ModuleBuild(bool Ok, string? Error, IReadOnlyList<Brush> Brushes, D3 Size, IReadOnlyList<int> TileCounts);

/// <summary>
/// Places a library module into a level: per-axis 3-slice stretch (vertices at or below the slice start stay, inside
/// scale, at or above the end shift), tile families re-instanced at even spacing between their kept end margins,
/// density-preserving UVs on non-rigidly moved faces, optional mirror, any yaw, texture replacement, detail forcing
/// and tags. The brushes are appended at the end of the time order in the module's own order as ONE undoable edit
/// (the same import path as an .rfg group, so it joins a running script's transaction).
/// </summary>
public static class ModuleInstancer
{
    private const double SizeTolerance = 1e-3;
    private const double MinZone = 0.05;

    /// <summary>Places <paramref name="module"/> (brushes loaded from its .rfg).</summary>
    public static ModulePlaceResult Place(EditorDocument doc, ModuleInfo module, ModulePlacement placement, GedObjectMetadataService? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        IReadOnlyList<Brush> local;
        try
        {
            local = ModuleLibrary.LoadBrushes(module);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or IO.Rfl.RflFormatException)
        {
            return ModulePlaceResult.Fail($"Module '{module.Id}' could not be read: {ex.Message}");
        }

        return Place(doc, module, local, placement, metadata);
    }

    /// <summary>Places <paramref name="module"/> from explicit local-frame brushes.</summary>
    public static ModulePlaceResult Place(EditorDocument doc, ModuleInfo module, IReadOnlyList<Brush> localBrushes, ModulePlacement placement, GedObjectMetadataService? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ModuleBuild build = BuildBrushes(module, localBrushes, placement);
        if (!build.Ok)
        {
            return ModulePlaceResult.Fail(build.Error!);
        }

        if (build.Brushes.Count == 0)
        {
            return ModulePlaceResult.Fail($"Module '{module.Id}' has no brushes.");
        }

        var rfg = new RfgFile { Version = ModuleLibraryBuilder.RfgVersion };
        var group = new RfgGroup { Name = module.Id };
        group.Brushes.Brushes.AddRange(build.Brushes);
        rfg.Groups.Add(group);

        UndoStack.Transaction tx = doc.Undo.BeginTransaction($"Place module {module.Id}");
        try
        {
            IReadOnlyList<int> uids = RfgInterop.Import(doc, rfg, Mat3.Identity, Vec3.Zero);
            if (placement.Tags is { Count: > 0 } tags)
            {
                GedObjectMetadataService meta = metadata ?? new GedObjectMetadataService(doc);
                var writes = new List<(int, GedMetadataType, byte[]?)>();
                foreach (int uid in uids)
                {
                    var next = new Dictionary<string, string>(meta.Tags(uid), StringComparer.Ordinal);
                    foreach ((string k, string v) in tags)
                    {
                        next[k] = v;
                    }

                    writes.Add((uid, GedMetadataType.Tags, ObjectTags.Encode(next)));
                }

                meta.SetBlocks(writes, "Tag module");
            }

            tx.Commit();
            return new ModulePlaceResult(true, null, uids, build.Size, build.TileCounts);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>Computes the placed world-space brushes without touching any level.</summary>
    public static ModuleBuild BuildBrushes(ModuleInfo module, IReadOnlyList<Brush> localBrushes, ModulePlacement placement)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(localBrushes);
        ArgumentNullException.ThrowIfNull(placement);
        int n = localBrushes.Count;
        var local = new D3[n][];
        D3 min = new(double.MaxValue, double.MaxValue, double.MaxValue), max = new(double.MinValue, double.MinValue, double.MinValue);
        for (int i = 0; i < n; i++)
        {
            Brush b = localBrushes[i];
            D3 pos = D3.From(b.Position);
            D3 r = D3.From(b.Rotation.Right), u = D3.From(b.Rotation.Up), f = D3.From(b.Rotation.Forward);
            local[i] = b.Geometry.Vertices.Select(v => pos + (r * v.X) + (u * v.Y) + (f * v.Z)).ToArray();
            foreach (D3 v in local[i])
            {
                min = D3.Min(min, v);
                max = D3.Max(max, v);
            }
        }

        if (n == 0)
        {
            return new ModuleBuild(true, null, Array.Empty<Brush>(), default, Array.Empty<int>());
        }

        // Per-axis stretch.
        double?[] target = { placement.Length, placement.Height, placement.Depth };
        var delta = new double[3];
        var zones = new ModuleSlice?[3];
        var newLen = new double[3];
        for (int a = 0; a < 3; a++)
        {
            if (target[a] is not double t)
            {
                continue;
            }

            if (!double.IsFinite(t) || t <= 0)
            {
                return Fail($"The requested {ModuleVocabulary.AxisName(a)} size must be a positive number (got {t}).");
            }

            double natural = max[a] - min[a];
            double d = t - natural;
            if (Math.Abs(d) < SizeTolerance)
            {
                continue;
            }

            ModuleSlice? zone = module.Stretch(a);
            if (zone is null)
            {
                string can = module.StretchAxes.Count == 0 ? "it cannot stretch" : "it can stretch on " + string.Join(", ", module.StretchAxes);
                return Fail(FormattableString.Invariant($"Module '{module.Id}' cannot stretch on {ModuleVocabulary.AxisName(a)} (natural {natural:0.###} m, asked {t:0.###} m): {can}."));
            }

            double l2 = Math.Max(MinZone, zone.Length + d);
            zones[a] = zone;
            newLen[a] = l2;
            delta[a] = l2 - zone.Length;
        }

        // Tile families re-instanced on stretched axes.
        var rigidAxis = new int[n];
        Array.Fill(rigidAxis, -1);
        var dropped = new bool[n];
        var copies = new List<(int Brush, int Axis, double Offset, int InsertAfter)>();
        var tileCounts = new List<int>();
        foreach (ModuleTileFamily fam in module.Tiles)
        {
            int a = fam.AxisIndex;
            ModuleSlice? zone = a >= 0 ? zones[a] : null;
            double last = zone is null ? fam.FirstCenter + (fam.Pitch * (fam.Count - 1)) : zone.B - fam.MarginEnd;
            bool inZone = zone is not null && fam.FirstCenter >= zone.A - 0.03 && last <= zone.B + 0.03
                && fam.First.All(i => i >= 0 && i < n) && fam.Replicas.All(i => i >= 0 && i < n);
            if (!inZone)
            {
                tileCounts.Add(fam.Count);
                continue;
            }

            // Unit 0 stays where it is (it lies inside the zone, so the slice leaves it in place); the last unit keeps
            // its stored end margin, and the units in between are spread evenly.
            double c0 = UnitCentre(local, fam.First, a);
            double lastNew = zone!.A + newLen[a] - fam.MarginEnd;
            double span = Math.Max(0, lastNew - c0);
            int count = Math.Max(2, (int)Math.Round(span / Math.Max(1e-6, fam.Pitch), MidpointRounding.AwayFromZero) + 1);
            double spacing = count > 1 ? span / (count - 1) : 0;
            tileCounts.Add(count);
            foreach (int i in fam.Replicas)
            {
                dropped[i] = true;
            }

            foreach (int i in fam.First)
            {
                rigidAxis[i] = a;
            }

            int after = fam.First.Max();
            for (int c = 1; c < count; c++)
            {
                foreach (int i in fam.First)
                {
                    copies.Add((i, a, spacing * c, after));
                }
            }
        }

        // Centred axes keep their anchor centred after a stretch.
        bool[] centred = CentredAxes(module.Attach);
        var centreShift = new double[3];
        for (int a = 0; a < 3; a++)
        {
            if (zones[a] is not null && centred[a])
            {
                centreShift[a] = -delta[a] * 0.5;
            }
        }

        D3 Map(D3 v, int rigid, int copyAxis, double copyOffset)
        {
            double[] c = { v.X, v.Y, v.Z };
            for (int a = 0; a < 3; a++)
            {
                if (zones[a] is not { } z)
                {
                    continue;
                }

                if (rigid == a)
                {
                    c[a] = v[a] + (copyAxis == a ? copyOffset : 0);
                }
                else if (c[a] >= z.B - 1e-9)
                {
                    c[a] = v[a] + delta[a];
                }
                else if (c[a] > z.A + 1e-9)
                {
                    c[a] = z.A + ((v[a] - z.A) * newLen[a] / z.Length);
                }

                c[a] += centreShift[a];
            }

            return new D3(c[0], c[1], c[2]);
        }

        // Emit in module order; re-instanced copies follow the last brush of their unit 0.
        var emit = new List<(int Brush, int CopyAxis, double CopyOffset)>();
        for (int i = 0; i < n; i++)
        {
            if (!dropped[i])
            {
                emit.Add((i, -1, 0));
            }

            foreach (var c in copies.Where(c => c.InsertAfter == i).GroupBy(c => c.Offset).OrderBy(g => g.Key))
            {
                foreach (var cc in c.OrderBy(x => x.Brush))
                {
                    emit.Add((cc.Brush, cc.Axis, cc.Offset));
                }
            }
        }

        double yaw = placement.YawDegrees;
        D3 origin = D3.From(placement.Position);
        var result = new List<Brush>(emit.Count);
        D3 pmin = new(double.MaxValue, double.MaxValue, double.MaxValue), pmax = new(double.MinValue, double.MinValue, double.MinValue);
        foreach ((int bi, int copyAxis, double copyOffset) in emit)
        {
            Brush src = localBrushes[bi];
            D3[] before = local[bi];
            var after = new D3[before.Length];
            for (int k = 0; k < before.Length; k++)
            {
                after[k] = Map(before[k], rigidAxis[bi], copyAxis, copyOffset);
                pmin = D3.Min(pmin, after[k]);
                pmax = D3.Max(pmax, after[k]);
            }

            Geometry g = GeometryClone.Deep(src.Geometry);

            // UVs: faces moved non-rigidly keep their texture density and orientation.
            for (int fi = 0; fi < g.Faces.Count; fi++)
            {
                Face face = g.Faces[fi];
                if (!Rigid(face, before, after))
                {
                    Retexture(face, before, after);
                }
            }

            if (placement.Mirror)
            {
                for (int k = 0; k < after.Length; k++)
                {
                    after[k] = after[k] with { X = -after[k].X };
                }

                foreach (Face face in g.Faces)
                {
                    face.Vertices.Reverse();
                    foreach (FaceVertex fv in face.Vertices)
                    {
                        fv.TextureCoords = fv.TextureCoords with { U = -fv.TextureCoords.U };
                    }
                }
            }

            var world = after.Select(v => YawFrame.ToWorld(v, origin, yaw)).ToArray();
            D3 centroid = D3.Zero;
            foreach (D3 w in world)
            {
                centroid += w;
            }

            centroid = world.Length == 0 ? origin : centroid * (1.0 / world.Length);
            g.Vertices = world.Select(w => (w - centroid).ToVec3()).ToList();
            if (placement.TextureMap is { Count: > 0 } map)
            {
                for (int t = 0; t < g.Textures.Count; t++)
                {
                    foreach ((string from, string to) in map)
                    {
                        if (string.Equals(g.Textures[t], from, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(Path.GetFileNameWithoutExtension(g.Textures[t]), Path.GetFileNameWithoutExtension(from), StringComparison.OrdinalIgnoreCase))
                        {
                            g.Textures[t] = to;
                            break;
                        }
                    }
                }
            }

            GeometryUtil.RecomputeAllPlanes(g);
            uint flags = src.Flags & ~(uint)BrushFlags.Geoable;
            if (placement.ForceDetail && (flags & (uint)BrushFlags.Air) == 0)
            {
                flags |= (uint)BrushFlags.Detail;
            }

            result.Add(new Brush
            {
                Uid = result.Count + 1,
                Position = centroid.ToVec3(),
                Rotation = Mat3.Identity,
                Geometry = g,
                Flags = flags,
                Life = src.Life,
                State = BrushState.Normal,
            });
        }

        D3 size = pmax - pmin;
        return new ModuleBuild(true, null, result, size, tileCounts);

        static ModuleBuild Fail(string error) => new(false, error, Array.Empty<Brush>(), default, Array.Empty<int>());
    }

    /// <summary>The centre of a unit's bounds on one axis (local frame).</summary>
    private static double UnitCentre(D3[][] local, IReadOnlyList<int> brushes, int axis)
    {
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (int i in brushes)
        {
            foreach (D3 v in local[i])
            {
                lo = Math.Min(lo, v[axis]);
                hi = Math.Max(hi, v[axis]);
            }
        }

        return (lo + hi) * 0.5;
    }

    /// <summary>Axes whose local origin is centred on the module for an attachment kind.</summary>
    private static bool[] CentredAxes(string attach) => attach switch
    {
        "wall" => new[] { true, false, false },
        "corner" => new[] { false, false, false },
        "edge" => new[] { true, false, false },
        "opening" => new[] { true, false, false },
        "span" => new[] { true, false, true },
        _ => new[] { true, false, true },
    };

    /// <summary>True when every vertex of the face moved by the same displacement.</summary>
    private static bool Rigid(Face face, D3[] before, D3[] after)
    {
        D3? d0 = null;
        foreach (FaceVertex fv in face.Vertices)
        {
            if (fv.Index < 0 || fv.Index >= before.Length)
            {
                continue;
            }

            D3 d = after[fv.Index] - before[fv.Index];
            if (d0 is null)
            {
                d0 = d;
            }
            else if ((d - d0.Value).Length > 1e-6)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Recomputes a face's UVs from its original UV gradient G (least squares over the original corners, in the
    /// face plane): <c>uv' = uv₀ + G·(p' − p₀')</c>, anchored at a corner that did not move when there is one.
    /// </summary>
    private static void Retexture(Face face, D3[] before, D3[] after)
    {
        var corners = face.Vertices.Where(fv => fv.Index >= 0 && fv.Index < before.Length).ToList();
        if (corners.Count < 3)
        {
            return;
        }

        D3 p0 = before[corners[0].Index];
        D3 normal = D3.Zero;
        for (int i = 0; i < corners.Count; i++)
        {
            D3 a = before[corners[i].Index], b = before[corners[(i + 1) % corners.Count].Index];
            normal += new D3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
        }

        normal = normal.Normalized();
        if (normal.Length < 0.5)
        {
            return;
        }

        D3 e1 = (Math.Abs(normal.X) < 0.9 ? new D3(1, 0, 0) : new D3(0, 1, 0)).Cross(normal).Normalized();
        D3 e2 = normal.Cross(e1).Normalized();

        // Least squares: [du dv] = A · [s t], A = (Σ uv·stᵀ)(Σ st·stᵀ)⁻¹.
        double sss = 0, sst = 0, stt = 0, uS = 0, uT = 0, vS = 0, vT = 0;
        Uv uv0 = corners[0].TextureCoords;
        foreach (FaceVertex fv in corners)
        {
            D3 d = before[fv.Index] - p0;
            double s = d.Dot(e1), t = d.Dot(e2);
            double du = fv.TextureCoords.U - uv0.U, dv = fv.TextureCoords.V - uv0.V;
            sss += s * s;
            sst += s * t;
            stt += t * t;
            uS += du * s;
            uT += du * t;
            vS += dv * s;
            vT += dv * t;
        }

        double det = (sss * stt) - (sst * sst);
        if (Math.Abs(det) < 1e-12)
        {
            return;
        }

        double i00 = stt / det, i01 = -sst / det, i11 = sss / det;
        double au = (uS * i00) + (uT * i01), bu = (uS * i01) + (uT * i11);
        double av = (vS * i00) + (vT * i01), bv = (vS * i01) + (vT * i11);
        D3 gu = (e1 * au) + (e2 * bu), gv = (e1 * av) + (e2 * bv);

        // Anchor: a corner that kept its position (the fixed side of the slice), else the first.
        FaceVertex anchor = corners.FirstOrDefault(fv => (after[fv.Index] - before[fv.Index]).Length < 1e-6) ?? corners[0];
        D3 pa = after[anchor.Index];
        Uv ua = anchor.TextureCoords;
        foreach (FaceVertex fv in corners)
        {
            D3 d = after[fv.Index] - pa;
            fv.TextureCoords = new Uv((float)(ua.U + gu.Dot(d)), (float)(ua.V + gv.Dot(d)));
        }
    }
}

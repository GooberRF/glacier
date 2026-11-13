using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Ged.Core.Editing.Organic;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Analysis;

/// <summary>One measured floor sample (debug view).</summary>
public sealed record DebugSample(double X, double Y, double Z, bool Valid, bool Corridor, double Width);

/// <summary>One detected opening (debug view): centre, travel axis (unit, horizontal), clear width.</summary>
public sealed record DebugOpening(Vec3d Center, Vec3d Axis, double Width, double Height);

/// <summary>One detected stair (debug view): bottom and top tread centres.</summary>
public sealed record DebugStair(Vec3d Bottom, Vec3d Top, double Riser, double? Tread, int Steps);

/// <summary>
/// The scale extractors' intermediate layers for the corpus eyeball set (CORPUS-NORMS-SPEC §4-3: plan renders with
/// openings, corridor samples and stairs). Written to image files by <c>corpus sweep --debug-render</c> only — not a
/// spatial query API, and not exposed to Lua, the CLI's agent commands or MCP.
/// </summary>
public sealed class AnalysisDebugView
{
    public required double Pitch { get; init; }

    public required IReadOnlyList<DebugSample> Samples { get; init; }

    public required IReadOnlyList<DebugOpening> Openings { get; init; }

    public required IReadOnlyList<DebugStair> Stairs { get; init; }

    /// <summary>Floor-face outline edges (world), for context.</summary>
    public required IReadOnlyList<(Vec3d A, Vec3d B)> FloorEdges { get; init; }

    /// <summary>Computes the debug layers of a level (null when it has no compiled geometry).</summary>
    public static AnalysisDebugView? Compute(byte[] bytes, MetricOptions? options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        options ??= new MetricOptions();
        RflFile rfl = RflFile.Load(bytes);
        RflContext ctx = rfl.Context;
        Geometry? geo = null;
        foreach (RflSection s in rfl.Sections)
        {
            if (s.TypeId == (uint)SectionType.StaticGeometry && RflSectionRegistry.TryParse(s, ctx, out IRflSectionContent? c) && c is GeometrySection g)
            {
                geo = g.Geometry;
                break;
            }
        }

        if (geo is not { Faces.Count: > 0 })
        {
            return null;
        }

        SpaceSampler sampler = SpaceSampler.Build(geo, ct, options.SamplerProbe);
        sampler.Measure(options.LatticePitch, options.MaxFloorSamples);
        var edges = new List<(Vec3d, Vec3d)>();
        foreach (SpaceSampler.SFace f in sampler.Faces.Where(f => f.Floor))
        {
            for (int i = 0; i < f.P.Length; i++)
            {
                edges.Add((f.P[i], f.P[(i + 1) % f.P.Length]));
            }
        }

        return new AnalysisDebugView
        {
            Pitch = sampler.Pitch,
            Samples = sampler.Samples.Select(s => new DebugSample(s.P.X, s.P.Y, s.P.Z, s.Valid, s.Corridor, s.Width)).ToList(),
            Openings = sampler.FindOpenings().Select(o => new DebugOpening(o.Center, o.Axis, o.Width, o.Height)).ToList(),
            Stairs = StairDetector.Detect(sampler.Faces, ct).Select(s => new DebugStair(s.Bottom, s.Top, s.Riser, s.Tread, s.Steps)).ToList(),
            FloorEdges = edges,
        };
    }
}

using System;
using System.Collections.Generic;

namespace Ged.Core.Editing;

/// <summary>
/// Pure screen-space logic for world-viewport marquee (drag-box) selection. The App
/// projects each candidate's screen point through the camera and calls
/// <see cref="Select"/>; this owns the rectangle math, the click-vs-drag threshold,
/// and the selection-filter gating (a candidate's kind must be enabled in the active
/// filter). Ctrl-add vs replace is applied by the caller with the returned ids.
/// </summary>
public static class MarqueeSelection
{
    /// <summary>An axis-aligned screen rectangle (pixels, top-left origin).</summary>
    public readonly record struct Rect(float MinX, float MinY, float MaxX, float MaxY);

    /// <summary>A pickable candidate projected to a screen point.</summary>
    public readonly record struct Candidate(int Id, SelectKinds Kind, float X, float Y);

    /// <summary>The normalized rectangle spanned by two screen corners.</summary>
    public static Rect FromCorners(float x0, float y0, float x1, float y1) => new(
        MathF.Min(x0, x1), MathF.Min(y0, y1), MathF.Max(x0, x1), MathF.Max(y0, y1));

    /// <summary>True when a drag is big enough to be a marquee rather than a click (defer to pick).</summary>
    public static bool IsMarquee(float x0, float y0, float x1, float y1, float threshold = 3f) =>
        (MathF.Abs(x1 - x0) + MathF.Abs(y1 - y0)) > threshold;

    /// <summary>True when the screen point lies inside the rectangle.</summary>
    public static bool Contains(Rect r, float px, float py) =>
        px >= r.MinX && px <= r.MaxX && py >= r.MinY && py <= r.MaxY;

    /// <summary>The active selection filter admits a candidate of the given kind.</summary>
    public static bool Admits(SelectKinds active, SelectKinds candidate) => (active & candidate) != 0;

    /// <summary>
    /// The ids of every candidate whose kind the filter admits and whose screen point
    /// falls inside the rectangle.
    /// </summary>
    public static List<int> Select(Rect rect, IEnumerable<Candidate> candidates, SelectKinds active)
    {
        var hits = new List<int>();
        foreach (Candidate c in candidates)
        {
            if (Admits(active, c.Kind) && Contains(rect, c.X, c.Y))
            {
                hits.Add(c.Id);
            }
        }

        return hits;
    }
}

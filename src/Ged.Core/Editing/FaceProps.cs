using System.Linq;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// Per-face property accessors used by Texture mode: the face-flag toggles
/// (full-bright, alpha, holes, invisible, show-sky, mirrored, liquid, scroll,
/// detail), the 2-bit lightmap resolution, the 32-bit smoothing-group mask, and the
/// per-face UV scroll velocities (stored in the geometry's face-scroll table keyed
/// by face id). Pure bit/table manipulation; the App wraps writes in undo-able
/// brush edits and shows mixed-value state across a multi-face selection.
/// </summary>
public static class FaceProps
{
    /// <summary>Reads a single face flag.</summary>
    public static bool Get(Face f, FaceFlags flag) => (f.Flags & (ushort)flag) != 0;

    /// <summary>Sets or clears a single face flag.</summary>
    public static void Set(Face f, FaceFlags flag, bool value)
    {
        if (value)
        {
            f.Flags |= (ushort)flag;
        }
        else
        {
            f.Flags = (ushort)(f.Flags & ~(ushort)flag);
        }
    }

    /// <summary>The 2-bit lightmap resolution (0 lowest .. 3 highest).</summary>
    public static int GetLightmapResolution(Face f) => (f.Flags & (ushort)FaceFlags.LightmapResolutionMask) >> 8;

    /// <summary>Sets the 2-bit lightmap resolution (clamped to 0..3).</summary>
    public static void SetLightmapResolution(Face f, int resolution)
    {
        int r = resolution < 0 ? 0 : (resolution > 3 ? 3 : resolution);
        f.Flags = (ushort)((f.Flags & ~(ushort)FaceFlags.LightmapResolutionMask) | (r << 8));
    }

    /// <summary>Reads one of the 32 smoothing-group bits.</summary>
    public static bool GetSmoothingGroup(Face f, int group) => group is >= 0 and < 32 && (f.SmoothingGroups & (1u << group)) != 0;

    /// <summary>Sets or clears one of the 32 smoothing-group bits.</summary>
    public static void SetSmoothingGroup(Face f, int group, bool value)
    {
        if (group is < 0 or >= 32)
        {
            return;
        }

        if (value)
        {
            f.SmoothingGroups |= 1u << group;
        }
        else
        {
            f.SmoothingGroups &= ~(1u << group);
        }
    }

    // ---- Scroll velocities (geometry face-scroll table, keyed by face id) ------

    /// <summary>The per-face UV scroll velocity, or (0,0) when the face has no scroll entry.</summary>
    public static Uv GetScroll(Geometry g, Face f)
    {
        FaceScrollData? s = g.FaceScrollData.FirstOrDefault(x => x.FaceId == f.FaceId);
        return s is null ? default : new Uv(s.UVelocity, s.VVelocity);
    }

    /// <summary>
    /// Sets the per-face UV scroll velocity. A non-zero velocity marks the face
    /// <see cref="FaceFlags.ScrollTexture"/> and adds/updates its scroll-table entry;
    /// a zero velocity clears the flag and removes the entry.
    /// </summary>
    public static void SetScroll(Geometry g, Face f, float uVelocity, float vVelocity)
    {
        bool scrolls = uVelocity != 0f || vVelocity != 0f;
        Set(f, FaceFlags.ScrollTexture, scrolls);

        FaceScrollData? existing = g.FaceScrollData.FirstOrDefault(x => x.FaceId == f.FaceId);
        if (!scrolls)
        {
            if (existing is not null)
            {
                g.FaceScrollData.Remove(existing);
            }

            return;
        }

        if (existing is null)
        {
            g.FaceScrollData.Add(new FaceScrollData { FaceId = f.FaceId, UVelocity = uVelocity, VVelocity = vVelocity });
        }
        else
        {
            existing.UVelocity = uVelocity;
            existing.VVelocity = vVelocity;
        }
    }
}

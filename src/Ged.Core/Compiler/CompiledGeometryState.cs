using System.Linq;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;

namespace Ged.Core.Compiler;

/// <summary>
/// Whether a level carries the compiled static geometry the game needs. Red Faction builds its world
/// from the compiled geometry section (rooms, faces), never from the editor's brushes, and crashes
/// loading a level that has brushes but no compiled geometry — so every writer that saves for play,
/// and the linter, ask this before trusting a file.
/// </summary>
public static class CompiledGeometryState
{
    /// <summary>True when the level has at least one brush.</summary>
    public static bool HasBrushes(RflFile rfl)
    {
        rfl.ParseAllKnownSections();
        return rfl.Sections.Any(s => s.Content is BrushesSection { Brushes.Count: > 0 });
    }

    /// <summary>True when the level has a compiled geometry section with faces and at least one room.</summary>
    public static bool HasCompiledGeometry(RflFile rfl)
    {
        rfl.ParseAllKnownSections();
        return rfl.Sections.Select(s => s.Content).OfType<GeometrySection>()
            .Any(g => g.Geometry.Faces.Count > 0 && g.Geometry.Rooms.Count > 0);
    }

    /// <summary>True when the level has brushes but no usable compiled geometry: it must be built before it is played.</summary>
    public static bool IsMissing(RflFile rfl) => HasBrushes(rfl) && !HasCompiledGeometry(rfl);
}

using System;
using System.Collections.Generic;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Linting;

/// <summary>
/// Geometry + budget statistics for the statistics dashboard. Geometry counts come
/// from the compiled <c>static_geometry</c> section (post-build); the budget bars
/// come from <see cref="LevelBudget"/>.
/// </summary>
public sealed record LevelStatistics(
    int Faces,
    int Vertices,
    int FaceVertices,
    int Rooms,
    int MainRooms,
    int Subrooms,
    int Portals,
    int Surfaces,
    int LightmapPages,
    int Brushes,
    IReadOnlyList<BudgetLine> Budgets)
{
    /// <summary>An empty (no-geometry) statistics record with zeroed budgets.</summary>
    public static LevelStatistics Empty { get; } = new(
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<BudgetLine>());
}

/// <summary>Computes a <see cref="LevelStatistics"/> snapshot from a parsed level.</summary>
public static class LevelStatisticsBuilder
{
    public static LevelStatistics Compute(RflFile rfl)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        rfl.ParseAllKnownSections();

        Geometry? geo = null;
        int lightmapPages = 0;
        int brushes = 0;

        foreach (RflSection section in rfl.Sections)
        {
            switch (section.Content)
            {
                case GeometrySection g when geo is null:
                    geo = g.Geometry;
                    break;
                case LightmapsSection lm:
                    lightmapPages = lm.Lightmaps.Count;
                    break;
                case BrushesSection bs:
                    brushes = bs.Brushes.Count;
                    break;
            }
        }

        int faces = 0, faceVerts = 0, verts = 0, rooms = 0, mainRooms = 0, subrooms = 0, portals = 0, surfaces = 0;
        if (geo is not null)
        {
            faces = geo.Faces.Count;
            foreach (Face f in geo.Faces)
            {
                faceVerts += f.Vertices.Count;
            }

            verts = geo.Vertices.Count;
            rooms = geo.Rooms.Count;
            foreach (Room r in geo.Rooms)
            {
                if (r.IsSubroom != 0)
                {
                    subrooms++;
                }
                else
                {
                    mainRooms++;
                }
            }

            portals = geo.Portals.Count;
            surfaces = geo.Surfaces.Count;
        }

        return new LevelStatistics(
            faces, verts, faceVerts, rooms, mainRooms, subrooms, portals, surfaces,
            lightmapPages, brushes, LevelBudget.Compute(rfl));
    }
}

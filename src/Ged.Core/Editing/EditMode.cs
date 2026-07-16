namespace Ged.Core.Editing;

/// <summary>
/// The editing modes. Each scopes what a click selects and which tool panel is shown.
/// (the former Texture mode was merged into Face mode — texturing lives on
/// Face mode's Texture/UV tab, so there is no separate Texture mode.)
/// </summary>
public enum EditMode
{
    Group,
    Brush,
    Face,
    Edge,
    Vertex,
    Object,
}

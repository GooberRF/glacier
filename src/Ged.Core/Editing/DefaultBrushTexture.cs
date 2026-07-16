using Ged.Core.Assets;

namespace Ged.Core.Editing;

/// <summary>
/// The new-brush default-texture guard (the white-brush fix, single source of truth). Every
/// brush-creation path — the Brush panel, the Draw Brush tool, and the scripting API's
/// <c>level.place_box</c> — resolves its configured per-orientation default texture through
/// this before stamping it on faces: a blank preference falls back to the stock rock default,
/// and a configured name that does not resolve in the mounted VFS (a stale persisted default
/// like the historical "Rck_Default01.tga", or a typo) also falls back, so a new brush never
/// renders the white missing-texture fallback while face properties show a dead name.
/// Unverifiable names (no VFS mounted) are kept rather than second-guessed.
/// </summary>
public static class DefaultBrushTexture
{
    /// <summary>Resolves a configured default-texture name against the mounted VFS (see class doc).</summary>
    public static string Resolve(AssetVfs? vfs, string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return BrushCreateParams.DefaultTexture;
        }

        if (vfs is null || vfs.ResolveTexture(configured) is not null)
        {
            return configured;
        }

        return BrushCreateParams.DefaultTexture;
    }
}

namespace Ged.Core.IO.Tex;

/// <summary>
/// Implements Red Faction / Alpine's texture "supercede" resolution: a request for
/// any texture name is satisfied by the highest-priority sibling that exists on
/// disk, regardless of the extension the level actually referenced. Priority order
/// is <c>.atx &gt; .dds &gt; .png &gt; .jpg &gt; .jpeg &gt; .vbm &gt; .tga</c>
/// (per Alpine's <c>editor_patch/textures.cpp</c> and <c>bmpman/atx.cpp</c>).
/// </summary>
public static class SupercedeChain
{
    /// <summary>Recognised texture extensions in descending supercede priority.</summary>
    public static readonly IReadOnlyList<string> Extensions = new[]
    {
        ".atx", ".dds", ".png", ".jpg", ".jpeg", ".vbm", ".tga",
    };

    /// <summary>True if <paramref name="extension"/> (with leading dot) is a recognised texture extension.</summary>
    public static bool IsTextureExtension(string extension) =>
        Extensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase));

    /// <summary>The name without its texture extension (case-insensitive); unchanged if it has none.</summary>
    public static string GetBaseName(string textureName)
    {
        ArgumentNullException.ThrowIfNull(textureName);
        int dot = textureName.LastIndexOf('.');
        if (dot >= 0 && IsTextureExtension(textureName[dot..]))
        {
            return textureName[..dot];
        }

        return textureName;
    }

    /// <summary>
    /// Resolves the winning file name for a texture reference. <paramref name="exists"/>
    /// is queried with candidate file names (base + each extension, highest priority
    /// first); the first hit wins. Returns null when no candidate exists.
    /// </summary>
    public static string? Resolve(string textureName, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(textureName);
        ArgumentNullException.ThrowIfNull(exists);

        string baseName = GetBaseName(textureName);
        foreach (string ext in Extensions)
        {
            string candidate = baseName + ext;
            if (exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Enumerates every candidate file name for a texture reference, highest priority first.</summary>
    public static IEnumerable<string> Candidates(string textureName)
    {
        string baseName = GetBaseName(textureName);
        foreach (string ext in Extensions)
        {
            yield return baseName + ext;
        }
    }
}

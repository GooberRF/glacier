namespace Ged.Core.Assets;

/// <summary>Options controlling how <see cref="GameMount"/> assembles a game install's VFS.</summary>
public sealed class GameMountOptions
{
    /// <summary>Mount <c>alpinefaction.vpp</c> (above base VPPs) when present. Default true.</summary>
    public bool MountAlpineVpp { get; set; } = true;

    /// <summary>The user-maps directory name under the install root. Default "user_maps".</summary>
    public string UserMapsDirName { get; set; } = "user_maps";

    /// <summary>Extra directories to mount at the very top (highest priority), in order.</summary>
    public IReadOnlyList<string> ExtraDirectories { get; set; } = Array.Empty<string>();

    /// <summary>Extra VPP files to mount just above the base VPPs, in order.</summary>
    public IReadOnlyList<string> ExtraVpps { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Assembles an <see cref="AssetVfs"/> for a real Red Faction install, mirroring the
/// game + Alpine editor mount order (highest priority first):
/// <list type="number">
/// <item>caller-supplied extra directories,</item>
/// <item><c>user_maps/textures/&lt;subdir&gt;</c> as "Custom - &lt;subdir&gt;" categories (sorted),</item>
/// <item><c>user_maps/textures</c> ("Custom" root),</item>
/// <item><c>user_maps</c> loose files (meshes/levels),</item>
/// <item>install-root loose files,</item>
/// <item>caller-supplied extra VPPs, then <c>alpinefaction.vpp</c>,</item>
/// <item>base-game VPPs (alphabetical).</item>
/// </list>
/// Loose content overriding packfiles matches RF's <c>file_add_path</c> search paths;
/// the "Custom - &lt;dir&gt;" categories match Alpine's editor_patch/textures.cpp.
/// </summary>
public static class GameMount
{
    private static readonly string[] LooseAssetExtensions =
    {
        ".tga", ".vbm", ".dds", ".atx", ".png", ".jpg", ".jpeg",
        ".v3m", ".v3c", ".vcm", ".v3d", ".rfa", ".vfx",
        ".wav", ".ogg", ".tbl", ".rfl",
        ".txt", // texture-list files (maps*.txt / maps_af.txt) may be overridden loose
    };

    private const string AlpineVppName = "alpinefaction.vpp";

    /// <summary>Builds the standard VFS for the install rooted at <paramref name="installDir"/>.</summary>
    public static AssetVfs Mount(string installDir, GameMountOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(installDir);
        if (!Directory.Exists(installDir))
        {
            throw new DirectoryNotFoundException($"RF install directory not found: {installDir}");
        }

        options ??= new GameMountOptions();
        var sources = new List<IAssetSource>();

        // 1. Caller-supplied top-priority directories.
        foreach (string dir in options.ExtraDirectories)
        {
            if (Directory.Exists(dir))
            {
                sources.Add(new DirectoryAssetSource(dir, recursive: false, extensions: LooseAssetExtensions));
            }
        }

        string userMaps = Path.Combine(installDir, options.UserMapsDirName);
        string userTextures = Path.Combine(userMaps, "textures");

        // 2. user_maps/textures/<subdir> as "Custom - <subdir>" categories.
        if (Directory.Exists(userTextures))
        {
            foreach (string sub in Directory.EnumerateDirectories(userTextures)
                         .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                string label = "Custom - " + Path.GetFileName(sub);
                sources.Add(new DirectoryAssetSource(sub, category: label, recursive: false,
                    extensions: TextureExtensions));
            }

            // 3. user_maps/textures root loose textures ("Custom").
            sources.Add(new DirectoryAssetSource(userTextures, category: "Custom", recursive: false,
                extensions: TextureExtensions));
        }

        // 4. user_maps loose files (meshes/levels/etc.).
        if (Directory.Exists(userMaps))
        {
            sources.Add(new DirectoryAssetSource(userMaps, recursive: false, extensions: LooseAssetExtensions));
        }

        // 5. Install-root loose files (mod overrides dropped next to the exe).
        sources.Add(new DirectoryAssetSource(installDir, recursive: false, extensions: LooseAssetExtensions));

        // 6a. Caller-supplied extra VPPs.
        foreach (string vpp in options.ExtraVpps)
        {
            if (File.Exists(vpp))
            {
                sources.Add(VppAssetSource.Open(vpp));
            }
        }

        // 6b. alpinefaction.vpp, if present and enabled.
        string alpineVpp = Path.Combine(installDir, AlpineVppName);
        if (options.MountAlpineVpp && File.Exists(alpineVpp))
        {
            sources.Add(VppAssetSource.Open(alpineVpp));
        }

        // 7. Base-game VPPs (alphabetical), excluding the Alpine VPP already mounted.
        foreach (string vpp in Directory.EnumerateFiles(installDir, "*.vpp")
                     .OrderBy(v => Path.GetFileName(v), StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetFileName(vpp).Equals(AlpineVppName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            sources.Add(VppAssetSource.Open(vpp));
        }

        return new AssetVfs(sources);
    }

    private static readonly string[] TextureExtensions =
    {
        ".tga", ".vbm", ".dds", ".atx", ".png", ".jpg", ".jpeg",
    };
}

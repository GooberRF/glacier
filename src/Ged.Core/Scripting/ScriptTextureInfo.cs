using System;
using Ged.Core.Assets;

namespace Ged.Core.Scripting;

/// <summary>One texture's provenance (Lua type <c>texture_info</c>; ORGANIC-GEOMETRY-SPEC §2.6).</summary>
[ScriptDoc("A texture's provenance: whether it exists, whether every player has it (stock), where the shown file comes from, and its pixel size.", Since = 2)]
public sealed class ScriptTextureInfo
{
    private readonly AssetVfs? _vfs;
    private readonly string? _resolved;
    private readonly Lazy<(int W, int H, Editing.TextureGrain Grain)> _size;

    internal ScriptTextureInfo(string name, AssetVfs? vfs, TextureProvenance? p)
    {
        Name = name;
        _vfs = vfs;
        _resolved = p?.ResolvedName;
        Exists = p is not null;
        Stock = p?.Stock ?? false;
        Origin = p is null ? null : StockContent.LuaName(p.Origin);
        Source = p?.Source;
        Overridden = p?.Overridden ?? false;
        _size = new Lazy<(int, int, Editing.TextureGrain)>(Decode);
    }

    [ScriptDoc("The texture name.", Since = 2)]
    public string Name { get; }

    [ScriptDoc("True when a file for the texture resolves in the mounted library (through the .dds/.png/.tga supercede chain).", Since = 2)]
    public bool Exists { get; }

    [ScriptDoc("True when a retail Red Faction packfile contains this texture: every player has it (identified by packfile name).", Since = 2)]
    public bool Stock { get; }

    [ScriptDoc("Where the file the editor shows comes from: \"stock\", \"alpine\", \"packfile\" (another .vpp) or \"loose\" (a folder); nil when it does not exist.", Since = 2)]
    public string? Origin { get; }

    [ScriptDoc("The packfile name or loose folder name the shown file comes from (never a full path); nil when it does not exist.", Since = 2)]
    public string? Source { get; }

    [ScriptDoc("True when the texture is stock but the file shown comes from somewhere else (a mod or loose file overrides it).", Since = 2)]
    public bool Overridden { get; }

    [ScriptDoc("The width in pixels (0 when missing or not decodable).", Since = 2)]
    public int Width => _size.Value.W;

    [ScriptDoc("The height in pixels (0 when missing or not decodable).", Since = 2)]
    public int Height => _size.Value.H;

    [ScriptDoc("Which image axis the texture's features run along: \"u\" (a girder lying down, a wide strip), \"v\" (a post standing up, a tall strip) or nil (a panel or plate). uv_align lays it along beams and posts.", Since = 2)]
    public string? Grain => Editing.TextureGrains.LuaName(_size.Value.Grain);

    public override string ToString() => Exists ? $"texture_info({Name}, {Origin}{(Stock ? ", stock" : string.Empty)})" : $"texture_info({Name}, missing)";

    private (int, int, Editing.TextureGrain) Decode()
    {
        if (_vfs is null || _resolved is null)
        {
            return (0, 0, Editing.TextureGrain.None);
        }

        try
        {
            return _vfs.LoadTexture(_resolved) is { Width: > 0, Height: > 0 } d
                ? (d.Width, d.Height, Editing.TextureGrains.Of(d.Width, d.Height, Editing.TextureGrains.Measure(d.Primary)))
                : (0, 0, Editing.TextureGrain.None);
        }
        catch (Exception)
        {
            return (0, 0, Editing.TextureGrain.None); // undecodable: reported as 0 × 0
        }
    }
}

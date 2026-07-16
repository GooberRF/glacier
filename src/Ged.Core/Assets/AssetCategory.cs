namespace Ged.Core.Assets;

/// <summary>A named group of texture files for the browser (e.g. "Custom - abruptdecay", "All").</summary>
public sealed class AssetCategory
{
    public AssetCategory(string name, IReadOnlyList<string> files)
    {
        Name = name;
        Files = files;
    }

    public string Name { get; }

    /// <summary>Bare texture file names in this category.</summary>
    public IReadOnlyList<string> Files { get; }

    public override string ToString() => $"{Name} ({Files.Count})";
}

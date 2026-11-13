using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Ged.Core.IO.Rfg;
using Ged.Core.Model;

namespace Ged.Core.Prefabs;

/// <summary>
/// A <c>.gedprefab</c> file: a plain zip (BCL <see cref="ZipArchive"/>) bundling a
/// <c>manifest.json</c> (<see cref="PrefabManifest"/>), the <c>payload.rfg</c> group
/// serialization (reused for placement with full UID remap), and an optional
/// <c>thumbnail.png</c>. Placement is a standard <see cref="RfgInterop"/> import, so
/// links are remapped exactly as for a .rfg drop.
/// </summary>
public sealed class PrefabPackage
{
    public const string Extension = ".gedprefab";
    private const string ManifestEntry = "manifest.json";
    private const string PayloadEntry = "payload.rfg";
    private const string ThumbnailEntry = "thumbnail.png";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public required PrefabManifest Manifest { get; init; }

    public required RfgFile Payload { get; init; }

    /// <summary>The rendered thumbnail PNG bytes, or null when the package carried none.</summary>
    public byte[]? Thumbnail { get; init; }

    /// <summary>Writes a prefab package (manifest + payload + optional thumbnail) to <paramref name="path"/>.</summary>
    public static void Save(string path, PrefabManifest manifest, RfgFile payload, byte[]? thumbnailPng)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using FileStream fs = File.Create(path);
        Save(fs, manifest, payload, thumbnailPng);
    }

    /// <summary>Writes a prefab package to <paramref name="stream"/>.</summary>
    public static void Save(Stream stream, PrefabManifest manifest, RfgFile payload, byte[]? thumbnailPng)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(payload);

        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);

        using (Stream m = zip.CreateEntry(ManifestEntry).Open())
        {
            JsonSerializer.Serialize(m, manifest, JsonOptions);
        }

        using (Stream p = zip.CreateEntry(PayloadEntry).Open())
        {
            byte[] rfgBytes = payload.Save();
            p.Write(rfgBytes, 0, rfgBytes.Length);
        }

        if (thumbnailPng is { Length: > 0 })
        {
            using Stream t = zip.CreateEntry(ThumbnailEntry).Open();
            t.Write(thumbnailPng, 0, thumbnailPng.Length);
        }
    }

    /// <summary>Reads a full prefab package (manifest + payload + thumbnail) from <paramref name="path"/>.</summary>
    public static PrefabPackage Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using FileStream fs = File.OpenRead(path);
        return Load(fs);
    }

    /// <summary>Reads a full prefab package from <paramref name="stream"/>.</summary>
    public static PrefabPackage Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        PrefabManifest manifest = ReadManifest(zip);
        RfgFile payload = ReadPayload(zip)
            ?? throw new InvalidDataException("Prefab is missing its payload.rfg entry.");
        byte[]? thumb = ReadEntryBytes(zip, ThumbnailEntry);

        return new PrefabPackage { Manifest = manifest, Payload = payload, Thumbnail = thumb };
    }

    /// <summary>
    /// Reads a package for placement: the package, its payload normalized to FIXED prefab-local space
    /// (<see cref="BasedPayload"/>), and the file's source hash (<see cref="FileHash(byte[])"/>, the
    /// lineage record's propagation key). The file is read once.
    /// </summary>
    public static (PrefabPackage Package, RfgFile Payload, string Hash) LoadBased(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        byte[] bytes = File.ReadAllBytes(path);
        using var ms = new MemoryStream(bytes, writable: false);
        PrefabPackage pkg = Load(ms);
        return (pkg, BasedPayload(pkg), FileHash(bytes));
    }

    /// <summary>
    /// The package payload in FIXED prefab-local space (origin == pivot). A legacy v1 package (not
    /// <see cref="PrefabManifest.PivotBased"/>) establishes its pivot once — the payload's bounding-box
    /// centre — and is re-based in memory, then treated as fixed for this placement.
    /// </summary>
    public static RfgFile BasedPayload(PrefabPackage pkg)
    {
        ArgumentNullException.ThrowIfNull(pkg);
        RfgFile payload = pkg.Payload;
        if (!pkg.Manifest.PivotBased)
        {
            Editing.RfgInterop.TransformInPlace(payload, Mat3.Identity, Editing.RfgInterop.ComputePivot(payload).Scale(-1f));
        }

        return payload;
    }

    /// <summary>The source hash of a package file (first 16 hex digits of its SHA-256), or "" when unreadable.</summary>
    public static string FileHash(string path)
    {
        try
        {
            return FileHash(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return string.Empty;
        }
    }

    /// <summary>The source hash of package bytes (first 16 hex digits of their SHA-256).</summary>
    public static string FileHash(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16];
    }

    /// <summary>Reads only the manifest + thumbnail (skips parsing the RFG payload) for the library grid.</summary>
    public static (PrefabManifest Manifest, byte[]? Thumbnail) LoadHeader(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using FileStream fs = File.OpenRead(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        return (ReadManifest(zip), ReadEntryBytes(zip, ThumbnailEntry));
    }

    private static PrefabManifest ReadManifest(ZipArchive zip)
    {
        ZipArchiveEntry? entry = zip.GetEntry(ManifestEntry);
        if (entry is null)
        {
            return new PrefabManifest { Name = "(no manifest)" };
        }

        using Stream s = entry.Open();
        return JsonSerializer.Deserialize<PrefabManifest>(s) ?? new PrefabManifest();
    }

    private static RfgFile? ReadPayload(ZipArchive zip)
    {
        byte[]? bytes = ReadEntryBytes(zip, PayloadEntry);
        return bytes is null ? null : RfgFile.Load(bytes);
    }

    private static byte[]? ReadEntryBytes(ZipArchive zip, string name)
    {
        ZipArchiveEntry? entry = zip.GetEntry(name);
        if (entry is null)
        {
            return null;
        }

        using Stream s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}

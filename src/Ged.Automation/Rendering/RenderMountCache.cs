using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Ged.Core.Assets;

namespace Ged.Automation.Rendering;

/// <summary>
/// Keeps one mounted Red Faction install (its <see cref="AssetVfs"/> and <see cref="TextureTraitsCache"/>) alive
/// across renders, so a long-lived process that renders repeatedly (the MCP server) opens the install's packfiles
/// once instead of once per render. Each render still gets a fresh private <see cref="HeadlessAutomationHost"/> and
/// a fresh document; only the asset mount is shared.
/// <para>
/// <b>Validity.</b> The mount is keyed by the install folder, every <c>*.vpp</c> in it (name, size, last write
/// time) and the last write times of the loose folders <see cref="GameMount"/> mounts (the install root,
/// <c>user_maps</c>, <c>user_maps/textures</c> and its sub-folders). Any change — a packfile added, removed or
/// rewritten, a loose texture added — produces a new key and a fresh mount on the next render.
/// </para>
/// <para>
/// <b>Thread safety.</b> A mount is used by one render at a time: <see cref="Acquire"/> hands out an exclusive
/// lease (the next caller waits until it is released), because packfile reads share one file stream.
/// </para>
/// <para>
/// <b>Reads.</b> Packfile reads are kept in memory (up to <see cref="ReadCacheBytes"/> in total), so a repeat
/// render of the same level reads no texture from disk at all — which matters when the install sits on a disk
/// that is slow to wake.
/// </para>
/// </summary>
public sealed class RenderMountCache : IDisposable
{
    /// <summary>The default packfile read budget (bytes kept in memory across renders).</summary>
    public const long DefaultReadCacheBytes = 256L * 1024 * 1024;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<string, AssetVfs> _mount;
    private Entry? _entry;
    private int _mounts;
    private bool _disposed;

    /// <param name="mount">Mounts an install folder (default <see cref="GameMount.Mount(string, GameMountOptions?)"/>).</param>
    /// <param name="readCacheBytes">The packfile read budget.</param>
    public RenderMountCache(Func<string, AssetVfs>? mount = null, long readCacheBytes = DefaultReadCacheBytes)
    {
        _mount = mount ?? (dir => GameMount.Mount(dir));
        ReadCacheBytes = Math.Max(0, readCacheBytes);
    }

    /// <summary>The packfile read budget in bytes.</summary>
    public long ReadCacheBytes { get; }

    /// <summary>How many times an install has been mounted (a reused mount does not count).</summary>
    public int MountCount => Volatile.Read(ref _mounts);

    /// <summary>
    /// Takes the mount for <paramref name="rfInstallDir"/> exclusively, mounting it first when there is none or its
    /// key changed (the previous mount is disposed). Dispose the lease when the render is done. Throws what the
    /// mount throws (e.g. <see cref="DirectoryNotFoundException"/>); the cache is then left without a mount.
    /// </summary>
    public RenderMountLease Acquire(string rfInstallDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(rfInstallDir);
        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            string dir = Path.GetFullPath(rfInstallDir);
            string key = KeyFor(dir);
            bool reused = _entry is { } current && current.Key == key;
            if (!reused)
            {
                _entry?.Dispose();
                _entry = null;
                AssetVfs mounted = _mount(dir);
                var shared = new AssetVfs(mounted.Sources.Select(s => s is VppAssetSource vpp ? new CachedReadSource(vpp, this) : s));
                _entry = new Entry(key, shared, new TextureTraitsCache(shared));
                Interlocked.Increment(ref _mounts);
            }

            return new RenderMountLease(this, rfInstallDir, _entry!.Assets, _entry.Traits, reused);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    internal void Release() => _gate.Release();

    /// <summary>
    /// The validity key of an install folder: its full path, every packfile's name, size and last write time,
    /// and the last write times of the loose folders a mount reads.
    /// </summary>
    public static string KeyFor(string rfInstallDir)
    {
        string dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rfInstallDir));
        var sb = new StringBuilder();
        sb.Append(dir.ToUpperInvariant()).Append('\n');
        if (!Directory.Exists(dir))
        {
            return sb.Append("missing").ToString();
        }

        foreach (FileInfo f in new DirectoryInfo(dir).EnumerateFiles("*.vpp").OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(f.Name.ToUpperInvariant()).Append('|').Append(f.Length).Append('|').Append(f.LastWriteTimeUtc.Ticks).Append('\n');
        }

        string userMaps = Path.Combine(dir, "user_maps");
        string textures = Path.Combine(userMaps, "textures");
        var loose = new List<string> { dir, userMaps, textures };
        if (Directory.Exists(textures))
        {
            loose.AddRange(Directory.EnumerateDirectories(textures).OrderBy(d => d, StringComparer.OrdinalIgnoreCase));
        }

        foreach (string d in loose)
        {
            sb.Append(d.ToUpperInvariant()).Append('|')
              .Append(Directory.Exists(d) ? Directory.GetLastWriteTimeUtc(d).Ticks : -1).Append('\n');
        }

        return sb.ToString();
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _entry?.Dispose();
            _entry = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- read budget (touched only under the lease) -----------------------------------------------

    private long _cachedBytes;

    private bool Reserve(int bytes)
    {
        if (_cachedBytes + bytes > ReadCacheBytes)
        {
            return false;
        }

        _cachedBytes += bytes;
        return true;
    }

    private sealed class Entry : IDisposable
    {
        public Entry(string key, AssetVfs assets, TextureTraitsCache traits)
        {
            Key = key;
            Assets = assets;
            Traits = traits;
        }

        public string Key { get; }

        public AssetVfs Assets { get; }

        public TextureTraitsCache Traits { get; }

        public void Dispose() => Assets.Dispose();
    }

    /// <summary>
    /// A packfile whose reads are kept in memory (within the cache's budget). Not a
    /// <see cref="DirectoryAssetSource"/>, so loose-file lookups are unaffected; disposing it closes the packfile
    /// and returns its share of the budget.
    /// </summary>
    private sealed class CachedReadSource : IAssetSource, IDisposable
    {
        private readonly VppAssetSource _inner;
        private readonly RenderMountCache _owner;
        private readonly Dictionary<string, byte[]?> _reads = new(StringComparer.OrdinalIgnoreCase);
        private long _held;

        public CachedReadSource(VppAssetSource inner, RenderMountCache owner)
        {
            _inner = inner;
            _owner = owner;
        }

        public string Description => _inner.Description;

        public AssetSourceKind Kind => _inner.Kind;

        public string? Category => _inner.Category;

        public bool Contains(string name) => _inner.Contains(name);

        public long? GetSize(string name) => _inner.GetSize(name);

        public IEnumerable<string> EnumerateFiles() => _inner.EnumerateFiles();

        public void Rescan() => _inner.Rescan();

        public byte[]? Read(string name)
        {
            if (_reads.TryGetValue(name, out byte[]? kept))
            {
                return kept is null ? null : (byte[])kept.Clone();
            }

            byte[]? data = _inner.Read(name);
            if (data is null || _owner.Reserve(data.Length))
            {
                _reads[name] = data;
                _held += data?.Length ?? 0;
                return data is null ? null : (byte[])data.Clone();
            }

            return data;
        }

        public void Dispose()
        {
            _owner._cachedBytes -= _held;
            _held = 0;
            _reads.Clear();
            _inner.Dispose();
        }
    }
}

/// <summary>
/// Exclusive use of a <see cref="RenderMountCache"/> mount for one render. Dispose it (or the host it was handed
/// to) when the render is done; the mount itself stays cached.
/// </summary>
public sealed class RenderMountLease : IDisposable
{
    private RenderMountCache? _owner;

    internal RenderMountLease(RenderMountCache owner, string rfInstallDir, AssetVfs assets, TextureTraitsCache traits, bool reused)
    {
        _owner = owner;
        RfInstallDir = rfInstallDir;
        Assets = assets;
        Traits = traits;
        Reused = reused;
    }

    /// <summary>The mounted install folder, as the caller named it.</summary>
    public string RfInstallDir { get; }

    /// <summary>The shared asset mount (do not dispose it).</summary>
    public AssetVfs Assets { get; }

    /// <summary>The shared texture-traits cache over <see cref="Assets"/>.</summary>
    public TextureTraitsCache Traits { get; }

    /// <summary>True when the mount was already cached (no packfile was opened for this lease).</summary>
    public bool Reused { get; }

    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
}

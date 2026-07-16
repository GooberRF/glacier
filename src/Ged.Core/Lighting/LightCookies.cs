using System;
using System.Collections.Generic;

namespace Ged.Core.Lighting;

/// <summary>
/// Builds the per-light cookie resolver the baker consults (item 4). Given the light UID → cookie
/// filename map (from the object-metadata chunk) and an image loader (VFS-backed at build time),
/// it decodes each unique cookie ONCE into a greyscale <see cref="LightCookie"/>, caches by
/// filename, and reports any file that cannot be resolved (the baker then lights without it).
/// Pure Core — the loader delegate keeps it independent of the VFS, so it is unit-testable.
/// </summary>
public static class LightCookies
{
    /// <summary>Loads a cookie image by filename to a top-origin RGBA8 buffer, or null if unavailable.</summary>
    public delegate (int Width, int Height, byte[] Rgba)? ImageLoader(string fileName);

    /// <summary>
    /// Resolves <paramref name="cookiesByUid"/> to a <c>uid → LightCookie?</c> function. Missing /
    /// undecodable cookies resolve to null and are reported once via <paramref name="onMissing"/>.
    /// Returns null when there are no cookies (so the baker skips cookie work entirely).
    /// </summary>
    public static Func<int, LightCookie?>? BuildResolver(
        IReadOnlyDictionary<int, string> cookiesByUid,
        ImageLoader load,
        Action<string>? onMissing = null)
    {
        ArgumentNullException.ThrowIfNull(load);
        if (cookiesByUid.Count == 0)
        {
            return null;
        }

        var byUid = new Dictionary<int, LightCookie?>();
        var byFile = new Dictionary<string, LightCookie?>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<int, string> kv in cookiesByUid)
        {
            string file = kv.Value;
            if (!byFile.TryGetValue(file, out LightCookie? cookie))
            {
                cookie = Decode(load, file, onMissing);
                byFile[file] = cookie;
            }

            byUid[kv.Key] = cookie;
        }

        return uid => byUid.TryGetValue(uid, out LightCookie? c) ? c : null;
    }

    private static LightCookie? Decode(ImageLoader load, string file, Action<string>? onMissing)
    {
        (int Width, int Height, byte[] Rgba)? img = string.IsNullOrWhiteSpace(file) ? null : load(file);
        if (img is { } i && i.Width > 0 && i.Height > 0 && i.Rgba.Length >= i.Width * i.Height * 4)
        {
            return LightCookie.FromRgba(i.Width, i.Height, i.Rgba);
        }

        onMissing?.Invoke(file);
        return null;
    }
}

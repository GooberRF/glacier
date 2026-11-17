using System;
using System.IO;

namespace Ged.Core.Assets;

/// <summary>
/// Writes a disposable cache file atomically: the content goes to a temporary file beside the target, which then
/// replaces the target in one move, so a reader never sees a half-written cache. A cache is an optimisation, so an
/// unwritable folder is not an error: the write is skipped and the cache is rebuilt next time.
/// </summary>
public static class CacheFile
{
    /// <summary>Writes <paramref name="file"/> through <paramref name="write"/>; returns false when the folder is not writable.</summary>
    public static bool TryWriteAtomic(string file, Action<BinaryWriter> write)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(write);
        string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            using (var w = new BinaryWriter(File.Create(temp)))
            {
                write(w);
            }

            File.Move(temp, file, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // Leave the stray temp file; it is ignored by readers.
            }

            return false;
        }
    }
}

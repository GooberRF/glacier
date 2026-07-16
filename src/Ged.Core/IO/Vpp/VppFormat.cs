namespace Ged.Core.IO.Vpp;

/// <summary>
/// Constants describing the VPP v1 packfile container (per <c>vpp_format.h</c>,
/// cross-verified against real archives and RF.exe's loader — see
/// docs/research/format-quirks.md §7).
/// </summary>
public static class VppFormat
{
    /// <summary>Magic value at the start of every VPP header.</summary>
    public const uint Signature = 0x51890ACE;

    /// <summary>The only version GED reads/writes.</summary>
    public const int Version = 1;

    /// <summary>All blocks (header, file table, each file) are padded to this boundary.</summary>
    public const int Alignment = 0x800; // 2048

    /// <summary>Maximum number of files an archive may contain.</summary>
    public const int MaxFiles = 65536;

    /// <summary>Header size in bytes: signature, version, file_count, archive_size.</summary>
    public const int HeaderSize = 16;

    /// <summary>Bytes reserved for a file name inside a directory entry (null-terminated).</summary>
    public const int NameFieldSize = 60;

    /// <summary>Directory entry size: 60-byte name + i32 size.</summary>
    public const int EntrySize = 64;

    /// <summary>Rounds <paramref name="value"/> up to the next <see cref="Alignment"/> boundary.</summary>
    public static long Align(long value) => (value + (Alignment - 1)) & ~((long)Alignment - 1);
}

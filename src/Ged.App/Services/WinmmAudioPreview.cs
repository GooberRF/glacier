using System;
using System.Runtime.InteropServices;

namespace Ged.App.Services;

/// <summary>
/// The Windows <see cref="IAudioPreview"/>: plays a WAV from memory via
/// <c>winmm.dll PlaySound</c> with SND_ASYNC | SND_MEMORY. The source buffer is
/// held alive for the duration of async playback (PlaySound reads from it).
/// </summary>
public sealed class WinmmAudioPreview : IAudioPreview
{
    private const uint SndAsync = 0x0001;
    private const uint SndNodefault = 0x0002;
    private const uint SndMemory = 0x0004;

    // SND_ASYNC plays from the buffer, which must stay alive for the duration.
    private byte[]? _buffer;

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(byte[]? data, IntPtr hmod, uint flags);

    public bool Play(byte[] wavData)
    {
        ArgumentNullException.ThrowIfNull(wavData);
        Stop();
        _buffer = wavData;
        return PlaySound(wavData, IntPtr.Zero, SndAsync | SndMemory | SndNodefault);
    }

    public void Stop()
    {
        try
        {
            PlaySound(null, IntPtr.Zero, 0);
        }
        catch (Exception)
        {
            // Non-fatal.
        }

        _buffer = null;
    }
}

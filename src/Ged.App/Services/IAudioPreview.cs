namespace Ged.App.Services;

/// <summary>
/// Plays a short WAV preview (the ambient-sound Play button) from an in-memory
/// buffer. The <see cref="WinmmAudioPreview"/> is the Windows implementation
/// (winmm <c>PlaySound</c>); CP3 adds a Linux implementation (process-spawn
/// <c>paplay</c>/<c>aplay</c>, or OpenAL) behind this same interface. Asset
/// resolution and the wav-only gate stay in the caller — this contract is purely
/// "play these bytes / stop".
/// </summary>
public interface IAudioPreview
{
    /// <summary>Plays <paramref name="wavData"/> asynchronously; returns false if playback could not start.</summary>
    bool Play(byte[] wavData);

    /// <summary>Stops any in-progress preview.</summary>
    void Stop();
}

/// <summary>The process-wide audio-preview backend. Defaults to the Windows (winmm) implementation.</summary>
public static class AudioPreview
{
    public static IAudioPreview Current { get; set; } = new WinmmAudioPreview();
}

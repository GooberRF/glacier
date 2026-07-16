namespace Ged.Core.Model;

/// <summary>A scripted cutscene (RFL <c>cutscene</c>).</summary>
public sealed class Cutscene
{
    public int Uid { get; set; }

    public byte HidePlayer { get; set; }

    public float Fov { get; set; }

    public List<CutsceneShot> Shots { get; set; } = new();
}

/// <summary>One camera shot within a <see cref="Cutscene"/> (RFL <c>cutscene_shot</c>).</summary>
public sealed class CutsceneShot
{
    public int CameraUid { get; set; }

    public float PreWait { get; set; }

    public float PathTime { get; set; }

    public float PostWait { get; set; }

    public int LookAtUid { get; set; }

    public int TriggerUid { get; set; }

    public string PathName { get; set; } = string.Empty;
}

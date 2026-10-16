namespace Aperture.Core.Playback;

/// <summary>
/// Optional sound processing, both off unless the user turns them on. Neither is AI or leaves
/// the computer: they are ordinary FFmpeg audio filters running inside the player as it plays.
/// </summary>
/// <param name="ClearDialogue">Lift voices above music and effects (FFmpeg's dialoguenhance; surround is mixed to stereo).</param>
/// <param name="EvenVolume">Even out loud and quiet passages, for night viewing (FFmpeg's dynaudnorm).</param>
public sealed record SoundOptions(bool ClearDialogue = false, bool EvenVolume = false)
{
    /// <summary>Both off: the sound is played as it is in the file.</summary>
    public static SoundOptions Off { get; } = new();

    /// <summary>True when nothing is processed.</summary>
    public bool IsOff => !ClearDialogue && !EvenVolume;

    /// <summary>
    /// The libmpv <c>af</c> value for these options: one FFmpeg filter graph, or an empty string
    /// for none. Dialogue enhancement works on stereo, so the sound is mixed to stereo first;
    /// levelling comes last so it sees the result. Its look-ahead is about a second, kept short
    /// so a seek doesn't start in silence for long.
    /// </summary>
    public string FilterGraph()
    {
        var filters = new List<string>();
        if (ClearDialogue)
        {
            filters.Add("aformat=channel_layouts=stereo");
            filters.Add("dialoguenhance=original=1:enhance=1.5:voice=2");
            filters.Add("aformat=channel_layouts=stereo");
        }

        if (EvenVolume)
        {
            filters.Add("dynaudnorm=f=150:g=15:p=0.9:m=8");
        }

        return filters.Count == 0 ? string.Empty : $"lavfi=[{string.Join(',', filters)}]";
    }

    /// <summary>"Clearer dialogue", "Even volume", both joined, or "Off".</summary>
    public string Describe() => (ClearDialogue, EvenVolume) switch
    {
        (true, true) => "Clearer dialogue and even volume",
        (true, false) => "Clearer dialogue",
        (false, true) => "Even volume",
        _ => "Off",
    };
}

namespace Aperture.Core.Input;

/// <summary>Everything the player can do from the keyboard. Names are stored in settings; do not rename.</summary>
public enum PlayerCommand
{
    PlayPause,
    Stop,
    SeekBack,
    SeekForward,
    SeekBackLong,
    SeekForwardLong,
    JumpBack,
    JumpForward,
    GoToStart,
    GoToEnd,
    FrameBack,
    FrameForward,
    VolumeUp,
    VolumeDown,
    Mute,
    Faster,
    Slower,
    NormalSpeed,
    NextSubtitle,
    NextAudio,
    SubtitlesEarlier,
    SubtitlesLater,
    AudioEarlier,
    AudioLater,
    Fullscreen,
    MiniPlayer,
    NextPictureShape,
    PlaybackInfo,
    Adjustments,
    Shortcuts,
    SkipSegment,
    NextEpisode,
    PreviousEpisode,
    AddBookmark,
    Bookmarks,
    LoopSection,
    ClearDialogue,
    EvenVolume,
}

/// <summary>Groups commands for the shortcut list, in the order they are shown.</summary>
public enum CommandGroup
{
    Playback,
    Seeking,
    Sound,
    Subtitles,
    PictureAndWindow,
}

/// <summary>What each command is called and where it is listed.</summary>
public static class PlayerCommands
{
    /// <summary>Every command, in the order the shortcut list shows them.</summary>
    public static IReadOnlyList<PlayerCommand> All { get; } = Enum.GetValues<PlayerCommand>();

    /// <summary>The command's name as shown in menus and the shortcut list.</summary>
    public static string Label(PlayerCommand command) => command switch
    {
        PlayerCommand.PlayPause => "Play or pause",
        PlayerCommand.Stop => "Stop and go Home",
        PlayerCommand.SeekBack => "Back 5 seconds",
        PlayerCommand.SeekForward => "Forward 5 seconds",
        PlayerCommand.SeekBackLong => "Back 30 seconds",
        PlayerCommand.SeekForwardLong => "Forward 30 seconds",
        PlayerCommand.JumpBack => "Back 10 seconds",
        PlayerCommand.JumpForward => "Forward 10 seconds",
        PlayerCommand.GoToStart => "Go to the start",
        PlayerCommand.GoToEnd => "Go to the end",
        PlayerCommand.FrameBack => "Previous frame",
        PlayerCommand.FrameForward => "Next frame",
        PlayerCommand.VolumeUp => "Volume up",
        PlayerCommand.VolumeDown => "Volume down",
        PlayerCommand.Mute => "Mute",
        PlayerCommand.Faster => "Faster",
        PlayerCommand.Slower => "Slower",
        PlayerCommand.NormalSpeed => "Normal speed",
        PlayerCommand.NextSubtitle => "Next subtitles",
        PlayerCommand.NextAudio => "Next audio track",
        PlayerCommand.SubtitlesEarlier => "Show subtitles earlier",
        PlayerCommand.SubtitlesLater => "Show subtitles later",
        PlayerCommand.AudioEarlier => "Play sound earlier",
        PlayerCommand.AudioLater => "Play sound later",
        PlayerCommand.Fullscreen => "Full screen",
        PlayerCommand.MiniPlayer => "Mini player",
        PlayerCommand.NextPictureShape => "Next picture shape",
        PlayerCommand.PlaybackInfo => "Playback info",
        PlayerCommand.Adjustments => "Sync and subtitle appearance",
        PlayerCommand.Shortcuts => "Keyboard shortcuts",
        PlayerCommand.SkipSegment => "Skip intro, recap or credits",
        PlayerCommand.NextEpisode => "Next episode",
        PlayerCommand.PreviousEpisode => "Previous episode",
        PlayerCommand.AddBookmark => "Bookmark this moment",
        PlayerCommand.Bookmarks => "Bookmarks and notes",
        PlayerCommand.LoopSection => "Loop a section (start, end, stop)",
        PlayerCommand.ClearDialogue => "Clearer dialogue on or off",
        PlayerCommand.EvenVolume => "Even volume on or off",
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
    };

    /// <summary>The group the command is listed under.</summary>
    public static CommandGroup Group(PlayerCommand command) => command switch
    {
        PlayerCommand.PlayPause or PlayerCommand.Stop or PlayerCommand.Faster or PlayerCommand.Slower
            or PlayerCommand.NormalSpeed or PlayerCommand.SkipSegment or PlayerCommand.NextEpisode
            or PlayerCommand.PreviousEpisode => CommandGroup.Playback,
        PlayerCommand.SeekBack or PlayerCommand.SeekForward or PlayerCommand.SeekBackLong or PlayerCommand.SeekForwardLong
            or PlayerCommand.JumpBack or PlayerCommand.JumpForward or PlayerCommand.GoToStart or PlayerCommand.GoToEnd
            or PlayerCommand.FrameBack or PlayerCommand.FrameForward or PlayerCommand.AddBookmark or PlayerCommand.Bookmarks
            or PlayerCommand.LoopSection => CommandGroup.Seeking,
        PlayerCommand.VolumeUp or PlayerCommand.VolumeDown or PlayerCommand.Mute or PlayerCommand.NextAudio
            or PlayerCommand.AudioEarlier or PlayerCommand.AudioLater or PlayerCommand.ClearDialogue
            or PlayerCommand.EvenVolume => CommandGroup.Sound,
        PlayerCommand.NextSubtitle or PlayerCommand.SubtitlesEarlier or PlayerCommand.SubtitlesLater => CommandGroup.Subtitles,
        _ => CommandGroup.PictureAndWindow,
    };

    /// <summary>The heading for a group.</summary>
    public static string GroupLabel(CommandGroup group) => group switch
    {
        CommandGroup.Playback => "Playback",
        CommandGroup.Seeking => "Moving around",
        CommandGroup.Sound => "Sound",
        CommandGroup.Subtitles => "Subtitles",
        CommandGroup.PictureAndWindow => "Picture and window",
        _ => throw new ArgumentOutOfRangeException(nameof(group), group, null),
    };

    /// <summary>
    /// Commands that make sense when held down (seeking, volume, frame steps). Others act once
    /// per key press, so holding F does not flicker between full screen and windowed.
    /// </summary>
    public static bool Repeats(PlayerCommand command) => command is
        PlayerCommand.SeekBack or PlayerCommand.SeekForward or PlayerCommand.SeekBackLong or PlayerCommand.SeekForwardLong
        or PlayerCommand.JumpBack or PlayerCommand.JumpForward or PlayerCommand.FrameBack or PlayerCommand.FrameForward
        or PlayerCommand.VolumeUp or PlayerCommand.VolumeDown or PlayerCommand.SubtitlesEarlier or PlayerCommand.SubtitlesLater
        or PlayerCommand.AudioEarlier or PlayerCommand.AudioLater;
}

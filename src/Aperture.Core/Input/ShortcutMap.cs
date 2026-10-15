using static Aperture.Core.Input.KeyNames.Codes;

namespace Aperture.Core.Input;

/// <summary>Why a key could not be given to a command.</summary>
public enum ShortcutProblem
{
    /// <summary>The key was assigned.</summary>
    None,

    /// <summary>Windows or Aperture itself uses this key everywhere (Alt+F4, Ctrl+O, Esc, Tab...).</summary>
    Reserved,

    /// <summary>Shift, Ctrl, Alt or Windows on its own.</summary>
    ModifierOnly,

    /// <summary>Another command already uses the key; see <see cref="ShortcutResult.ConflictsWith"/>.</summary>
    InUse,
}

/// <summary>The outcome of <see cref="ShortcutMap.Assign"/>.</summary>
/// <param name="Map">The map after the change (unchanged when there was a problem).</param>
/// <param name="Problem">Why the key was not assigned, or <see cref="ShortcutProblem.None"/>.</param>
/// <param name="ConflictsWith">The command that already uses the key, for <see cref="ShortcutProblem.InUse"/>.</param>
public sealed record ShortcutResult(ShortcutMap Map, ShortcutProblem Problem, PlayerCommand? ConflictsWith = null)
{
    /// <summary>A sentence explaining the problem, or null when there is none.</summary>
    public string? Message => Problem switch
    {
        ShortcutProblem.Reserved => "Windows or Aperture already uses that key everywhere, so the player can’t take it.",
        ShortcutProblem.ModifierOnly => "Shift, Ctrl and Alt need to be held with another key.",
        ShortcutProblem.InUse when ConflictsWith is { } other => $"That key is used for “{PlayerCommands.Label(other)}”.",
        _ => null,
    };
}

/// <summary>
/// The player's keyboard map: sensible defaults, with any command re-bindable. Immutable;
/// changes return a new map. A key belongs to at most one command, so a key press always
/// means the same thing, and keys Windows or Aperture use everywhere cannot be taken.
/// </summary>
public sealed class ShortcutMap
{
    private static readonly KeyChord[] ReservedChords =
    [
        // Windows.
        new(F4, KeyModifiers.Alt), new(Tab, KeyModifiers.Alt), new(Escape, KeyModifiers.Alt), new(Escape, KeyModifiers.Control),
        new(Space, KeyModifiers.Alt), new(Tab, KeyModifiers.Alt | KeyModifiers.Shift), new(Escape, KeyModifiers.Control | KeyModifiers.Shift),
        new(F10, KeyModifiers.Shift), new(Apps), new(F10),
        // Aperture, everywhere: open, search, settings, library, back, leave full screen, focus movement.
        new(Letter('O'), KeyModifiers.Control), new(Letter('F'), KeyModifiers.Control), new(Comma, KeyModifiers.Control),
        new(Letter('L'), KeyModifiers.Control), new(Left, KeyModifiers.Alt), new(Back), new(Escape), new(Tab), new(Tab, KeyModifiers.Shift),
        new(Enter),
    ];

    private readonly IReadOnlyDictionary<PlayerCommand, IReadOnlyList<KeyChord>> _bindings;
    private readonly Dictionary<KeyChord, PlayerCommand> _byChord;

    private ShortcutMap(IReadOnlyDictionary<PlayerCommand, IReadOnlyList<KeyChord>> bindings)
    {
        _bindings = bindings;
        _byChord = [];
        foreach (var (command, chords) in bindings)
        {
            foreach (var chord in chords)
            {
                _byChord[chord] = command;
            }
        }
    }

    /// <summary>The keys Aperture ships with.</summary>
    public static ShortcutMap Default { get; } = new(DefaultBindings());

    /// <summary>The keys bound to a command, primary first. May be empty.</summary>
    public IReadOnlyList<KeyChord> KeysFor(PlayerCommand command) =>
        _bindings.TryGetValue(command, out var chords) ? chords : [];

    /// <summary>"Space", "Shift+Right", or null when the command has no key.</summary>
    public string? Label(PlayerCommand command) => KeysFor(command) is [var first, ..] ? first.ToString() : null;

    /// <summary>"Pause (Space)" for tooltips; just the text when the command has no key.</summary>
    public string WithKey(string text, PlayerCommand command) => Label(command) is { } key ? $"{text} ({key})" : text;

    /// <summary>The command a key press means, if any.</summary>
    public PlayerCommand? Resolve(KeyChord chord) => _byChord.TryGetValue(chord, out var command) ? command : null;

    /// <summary>True when the command's keys differ from the defaults.</summary>
    public bool IsCustomized(PlayerCommand command) => !KeysFor(command).SequenceEqual(Default.KeysFor(command));

    /// <summary>True for keys that can never be bound.</summary>
    public static bool IsReserved(KeyChord chord) =>
        Array.IndexOf(ReservedChords, chord) >= 0 || chord.Key is LeftWindows or RightWindows;

    /// <summary>
    /// Makes <paramref name="chord"/> the only key for <paramref name="command"/>. When another
    /// command uses it, nothing changes unless <paramref name="replace"/> is true, in which case
    /// the key moves (the other command keeps any other keys it has).
    /// </summary>
    public ShortcutResult Assign(PlayerCommand command, KeyChord chord, bool replace = false)
    {
        if (IsReserved(chord))
        {
            return new ShortcutResult(this, ShortcutProblem.Reserved);
        }

        if (KeyNames.IsModifier(chord.Key))
        {
            return new ShortcutResult(this, ShortcutProblem.ModifierOnly);
        }

        var owner = Resolve(chord);
        if (owner is { } other && other != command && !replace)
        {
            return new ShortcutResult(this, ShortcutProblem.InUse, other);
        }

        var bindings = Copy();
        if (owner is { } previous && previous != command)
        {
            bindings[previous] = bindings[previous].Where(c => c != chord).ToList();
        }

        bindings[command] = [chord];
        return new ShortcutResult(new ShortcutMap(bindings), ShortcutProblem.None);
    }

    /// <summary>Removes every key from a command.</summary>
    public ShortcutMap Clear(PlayerCommand command)
    {
        var bindings = Copy();
        bindings[command] = [];
        return new ShortcutMap(bindings);
    }

    /// <summary>
    /// Puts a command back to its default keys. A default key now used by another command
    /// stays with that command, so resetting never silently changes a different command.
    /// </summary>
    public ShortcutMap Reset(PlayerCommand command)
    {
        var bindings = Copy();
        bindings[command] = Default.KeysFor(command)
            .Where(chord => Resolve(chord) is not { } owner || owner == command)
            .ToList();
        return new ShortcutMap(bindings);
    }

    /// <summary>
    /// The customised commands as text for storage: command name to keys separated by
    /// spaces ("" when the command has no key). Commands left at their defaults are omitted.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToStorage() =>
        PlayerCommands.All
            .Where(IsCustomized)
            .ToDictionary(c => c.ToString(), c => string.Join(' ', KeysFor(c)), StringComparer.Ordinal);

    /// <summary>
    /// Rebuilds a map from <see cref="ToStorage"/> text. Unknown commands, unreadable or
    /// reserved keys, and keys claimed twice are dropped, so a damaged setting can never make
    /// a key ambiguous.
    /// </summary>
    public static ShortcutMap FromStorage(IReadOnlyDictionary<string, string>? stored)
    {
        if (stored is null || stored.Count == 0)
        {
            return Default;
        }

        var overrides = new Dictionary<PlayerCommand, List<KeyChord>>();
        foreach (var (name, text) in stored)
        {
            if (!Enum.TryParse<PlayerCommand>(name, ignoreCase: false, out var command) || !Enum.IsDefined(command))
            {
                continue;
            }

            var chords = new List<KeyChord>();
            foreach (var part in (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (KeyChord.TryParse(part, out var chord) && !IsReserved(chord) && !KeyNames.IsModifier(chord.Key) && !chords.Contains(chord))
                {
                    chords.Add(chord);
                }
            }

            overrides[command] = chords;
        }

        // Customised keys win; defaults keep only keys nobody customised.
        var bindings = new Dictionary<PlayerCommand, IReadOnlyList<KeyChord>>();
        var taken = new HashSet<KeyChord>();
        foreach (var command in PlayerCommands.All)
        {
            if (overrides.TryGetValue(command, out var chords))
            {
                var kept = chords.Where(taken.Add).ToList();
                bindings[command] = kept;
            }
        }

        foreach (var command in PlayerCommands.All.Where(c => !overrides.ContainsKey(c)))
        {
            bindings[command] = Default.KeysFor(command).Where(taken.Add).ToList();
        }

        return new ShortcutMap(bindings);
    }

    private Dictionary<PlayerCommand, IReadOnlyList<KeyChord>> Copy() =>
        PlayerCommands.All.ToDictionary(c => c, KeysFor);

    private static Dictionary<PlayerCommand, IReadOnlyList<KeyChord>> DefaultBindings()
    {
        static KeyChord K(int key, KeyModifiers modifiers = KeyModifiers.None) => new(key, modifiers);
        static KeyChord L(char letter, KeyModifiers modifiers = KeyModifiers.None) => new(Letter(letter), modifiers);

        return new Dictionary<PlayerCommand, IReadOnlyList<KeyChord>>
        {
            [PlayerCommand.PlayPause] = [K(Space), L('K'), K(MediaPlayPause)],
            [PlayerCommand.Stop] = [L('S'), K(MediaStop)],
            [PlayerCommand.SeekBack] = [K(Left)],
            [PlayerCommand.SeekForward] = [K(Right)],
            [PlayerCommand.SeekBackLong] = [K(Left, KeyModifiers.Shift)],
            [PlayerCommand.SeekForwardLong] = [K(Right, KeyModifiers.Shift)],
            [PlayerCommand.JumpBack] = [L('J')],
            [PlayerCommand.JumpForward] = [L('L')],
            [PlayerCommand.GoToStart] = [K(Home)],
            [PlayerCommand.GoToEnd] = [K(End)],
            [PlayerCommand.FrameBack] = [K(Comma)],
            [PlayerCommand.FrameForward] = [K(Period)],
            [PlayerCommand.VolumeUp] = [K(Up)],
            [PlayerCommand.VolumeDown] = [K(Down)],
            [PlayerCommand.Mute] = [L('M')],
            [PlayerCommand.Faster] = [K(CloseBracket)],
            [PlayerCommand.Slower] = [K(OpenBracket)],
            [PlayerCommand.NormalSpeed] = [K(Backslash)],
            [PlayerCommand.NextSubtitle] = [L('C')],
            [PlayerCommand.NextAudio] = [L('A')],
            [PlayerCommand.SubtitlesEarlier] = [L('Z')],
            [PlayerCommand.SubtitlesLater] = [L('X')],
            [PlayerCommand.AudioEarlier] = [L('Z', KeyModifiers.Shift)],
            [PlayerCommand.AudioLater] = [L('X', KeyModifiers.Shift)],
            [PlayerCommand.Fullscreen] = [L('F'), K(F11)],
            [PlayerCommand.MiniPlayer] = [L('P')],
            [PlayerCommand.NextPictureShape] = [L('R')],
            [PlayerCommand.PlaybackInfo] = [L('I')],
            [PlayerCommand.Adjustments] = [L('G')],
            [PlayerCommand.Shortcuts] = [K(Slash, KeyModifiers.Shift), K(F1)],
            [PlayerCommand.SkipSegment] = [L('N')],
            [PlayerCommand.NextEpisode] = [L('N', KeyModifiers.Shift), K(MediaNext)],
            [PlayerCommand.PreviousEpisode] = [L('P', KeyModifiers.Shift), K(MediaPrevious)],
            [PlayerCommand.AddBookmark] = [L('B')],
            [PlayerCommand.Bookmarks] = [L('B', KeyModifiers.Shift)],
            [PlayerCommand.LoopSection] = [L('L', KeyModifiers.Shift)],
            [PlayerCommand.ClearDialogue] = [L('D')],
            [PlayerCommand.EvenVolume] = [L('E')],
        };
    }
}

using System.Globalization;

namespace Aperture.Core.Input;

/// <summary>Modifier keys held with a key.</summary>
[Flags]
public enum KeyModifiers
{
    /// <summary>No modifier.</summary>
    None = 0,

    /// <summary>Ctrl.</summary>
    Control = 1,

    /// <summary>Shift.</summary>
    Shift = 2,

    /// <summary>Alt.</summary>
    Alt = 4,
}

/// <summary>
/// One key with its modifiers, for example Shift+Right. <see cref="Key"/> is a Windows
/// virtual-key code, so this type stays platform-neutral and testable while matching what
/// WinUI reports.
/// </summary>
/// <param name="Key">Windows virtual-key code.</param>
/// <param name="Modifiers">Modifiers held with the key.</param>
public readonly record struct KeyChord(int Key, KeyModifiers Modifiers = KeyModifiers.None)
{
    /// <summary>Text shown to people and stored in settings: "Shift+Right", "Ctrl+]", "Space".</summary>
    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(KeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(KeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(KeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        parts.Add(KeyNames.Name(Key));
        return string.Join('+', parts);
    }

    /// <summary>Reads text written by <see cref="ToString"/>. Returns false for anything else.</summary>
    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = KeyModifiers.None;
        var rest = text.Trim();
        while (true)
        {
            if (TryStrip(ref rest, "Ctrl+"))
            {
                modifiers |= KeyModifiers.Control;
            }
            else if (TryStrip(ref rest, "Alt+"))
            {
                modifiers |= KeyModifiers.Alt;
            }
            else if (TryStrip(ref rest, "Shift+"))
            {
                modifiers |= KeyModifiers.Shift;
            }
            else
            {
                break;
            }
        }

        if (!KeyNames.TryParse(rest, out var key))
        {
            return false;
        }

        chord = new KeyChord(key, modifiers);
        return true;
    }

    private static bool TryStrip(ref string text, string prefix)
    {
        // "+" itself is a key name, so a lone "Ctrl+" prefix must leave something behind.
        if (text.Length > prefix.Length && text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            text = text[prefix.Length..];
            return true;
        }

        return false;
    }
}

/// <summary>Names for Windows virtual-key codes, as Aperture shows them.</summary>
public static class KeyNames
{
    /// <summary>Virtual-key codes Aperture refers to by name.</summary>
    public static class Codes
    {
        public const int Back = 0x08;
        public const int Tab = 0x09;
        public const int Enter = 0x0D;
        public const int Shift = 0x10;
        public const int Control = 0x11;
        public const int Menu = 0x12;
        public const int Escape = 0x1B;
        public const int Space = 0x20;
        public const int PageUp = 0x21;
        public const int PageDown = 0x22;
        public const int End = 0x23;
        public const int Home = 0x24;
        public const int Left = 0x25;
        public const int Up = 0x26;
        public const int Right = 0x27;
        public const int Down = 0x28;
        public const int Delete = 0x2E;
        public const int LeftWindows = 0x5B;
        public const int RightWindows = 0x5C;
        public const int Apps = 0x5D;
        public const int F1 = 0x70;
        public const int F4 = 0x73;
        public const int F10 = 0x79;
        public const int F11 = 0x7A;
        public const int VolumeMute = 0xAD;
        public const int VolumeDown = 0xAE;
        public const int VolumeUp = 0xAF;
        public const int MediaNext = 0xB0;
        public const int MediaPrevious = 0xB1;
        public const int MediaStop = 0xB2;
        public const int MediaPlayPause = 0xB3;
        public const int Semicolon = 0xBA;
        public const int Plus = 0xBB;
        public const int Comma = 0xBC;
        public const int Minus = 0xBD;
        public const int Period = 0xBE;
        public const int Slash = 0xBF;
        public const int OpenBracket = 0xDB;
        public const int Backslash = 0xDC;
        public const int CloseBracket = 0xDD;

        /// <summary>The code of a letter key, A to Z.</summary>
        public static int Letter(char letter) => char.ToUpperInvariant(letter);
    }

    private static readonly Dictionary<int, string> Named = new()
    {
        [Codes.Back] = "Backspace",
        [Codes.Tab] = "Tab",
        [Codes.Enter] = "Enter",
        [Codes.Escape] = "Esc",
        [Codes.Space] = "Space",
        [Codes.PageUp] = "Page Up",
        [Codes.PageDown] = "Page Down",
        [Codes.End] = "End",
        [Codes.Home] = "Home",
        [Codes.Left] = "Left",
        [Codes.Up] = "Up",
        [Codes.Right] = "Right",
        [Codes.Down] = "Down",
        [Codes.Delete] = "Delete",
        [Codes.Apps] = "Menu",
        [Codes.VolumeMute] = "Mute key",
        [Codes.VolumeDown] = "Volume Down key",
        [Codes.VolumeUp] = "Volume Up key",
        [Codes.MediaNext] = "Next Track key",
        [Codes.MediaPrevious] = "Previous Track key",
        [Codes.MediaStop] = "Stop key",
        [Codes.MediaPlayPause] = "Play/Pause key",
        [Codes.Semicolon] = ";",
        [Codes.Plus] = "=",
        [Codes.Comma] = ",",
        [Codes.Minus] = "-",
        [Codes.Period] = ".",
        [Codes.Slash] = "/",
        [Codes.OpenBracket] = "[",
        [Codes.Backslash] = "\\",
        [Codes.CloseBracket] = "]",
    };

    private static readonly Dictionary<string, int> ByName = Named.ToDictionary(p => p.Value, p => p.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>The name of a key: "Space", "Left", "F11", "]", "K".</summary>
    public static string Name(int key)
    {
        if (Named.TryGetValue(key, out var name))
        {
            return name;
        }

        if (key is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            return ((char)key).ToString();
        }

        if (key is >= Codes.F1 and <= Codes.F1 + 23)
        {
            return "F" + (key - Codes.F1 + 1).ToString(CultureInfo.InvariantCulture);
        }

        if (key is >= 0x60 and <= 0x69)
        {
            return "Num " + (key - 0x60).ToString(CultureInfo.InvariantCulture);
        }

        return "Key " + key.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Reads a name written by <see cref="Name"/>.</summary>
    public static bool TryParse(string? name, out int key)
    {
        key = 0;
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (ByName.TryGetValue(name, out key))
        {
            return true;
        }

        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0]))
        {
            key = char.ToUpperInvariant(name[0]);
            return true;
        }

        if (name.Length is 2 or 3 && (name[0] is 'F' or 'f')
            && int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var function)
            && function is >= 1 and <= 24)
        {
            key = Codes.F1 + function - 1;
            return true;
        }

        if (name.StartsWith("Num ", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(name.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var digit) && digit is >= 0 and <= 9)
        {
            key = 0x60 + digit;
            return true;
        }

        if (name.StartsWith("Key ", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(name.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var code) && code is > 0 and < 256)
        {
            key = code;
            return true;
        }

        return false;
    }

    /// <summary>True for keys that only modify others (Shift, Ctrl, Alt, Windows); they cannot be bound alone.</summary>
    public static bool IsModifier(int key) =>
        key is Codes.Shift or Codes.Control or Codes.Menu or Codes.LeftWindows or Codes.RightWindows
            or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;
}

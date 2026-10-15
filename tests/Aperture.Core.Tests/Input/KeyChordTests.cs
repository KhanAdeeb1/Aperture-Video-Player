using Aperture.Core.Input;
using static Aperture.Core.Input.KeyNames.Codes;

namespace Aperture.Core.Tests.Input;

public class KeyChordTests
{
    [Theory]
    [InlineData(Space, KeyModifiers.None, "Space")]
    [InlineData(Right, KeyModifiers.Shift, "Shift+Right")]
    [InlineData('U', KeyModifiers.Control | KeyModifiers.Shift, "Ctrl+Shift+U")]
    [InlineData(CloseBracket, KeyModifiers.Alt, "Alt+]")]
    [InlineData(F11, KeyModifiers.None, "F11")]
    [InlineData(MediaPlayPause, KeyModifiers.None, "Play/Pause key")]
    [InlineData(0x63, KeyModifiers.None, "Num 3")]
    [InlineData(Plus, KeyModifiers.Control, "Ctrl+=")]
    public void Names_round_trip(int key, KeyModifiers modifiers, string text)
    {
        var chord = new KeyChord(key, modifiers);
        Assert.Equal(text, chord.ToString());
        Assert.True(KeyChord.TryParse(text, out var parsed));
        Assert.Equal(chord, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Hyper+K")]
    [InlineData("F99")]
    public void Unreadable_text_is_rejected(string text) => Assert.False(KeyChord.TryParse(text, out _));

    [Fact]
    public void Unnamed_keys_round_trip_by_code()
    {
        var chord = new KeyChord(0xE2);
        Assert.Equal("Key 226", chord.ToString());
        Assert.True(KeyChord.TryParse("Key 226", out var parsed));
        Assert.Equal(chord, parsed);
    }
}

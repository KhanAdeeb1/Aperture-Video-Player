using Aperture.Core.Input;
using static Aperture.Core.Input.KeyNames.Codes;

namespace Aperture.Core.Tests.Input;

public class ShortcutMapTests
{
    private static KeyChord K(int key, KeyModifiers modifiers = KeyModifiers.None) => new(key, modifiers);

    [Fact]
    public void Defaults_cover_every_command_and_never_share_a_key()
    {
        var map = ShortcutMap.Default;
        var all = PlayerCommands.All.SelectMany(c => map.KeysFor(c)).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.All(PlayerCommands.All, c => Assert.NotEmpty(map.KeysFor(c)));
        Assert.DoesNotContain(all, ShortcutMap.IsReserved);
    }

    [Theory]
    [InlineData(Space, KeyModifiers.None, PlayerCommand.PlayPause)]
    [InlineData('K', KeyModifiers.None, PlayerCommand.PlayPause)]
    [InlineData(Right, KeyModifiers.None, PlayerCommand.SeekForward)]
    [InlineData(Right, KeyModifiers.Shift, PlayerCommand.SeekForwardLong)]
    [InlineData('Z', KeyModifiers.None, PlayerCommand.SubtitlesEarlier)]
    [InlineData('X', KeyModifiers.Shift, PlayerCommand.AudioLater)]
    [InlineData(CloseBracket, KeyModifiers.None, PlayerCommand.Faster)]
    [InlineData(Slash, KeyModifiers.Shift, PlayerCommand.Shortcuts)]
    public void Default_keys_resolve(int key, KeyModifiers modifiers, PlayerCommand expected) =>
        Assert.Equal(expected, ShortcutMap.Default.Resolve(K(key, modifiers)));

    [Fact]
    public void Modifiers_make_a_different_key() =>
        Assert.Null(ShortcutMap.Default.Resolve(K(Right, KeyModifiers.Control)));

    [Fact]
    public void Assign_refuses_a_key_in_use_unless_replacing()
    {
        var refused = ShortcutMap.Default.Assign(PlayerCommand.Fullscreen, K('M'));
        Assert.Equal(ShortcutProblem.InUse, refused.Problem);
        Assert.Equal(PlayerCommand.Mute, refused.ConflictsWith);
        Assert.Same(ShortcutMap.Default, refused.Map);
        Assert.Contains("Mute", refused.Message, StringComparison.Ordinal);

        var moved = ShortcutMap.Default.Assign(PlayerCommand.Fullscreen, K('M'), replace: true);
        Assert.Equal(ShortcutProblem.None, moved.Problem);
        Assert.Equal(PlayerCommand.Fullscreen, moved.Map.Resolve(K('M')));
        Assert.Empty(moved.Map.KeysFor(PlayerCommand.Mute));
        // F and F11 no longer toggle full screen: the command has exactly the key chosen.
        Assert.Null(moved.Map.Resolve(K('F')));
    }

    [Theory]
    [InlineData(F4, KeyModifiers.Alt)]
    [InlineData('O', KeyModifiers.Control)]
    [InlineData(Escape, KeyModifiers.None)]
    [InlineData(Tab, KeyModifiers.None)]
    [InlineData(Enter, KeyModifiers.None)]
    [InlineData(LeftWindows, KeyModifiers.None)]
    public void Reserved_keys_cannot_be_taken(int key, KeyModifiers modifiers) =>
        Assert.Equal(ShortcutProblem.Reserved, ShortcutMap.Default.Assign(PlayerCommand.Mute, K(key, modifiers)).Problem);

    [Fact]
    public void A_modifier_alone_cannot_be_taken() =>
        Assert.Equal(ShortcutProblem.ModifierOnly, ShortcutMap.Default.Assign(PlayerCommand.Mute, K(Shift)).Problem);

    [Fact]
    public void Reassigning_a_commands_own_key_is_allowed()
    {
        var result = ShortcutMap.Default.Assign(PlayerCommand.Mute, K('M'));
        Assert.Equal(ShortcutProblem.None, result.Problem);
        Assert.False(result.Map.IsCustomized(PlayerCommand.Mute));
    }

    [Fact]
    public void Reset_restores_defaults_without_stealing_keys_given_to_others()
    {
        var map = ShortcutMap.Default.Assign(PlayerCommand.Fullscreen, K('M'), replace: true).Map;
        map = map.Reset(PlayerCommand.Mute);
        // M now belongs to full screen, so Mute stays without a key rather than taking it back.
        Assert.Empty(map.KeysFor(PlayerCommand.Mute));
        Assert.Equal(PlayerCommand.Fullscreen, map.Resolve(K('M')));

        map = map.Reset(PlayerCommand.Fullscreen).Reset(PlayerCommand.Mute);
        Assert.Equal(PlayerCommand.Mute, map.Resolve(K('M')));
        Assert.Equal(PlayerCommand.Fullscreen, map.Resolve(K('F')));
    }

    [Fact]
    public void Storage_round_trips_only_customisations()
    {
        var map = ShortcutMap.Default
            .Assign(PlayerCommand.Mute, K('U', KeyModifiers.Control | KeyModifiers.Shift)).Map
            .Clear(PlayerCommand.FrameBack);
        var stored = map.ToStorage();
        Assert.Equal(2, stored.Count);
        Assert.Equal("Ctrl+Shift+U", stored["Mute"]);
        Assert.Equal(string.Empty, stored["FrameBack"]);

        var restored = ShortcutMap.FromStorage(stored);
        Assert.Equal(PlayerCommand.Mute, restored.Resolve(K('U', KeyModifiers.Control | KeyModifiers.Shift)));
        Assert.Null(restored.Resolve(K('M')));
        Assert.Empty(restored.KeysFor(PlayerCommand.FrameBack));
        Assert.Equal(PlayerCommand.PlayPause, restored.Resolve(K(Space)));
    }

    [Fact]
    public void Damaged_storage_never_makes_a_key_ambiguous()
    {
        var restored = ShortcutMap.FromStorage(new Dictionary<string, string>
        {
            ["Mute"] = "Space nonsense Alt+F4",
            ["NoSuchCommand"] = "Q",
            ["Fullscreen"] = "Space",
        });

        // Customised commands claim keys first, in command order; defaults lose clashes.
        Assert.Equal(PlayerCommand.Mute, restored.Resolve(K(Space)));
        Assert.Empty(restored.KeysFor(PlayerCommand.Fullscreen));
        Assert.DoesNotContain(K(Space), restored.KeysFor(PlayerCommand.PlayPause));
        Assert.Equal(PlayerCommand.PlayPause, restored.Resolve(K('K')));
        Assert.Null(restored.Resolve(K(F4, KeyModifiers.Alt)));
    }

    [Fact]
    public void Tooltips_name_the_current_key()
    {
        Assert.Equal("Mute (M)", ShortcutMap.Default.WithKey("Mute", PlayerCommand.Mute));
        Assert.Equal("Mute", ShortcutMap.Default.Clear(PlayerCommand.Mute).WithKey("Mute", PlayerCommand.Mute));
    }

    [Fact]
    public void Every_command_has_a_label_and_group()
    {
        foreach (var command in PlayerCommands.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(PlayerCommands.Label(command)));
            Assert.False(string.IsNullOrWhiteSpace(PlayerCommands.GroupLabel(PlayerCommands.Group(command))));
        }
    }
}

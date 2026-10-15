using Aperture.Core.Input;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace Aperture.App.Views;

/// <summary>Shows every player shortcut and lets the user change, remove or reset keys.</summary>
public sealed partial class ShortcutsDialog : ContentDialog
{
    private PlayerCommand? _capturing;
    private (PlayerCommand Command, KeyChord Chord)? _pendingMove;

    internal ShortcutsDialog(ShortcutMap map)
    {
        Map = map;
        InitializeComponent();
        BuildRows();
    }

    /// <summary>The map as the user left it.</summary>
    internal ShortcutMap Map { get; private set; }

    private void BuildRows()
    {
        Rows.Children.Clear();
        foreach (var group in PlayerCommands.All.GroupBy(PlayerCommands.Group))
        {
            var header = new TextBlock
            {
                Text = PlayerCommands.GroupLabel(group.Key),
                Style = (Style)Application.Current.Resources["SettingGroupHeaderTextStyle"],
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHeadingLevel(header, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level3);
            Rows.Children.Add(header);
            foreach (var command in group)
            {
                Rows.Children.Add(Row(command));
            }
        }
    }

    private Grid Row(PlayerCommand command)
    {
        var row = new Grid { ColumnSpacing = 8, MinHeight = 40 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = PlayerCommands.Label(command),
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["BodyTextStyle"],
        };
        row.Children.Add(label);

        var keys = Map.KeysFor(command);
        var capturing = _capturing == command;
        var keyText = new TextBlock
        {
            Text = capturing ? "Press a key…" : keys.Count == 0 ? "No key" : string.Join("  or  ", keys),
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources[capturing || keys.Count > 0 ? "BodyStrongTextStyle" : "CaptionTextStyle"],
        };
        if (capturing)
        {
            keyText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentBrush"];
        }

        Grid.SetColumn(keyText, 1);
        row.Children.Add(keyText);

        var change = new Button
        {
            Content = capturing ? "Cancel" : "Change",
            Style = (Style)Application.Current.Resources["QuietButtonStyle"],
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(change, capturing ? "Cancel changing the key" : $"Change the key for {PlayerCommands.Label(command)}");
        change.Click += (_, _) => StartCapture(capturing ? null : command);
        Grid.SetColumn(change, 2);
        row.Children.Add(change);

        if (!capturing && (Map.IsCustomized(command) || keys.Count > 0))
        {
            var more = new Button
            {
                Content = new FontIcon { Glyph = "", Style = (Style)Application.Current.Resources["IconGlyphStyle"] },
                Style = (Style)Application.Current.Resources["IconButtonStyle"],
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(more, $"More for {PlayerCommands.Label(command)}");
            var menu = new MenuFlyout();
            if (keys.Count > 0)
            {
                var clear = new MenuFlyoutItem { Text = "Remove key" };
                clear.Click += (_, _) => Apply(Map.Clear(command));
                menu.Items.Add(clear);
            }

            if (Map.IsCustomized(command))
            {
                var reset = new MenuFlyoutItem { Text = $"Reset to {ShortcutMap.Default.Label(command) ?? "no key"}" };
                reset.Click += (_, _) => Apply(Map.Reset(command));
                menu.Items.Add(reset);
            }

            more.Flyout = menu;
            Grid.SetColumn(more, 3);
            row.Children.Add(more);
        }

        return row;
    }

    private void StartCapture(PlayerCommand? command)
    {
        _capturing = command;
        _pendingMove = null;
        HideMessage();
        BuildRows();
    }

    private void Apply(ShortcutMap map)
    {
        Map = map;
        _capturing = null;
        _pendingMove = null;
        HideMessage();
        BuildRows();
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_capturing is not { } command)
        {
            return;
        }

        e.Handled = true;
        if (e.Key == VirtualKey.Escape)
        {
            StartCapture(null);
            return;
        }

        var key = (int)e.Key;
        if (KeyNames.IsModifier(key))
        {
            // Wait for the key the modifier is held with.
            return;
        }

        var modifiers = KeyModifiers.None;
        if (IsDown(VirtualKey.Control))
        {
            modifiers |= KeyModifiers.Control;
        }

        if (IsDown(VirtualKey.Shift))
        {
            modifiers |= KeyModifiers.Shift;
        }

        if (IsDown(VirtualKey.Menu))
        {
            modifiers |= KeyModifiers.Alt;
        }

        var chord = new KeyChord(key, modifiers);
        var result = Map.Assign(command, chord);
        if (result.Problem == ShortcutProblem.None)
        {
            Apply(result.Map);
            return;
        }

        ShowMessage(result.Message ?? "That key can’t be used.", result.Problem == ShortcutProblem.InUse);
        _pendingMove = result.Problem == ShortcutProblem.InUse ? (command, chord) : null;
    }

    private void OnMoveKeyClick(object sender, RoutedEventArgs e)
    {
        if (_pendingMove is { } move)
        {
            Apply(Map.Assign(move.Command, move.Chord, replace: true).Map);
        }
    }

    private void OnResetAllClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Keep the dialog open so the result can be seen.
        args.Cancel = true;
        Apply(ShortcutMap.Default);
    }

    private void ShowMessage(string text, bool canMove)
    {
        MessageText.Text = text;
        MoveKeyButton.Visibility = canMove ? Visibility.Visible : Visibility.Collapsed;
        MessagePanel.Visibility = Visibility.Visible;
    }

    private void HideMessage() => MessagePanel.Visibility = Visibility.Collapsed;

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}

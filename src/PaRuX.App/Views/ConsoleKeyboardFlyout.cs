using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PaRuX.Contracts;

namespace PaRuX.Views;

/// <summary>
/// Controller-friendly on-screen keyboard for Console/TV mode. Plain WinUI buttons in a Flyout, so
/// D-pad / left stick use the same XY focus navigation as everything else, A activates a key and
/// B / Esc closes it. No driver, no injected input and no worker: a key press edits the target
/// TextBox directly through <see cref="ConsoleTextEntry"/>.
/// </summary>
internal static class ConsoleKeyboardFlyout
{
    public static Flyout Create(TextBox target, string doneLabel, string backspaceLabel, string clearLabel, string spaceLabel)
    {
        var grid = new Grid { RowSpacing = 8, ColumnSpacing = 8, Padding = new Thickness(8) };
        int rows = ConsoleTextEntry.Rows.Count + 1;
        for (int i = 0; i < rows; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < 10; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        void Apply(Func<string, int, (string, int)> edit)
        {
            (string text, int caret) = edit(target.Text, target.SelectionStart);
            target.Text = text;
            target.SelectionStart = caret;
        }

        Button Key(string label, string automationName, Action onClick, int row, int column, int span = 1)
        {
            var button = new Button
            {
                Content = label,
                MinWidth = 64,
                MinHeight = 56,
                FontSize = 22,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            AutomationProperties.SetName(button, automationName);
            button.Click += (_, _) => onClick();
            Grid.SetRow(button, row);
            Grid.SetColumn(button, column);
            Grid.SetColumnSpan(button, span);
            grid.Children.Add(button);
            return button;
        }

        for (int r = 0; r < ConsoleTextEntry.Rows.Count; r++)
        {
            string line = ConsoleTextEntry.Rows[r];
            for (int c = 0; c < line.Length; c++)
            {
                string key = line[c].ToString();
                Key(key.ToUpperInvariant(), key, () => Apply((t, p) => ConsoleTextEntry.Insert(t, p, key)), r, c);
            }
        }

        int last = ConsoleTextEntry.Rows.Count;
        Key(spaceLabel, spaceLabel, () => Apply((t, p) => ConsoleTextEntry.Insert(t, p, " ")), last, 0, 4);
        Key("⌫", backspaceLabel, () => Apply(ConsoleTextEntry.Backspace), last, 4, 2);
        Key(clearLabel, clearLabel, () => Apply(ConsoleTextEntry.Clear), last, 6, 2);
        var flyout = new Flyout { Content = grid, Placement = FlyoutPlacementMode.Bottom };
        Key(doneLabel, doneLabel, () => flyout.Hide(), last, 8, 2);
        AutomationProperties.SetName(grid, doneLabel);
        return flyout;
    }
}

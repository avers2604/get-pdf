using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace GetDocument.Desktop;

/// <summary>
/// GET DS 4.1 modal dialogs (confirm / info / text input / custom form) for the Avalonia
/// client: white card, navy title, muted body, primary action on the right, Esc cancels.
/// </summary>
internal static class GetDialogs
{
    public enum Tone { Info, Warning, Error, Success }

    public static async Task<bool> ConfirmAsync(Window owner, string title, string text,
        string confirm = "Продолжить", string cancel = "Отмена", Tone tone = Tone.Warning)
    {
        bool result = false;
        var dialog = Create(title, text, tone, out var buttons, out _);
        var no = new Button { Content = cancel, IsCancel = true };
        no.Classes.Add("ghost");
        no.Click += (_, _) => dialog.Close();
        var yes = new Button { Content = confirm, IsDefault = true };
        yes.Classes.Add("primary");
        yes.Click += (_, _) => { result = true; dialog.Close(); };
        buttons.Children.Add(no);
        buttons.Children.Add(yes);
        await dialog.ShowDialog(owner);
        return result;
    }

    public static async Task InfoAsync(Window owner, string title, string text, Tone tone = Tone.Info)
    {
        var dialog = Create(title, text, tone, out var buttons, out _);
        var ok = new Button { Content = "Понятно", IsDefault = true, IsCancel = true };
        ok.Classes.Add("primary");
        ok.Click += (_, _) => dialog.Close();
        buttons.Children.Add(ok);
        await dialog.ShowDialog(owner);
    }

    public static async Task<string?> AskTextAsync(Window owner, string title, string prompt,
        string initial = "", int maxLength = 120, string confirm = "Готово")
    {
        string? result = null;
        var dialog = Create(title, prompt, Tone.Info, out var buttons, out var body);
        var box = new TextBox { Text = initial, MaxLength = maxLength, MinWidth = 380 };
        body.Children.Add(box);
        var no = new Button { Content = "Отмена", IsCancel = true };
        no.Classes.Add("ghost");
        no.Click += (_, _) => dialog.Close();
        var yes = new Button { Content = confirm, IsDefault = true };
        yes.Classes.Add("primary");
        yes.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(box.Text)) return;
            result = box.Text.Trim();
            dialog.Close();
        };
        buttons.Children.Add(no);
        buttons.Children.Add(yes);
        dialog.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        await dialog.ShowDialog(owner);
        return result;
    }

    /// <summary>Dialog with caller-supplied controls; returns true when the primary button was used.</summary>
    public static async Task<bool> FormAsync(Window owner, string title, string text, Control content,
        string confirm, Func<bool>? validate = null, (string Text, Action Action)? extra = null)
    {
        bool result = false;
        var dialog = Create(title, text, Tone.Info, out var buttons, out var body);
        body.Children.Add(content);
        if (extra is { } secondary)
        {
            var other = new Button { Content = secondary.Text };
            other.Classes.Add("ghost");
            other.Click += (_, _) => { secondary.Action(); dialog.Close(); };
            buttons.Children.Add(other);
        }
        var no = new Button { Content = "Отмена", IsCancel = true };
        no.Classes.Add("ghost");
        no.Click += (_, _) => dialog.Close();
        var yes = new Button { Content = confirm, IsDefault = true };
        yes.Classes.Add("primary");
        yes.Click += (_, _) =>
        {
            if (validate is not null && !validate()) return;
            result = true;
            dialog.Close();
        };
        buttons.Children.Add(no);
        buttons.Children.Add(yes);
        await dialog.ShowDialog(owner);
        return result;
    }

    private static Window Create(string title, string text, Tone tone, out StackPanel buttons, out StackPanel body)
    {
        var (glyph, brush, soft) = tone switch
        {
            Tone.Error => (Icons.Alert, "#E4032E", "#FDE8EC"),
            Tone.Warning => (Icons.Alert, "#8A6200", "#FFF6DD"),
            Tone.Success => (Icons.Check, "#5E7A10", "#F1F7DE"),
            _ => (Icons.Alert, "#00809A", "#E6F5F9")
        };
        var badge = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(20),
            Background = new SolidColorBrush(Color.Parse(soft)),
            VerticalAlignment = VerticalAlignment.Top,
            Child = Icons.Create(glyph, 22)
        };
        badge.Child.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.Parse(brush)));
        if (badge.Child is Viewbox { Child: Canvas { Children: [Avalonia.Controls.Shapes.Path path] } })
            path.Stroke = new SolidColorBrush(Color.Parse(brush));
        var heading = new TextBlock { Text = title };
        heading.Classes.Add("section");
        heading.FontSize = 18;
        var message = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 520,
            Foreground = new SolidColorBrush(Color.Parse("#5C6480")) };
        body = new StackPanel { Spacing = 10 };
        body.Children.Add(heading);
        if (text.Length > 0) body.Children.Add(message);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        row.Children.Add(badge);
        Grid.SetColumn(body, 1);
        body.Margin = new Thickness(16, 0, 0, 0);
        row.Children.Add(body);
        buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        var layout = new StackPanel { Margin = new Thickness(24, 22) };
        layout.Children.Add(row);
        layout.Children.Add(buttons);
        var window = new Window
        {
            Title = title,
            Content = layout,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            MinWidth = 420,
            MaxWidth = 680,
            Background = Brushes.White
        };
        window.KeyDown += (_, e) => { if (e.Key == Key.Escape) window.Close(); };
        return window;
    }
}

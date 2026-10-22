using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Path = System.Windows.Shapes.Path;

namespace GetPdf;

/// <summary>
/// GET DS 4.1 replacement for <see cref="MessageBox"/>: same call shape and results, but a
/// themed dialog (tinted status icon, Primary default action, selectable text, Enter/Esc).
/// Before any WPF window exists it falls back to the system box, so a startup message can
/// never become the application's last window.
/// </summary>
internal static class GetMessageBox
{
    public static MessageBoxResult Show(string text) =>
        Show(null, text, "ГЭТ PDF", MessageBoxButton.OK, MessageBoxImage.None);
    public static MessageBoxResult Show(string text, string caption) =>
        Show(null, text, caption, MessageBoxButton.OK, MessageBoxImage.None);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button) =>
        Show(null, text, caption, button, MessageBoxImage.None);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button, MessageBoxImage image) =>
        Show(null, text, caption, button, image);
    public static MessageBoxResult Show(Window? owner, string text) =>
        Show(owner, text, "ГЭТ PDF", MessageBoxButton.OK, MessageBoxImage.None);
    public static MessageBoxResult Show(Window? owner, string text, string caption) =>
        Show(owner, text, caption, MessageBoxButton.OK, MessageBoxImage.None);
    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton button) =>
        Show(owner, text, caption, button, MessageBoxImage.None);

    public static MessageBoxResult Show(Window? owner, string text, string caption,
        MessageBoxButton button, MessageBoxImage image)
    {
        var app = Application.Current;
        owner ??= app?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? app?.MainWindow;
        if (app is null || owner is null || !owner.IsLoaded || !app.Dispatcher.CheckAccess())
            return owner is null ? MessageBox.Show(text, caption, button, image)
                : MessageBox.Show(owner, text, caption, button, image);
        var dialog = new MessageDialog(text, caption, button, image) { Owner = owner };
        dialog.ShowDialog();
        return dialog.Result;
    }

    private sealed class MessageDialog : Window
    {
        internal MessageBoxResult Result { get; private set; }
        private readonly MessageBoxResult _escape;

        internal MessageDialog(string text, string caption, MessageBoxButton button, MessageBoxImage image)
        {
            Title = string.IsNullOrWhiteSpace(caption) ? "ГЭТ PDF" : caption;
            Width = 480; SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Brush("Surface"); Foreground = Brush("Ink");
            FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
            (_escape, Result) = button switch
            {
                MessageBoxButton.OKCancel => (MessageBoxResult.Cancel, MessageBoxResult.Cancel),
                MessageBoxButton.YesNo => (MessageBoxResult.No, MessageBoxResult.No),
                MessageBoxButton.YesNoCancel => (MessageBoxResult.Cancel, MessageBoxResult.Cancel),
                _ => (MessageBoxResult.OK, MessageBoxResult.OK)
            };

            var root = new DockPanel { Margin = new Thickness(24, 22, 24, 20) };
            Content = root;
            var commands = new StackPanel { Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
            DockPanel.SetDock(commands, Dock.Bottom);
            root.Children.Add(commands);

            var body = new DockPanel();
            root.Children.Add(body);
            if (image != MessageBoxImage.None)
            {
                var badge = StatusBadge(image);
                DockPanel.SetDock(badge, Dock.Left);
                body.Children.Add(badge);
            }
            // Read-only TextBox keeps the text selectable/copyable like the system box (Ctrl+C).
            var message = new TextBox
            {
                Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                Padding = new Thickness(0), MinHeight = 0, VerticalContentAlignment = VerticalAlignment.Top,
                FontSize = 13, Focusable = true, IsTabStop = false
            };
            System.Windows.Automation.AutomationProperties.SetName(message, text);
            body.Children.Add(new ScrollViewer
            {
                Content = message, MaxHeight = 420, VerticalAlignment = VerticalAlignment.Center,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            });

            foreach (var (label, result, primary) in Buttons(button))
            {
                var command = new Button { Content = label, MinWidth = 104, Margin = new Thickness(8, 0, 0, 0),
                    IsDefault = primary, IsCancel = result == _escape && !primary };
                if (primary) command.Style = (Style)Application.Current.FindResource("PrimaryButton");
                command.Click += (_, _) => { Result = result; DialogResult = true; };
                commands.Children.Add(command);
                if (primary) Loaded += (_, _) => command.Focus();
            }
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) { Result = _escape; DialogResult = false; e.Handled = true; }
            };
        }

        private static IEnumerable<(string Label, MessageBoxResult Result, bool Primary)> Buttons(MessageBoxButton button) =>
            button switch
            {
                MessageBoxButton.OKCancel => [("ОК", MessageBoxResult.OK, true), ("Отмена", MessageBoxResult.Cancel, false)],
                MessageBoxButton.YesNo => [("Да", MessageBoxResult.Yes, true), ("Нет", MessageBoxResult.No, false)],
                MessageBoxButton.YesNoCancel => [("Да", MessageBoxResult.Yes, true), ("Нет", MessageBoxResult.No, false),
                    ("Отмена", MessageBoxResult.Cancel, false)],
                _ => [("ОК", MessageBoxResult.OK, true)]
            };

        /// <summary>DS status badge: tinted circle + 24×24 outline icon (info, warning, error, question).</summary>
        private static FrameworkElement StatusBadge(MessageBoxImage image)
        {
            var (icon, tint, ink) = image switch
            {
                MessageBoxImage.Error => ("IconError", Color.FromRgb(0xFD, 0xE8, 0xEC), Color.FromRgb(0xC4, 0x02, 0x27)),
                MessageBoxImage.Warning => ("IconAlert", Color.FromRgb(0xFF, 0xF4, 0xD6), Color.FromRgb(0x8A, 0x5A, 0x00)),
                MessageBoxImage.Question => ("IconHelp", Color.FromRgb(0xE6, 0xF5, 0xF9), Color.FromRgb(0x00, 0x80, 0x9A)),
                _ => ("IconInfo", Color.FromRgb(0xE6, 0xF5, 0xF9), Color.FromRgb(0x00, 0x80, 0x9A))
            };
            var path = new Path
            {
                Data = (Geometry)Application.Current.FindResource(icon), Stroke = new SolidColorBrush(ink),
                StrokeThickness = 1.75, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            };
            return new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Background = new SolidColorBrush(tint),
                Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top,
                Child = new Viewbox { Width = 22, Height = 22, Child = new Canvas { Width = 24, Height = 24, Children = { path } } }
            };
        }

        private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
    }
}

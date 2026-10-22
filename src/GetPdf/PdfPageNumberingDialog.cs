using System.Windows;
using System.Windows.Controls;

namespace GetPdf;

/// <summary>Page-numbering options: format, position, first number and a skipped title page.</summary>
internal sealed class PdfPageNumberingDialog : Window
{
    internal PdfPageNumberingOptions? Options { get; private set; }
    internal bool RemoveRequested { get; private set; }

    internal PdfPageNumberingDialog(int pageCount, bool hasNumbers)
    {
        Title = "Нумерация страниц";
        Width = 500; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(24, 20, 24, 20) };
        Content = root;

        var commands = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        DockPanel.SetDock(commands, Dock.Bottom);
        root.Children.Add(commands);

        var panel = new StackPanel();
        root.Children.Add(panel);
        panel.Children.Add(new TextBlock
        {
            Text = $"Номера появятся на всех {pageCount} страницах и войдут в сохранённую копию и печать. " +
                   "Исходный файл не меняется; действие можно отменить (Ctrl+Z).",
            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None,
            Margin = new Thickness(0, 0, 0, 16)
        });
        var format = Choice(panel, "Вид номера", ["1", "1 из N", "Страница 1 из N"], 0);
        var position = Choice(panel, "Расположение",
            ["Внизу по центру", "Внизу справа", "Вверху справа"], 0);
        panel.Children.Add(Label("Начать нумерацию с"));
        var start = new TextBox { Text = "1", MaxLength = 4, Width = 96, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(start);
        var skipFirst = new CheckBox
        {
            Content = "Без номера на первой странице (титульный лист)",
            Margin = new Thickness(0, 14, 0, 0)
        };
        panel.Children.Add(skipFirst);

        var apply = new Button { Content = "Пронумеровать", IsDefault = true, MinWidth = 140,
            Style = (Style)Application.Current.FindResource("PrimaryButton") };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        if (hasNumbers)
        {
            var remove = new Button { Content = "Убрать нумерацию", Margin = new Thickness(0, 0, 8, 0) };
            remove.Click += (_, _) => { RemoveRequested = true; DialogResult = true; };
            commands.Children.Add(remove);
        }
        commands.Children.Add(apply);
        commands.Children.Add(cancel);
        apply.Click += (_, _) =>
        {
            if (!int.TryParse(start.Text, out int first) || first is < 1 or > 9999)
            {
                GetMessageBox.Show(this, "Укажите номер первой страницы от 1 до 9999.", Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                start.Focus();
                return;
            }
            Options = new PdfPageNumberingOptions((PdfPageNumberFormat)format.SelectedIndex,
                (PdfPageNumberPosition)position.SelectedIndex, first, skipFirst.IsChecked == true);
            DialogResult = true;
        };
    }

    private static TextBlock Label(string text) =>
        new() { Text = text, Style = (Style)Application.Current.FindResource("FieldLabel"),
            Margin = new Thickness(0, 10, 0, 4) };

    private static ComboBox Choice(Panel parent, string label, IReadOnlyList<string> values, int selected)
    {
        parent.Children.Add(Label(label));
        var box = new ComboBox();
        foreach (string value in values) box.Items.Add(new ComboBoxItem { Content = value });
        box.SelectedIndex = selected;
        parent.Children.Add(box);
        return box;
    }
}

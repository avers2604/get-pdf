using System.Windows;
using System.Windows.Controls;

namespace GetPdf;

/// <summary>Driver-aware print configuration before the existing raster preview.</summary>
internal sealed class PdfPrintSettingsDialog : Window
{
    internal PdfPrinterSettings Settings { get; private set; }
    internal PdfPrintSettingsDialog(PdfPrinterSettings initial)
    {
        PdfPrinterSettingsStore.Validate(initial);
        Settings = initial;
        Title = "ГЭТ PDF — параметры печати";
        Width = 425; Height = 380; MinWidth = 390; MinHeight = 335;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        Content = root;
        var panel = new StackPanel();
        root.Children.Add(panel);
        panel.Children.Add(new TextBlock
        {
            Text = "Задание передаётся выбранному драйверу Windows. " +
                "Недоступные форматы/дуплекс будут отклонены до печати.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 13)
        });
        var paper = Choice(panel, "Бумага", [
            "Как установлено в драйвере", "A4", "A3"], (int)initial.Paper);
        var orientation = Choice(panel, "Ориентация", [
            "Как установлено в драйвере", "Книжная", "Альбомная"], (int)initial.Orientation);
        var duplex = Choice(panel, "Двусторонняя печать", [
            "Как установлено в драйвере", "Односторонняя", "Дуплекс по длинной стороне",
            "Дуплекс по короткой стороне"], (int)initial.Duplex);
        panel.Children.Add(new TextBlock { Text = "Количество копий", Margin = new Thickness(0, 6, 0, 3) });
        var copies = new TextBox { Text = initial.Copies.ToString(), MaxLength = 2, Height = 30 };
        panel.Children.Add(copies);
        var commands = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = "Предпросмотр", MinWidth = 125,
            Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Padding = new Thickness(8) };
        commands.Children.Add(ok); commands.Children.Add(cancel);
        panel.Children.Add(commands);
        ok.Click += (_, _) =>
        {
            if (!int.TryParse(copies.Text, out int n))
            {
                MessageBox.Show(this, "Укажите количество копий от 1 до 99.");
                return;
            }
            var choice = new PdfPrinterSettings(
                (PdfPaperChoice)paper.SelectedIndex,
                (PdfOrientationChoice)orientation.SelectedIndex,
                (PdfDuplexChoice)duplex.SelectedIndex, n);
            try { PdfPrinterSettingsStore.Validate(choice); }
            catch (ArgumentOutOfRangeException)
            {
                MessageBox.Show(this, "Проверьте параметры печати.");
                return;
            }
            Settings = choice;
            DialogResult = true;
        };
    }

    private static ComboBox Choice(StackPanel parent, string label,
        IReadOnlyList<string> values, int selected)
    {
        parent.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 3) });
        var select = new ComboBox { Height = 30, SelectedIndex = selected };
        foreach (string value in values) select.Items.Add(new ComboBoxItem { Content = value });
        parent.Children.Add(select);
        return select;
    }
}

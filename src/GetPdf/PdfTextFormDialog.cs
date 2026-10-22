using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GetPdf;

/// <summary>Modal form editor: shows field names locally; never logs field contents.</summary>
internal sealed class PdfTextFormDialog : Window
{
    private readonly Dictionary<string, TextBox> _inputs = new(StringComparer.Ordinal);
    internal IReadOnlyDictionary<string, string> Values { get; private set; } =
        new Dictionary<string, string>();

    internal PdfTextFormDialog(IReadOnlyList<string> fields)
    {
        if (fields.Count is < 1 or > PdfFormFillLabPolicy.MaxFields)
            throw new ArgumentException("Некорректное число полей формы.");
        Title = "ГЭТ PDF — заполнение текстовой формы";
        Width = 540; Height = 610; MinWidth = 420; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(18) };
        Content = root;
        var instruction = new TextBlock
        {
            Text = "Поддерживаются только простые неподписанные текстовые формы. " +
                "Заполненные значения сохраняются в отдельный PDF; пустые поля не изменяются.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16)
        };
        DockPanel.SetDock(instruction, Dock.Top); root.Children.Add(instruction);

        var controls = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var save = new Button { Content = "Сохранить копию…", MinWidth = 160,
            Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 80 };
        controls.Children.Add(save); controls.Children.Add(cancel);
        DockPanel.SetDock(controls, Dock.Bottom); root.Children.Add(controls);

        var panel = new StackPanel();
        foreach (string field in fields)
        {
            panel.Children.Add(new TextBlock
            {
                Text = field, Margin = new Thickness(0, 8, 0, 3),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = field, Foreground = Brushes.Black
            });
            var input = new TextBox
            {
                MaxLength = PdfFormFillLabPolicy.MaxValueLength,
                Height = 30, Padding = new Thickness(5, 3, 5, 3)
            };
            _inputs.Add(field, input);
            panel.Children.Add(input);
        }
        root.Children.Add(new ScrollViewer { Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        save.Click += (_, _) =>
        {
            var filled = _inputs.Where(x => x.Value.Text.Length > 0)
                .ToDictionary(x => x.Key, x => x.Value.Text, StringComparer.Ordinal);
            try { PdfTextFormService.ValidateValues(filled); }
            catch (ArgumentException)
            {
                GetMessageBox.Show(this, "Заполните хотя бы одно поле одной строкой " +
                    "длиной не более 512 символов.", "Проверка AcroForm",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Values = filled;
            DialogResult = true;
        };
    }
}

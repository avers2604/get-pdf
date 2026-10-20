using System.Windows;
using System.Windows.Controls;

namespace GetPdf;

/// <summary>Local, ephemeral SHA-256 input; never persisted or sent to network.</summary>
internal sealed class Gup2HashDialog : Window
{
    internal string ExpectedSha256 { get; private set; } = "";

    internal Gup2HashDialog()
    {
        Title = "ГЭТ PDF — проверка оригинала из GUP-2";
        Width = 545; Height = 255; MinWidth = 470; MinHeight = 230;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new Thickness(18) };
        Content = root;
        root.Children.Add(new TextBlock
        {
            Text = "Скопируйте SHA-256 оригинала из карточки документа GUP-2. " +
                "Сравнение выполняется локально с открытым PDF; ни файл, ни хеш не передаются в сеть.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });
        root.Children.Add(new TextBlock { Text = "Ожидаемый SHA-256 (64 символа)",
            Margin = new Thickness(0, 0, 0, 6) });
        var field = new TextBox { MaxLength = 64, Height = 34,
            Padding = new Thickness(7, 5, 7, 5),
            ToolTip = "SHA-256 исходного WORM PDF, указанный в GUP-2" };
        root.Children.Add(field);
        var row = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0) };
        var verify = new Button { Content = "Проверить", MinWidth = 120,
            Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 5, 8, 5) };
        var cancel = new Button { Content = "Отмена", IsCancel = true,
            Padding = new Thickness(8, 5, 8, 5) };
        row.Children.Add(verify); row.Children.Add(cancel); root.Children.Add(row);
        verify.Click += (_, _) =>
        {
            string raw = field.Text.Trim();
            if (raw.Length != 64 || !raw.All(Uri.IsHexDigit))
            {
                MessageBox.Show(this, "Укажите 64 символа SHA-256 из карточки GUP-2.",
                    "Проверка SHA-256", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ExpectedSha256 = raw;
            DialogResult = true;
        };
    }
}

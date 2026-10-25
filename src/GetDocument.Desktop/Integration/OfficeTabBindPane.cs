using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using GetDocument.Core.Integration;

namespace GetDocument.Desktop.Integration;

/// <summary>
/// Links exactly the document that was selected before opening this pane.
/// A 256-bit browser code and local-file SHA are required for the link.
/// No file contents, cookies, URL parameters or UUID are sent to the server.
/// </summary>
public sealed class OfficeTabBindPane : UserControl
{
    public OfficeTabBindPane(string path, OfficeTabBindingPolicy policy,
        Action<OfficeTabBindingReceipt> onBound)
    {
        var secret = new TextBox
        {
            Width = 450,
            PasswordChar = '●',
            PlaceholderText = "Одноразовый код привязки из карточки GUP-2",
        };
        var status = new TextBlock
        {
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        var content = new StackPanel
        {
            Margin = new Thickness(32), Spacing = 16,
            HorizontalAlignment = HorizontalAlignment.Left, Width = 620,
        };
        content.Children.Add(new TextBlock
        {
            Text = "Связать вкладку с GUP-2",
            FontSize = 22,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
        });
        content.Children.Add(new TextBlock
        {
            Text = "Выбран открытый файл: " + Path.GetFileName(path) +
                   ". Получите в карточке GUP-2 код привязки и вставьте его. " +
                   "Программа проверит SHA-256 именно этого локального DOCX/XLSX. " +
                   "Привязка не разрешает отправку файлов или замену редакций.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        content.Children.Add(secret);
        var confirm = new Button { Content = "Проверить файл и связать вкладку" };
        confirm.Click += async (_, _) =>
        {
            var code = secret.Text?.Trim() ?? "";
            secret.Text = string.Empty;
            confirm.IsEnabled = false;
            status.Text = "Проверяется SHA-256 и соответствие карточке GUP-2…";
            try
            {
                var receipt = await OfficeTabBindingClient.BindAsync(path, code, policy);
                onBound(receipt);
                status.Text = "Связано с карточкой №" + receipt.RegistrationNumber +
                              " (версия " + receipt.EditVersion +
                              "). Привязка сохраняется только до изменения файла или закрытия окна.";
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or
                IOException or UnauthorizedAccessException or HttpRequestException or
                InvalidOperationException or OperationCanceledException or System.Text.Json.JsonException)
            {
                status.Text = "Привязка не подтверждена. Проверьте, что открыт " +
                    "именно скачанный из этой карточки DOCX/XLSX, затем получите новый код.";
            }
            finally { confirm.IsEnabled = true; }
        };
        content.Children.Add(confirm);
        content.Children.Add(status);
        Content = new ScrollViewer { Content = content };
    }
}

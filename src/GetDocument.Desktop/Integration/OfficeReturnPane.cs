using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using System.Text.Json;
using GetDocument.Core.Integration;

namespace GetDocument.Desktop.Integration;

/// <summary>
/// Explicit user-initiated return of an already saved Office working copy.
/// The destination is read only from an IT-managed local policy, not from a
/// text box, and credentials never persist beyond the upload request.
/// </summary>
public sealed class OfficeReturnPane : UserControl
{
    private readonly Window _owner;
    private readonly OfficeReturnPolicy _policy;
    private readonly TextBox _code = new() { PasswordChar = '●', PlaceholderText = "Одноразовый код из GUP-2", Width = 440 };
    private readonly TextBlock _selected = new() { Text = "Файл не выбран" };
    private readonly TextBlock _status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private string? _filePath;

    public OfficeReturnPane(Window owner, OfficeReturnPolicy policy)
    {
        _owner = owner;
        _policy = policy.Enabled ? policy : throw new InvalidOperationException("Интеграция выключена.");

        var content = new StackPanel
        {
            Margin = new Thickness(32), Spacing = 14,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 610
        };
        content.Children.Add(new TextBlock
        {
            Text = "Возврат рабочей копии в GUP-2",
            FontSize = 22,
            FontWeight = Avalonia.Media.FontWeight.SemiBold
        });
        content.Children.Add(new TextBlock
        {
            Text = "Сначала сохраните отдельную DOCX/XLSX-копию в ГЭТ Документ. " +
                   "В карточке черновика GUP-2 получите одноразовый код, затем выберите " +
                   "сохранённый файл и отправьте его. Исходный WORM PDF не изменяется.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });

        var choose = new Button { Content = "Выбрать DOCX/XLSX" };
        choose.Click += async (_, _) =>
        {
            try
            {
                var picked = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Выберите сохранённую рабочую копию",
                    AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("Office")
                    {
                        Patterns = ["*.docx", "*.xlsx"]
                    }]
                });
                var file = picked.FirstOrDefault();
                if (file is null) return;
                if (!file.Path.IsFile || !OfficeReturnClient.IsSupportedCopy(file.Path.LocalPath))
                {
                    _status.Text = "Можно выбрать только локальную или сетевую рабочую копию DOCX/XLSX.";
                    return;
                }
                _filePath = file.Path.LocalPath;
                _selected.Text = "Выбран файл: " + Path.GetFileName(_filePath);
                _status.Text = "";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                InvalidOperationException or NotSupportedException)
            {
                _status.Text = "Не удалось выбрать рабочую копию.";
            }
        };
        content.Children.Add(choose);
        content.Children.Add(_selected);
        content.Children.Add(_code);

        var send = new Button { Content = "Отправить рабочую копию в GUP-2" };
        send.Click += async (_, _) =>
        {
            if (_filePath is null)
            {
                _status.Text = "Сначала выберите сохранённый DOCX/XLSX.";
                return;
            }
            string code = _code.Text?.Trim() ?? "";
            _code.Text = string.Empty; // do not keep capability in the control
            send.IsEnabled = false;
            choose.IsEnabled = false;
            _status.Text = "Проверяется приём рабочей копии сервером GUP-2…";
            try
            {
                OfficeReturnReceipt receipt =
                    await OfficeReturnClient.UploadAsync(_filePath, code, _policy);
                _filePath = null;
                _selected.Text = "Файл не выбран";
                _status.Text = "Рабочая копия сохранена в GUP-2. Версия карточки: " +
                               receipt.EditVersion + ". Проверьте результат в GUP-2.";
            }
            catch (OfficeReturnConflictException)
            {
                _status.Text = "Конфликт редакций: другой сотрудник уже изменил " +
                               "рабочую копию в GUP-2. Ничего не перезаписано. " +
                               "Обновите карточку, сравните изменения и получите новый код.";
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or
                IOException or UnauthorizedAccessException or HttpRequestException or
                OperationCanceledException or InvalidOperationException or JsonException)
            {
                // Never echo the secret, body, URL or local document paths.
                _status.Text = "Возврат не подтверждён. Проверьте доступ, код, " +
                               "подключение и версию карточки; для повтора получите новый код.";
            }
            finally
            {
                send.IsEnabled = true;
                choose.IsEnabled = true;
            }
        };
        content.Children.Add(send);
        content.Children.Add(_status);
        Content = new ScrollViewer { Content = content };
    }
}

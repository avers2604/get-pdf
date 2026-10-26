using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using GetDocument.Core.Integration;

namespace GetDocument.Desktop.Integration;

/// <summary>
/// Read-only GUP-2 identity verification for a selected local working copy.
/// No document bytes are uploaded, no editing rights or Euro-Office session
/// are issued by the binding endpoint.
/// </summary>
public sealed class OfficeBindingPane : UserControl, IDisposable
{
    private readonly Window _owner;
    private readonly OfficeReturnPolicy _policy;
    private readonly TextBox _code = new()
    {
        PasswordChar = '●',
        PlaceholderText = "Одноразовый код привязки из карточки GUP-2",
        Width = 480
    };
    private readonly TextBlock _selected = new() { Text = "Рабочая копия не выбрана" };
    private readonly TextBlock _status = new()
    {
        TextWrapping = Avalonia.Media.TextWrapping.Wrap
    };
    private readonly CancellationTokenSource _lifetime = new();
    private string? _selectedPath;
    private bool _busy;
    private bool _disposed;

    public Gup2OfficeTabBinding? BoundMetadata { get; private set; }

    public OfficeBindingPane(Window owner, OfficeReturnPolicy policy)
    {
        _owner = owner;
        _policy = policy.AllowGup2TabBinding ? policy
            : throw new InvalidOperationException("Привязка GUP-2 отключена.");

        var content = new StackPanel
        {
            Margin = new Thickness(32),
            Spacing = 14,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 650
        };
        content.Children.Add(new TextBlock
        {
            Text = "Привязать рабочую копию к GUP-2",
            FontSize = 18,
            FontWeight = Avalonia.Media.FontWeight.SemiBold
        });
        content.Children.Add(new TextBlock
        {
            Text = "Выберите сохранённый DOCX/XLSX, затем в карточке GUP-2 " +
                   "получите одноразовый код «Привязать вкладку». " +
                   "Приложение проверит SHA-256, актуальную версию и права доступа. " +
                   "Привязка не сохраняет файл на сервере, не выдаёт права на редактирование " +
                   "и не открывает Euro-Office без отдельной авторизации.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });

        var choose = new Button { Content = "Выбрать DOCX/XLSX" };
        choose.Click += async (_, _) =>
        {
            if (_disposed || _busy) return;
            try
            {
                var picked = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Выберите текущую рабочую копию",
                    AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("Office")
                    {
                        Patterns = ["*.docx", "*.xlsx"]
                    }]
                });
                var file = picked.FirstOrDefault();
                if (file is null) return;
                if (!file.Path.IsFile ||
                    !OfficeReturnClient.IsSupportedCopy(file.Path.LocalPath))
                {
                    _status.Text = "Требуется сохранённый локальный или сетевой DOCX/XLSX.";
                    return;
                }
                _selectedPath = file.Path.LocalPath;
                BoundMetadata = null;
                _selected.Text = "Выбрано: " + Path.GetFileName(_selectedPath);
                _status.Text = "";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                ArgumentException or InvalidOperationException or NotSupportedException)
            {
                _status.Text = "Не удалось выбрать файл.";
            }
        };
        content.Children.Add(choose);
        content.Children.Add(_selected);
        content.Children.Add(_code);
        var bind = new Button { Content = "Проверить привязку в GUP-2" };
        bind.Click += async (_, _) =>
        {
            if (_disposed || _busy) return;
            if (_selectedPath is null)
            {
                _status.Text = "Сначала выберите рабочую копию.";
                return;
            }
            string code = _code.Text?.Trim() ?? "";
            _code.Text = string.Empty;
            _busy = true;
            choose.IsEnabled = false;
            bind.IsEnabled = false;
            BoundMetadata = null;
            _status.Text = "Проверяется SHA-256 и права доступа…";
            try
            {
                var binding = await Gup2OfficeTabBindingClient.RedeemAsync(
                    _selectedPath, code, _policy, _lifetime.Token);
                if (_disposed) return;
                BoundMetadata = binding;
                _status.Text = "Совпадение подтверждено GUP-2: " +
                    binding.RegistrationNumber + "; версия " + binding.EditVersion +
                    ". Это проверка связи с карточкой, не разрешение на редактирование.";
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or
                HttpRequestException or InvalidDataException or ArgumentException or
                UnauthorizedAccessException or OperationCanceledException)
            {
                if (!_disposed)
                    _status.Text = "Привязка не подтверждена: код истёк, документ " +
                                   "изменился или доступ запрещён. Получите новый код в GUP-2.";
            }
            finally
            {
                _busy = false;
                if (!_disposed)
                {
                    choose.IsEnabled = true;
                    bind.IsEnabled = true;
                }
                else _lifetime.Dispose();
            }
        };
        content.Children.Add(bind);
        content.Children.Add(_status);
        Content = new ScrollViewer { Content = content };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _code.Text = string.Empty;
        _selectedPath = null;
        BoundMetadata = null;
        _lifetime.Cancel();
        if (!_busy) _lifetime.Dispose();
    }
}

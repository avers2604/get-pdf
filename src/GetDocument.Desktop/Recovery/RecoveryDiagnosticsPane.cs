using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using GetDocument.Office.LibreOffice;

namespace GetDocument.Desktop.Recovery;

/// <summary>
/// GET DS 4.1 read-only recovery diagnostic tab. Uses the existing per-source
/// OfficeRecoveryStore inventory; never opens a recovery file, publishes a
/// revision, or offers deletion/restoration actions.
/// </summary>
internal sealed class RecoveryDiagnosticsPane : Border, IDisposable
{
    private readonly string _sourcePath;
    private readonly OfficeRecoveryStore _store = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TextBlock _summary = new()
    {
        FontSize = 15,
        FontWeight = FontWeight.SemiBold,
        TextWrapping = TextWrapping.Wrap
    };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _entries = new() { Spacing = 10 };
    private readonly Button _refresh = new() { Content = "Обновить сведения" };
    private CancellationTokenSource? _activeInspection;
    // A diagnostics tab may exist without being the selected document view.
    // Do not inspect a hidden tab; only the selected tab may run enumeration.
    private bool _suspended = true;
    private bool _resumePending;
    private bool _busy;
    private bool _disposed;

    public RecoveryDiagnosticsPane(string sourcePath)
    {
        _sourcePath = Path.GetFullPath(sourcePath);
        Padding = new Thickness(28);
        Focusable = true;
        Avalonia.Automation.AutomationProperties.SetName(this,
            "Диагностика аварийных копий. Только просмотр.");
        // The tab content is attached after TabControl selects it. Focus
        // this read-only surface, not its temporarily disabled refresh button.
        AttachedToVisualTree += (_, _) =>
        {
            if (!_disposed) Focus();
        };
        KeyDown += OnPaneKeyDown;

        var body = new StackPanel
        {
            Spacing = 14,
            MaxWidth = 940,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var title = new TextBlock { Text = "Диагностика аварийных копий" };
        title.Classes.Add("title");
        body.Children.Add(title);

        var lead = new TextBlock
        {
            Text = "Только просмотр временных файлов. Сведения не подтверждают " +
                   "возможность восстановления и не означают, что редактор завершил запись.",
            TextWrapping = TextWrapping.Wrap
        };
        lead.Classes.Add("hint");
        body.Children.Add(lead);

        var fileCard = Card();
        var fileStack = new StackPanel { Spacing = 5 };
        var fileLabel = new TextBlock { Text = "Выбранный документ" };
        fileLabel.Classes.Add("hint");
        fileStack.Children.Add(fileLabel);
        fileStack.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(_sourcePath),
            FontWeight = FontWeight.SemiBold,
            FontSize = 17,
            TextWrapping = TextWrapping.Wrap
        });
        fileCard.Child = fileStack;
        body.Children.Add(fileCard);

        // A horizontal StackPanel clips the long hint on compact displays.
        // WrapPanel preserves tab order while moving the hint to a new row.
        var controls = new WrapPanel
        {
            Orientation = Orientation.Horizontal
        };
        _refresh.Margin = new Thickness(0, 0, 14, 8);
        _refresh.Classes.Add("toolbar");
        Avalonia.Automation.AutomationProperties.SetName(
            _refresh, "Обновить сведения об аварийных копиях");
        _refresh.Click += async (_, _) => await RefreshAsync();
        controls.Children.Add(_refresh);
        var scope = new TextBlock
        {
            Text = "Один документ · до 256 записей · без доступа к содержимому",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap
        };
        scope.Classes.Add("hint");
        controls.Children.Add(scope);
        body.Children.Add(controls);

        var summaryCard = Card();
        var summaryStack = new StackPanel { Spacing = 8 };
        _summary.Text = "Сведения ещё не проверялись";
        Avalonia.Automation.AutomationProperties.SetLiveSetting(
            _summary, Avalonia.Automation.AutomationLiveSetting.Polite);
        _details.Text = "Обновите сведения, чтобы проверить наличие незавершённых файлов.";
        _details.Classes.Add("hint");
        UpdateAccessibleStatus();
        summaryStack.Children.Add(_summary);
        summaryStack.Children.Add(_details);
        summaryCard.Child = summaryStack;
        body.Children.Add(summaryCard);

        var listTitle = new TextBlock { Text = "Обнаруженные временные объекты" };
        listTitle.Classes.Add("section");
        body.Children.Add(listTitle);
        body.Children.Add(_entries);

        var footer = new TextBlock
        {
            Text = "Автоматическое удаление и восстановление отключены. " +
                   "Даже обычный файл может принадлежать активному процессу; " +
                   "для действий с копиями требуется отдельная проверка ИТ-службы.",
            TextWrapping = TextWrapping.Wrap
        };
        footer.Classes.Add("hint");
        body.Children.Add(footer);

        Child = new ScrollViewer { Content = body };
    }

    /// <summary>F5 refresh works from either the pane or its single button.</summary>
    private void OnPaneKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _disposed || e.Key != Key.F5) return;
        e.Handled = true;
        _ = RefreshAsync();
    }

    /// <summary>
    /// A static automation name masks a changing TextBlock in some readers.
    /// Keep the announced name in sync with both status and limitations.
    /// </summary>
    private void UpdateAccessibleStatus()
    {
        Avalonia.Automation.AutomationProperties.SetName(
            _summary, "Состояние диагностики: " + _summary.Text + ". " + _details.Text);
    }

    /// <summary>
    /// Called when a different tab becomes active. Cancel only this scan,
    /// never remove any file or invalidate the whole per-user recovery store.
    /// </summary>
    internal void SuspendInspection()
    {
        if (_disposed || _suspended) return;
        _suspended = true;
        _resumePending = false;
        _activeInspection?.Cancel();
        _refresh.IsEnabled = false;
        _summary.Foreground = Brush("GetMutedBrush");
        _summary.Text = "Проверка приостановлена";
        _details.Text = "Вкладка неактивна. При возвращении сведения будут проверены заново.";
        UpdateAccessibleStatus();
    }

    /// <summary>
    /// Resuming during cancellation defers the new scan until its old task
    /// has exited. Repeated selection events must not launch duplicate work.
    /// </summary>
    internal void ResumeInspection()
    {
        if (_disposed || !_suspended) return;
        _suspended = false;
        if (_busy)
        {
            _resumePending = true;
            _summary.Text = "Подготовка повторной проверки…";
            _details.Text = "Завершается предыдущая проверка; сведения пока не обновлены.";
            UpdateAccessibleStatus();
            return;
        }

        _ = RefreshAsync();
    }

    internal async Task RefreshAsync()
    {
        if (_disposed || _busy || _suspended) return;
        _busy = true;
        using var inspection = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token);
        _activeInspection = inspection;
        CancellationToken token = inspection.Token;
        _refresh.IsEnabled = false;
        _summary.Foreground = Brush("GetNavyBrush");
        _summary.Text = "Проверка временных объектов…";
        _details.Text = "Читаются только имена и доступные метаданные в каталоге выбранного документа.";
        UpdateAccessibleStatus();
        _entries.Children.Clear();

        try
        {
            // Never block Avalonia's UI thread with directory enumeration.
            // Cancellation on closing this tab cannot authorize any action.
            OfficeRecoveryIncompleteInventory result = await Task.Run(
                () => _store.InspectIncomplete(
                    _sourcePath, cancellationToken: token),
                token);
            // Stale results from a deselected pane can never be presented as
            // a fresh, selected-tab observation.
            if (_disposed || _suspended || token.IsCancellationRequested) return;
            Render(result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Cancellation means "not checked", never "no temporary files".
            // Suspend/Dispose own the explicit state and must not be undone.
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or
                                   UnauthorizedAccessException or ArgumentException or
                                   NotSupportedException or System.Security.SecurityException)
        {
            if (_disposed || _suspended || token.IsCancellationRequested) return;
            _summary.Text = "Не удалось проверить временные файлы";
            _summary.Foreground = Brush("GetRedBrush");
            _details.Text = "Доступ или безопасность каталога не подтверждены. " +
                            "Ничего не удалено и не восстановлено.";
            UpdateAccessibleStatus();
        }
        finally
        {
            if (ReferenceEquals(_activeInspection, inspection))
                _activeInspection = null;
            _busy = false;
            if (!_disposed)
                _refresh.IsEnabled = !_suspended;

            // A user may switch back while the cancelled background task is
            // still exiting. Start exactly one fresh scan only after it ends.
            if (_resumePending && !_disposed && !_suspended)
            {
                _resumePending = false;
                _ = RefreshAsync();
            }
        }
    }

    private void Render(OfficeRecoveryIncompleteInventory result)
    {
        int untrusted = result.Candidates.Count(x =>
            x.Status != OfficeRecoveryIncompleteStatus.RegularUnverified);
        _summary.Foreground = Brush(untrusted > 0 || result.Truncated
            ? "GetRedBrush" : "GetNavyBrush");
        _summary.Text = result.Candidates.Count switch
        {
            0 when !result.Truncated => "Временные файлы по шаблону не обнаружены",
            0 => "Проверка не завершена — перечень может быть неполным",
            1 => "Обнаружен 1 временный объект — не проверен",
            _ => $"Обнаружено {result.Candidates.Count} временных объектов — не проверены"
        };
        // Both problems can coexist: a truncated listing may ALSO contain
        // untrusted entries. Never mask one warning with the other.
        var warnings = new List<string>();
        if (result.Truncated)
            warnings.Add("Превышен предел просмотра (256 записей). Список неполон; " +
                         "отсутствие объекта в нём ничего не доказывает.");
        if (untrusted > 0)
            warnings.Add($"Недоверенных или недоступных объектов: {untrusted}. " +
                         "Не открывайте и не удаляйте их автоматически.");
        warnings.Add("Показаны только имена и метаданные. " +
                     "Наличие файла не подтверждает его пригодность для восстановления.");
        _details.Text = string.Join(" ", warnings);
        UpdateAccessibleStatus();

        _entries.Children.Clear();
        foreach (OfficeRecoveryIncompleteCandidate item in result.Candidates)
            _entries.Children.Add(CreateRow(item));

        if (result.Candidates.Count == 0)
        {
            var hint = new TextBlock
            {
                Text = result.Truncated
                    ? "Проверка остановлена на лимите; перечень не исчерпывающий."
                    : "Незавершённых файлов с ожидаемыми именами не найдено. " +
                      "Это не проверка целостности уже опубликованных копий.",
                TextWrapping = TextWrapping.Wrap
            };
            hint.Classes.Add("hint");
            _entries.Children.Add(hint);
        }
    }

    private static Border CreateRow(OfficeRecoveryIncompleteCandidate item)
    {
        var card = Card();
        var info = new StackPanel { Spacing = 7 };

        string kind = item.Kind switch
        {
            OfficeRecoveryIncompleteKind.StagedOfficeCopy => "Временная офисная копия",
            OfficeRecoveryIncompleteKind.TemporaryManifest => "Временный манифест",
            _ => "Неизвестный тип"
        };
        string state = item.Status switch
        {
            OfficeRecoveryIncompleteStatus.RegularUnverified => "Обнаружен · не проверен",
            OfficeRecoveryIncompleteStatus.UntrustedLinkOrNonFile =>
                "Недоверенный объект: ссылка или каталог",
            OfficeRecoveryIncompleteStatus.MetadataUnavailable =>
                "Метаданные недоступны",
            _ => "Состояние неизвестно"
        };
        info.Children.Add(new TextBlock
        {
            Text = kind,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        var name = new TextBlock { Text = item.FileName, TextWrapping = TextWrapping.Wrap };
        name.Classes.Add("hint");
        info.Children.Add(name);

        info.Children.Add(new TextBlock
        {
            Text = state,
            Foreground = Brush(item.Status == OfficeRecoveryIncompleteStatus.RegularUnverified
                ? "GetMutedBrush" : "GetRedBrush"),
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold
        });

        if (item.Status == OfficeRecoveryIncompleteStatus.RegularUnverified)
        {
            string size = item.LengthBytes is { } length
                ? $"{length.ToString("N0", CultureInfo.CurrentCulture)} байт"
                : "размер неизвестен";
            string date = item.LastWriteUtc is { } time
                ? time.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture)
                : "время неизвестно";
            var meta = new TextBlock
            {
                Text = $"{size} · изменение: {date}",
                TextWrapping = TextWrapping.Wrap
            };
            meta.Classes.Add("hint");
            info.Children.Add(meta);
        }

        card.Child = info;
        return card;
    }

    private static Border Card()
    {
        var card = new Border { Padding = new Thickness(18) };
        card.Classes.Add("card");
        return card;
    }

    private static IBrush Brush(string key) =>
        (IBrush)Application.Current!.FindResource(key)!;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _suspended = true;
        _resumePending = false;
        _activeInspection?.Cancel();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

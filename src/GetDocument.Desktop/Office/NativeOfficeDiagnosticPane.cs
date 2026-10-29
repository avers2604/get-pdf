using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using GetDocument.Office.EuroOfficeDesktop;

namespace GetDocument.Desktop.Office;

/// <summary>
/// Passive content for a future EuroOffice tab in the existing GET tab strip.
/// Does not launch Qt/CEF, access files or display an unverified editor.
/// It is intentionally not reachable from the normal production launcher.
/// </summary>
internal sealed class NativeOfficeDiagnosticPane : Border
{
    private EuroOfficeOsrLabInputChannel? _labInput;
    private EuroOfficeImeLabComposition? _imeComposition;
    private AvaloniaNativeImeLabClient? _nativeImeClient;
    private Action? _onLabInputFailure;
    private bool _labFocused;
    private int _imageWidth, _imageHeight;
    private long _lastMoveTimestamp;
    private long _lastPressTimestamp;
    private double _previousPressX, _previousPressY;
    private double _latestPointerX, _latestPointerY;
    private bool _labButtonHeld;
    private IPointer? _capturedLabPointer;
    private readonly SemaphoreSlim _labGestureGate = new(1, 1);
    private bool _labReservedModifierActive;
    private readonly TextBlock _state = new()
    {
        FontSize = 16,
        FontWeight = FontWeight.SemiBold,
        TextWrapping = TextWrapping.Wrap
    };

    private readonly TextBlock _details = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.8
    };

    // A decoded, bounded Qt painter preview, not a CEF screenshot or editor.
    private readonly Image _preview = new()
    {
        Stretch = Stretch.Uniform,
        MaxWidth = 640,
        MaxHeight = 360,
        IsVisible = false,
        Focusable = true
    };

    private readonly TextBlock _connection = new()
    {
        Text = "Qt IPC: соединение не подтверждено; CEF не подключён",
        TextWrapping = TextWrapping.Wrap
    };

    public NativeOfficeDiagnosticPane(EuroOfficeNativeWorkspaceTabState initial)
    {
        Padding = new Thickness(28);
        var layout = new StackPanel
        {
            Spacing = 15,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        layout.Children.Add(new TextBlock
        {
            Text = "EuroOffice DesktopEditors — лабораторный сеанс",
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        layout.Children.Add(_state);
        layout.Children.Add(_connection);
        layout.Children.Add(_preview);
        layout.Children.Add(_details);
        // Pointer-only CEF input is enabled exclusively by the lab process
        // supervisor. A pointer press grants focus, NOT a browser click.
        _preview.PointerPressed += OnLabPointerPressed;
        _preview.PointerReleased += OnLabPointerReleased;
        _preview.PointerCaptureLost += OnLabPointerCaptureLost;
        _preview.KeyDown += OnLabKeyDown;
        _preview.KeyUp += OnLabKeyUp;
        _preview.TextInput += OnLabTextInput;
        _preview.TextInputMethodClientRequested += OnLabImeClientRequested;
        _preview.PointerMoved += OnLabPointerMoved;
        _preview.PointerWheelChanged += OnLabPointerWheel;
        _preview.PointerExited += OnLabPointerExited;
        layout.Children.Add(new TextBlock
        {
            Text = "Это диагностическая вкладка, а не открытый офисный документ. " +
                   "Сохранение, PDF и печать недоступны до проверки нативного редактора, " +
                   "WORM/CAS и собственного мастера печати ГЭТ.",
            TextWrapping = TextWrapping.Wrap
        });
        Child = new ScrollViewer { Content = layout };
        Update(initial);
    }

    /// <summary>Qt transport ACK is NOT a document-loaded event.</summary>
    public void SetTransportConnected() =>
        _connection.Text = "Qt IPC подключён. Редактор CEF и документ не подтверждены.";

    /// <summary>
    /// Native SDK path binding is useful LAB evidence, never a verified OOXML
    /// document or permission to save/print. Called only after the scoped
    /// synthetic Qt host has been reaped and its receipt parsed.
    /// </summary>
    public void SetNativeBindingEvidence(EuroOfficeNativeBindingReceipt evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.DocumentVerified || evidence.SaveCompleted ||
            evidence.PrintApproved || evidence.ApprovedForProduction)
            throw new InvalidDataException("Binding-лаборатория не может выдать права редактора.");
        _connection.Text = evidence.BindingObserved
            ? "Нативный EuroOffice подтвердил привязку к синтетическому файлу и загрузку CEF view. " +
              "Содержимое OOXML, сохранение и печать НЕ подтверждены."
            : "Нативная привязка EuroOffice к синтетическому файлу не подтверждена. " +
              "Открытие документа и сохранение запрещены.";
    }

    /// <summary>
    /// Reports a private synthetic conversion *candidate*. Editor.bin belongs
    /// to the disposable lab profile; it is not an OOXML import or save ACK.
    /// </summary>
    public void SetNativeConversionCandidate(EuroOfficeNativeConversionCandidate evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.DocumentVerified || evidence.EditReady ||
            evidence.SaveCompleted || evidence.PdfReady ||
            evidence.ApprovedForProduction)
            throw new InvalidDataException("Конвертер не даёт прав на редактирование или запись.");
        _connection.Text = evidence.ConversionArtifactObserved
            ? "EuroOffice: найден стабильный результат конвертера в изолированном профиле. " +
              "Редактирование DOCX/XLSX/PPTX, корректность импорта и сохранение НЕ подтверждены."
            : "EuroOffice: результат конвертации тестового файла не подтверждён. " +
              "Редактирование и сохранение недоступны.";
    }

    /// <summary>
    /// Explicit patched-SDK renderer notification correlated with private
    /// synthetic conversion artifact; still no validated OOXML or edit/save.
    /// </summary>
    public void SetNativeReadyCandidate(EuroOfficeNativeReadyCandidate evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.DocumentVerified || evidence.EditReady ||
            evidence.SaveCompleted || evidence.PdfReady ||
            evidence.ApprovedForProduction)
            throw new InvalidDataException("Нельзя повышать права по CEF ready событию.");
        _connection.Text = evidence.ReadyAndConversionCorrelated
            ? "EuroOffice: тестовый сигнал ContentReady связан с результатом конвертера. " +
              "Это только лабораторное наблюдение; импорт OOXML, редактирование и сохранение НЕ подтверждены."
            : "EuroOffice: полный набор лабораторных признаков загрузки НЕ получен. " +
              "Рабочий редактор и сохранение недоступны.";
    }

    /// <summary>
    /// A post-save OOXML ZIP pass is lab evidence only, not a production
    /// revision, a valid MS Office compatibility round-trip or WORM write.
    /// </summary>
    public void SetNativeRoundTripReview(EuroOfficeNativeRoundTripLab.Review review)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (review.SaveCompleted || review.AuthorizedForPublication ||
            review.DocumentVerified)
            throw new InvalidDataException("Нативный тест не может разрешать публикацию документа.");
        _connection.Text = review.CandidateValidatedAsOoxml
            ? "EuroOffice: сохранена ИЗОЛИРОВАННАЯ тестовая копия и проверена структура OOXML. " +
              "Кандидат в карантине; редактирование рабочих документов и публикация ЗАПРЕЩЕНЫ."
            : "EuroOffice: нет проверенного сохранённого OOXML-кандидата. " +
              "Эталонный файл не изменён, разрешения на запись нет.";
    }

    /// <summary>
    /// Isolated SDK Save As dialog to approved same-format QTemporaryDir file.
    /// No user file picker, publishing or real document save permission.
    /// </summary>
    public void SetNativeSaveAsReview(EuroOfficeNativeSaveAsLab.Review evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.DocumentVerified || evidence.SaveCompleted ||
            evidence.AuthorizedForPublication)
            throw new InvalidDataException("Нельзя выдавать права сохранения по SDK лаборатории.");
        _connection.Text = evidence.CandidateValidatedAsOoxml
            ? "EuroOffice: типизированное «Сохранить как» создало тестовый OOXML-кандидат " +
              "в карантине. Совместимость MS Office и сохранение рабочих файлов НЕ подтверждены."
            : "EuroOffice: лабораторное «Сохранить как» не подтвердило корректный OOXML. " +
              "Пользовательские файлы и WORM недоступны.";
    }

    public void SetTransportLost()
    {
        DetachOsrLabInput();
        _connection.Text = "Связь с Qt-хостом потеряна; правки не восстановлены.";
        _preview.IsVisible = false;
        if (_preview.Source is IDisposable old) old.Dispose();
        _preview.Source = null;
    }

    /// <summary>
    /// Display only the validated, bounded Qt painter image on UI thread.
    /// No native HWND/XID or Qt QWidget* is attached to Avalonia.
    /// </summary>
    public void SetDiagnosticFrame(EuroOfficeNativeDiagnosticFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.CefRendered || frame.OfficeDocumentVerified ||
            frame.RevisionCommitted || frame.PrintApproved)
            throw new InvalidDataException("Нельзя показывать неподтверждённый офисный кадр.");
        using var input = new MemoryStream(frame.PngBytes, writable: false);
        var bitmap = new Bitmap(input);
        if (bitmap.PixelSize.Width != frame.Width ||
            bitmap.PixelSize.Height != frame.Height)
        {
            bitmap.Dispose();
            throw new InvalidDataException("Размеры диагностического кадра не совпадают.");
        }
        if (_preview.Source is IDisposable previous) previous.Dispose();
        _preview.Source = bitmap;
        _preview.IsVisible = true;
        _connection.Text = "Доставлен тестовый кадр Qt (без CEF и офисного документа).";
    }

    /// <summary>
    /// A genuine upstream CEF OnPaint frame from a synthetic-only isolated
    /// Wayland laboratory. STILL NOT a verified office document or editor.
    /// </summary>
    public void SetCefOsrLabFrame(EuroOfficeOsrStreamFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.OfficeDocumentVerified || frame.SaveCompleted ||
            frame.PdfReady || frame.PrintApproved || frame.ApprovedForProduction)
            throw new InvalidDataException("Лабораторный CEF кадр не даёт офисных полномочий.");
        using var input = new MemoryStream(frame.PngBytes, writable: false);
        var image = new Bitmap(input);
        if (image.PixelSize.Width != frame.Width ||
            image.PixelSize.Height != frame.Height)
        {
            image.Dispose();
            throw new InvalidDataException("PNG кадр CEF не соответствует заявленным размерам.");
        }
        if (_preview.Source is IDisposable previous) previous.Dispose();
        _preview.Source = image;
        _preview.IsVisible = true;
        _imageWidth = frame.Width;
        _imageHeight = frame.Height;
        _connection.Text = $"Реальный CEF OnPaint: кадр {frame.FrameNumber}. " +
                           "Только Wayland-лаборатория, документ и сохранение не подтверждены.";
    }

    /// <summary>Explicitly attached only to a SHA-approved synthetic OSR process.</summary>
    public void AttachOsrLabInput(EuroOfficeOsrLabInputChannel input,
        Action onInputFailure)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(onInputFailure);
        DetachOsrLabInput();
        _labInput = input;
        _onLabInputFailure = onInputFailure;
        if (input.ImeCommitLabApproved)
        {
            _imeComposition = new EuroOfficeImeLabComposition(input.SessionId, input.TabId,
                approved: true);
            _nativeImeClient = new AvaloniaNativeImeLabClient(
                _preview, input.SessionId, input.TabId,
                OnLabImePreeditChanged,
                () => ReferenceEquals(_labInput, input) && _labFocused);
            // No CEF editor caret telemetry exists yet, so CanRegisterWithOsIme
            // remains FALSE. Do NOT use pointer coordinates to fake the caret.
        }
        _connection.Text = input.ImeCommitLabApproved
            ? "Wayland CEF: локальная IME-композиция и итоговый commit подготовлены. " +
              "Preedit в CEF не передаётся; clipboard отключён."
            : input.KeyboardLabApproved
            ? "Wayland CEF: тестовая навигация и одиночные символы разрешены. " +
              "Ctrl/Alt/Meta, IME-композиция, clipboard и сохранение запрещены."
            : input.ClickLabApproved
                ? "Wayland CEF: лабораторные клики, двойной клик и выделение разрешены. " +
                  "Клавиатура, IME, буфер обмена и сохранение запрещены."
                : "Лабораторная мышь: нажмите на изображение для фокуса. " +
                  "Клики, клавиатура, IME и буфер обмена заблокированы.";
    }

    public void DetachOsrLabInput(EuroOfficeOsrLabInputChannel? expectedChannel = null)
    {
        if (expectedChannel is not null && !ReferenceEquals(_labInput, expectedChannel))
            return; // late callback from an older laboratory generation
        _nativeImeClient?.Dispose();
        _nativeImeClient = null;
        _imeComposition?.Dispose();
        _imeComposition = null;
        _labFocused = false;
        _labReservedModifierActive = false;
        _labInput = null;
        _onLabInputFailure = null;
        _lastMoveTimestamp = 0;
        _labButtonHeld = false;
        _capturedLabPointer?.Capture(null);
        _capturedLabPointer = null;
    }

    /// <summary>
    /// On tab-switch, revoke browser focus even if PointerExited was not
    /// delivered by Avalonia's layout/visibility transition.
    /// </summary>
    public async Task SuspendLabInputAsync()
    {
        await _labGestureGate.WaitAsync();
        try
        {
            if (_labInput is not { } input) return;
            _nativeImeClient?.Revoke();
            _imeComposition?.Cancel(); // a tab switch must drop local preedit
            await ReleaseLabButtonAsync(input);
            if (_labFocused)
            {
                _labFocused = false;
                _labReservedModifierActive = false;
                await input.SendFocusAsync(false, CancellationToken.None);
            }
        }
        catch (Exception ex) { FailLabInput(ex); }
        finally { _labGestureGate.Release(); }
    }

    // Only call while the gesture lock is owned. Clearing the local flag
    // before releasing pointer capture prevents duplicate up on capture-lost.
    private async Task ReleaseLabButtonAsync(EuroOfficeOsrLabInputChannel input)
    {
        if (!_labButtonHeld) return;
        _labButtonHeld = false;
        _capturedLabPointer?.Capture(null);
        _capturedLabPointer = null;
        await input.SendLeftButtonUpAsync(
            _latestPointerX, _latestPointerY, CancellationToken.None);
    }

    private async void OnLabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_labInput is not { } input || !_preview.IsVisible ||
            e.GetCurrentPoint(_preview).Properties.PointerUpdateKind !=
                PointerUpdateKind.LeftButtonPressed ||
            !TryNormalizedPosition(e.GetPosition(_preview), out double x, out double y))
            return;
        e.Handled = true;
        if (input.ClickLabApproved)
        {
            if (_labButtonHeld) return;
            _latestPointerX = x;
            _latestPointerY = y;
            // Capture and mark BEFORE the first async await. A fast release
            // queues behind this press, so CEF always sees down before up.
            _labButtonHeld = true;
            _capturedLabPointer = e.Pointer;
            e.Pointer.Capture(_preview);
        }
        await _labGestureGate.WaitAsync();
        try
        {
            if (!_labFocused)
            {
                await input.SendFocusAsync(true, CancellationToken.None);
                _labFocused = true;
                if (input.KeyboardLabApproved) _preview.Focus();
            }
            if (!input.ClickLabApproved) return; // GD-31J.14 mode: focus only
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            bool nearby = Math.Abs(x - _previousPressX) <= .015 &&
                          Math.Abs(y - _previousPressY) <= .015;
            double elapsed = _lastPressTimestamp == 0
                ? double.PositiveInfinity
                : (now - _lastPressTimestamp) * 1000.0 /
                  System.Diagnostics.Stopwatch.Frequency;
            int clickCount = nearby && elapsed <= 450 ? 2 : 1;
            _previousPressX = x;
            _previousPressY = y;
            _lastPressTimestamp = now;
            await input.SendLeftButtonDownAsync(x, y, clickCount,
                CancellationToken.None);
        }
        catch (Exception ex) { FailLabInput(ex); }
        finally { _labGestureGate.Release(); }
    }

    private async void OnLabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_labButtonHeld || _labInput is not { } input ||
            e.GetCurrentPoint(_preview).Properties.PointerUpdateKind !=
                PointerUpdateKind.LeftButtonReleased)
            return;
        e.Handled = true;
        if (TryNormalizedPosition(e.GetPosition(_preview), out double x, out double y))
        {
            _latestPointerX = x;
            _latestPointerY = y;
        }
        await _labGestureGate.WaitAsync();
        try { await ReleaseLabButtonAsync(input); }
        catch (Exception ex) { FailLabInput(ex); }
        finally { _labGestureGate.Release(); }
    }

    private async void OnLabPointerCaptureLost(object? sender,
        PointerCaptureLostEventArgs e)
    {
        if (_labButtonHeld) await SuspendLabInputAsync();
    }

    private async void OnLabPointerExited(object? sender, PointerEventArgs e)
    {
        await SuspendLabInputAsync();
    }

    private async void OnLabPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_labFocused || _labInput is not { } input) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastMoveTimestamp != 0 &&
            (now - _lastMoveTimestamp) * 1000.0 /
                System.Diagnostics.Stopwatch.Frequency < 75)
            return; // <= 13 pointer moves/sec, avoids exhausting native rate cap
        if (!TryNormalizedPosition(e.GetPosition(_preview), out double x, out double y))
            return;
        _latestPointerX = x;
        _latestPointerY = y;
        _lastMoveTimestamp = now;
        await _labGestureGate.WaitAsync();
        try { await input.SendPointerMoveAsync(x, y, CancellationToken.None); }
        catch (Exception ex) { FailLabInput(ex); }
        finally { _labGestureGate.Release(); }
    }

    private async void OnLabPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!_labFocused || _labInput is not { } input ||
            !TryNormalizedPosition(e.GetPosition(_preview), out double x, out double y))
            return;
        int deltaX = Math.Clamp((int)Math.Round(e.Delta.X * 120), -120, 120);
        int deltaY = Math.Clamp((int)Math.Round(e.Delta.Y * 120), -120, 120);
        if (deltaX == 0 && deltaY == 0 || _labButtonHeld) return;
        await _labGestureGate.WaitAsync();
        try
        {
            await input.SendWheelAsync(x, y, deltaX, deltaY, CancellationToken.None);
            e.Handled = true;
        }
        catch (Exception ex) { FailLabInput(ex); }
        finally { _labGestureGate.Release(); }
    }

    /// <summary>
    /// System and GET shell shortcuts are never forwarded. The editor gets
    /// only an explicit navigation whitelist, with optional Shift selection.
    /// </summary>
    private async void OnLabKeyDown(object? sender, KeyEventArgs e)
    {
        _labReservedModifierActive = (e.KeyModifiers &
            (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0;
        if (!_labFocused || _labButtonHeld ||
            _labInput is not { KeyboardLabApproved: true } input)
            return;
        if ((e.KeyModifiers &
            (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0)
            return; // Ctrl+Tab, Ctrl+W, Ctrl+P, Alt+F4, Win, etc -> GET/OS.
        string? name = e.Key switch
        {
            Key.Left => "ArrowLeft",
            Key.Right => "ArrowRight",
            Key.Up => "ArrowUp",
            Key.Down => "ArrowDown",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Escape => "Escape",
            Key.Back => "Backspace",
            Key.Delete => "Delete",
            Key.Enter => "Enter",
            Key.Tab => "Tab",
            _ => null
        };
        if (name is null) return; // never pass generic virtual key codes
        e.Handled = true;
        await _labGestureGate.WaitAsync();
        try
        {
            await input.SendNavigationKeyAsync(name,
                (e.KeyModifiers & KeyModifiers.Shift) != 0, CancellationToken.None);
        }
        catch (Exception ex) { FailLabInput(ex); }
        finally { _labGestureGate.Release(); }
    }

    private void OnLabKeyUp(object? sender, KeyEventArgs e)
    {
        _labReservedModifierActive = (e.KeyModifiers &
            (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0;
    }

    /// <summary>
    /// TextInput delivers already committed characters. Do not forward
    /// preedit/intermediate IME strings or multi-character paste payloads.
    /// </summary>
    private async void OnLabTextInput(object? sender, TextInputEventArgs e)
    {
        if (!_labFocused || _labButtonHeld || _labReservedModifierActive ||
            _imeComposition?.IsComposing == true ||
            _labInput is not { KeyboardLabApproved: true } input ||
            e.Text is not { Length: 1 } text)
            return;
        char c = text[0];
        if (char.IsControl(c) || char.IsSurrogate(c) ||
            char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)
            return;
        e.Handled = true;
        await _labGestureGate.WaitAsync();
        try { await input.SendTextCharacterAsync(c, CancellationToken.None); }
        catch (Exception ex) { FailLabInput(ex); }
        finally { _labGestureGate.Release(); }
    }

    /// <summary>
    /// Avalonia 12.1.4 will ask for an OS IME client on input focus. Until
    /// real, authenticated CEF caret telemetry exists, we must NOT claim
    /// that mouse/pixel-preview coordinates are the editor caret.
    /// </summary>
    private void OnLabImeClientRequested(object? sender,
        TextInputMethodClientRequestedEventArgs e)
    {
        if (!_labFocused || _labInput is not { ImeCommitLabApproved: true } ||
            _nativeImeClient is not { CanRegisterWithOsIme: true } client)
            return;
        e.Client = client;
    }

    private void OnLabImePreeditChanged(string? preedit)
    {
        if (_imeComposition is not { } composition) return;
        if (string.IsNullOrEmpty(preedit))
        {
            composition.Cancel();
            return;
        }
        try
        {
            if (!composition.IsComposing)
                composition.Begin();
            composition.Update(preedit);
            // Do not echo text into diagnostics or audit. OS candidate UI
            // must be independently verified against actual CEF caret rect.
            _connection.Text = "IME preedit локально активен; данные не отправлены в CEF.";
        }
        catch (ArgumentException)
        {
            composition.Cancel();
        }
        catch (InvalidOperationException)
        {
            composition.Cancel();
        }
    }

    // These methods are intentionally NOT wired to TextInput/clipboard: a
    // multi-character TextInput event could be paste, not an OS IME commit.
    // A reviewed ITextInputMethodClient adapter is required to invoke them.
    internal void BeginImeLabComposition()
    {
        if (!_labFocused || _labButtonHeld || _labInput is not { ImeCommitLabApproved: true })
            throw new InvalidOperationException("CEF IME Lab не в фокусе.");
        (_imeComposition ?? throw new NotSupportedException(
            "IME Lab input method adapter не подключён.")).Begin();
    }

    internal void UpdateImeLabComposition(string preedit) =>
        (_imeComposition ?? throw new NotSupportedException("IME Lab не подключён."))
            .Update(preedit);

    internal async Task CommitImeLabCompositionAsync(CancellationToken token)
    {
        if (!_labFocused || _labButtonHeld || _labInput is not { ImeCommitLabApproved: true } input)
            throw new InvalidOperationException("Нельзя отправить IME commit без фокуса.");
        var composition = _imeComposition ??
            throw new NotSupportedException("IME preedit не подключён.");
        await _labGestureGate.WaitAsync(token);
        try { await composition.CommitAsync(input, token); }
        catch (Exception ex)
        {
            FailLabInput(ex);
            throw;
        }
        finally { _labGestureGate.Release(); }
    }

    internal void CancelImeLabComposition() => _imeComposition?.Cancel();

    private bool TryNormalizedPosition(Point position, out double x, out double y)
    {
        x = y = 0;
        if (_imageWidth < 1 || _imageHeight < 1 ||
            _preview.Bounds.Width <= 0 || _preview.Bounds.Height <= 0)
            return false;
        // Stretch.Uniform may letterbox the frame inside Image.Bounds.
        // Exclude the letterbox; never send pointer coordinates from outside
        // the actually visible page preview.
        double ratio = Math.Min(_preview.Bounds.Width / _imageWidth,
                                _preview.Bounds.Height / _imageHeight);
        double drawnWidth = _imageWidth * ratio;
        double drawnHeight = _imageHeight * ratio;
        double left = (_preview.Bounds.Width - drawnWidth) / 2;
        double top = (_preview.Bounds.Height - drawnHeight) / 2;
        x = (position.X - left) / drawnWidth;
        y = (position.Y - top) / drawnHeight;
        return double.IsFinite(x) && double.IsFinite(y) &&
               x >= 0 && x < 1 && y >= 0 && y < 1;
    }

    private void FailLabInput(Exception exception)
    {
        // Never claim a CEF action succeeded after a broken pipe.
        // Terminate the associated child process, not only the UI controls.
        var terminate = _onLabInputFailure;
        DetachOsrLabInput();
        terminate?.Invoke();
        _connection.Text = "Лабораторный ввод отключён: " +
            exception.GetType().Name + ". Документ не проверен.";
    }

    public void SetCefOsrLabCompleted(int frames) =>
        _connection.Text = $"Лабораторный CEF поток завершён ({frames} кадров). " +
                           "Содержимое документа и сохранение не проверены.";

    public void Update(EuroOfficeNativeWorkspaceTabState state)
    {
        _state.Text = state.Status switch
        {
            EuroOfficeNativeDocumentStatus.Requested =>
                "Соединение с нативным редактором не подтверждено",
            EuroOfficeNativeDocumentStatus.ViewLoadedUnverified =>
                "Получено событие окна CEF. Документ не проверен",
            EuroOfficeNativeDocumentStatus.Failed =>
                "Нативный процесс завершился с ошибкой. Сеанс не восстановлен",
            EuroOfficeNativeDocumentStatus.Closed =>
                "Диагностический сеанс закрыт",
            _ => "Неподтверждённый сеанс"
        };
        _details.Text = $"Документ: {state.Title}. Поколение сеанса: {state.Generation}. " +
                        (state.ModifiedObserved
                            ? "Есть уведомление об изменении; сохранённая редакция не подтверждена."
                            : "Подтверждённых изменений нет.") +
                        " Открытие, сохранение и печать не подтверждены.";
    }
}

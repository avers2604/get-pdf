using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GetPdf.Controls;

namespace GetPdf;

/// <summary>
/// Tab host for independent PDF reader controls. Every tab retains its own
/// PDF source adapter, edit history, render scheduler and scratch folder.
/// </summary>
internal sealed partial class PdfTabbedWindow : Window
{
    private sealed record Handle(Guid Id, PdfViewerWindow Viewer, TabItem Tab,
        TextBlock Caption, Action OnReady);

    private static readonly HashSet<PdfTabbedWindow> OpenShells = [];
    private readonly PdfTabRegistry _registry = new();
    private readonly Dictionary<Guid, Handle> _handles = [];
    private readonly TabControl _tabs = new ShellTabControl { BorderThickness = new Thickness(0) };
    private TabStripPanel? _tabStrip;
    private bool _shutdownApproved;
    private bool _shutdownPending;
    private bool _suppressSelection;
    private WindowState _normalWindowState = WindowState.Normal;
    private WindowStyle _normalWindowStyle = WindowStyle.SingleBorderWindow;
    internal bool IsFullscreen { get; private set; }

    internal void ToggleFullscreen()
    {
        if (!IsFullscreen)
        {
            _normalWindowState = WindowState;
            _normalWindowStyle = WindowStyle;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowStyle = _normalWindowStyle;
            WindowState = _normalWindowState;
        }
        IsFullscreen = !IsFullscreen;
    }

    internal PdfTabbedWindow(IReadOnlyList<string> paths, int activeIndex = 0)
        : this(paths, activeIndex, includeEmptyPlaceholder: true) { }

    private PdfTabbedWindow(IReadOnlyList<string> paths, int activeIndex,
        bool includeEmptyPlaceholder)
    {
        Title = "ГЭТ PDF — просмотр документов";
        Width = 1440;
        Height = 950;
        MinWidth = 950;
        MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (Brush)FindResource("CanvasBrush");
        // Inherited text defaults (the implicit TextBlock style deliberately sets no colour).
        Foreground = (Brush)FindResource("Ink");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        // GET DS 4.1 GET PDF layout: mark + product name, document tabs and "+" share one header row.
        _tabs.Style = (Style)FindResource("ShellTabs");
        var newTab = IconAction("IconPlus", "Новая вкладка", "Новая вкладка (Ctrl+T)", () =>
        {
            if (_registry.Items.Count >= 16)
                GetMessageBox.Show(this, "Допускается до 16 вкладок в одном окне.",
                    "ГЭТ PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            else AddWorkspace(null);
        });
        newTab.Background = (Brush)FindResource("CanvasBrush");
        var trailing = new StackPanel { Orientation = Orientation.Horizontal };
        trailing.Children.Add(IconAction("IconArrowLeft", "Переместить вкладку влево",
            "Переместить вкладку влево", () => MoveSelected(-1)));
        trailing.Children.Add(IconAction("IconArrowRight", "Переместить вкладку вправо",
            "Переместить вкладку вправо", () => MoveSelected(1)));
        trailing.Children.Add(IconAction("IconArrowUpRight", "Открыть вкладку в отдельном окне",
            "В отдельное окно", DetachSelected));
        trailing.Children.Add(IconAction("IconHelp", "Справка по клавиатурным командам ГЭТ PDF",
            "Клавиатурная справка (F1)", ShowKeyboardHelp));
        _tabs.Tag = new ShellHeaderSlots { Leading = newTab, Trailing = trailing };
        _tabs.Loaded += (_, _) => BindTabStrip();
        _tabs.SelectionChanged += (_, e) =>
        {
            if (_suppressSelection || !ReferenceEquals(e.OriginalSource, _tabs)) return;
            _tabStrip?.InvalidateArrange(); // keep the selected tab scrolled into view
            if (_tabs.SelectedItem is TabItem selected && selected.Tag is Guid id)
            {
                _registry.Select(id);
                SaveSessions();
            }
        };
        Content = _tabs;
        foreach (string path in paths.Take(16))
        {
            if (PdfOpenRequest.TryParse([path], out string? valid) && valid is not null)
                AddWorkspace(valid, persist: false);
        }
        if (_tabs.Items.Count == 0 && includeEmptyPlaceholder)
            AddWorkspace(null, persist: false);
        if (_tabs.Items.Count > 0)
            _tabs.SelectedIndex = Math.Clamp(activeIndex, 0, _tabs.Items.Count - 1);
        OpenShells.Add(this);
        PublishGetSystemStatus();
        Loaded += (_, _) => DocumentWindowLayout.Clamp(this);
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.F1)
            {
                ShowKeyboardHelp();
                e.Handled = true;
                return;
            }
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (e.Key == Key.Tab)
            {
                if (_tabs.Items.Count > 1)
                    _tabs.SelectedIndex = PdfTabNavigation.Cycle(
                        _tabs.SelectedIndex, _tabs.Items.Count, shift ? -1 : 1);
                e.Handled = true;
            }
            else if (!shift && e.Key == Key.T)
            {
                if (_registry.Items.Count < 16) AddWorkspace(null);
                e.Handled = true;
            }
            else if (!shift && e.Key == Key.W)
            {
                if (_tabs.SelectedItem is TabItem tab && tab.Tag is Guid id)
                    await CloseTabAsync(id);
                e.Handled = true;
            }
        };
    }

    private PdfTabbedWindow(PdfViewerWindow viewer, PdfTabItem moved)
        : this([], 0, includeEmptyPlaceholder: false)
    {
        // Do not allocate an unused PDF control/scratch adapter on detach.
        AddWorkspace(moved.Path, viewer, moved, persist: false);
        SaveSessions();
    }

    private void ShowKeyboardHelp() =>
        GetMessageBox.Show(this, PdfKeyboardHelp.Text, "ГЭТ PDF — справка",
            MessageBoxButton.OK, MessageBoxImage.Information);

    private static Button IconAction(string icon, string name, string tip, Action action)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.FindResource("IconButton"),
            ToolTip = tip
        };
        Ui.SetIcon(button, (Geometry)Application.Current.FindResource(icon));
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>The "+" slot follows the last tab, so cap the strip to leave room for it.</summary>
    private void BindTabStrip()
    {
        if (_tabStrip is not null) return;
        _tabs.ApplyTemplate();
        if (_tabs.Template.FindName("PART_Strip", _tabs) is not Grid strip ||
            _tabs.Template.FindName("PART_TabStrip", _tabs) is not TabStripPanel panel) return;
        _tabStrip = panel;
        void Fit() => panel.MaxWidth = Math.Max(0, strip.ActualWidth - 56);
        strip.SizeChanged += (_, _) => Fit();
        Fit();
    }

    private static FrameworkElement TabIcon(TabItem owner)
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = (Geometry)Application.Current.FindResource("IconFileText"),
            StrokeThickness = 1.75,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,
            new System.Windows.Data.Binding(nameof(Control.Foreground)) { Source = owner });
        return new Viewbox
        {
            Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0),
            Child = new Canvas { Width = 24, Height = 24, Children = { path } }
        };
    }

    private Handle AddWorkspace(string? path, PdfViewerWindow? existing = null,
        PdfTabItem? moved = null, bool persist = true)
    {
        if (_registry.Items.Count >= 16)
        {
            GetMessageBox.Show(this, "Допускается до 16 вкладок в одном окне.",
                "ГЭТ PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            throw new InvalidOperationException("Превышен лимит вкладок PDF.");
        }
        if (moved is not null) _registry.Attach(moved);
        var item = moved ?? _registry.Open(path);
        if (_handles.TryGetValue(item.Id, out var found))
        {
            _tabs.SelectedItem = found.Tab;
            return found;
        }
        var viewer = existing ?? new PdfViewerWindow(path);
        var title = new TextBlock
        {
            Text = path is null ? "Новая вкладка" : PdfSafeDisplayName.Normalize(System.IO.Path.GetFileName(path)),
            MaxWidth = 220,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var tab = new TabItem { Tag = item.Id, Content = viewer,
            ToolTip = path, MinWidth = 110, AllowDrop = true };
        tab.DragOver += (_, e) =>
        {
            if (e.Data.GetDataPresent(typeof(PdfTabPageDrag)) &&
                e.Data.GetData(typeof(PdfTabPageDrag)) is PdfTabPageDrag drag &&
                !ReferenceEquals(drag.Source, viewer))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        };
        tab.Drop += async (_, e) =>
        {
            if (e.Data.GetDataPresent(typeof(PdfTabPageDrag)) &&
                e.Data.GetData(typeof(PdfTabPageDrag)) is PdfTabPageDrag drag &&
                !ReferenceEquals(drag.Source, viewer))
            {
                e.Handled = true;
                await InsertPageToTabAsync(drag.Source, viewer, drag.PageId);
            }
        };
        // Implicit TextBlock style pins Ink; follow the tab's selected/unselected colour instead.
        title.SetBinding(TextBlock.ForegroundProperty,
            new System.Windows.Data.Binding(nameof(Control.Foreground)) { Source = tab });
        var header = new DockPanel { LastChildFill = true };
        var close = new Button
        {
            Style = (Style)FindResource("SmallIconButton"),
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "Закрыть документ (Ctrl+W)"
        };
        Ui.SetIcon(close, (Geometry)FindResource("IconX"));
        System.Windows.Automation.AutomationProperties.SetName(close, "Закрыть документ");
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var icon = TabIcon(tab);
        DockPanel.SetDock(icon, Dock.Left);
        header.Children.Add(icon);
        header.Children.Add(title);
        close.Click += async (_, e) => { e.Handled = true; await CloseTabAsync(item.Id); };
        tab.Header = header;

        Action onReady = () =>
        {
            title.Text = viewer.DisplayDocumentName;
            tab.ToolTip = viewer.CurrentDocumentPath;
            if (viewer.CurrentDocumentPath is string opened)
            {
                try { _registry.AssignPath(item.Id, opened); }
                catch (InvalidOperationException) { /* The host deduplicates before opening. */ }
            }
            SaveSessions();
        };
        viewer.DocumentReady += onReady;
        var handle = new Handle(item.Id, viewer, tab, title, onReady);
        _handles.Add(item.Id, handle);
        _tabs.Items.Add(tab);
        _tabs.SelectedItem = tab;
        if (persist) SaveSessions();
        return handle;
    }

    internal async Task OpenPathsAsync(IEnumerable<string> paths)
    {
        foreach (string path in paths.Take(32))
        {
            if (!PdfOpenRequest.TryParse([path], out var valid) || valid is null)
            {
                GetMessageBox.Show(this, "Выберите существующий PDF-файл.",
                    "ГЭТ PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }
            var same = _registry.Items.FirstOrDefault(item =>
                string.Equals(item.Path, valid, StringComparison.OrdinalIgnoreCase));
            if (same is not null && _handles.TryGetValue(same.Id, out var previous))
            {
                _tabs.SelectedItem = previous.Tab;
                continue;
            }
            if (_tabs.SelectedItem is TabItem selected && selected.Tag is Guid id &&
                _handles.TryGetValue(id, out var current) && current.Viewer.IsEmpty)
            {
                await current.Viewer.LoadPdfAsync(valid);
                continue;
            }
            if (_registry.Items.Count >= 16)
            {
                GetMessageBox.Show(this, "Достигнут лимит: 16 открытых PDF в окне.",
                    "ГЭТ PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
            }
            AddWorkspace(valid);
        }
    }

    private void MoveSelected(int direction)
    {
        if (_tabs.SelectedItem is not TabItem tab || tab.Tag is not Guid id) return;
        int current = _tabs.Items.IndexOf(tab), destination = current + direction;
        if (destination < 0 || destination >= _tabs.Items.Count) return;
        _suppressSelection = true;
        try
        {
            _registry.Move(id, destination);
            _tabs.Items.RemoveAt(current);
            _tabs.Items.Insert(destination, tab);
            _tabs.SelectedItem = tab;
        }
        finally { _suppressSelection = false; }
        SaveSessions();
    }

    private void DetachSelected()
    {
        if (_tabs.SelectedItem is not TabItem tab || tab.Tag is not Guid id ||
            !_handles.TryGetValue(id, out var handle) || handle.Viewer.IsBusy || IsFullscreen)
            return;

        handle.Viewer.DocumentReady -= handle.OnReady;
        tab.Content = null; // Remove visual parent before attaching to another shell.
        _tabs.Items.Remove(tab);
        _handles.Remove(id);
        var moved = _registry.Detach(id);
        var other = new PdfTabbedWindow(handle.Viewer, moved);
        other.Show();
        if (_registry.Items.Count == 0) AddWorkspace(null);
        SaveSessions();
    }

    private async Task CloseTabAsync(Guid id)
    {
        if (!_handles.TryGetValue(id, out var handle) || !handle.Viewer.CanCloseDocument())
            return;
        try
        {
            await handle.Viewer.ReleaseForCloseAsync();
            handle.Viewer.DocumentReady -= handle.OnReady;
            handle.Tab.Content = null;
            _tabs.Items.Remove(handle.Tab);
            _handles.Remove(id);
            _registry.Close(id);
            if (_registry.Items.Count == 0) AddWorkspace(null);
            SaveSessions();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Failure("pdf-tab-close", ex);
            GetMessageBox.Show(this, "Не удалось корректно закрыть вкладку.",
                "ГЭТ PDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void PublishGetSystemStatus()
    {
        if (!WorkstationPolicy.Current.ExportGetSystemStatus) return;
        try
        {
            var status = PdfGetSystemStatusWriter.Create(DateTimeOffset.UtcNow,
                OpenShells.Count,
                OpenShells.Sum(shell => shell._registry.Items.Count(
                    tab => tab.Path is not null)),
                WorkstationPolicy.Current.AllowPrinting);
            PdfGetSystemStatusWriter.Write(status);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   ArgumentException or InvalidOperationException)
        { DiagnosticLog.Failure("get-system-status-export", ex); }
    }

    private static void SaveSessions()
    {
        PublishGetSystemStatus();
        if (!WorkstationPolicy.Current.RestoreTabs || OpenShells.Count == 0) return;
        try
        {
            string[] all = OpenShells.SelectMany(shell => shell._registry.Snapshot().Paths)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToArray();
            var activeShell = OpenShells.FirstOrDefault(w => w.IsActive) ??
                              OpenShells.FirstOrDefault();
            string? activePath = activeShell?._registry.Items
                .FirstOrDefault(item => item.Id == activeShell._registry.ActiveId)?.Path;
            int activeIndex = Array.FindIndex(all, path =>
                string.Equals(path, activePath, StringComparison.OrdinalIgnoreCase));
            PdfTabSessionStore.Save(new PdfTabSnapshot(all, Math.Max(0, activeIndex)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { DiagnosticLog.Failure("pdf-tabs-persist", ex); }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_shutdownApproved) { base.OnClosing(e); return; }
        e.Cancel = true;
        if (_shutdownPending) return;
        _shutdownPending = true;
        try
        {
            var tabs = _handles.Values.ToArray();
            // Ask all tabs first: a refusal leaves ALL workspaces untouched.
            if (tabs.Any(tab => !tab.Viewer.CanCloseDocument())) return;
            foreach (var tab in tabs)
                await tab.Viewer.ReleaseForCloseAsync();
            SaveSessions();
            _shutdownApproved = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Failure("pdf-tab-shutdown", ex);
            GetMessageBox.Show(this, "Не удалось закрыть все PDF. Проверьте выполняемые операции.",
                "ГЭТ PDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _shutdownPending = false; }
    }

    protected override void OnClosed(EventArgs e)
    {
        bool last = OpenShells.Count == 1;
        if (!last) OpenShells.Remove(this);
        SaveSessions();
        OpenShells.Remove(this);
        PublishGetSystemStatus();
        base.OnClosed(e);
    }
}

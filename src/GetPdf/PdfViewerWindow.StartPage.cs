using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GetPdf.Controls;

namespace GetPdf;

/// <summary>One row of the start screen: a favourite or a recently opened PDF.</summary>
internal sealed class PdfStartItem
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string Detail { get; init; }
    public bool IsFavorite { get; init; }
    public bool Exists { get; init; }
    public bool IsRecentRow { get; init; }
    public bool CanFavorite { get; init; }
    public double RowOpacity => Exists ? 1 : 0.55;
    public string StarToolTip => IsFavorite ? "Убрать из избранного" : "Добавить в избранное";
    public Visibility StarVisibility => CanFavorite ? Visibility.Visible : Visibility.Collapsed;
    // Hidden (not Collapsed) keeps the stars of both lists in one column.
    public Visibility RemoveVisibility => IsRecentRow ? Visibility.Visible : Visibility.Hidden;
    public string AccessibleName => Exists ? $"Открыть {Name}" : $"{Name}: файл не найден";

    /// <summary>"сегодня, 14:05" / "вчера, 09:12" / "12 окт., 10:00" / "12 окт. 2025".</summary>
    internal static string DescribeOpened(DateTime openedUtc, DateTime nowLocal)
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var local = openedUtc.ToLocalTime();
        if (local.Date == nowLocal.Date) return "сегодня, " + local.ToString("HH:mm", ru);
        if (local.Date == nowLocal.Date.AddDays(-1)) return "вчера, " + local.ToString("HH:mm", ru);
        return local.Year == nowLocal.Year
            ? local.ToString("d MMM, HH:mm", ru)
            : local.ToString("d MMM yyyy", ru);
    }
}

public partial class PdfViewerWindow
{
    private int _startPageVersion;
    private bool _layoutHasDocument;

    private void InitializeStartPage()
    {
        var policy = WorkstationPolicy.Current;
        RecentButton.Visibility = policy.RememberRecentDocuments || policy.AllowFavoriteDocuments
            ? Visibility.Visible : Visibility.Collapsed;
        FavoritesSection.Visibility = policy.AllowFavoriteDocuments ? Visibility.Visible : Visibility.Collapsed;
        RecentSection.Visibility = policy.RememberRecentDocuments ? Visibility.Visible : Visibility.Collapsed;
        // Tabs share the lists: refresh whenever this start screen becomes visible again.
        EmptyMessage.IsVisibleChanged += async (_, e) =>
        {
            if (e.NewValue is true) await RefreshStartPageAsync();
        };
    }

    private async Task RefreshStartPageAsync()
    {
        var policy = WorkstationPolicy.Current;
        int version = ++_startPageVersion;
        // File.Exists may stall on an offline network share: keep it off the UI thread.
        var (favorites, recent) = await Task.Run(() =>
        {
            var favoritePaths = policy.AllowFavoriteDocuments ? PdfFavoriteDocuments.Read() : Array.Empty<string>();
            var favoriteSet = new HashSet<string>(favoritePaths, StringComparer.OrdinalIgnoreCase);
            var now = DateTime.Now;
            var favoriteRows = favoritePaths.Select(path => Row(path, null, true, false)).ToArray();
            var recentRows = (policy.RememberRecentDocuments ? PdfRecentDocuments.Read() : Array.Empty<PdfRecentEntry>())
                .Select(entry => Row(entry.Path, entry.OpenedUtc, favoriteSet.Contains(entry.Path), true)).ToArray();
            return (favoriteRows, recentRows);

            PdfStartItem Row(string path, DateTime? opened, bool favorite, bool recentRow)
            {
                bool exists;
                try { exists = File.Exists(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { exists = false; }
                string folder = Path.GetDirectoryName(path) ?? "";
                string detail = !exists ? "Файл не найден · " + folder
                    : opened is { } at ? PdfStartItem.DescribeOpened(at, now) + " · " + folder
                    : folder;
                return new PdfStartItem
                {
                    Path = path,
                    Name = PdfSafeDisplayName.Normalize(Path.GetFileName(path)),
                    Detail = detail,
                    IsFavorite = favorite,
                    Exists = exists,
                    IsRecentRow = recentRow,
                    CanFavorite = policy.AllowFavoriteDocuments
                };
            }
        });
        if (version != _startPageVersion || _closing) return;
        FavoritesList.ItemsSource = favorites;
        FavoritesEmptyHint.Visibility = favorites.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentList.ItemsSource = recent;
        RecentEmptyHint.Visibility = recent.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearRecentButton.Visibility = recent.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void StartItemOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _loading || (sender as FrameworkElement)?.DataContext is not PdfStartItem item) return;
        if (!File.Exists(item.Path))
        {
            var answer = GetMessageBox.Show(HostWindow,
                $"Файл «{item.Name}» не найден. Возможно, его переместили, переименовали или сетевая папка недоступна.\n\n" +
                "Убрать его из списка?", "Документ не найден", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes) ForgetStartItem(item);
            return;
        }
        await OpenPdfPathsAsync([item.Path]);
    }

    private async void StartItemFavorite_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PdfStartItem item) return;
        ToggleFavorite(item.Path);
        await RefreshStartPageAsync();
    }

    private async void StartItemRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PdfStartItem item) return;
        try { PdfRecentDocuments.Forget(item.Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { DiagnosticLog.Failure("pdf-recent-forget", ex); }
        await RefreshStartPageAsync();
    }

    private void ForgetStartItem(PdfStartItem item)
    {
        try
        {
            if (item.IsRecentRow) PdfRecentDocuments.Forget(item.Path);
            else if (item.IsFavorite) PdfFavoriteDocuments.Toggle(item.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { DiagnosticLog.Failure("pdf-start-forget", ex); }
        _ = RefreshStartPageAsync();
    }

    private async void ClearRecent_Click(object sender, RoutedEventArgs e)
    {
        if (GetMessageBox.Show(HostWindow, "Очистить список недавних документов? Сами файлы не удаляются, избранное сохранится.",
                "Недавние документы", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { PdfRecentDocuments.Clear(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { DiagnosticLog.Failure("pdf-recent-clear", ex); }
        await RefreshStartPageAsync();
    }

    private void ToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (_originalPath is null) return;
        bool now = ToggleFavorite(_originalPath);
        UpdateFavoriteButton();
        StatusText.Text = now ? "Документ добавлен в избранное." : "Документ убран из избранного.";
    }

    /// <returns>True when the document is a favourite after the call.</returns>
    private bool ToggleFavorite(string path)
    {
        try { return PdfFavoriteDocuments.Toggle(path); }
        catch (InvalidOperationException ex)
        {
            GetMessageBox.Show(HostWindow, ex.Message, "Избранное", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Failure("pdf-favorite", ex);
            GetMessageBox.Show(HostWindow, "Не удалось сохранить избранное. Попробуйте ещё раз.", "Избранное",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        return PdfFavoriteDocuments.Contains(path);
    }

    private void UpdateFavoriteButton()
    {
        bool available = WorkstationPolicy.Current.AllowFavoriteDocuments && _originalPath is not null;
        FavoriteButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        if (!available) return;
        bool favorite = PdfFavoriteDocuments.Contains(_originalPath!);
        Ui.SetIsActive(FavoriteButton, favorite);
        string tip = favorite ? "Убрать документ из избранного" : "Добавить документ в избранное";
        FavoriteButton.ToolTip = tip;
        System.Windows.Automation.AutomationProperties.SetName(FavoriteButton, tip);
    }

    private void Recent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button owner) return;
        var policy = WorkstationPolicy.Current;
        var menu = new ContextMenu();
        void AddHeader(string text) =>
            menu.Items.Add(new MenuItem { Header = text, IsEnabled = false, FontWeight = FontWeights.SemiBold });
        void AddDocument(string path)
        {
            var item = new MenuItem { Header = PdfSafeDisplayName.Normalize(Path.GetFileName(path)), ToolTip = path };
            item.Click += async (_, _) =>
            {
                if (_closing || _loading) return;
                await OpenPdfPathsAsync([path]);
            };
            menu.Items.Add(item);
        }
        if (policy.AllowFavoriteDocuments)
        {
            AddHeader("Избранное");
            var favorites = PdfFavoriteDocuments.Read();
            foreach (string path in favorites) AddDocument(path);
            if (favorites.Count == 0) menu.Items.Add(new MenuItem { Header = "Пока пусто", IsEnabled = false });
        }
        if (policy.RememberRecentDocuments)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            AddHeader("Недавние");
            var recent = PdfRecentDocuments.Read();
            foreach (var entry in recent) AddDocument(entry.Path);
            if (recent.Count == 0) menu.Items.Add(new MenuItem { Header = "Пока пусто", IsEnabled = false });
            else
            {
                menu.Items.Add(new Separator());
                var clear = new MenuItem { Header = "Очистить историю" };
                clear.Click += (_, _) => ClearRecent_Click(clear, new RoutedEventArgs());
                menu.Items.Add(clear);
            }
        }
        owner.ContextMenu = menu;
        menu.PlacementTarget = owner;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}

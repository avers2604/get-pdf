using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace GetPdf.Setup;

public partial class SetupWindow : Window
{
    private enum Mode { Install, Upgrade, Maintenance, NewerInstalled }
    private enum Stage { Welcome, Busy, Done, Failed }

    private static readonly Version PackageVersion = typeof(App).Assembly.GetName().Version ?? new Version(0, 0);
    private static string PackageVersionText => $"{PackageVersion.Major}.{PackageVersion.Minor}.{PackageVersion.Build}";
    private const string DefaultAppsUri = "ms-settings:defaultapps?registeredAppMachine=GetPdf";

    private Mode _mode;
    private Stage _stage;
    private (string ProductCode, Version Version, string Location)? _installed;
    private MsiSession? _session;
    private string _logPath = "";
    private bool _uninstalling;

    public SetupWindow()
    {
        InitializeComponent();
        VersionText.Text = "Версия " + PackageVersionText;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (!Environment.Is64BitOperatingSystem)
        {
            ShowFailure("Нужна 64-разрядная Windows", "ГЭТ PDF работает только в 64-разрядной Windows 10 или 11.", log: false);
            return;
        }
        _installed = MsiSession.FindInstalled();
        FolderBox.Text = InstalledFolder() ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GET PDF");
        if (_installed is { } current)
        {
            string was = $"{current.Version.Major}.{current.Version.Minor}.{current.Version.Build}";
            int compare = new Version(current.Version.Major, current.Version.Minor, Math.Max(0, current.Version.Build))
                .CompareTo(new Version(PackageVersion.Major, PackageVersion.Minor, Math.Max(0, PackageVersion.Build)));
            if (compare < 0)
            {
                _mode = Mode.Upgrade;
                WelcomeTitle.Text = "Обновление ГЭТ PDF";
                WelcomeText.Text = $"Установлена версия {was}. Она будет обновлена до {PackageVersionText}. " +
                                   "История, избранное и настройки сохранятся.";
                PrimaryButton.Content = "Обновить";
                FolderSection.Visibility = Visibility.Collapsed;
            }
            else if (compare == 0)
            {
                _mode = Mode.Maintenance;
                WelcomeTitle.Text = "ГЭТ PDF уже установлен";
                WelcomeText.Text = $"Версия {was}. Можно переустановить программу (восстановить её файлы) или удалить её.";
                PrimaryButton.Content = "Переустановить";
                FolderSection.Visibility = Visibility.Collapsed;
                RemoveButton.Visibility = Visibility.Visible;
            }
            else
            {
                _mode = Mode.NewerInstalled;
                WelcomeTitle.Text = "Установлена более новая версия";
                WelcomeText.Text = $"На компьютере уже есть ГЭТ PDF {was}. Эта программа установки ({PackageVersionText}) не нужна.";
                FeatureList.Visibility = FolderSection.Visibility = DefaultAppCheck.Visibility = Visibility.Collapsed;
                PrimaryButton.Visibility = Visibility.Collapsed;
                SecondaryButton.Content = "Закрыть";
                RemoveButton.Visibility = Visibility.Visible;
            }
        }
        if (OpenPackageStream() is null)
        {
            ShowFailure("Пакет установки повреждён",
                "Внутри программы установки нет пакета ГЭТ PDF. Скачайте установщик заново.", log: false);
            return;
        }
        PrimaryButton.Focus();
    }

    /// <summary>Folder of an existing install (keeps a custom location on update).</summary>
    private static string? InstalledFolder()
    {
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\GetPdf.exe");
            return key?.GetValue(null) is string exe && exe.Length > 0 ? Path.GetDirectoryName(exe) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return null; }
    }

    private static Stream? OpenPackageStream() =>
        typeof(App).Assembly.GetManifestResourceStream("GetPdf.msi");

    private void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Папка, в которую будет установлен ГЭТ PDF",
            SelectedPath = Directory.Exists(FolderBox.Text) ? FolderBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        string chosen = dialog.SelectedPath;
        // A bare drive or an existing folder of other files: install into its own "GET PDF" subfolder.
        if (Path.GetPathRoot(chosen)?.TrimEnd('\\') == chosen.TrimEnd('\\') ||
            (Directory.Exists(chosen) && Directory.EnumerateFileSystemEntries(chosen).Any() &&
             !File.Exists(Path.Combine(chosen, "GetPdf.exe"))))
            chosen = Path.Combine(chosen, "GET PDF");
        FolderBox.Text = chosen;
    }

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        switch (_stage)
        {
            case Stage.Welcome when _mode != Mode.NewerInstalled:
                await RunAsync(uninstallOnly: false);
                break;
            case Stage.Done:
                FinishAndClose();
                break;
            case Stage.Failed:
                Close();
                break;
        }
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        if (_stage == Stage.Busy) { RequestCancel(); return; }
        Close();
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_stage != Stage.Welcome || _installed is null) return;
        if (MessageBox.Show(this, "Удалить ГЭТ PDF с этого компьютера? Ваши PDF-файлы не удаляются.",
                "Удаление ГЭТ PDF", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunAsync(uninstallOnly: true);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_stage != Stage.Busy) return;
        e.Cancel = true;
        RequestCancel();
    }

    private void RequestCancel()
    {
        if (_session is null) return;
        if (MessageBox.Show(this, "Прервать установку? Уже скопированные файлы будут удалены.", "Установка ГЭТ PDF",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        ActionText.Text = "Отменяем и возвращаем всё как было…";
        SecondaryButton.IsEnabled = false;
        _session.Cancel();
    }

    private bool GetPdfIsRunning() =>
        Process.GetProcessesByName("GetPdf").Any(p => { try { return !p.HasExited; } catch { return false; } finally { p.Dispose(); } });

    private async Task RunAsync(bool uninstallOnly)
    {
        if (GetPdfIsRunning())
        {
            AlertText.Text = "ГЭТ PDF сейчас открыт. Сохраните документы, закройте программу и нажмите кнопку ещё раз.";
            AlertBox.Visibility = Visibility.Visible;
            return;
        }
        AlertBox.Visibility = Visibility.Collapsed;
        string folder = FolderBox.Text.Trim();
        if (!uninstallOnly && (folder.Length == 0 || !Path.IsPathRooted(folder) || folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0))
        {
            AlertText.Text = "Укажите полный путь к папке установки, например C:\\Program Files\\GET PDF.";
            AlertBox.Visibility = Visibility.Visible;
            return;
        }

        _uninstalling = uninstallOnly;
        _logPath = Path.Combine(Path.GetTempPath(), $"GetPdf-Setup-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        SetStage(Stage.Busy);
        ProgressTitle.Text = uninstallOnly ? "Удаляем ГЭТ PDF" : _mode == Mode.Upgrade ? "Обновляем ГЭТ PDF" : "Устанавливаем ГЭТ PDF";
        var session = _session = new MsiSession(new WindowInteropHelper(this).Handle);
        session.Progress += (fraction, action) => Dispatcher.BeginInvoke(new Action(() =>
        {
            double value = Math.Max(InstallProgress.Value, fraction);
            InstallProgress.Value = value;
            PercentText.Text = $"{Math.Round(value * 100):0} %";
            if (!string.IsNullOrWhiteSpace(action)) ActionText.Text = action;
        }));

        uint result;
        try
        {
            result = await Task.Run(() =>
            {
                uint code = 0;
                if (_installed is { } current && (uninstallOnly || _mode == Mode.Maintenance))
                {
                    code = session.Uninstall(current.ProductCode, _logPath);
                    if (code is not (0 or 3010) || uninstallOnly)
                    {
                        if (code is 0 or 3010) TryDeletePackageCache(null);
                        return code;
                    }
                }
                string package = ExtractPackage();
                Dispatcher.BeginInvoke(new Action(() => { InstallProgress.Value = 0; ActionText.Text = "Подготовка…"; }));
                code = session.Install(package, $"INSTALLFOLDER=\"{folder.TrimEnd('\\')}\\\" REBOOT=ReallySuppress", _logPath);
                if (code is 0 or 3010) TryDeletePackageCache(keep: package);
                return code;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowFailure("Установка не завершена", "Не удалось подготовить пакет установки: " + ex.Message, log: false);
            return;
        }
        finally { _session = null; }

        switch (result)
        {
            case 0:
            case 3010:
                ShowSuccess(rebootNeeded: result == 3010);
                break;
            case 1602:
                ShowFailure(uninstallOnly ? "Удаление отменено" : "Установка отменена",
                    "Изменения отменены, компьютер в прежнем состоянии.", log: false);
                break;
            case 1618:
                ShowFailure("Идёт другая установка",
                    "Windows сейчас устанавливает другую программу. Дождитесь её завершения и запустите установку ещё раз.", log: false);
                break;
            default:
                ShowFailure(uninstallOnly ? "Удаление не завершено" : "Установка не завершена",
                    $"Windows Installer вернул код {result}. Изменения отменены. Передайте журнал установки в ИТ-службу.", log: true);
                break;
        }
    }

    /// <summary>
    /// Keeps the MSI in ProgramData: Windows Installer may need the original package for a later
    /// repair from "Apps &amp; Features" (it caches the database without the embedded cabinet).
    /// </summary>
    private static string ExtractPackage()
    {
        string folder = Path.Combine(PackageCacheRoot, PackageVersionText);
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, $"GetPdf-{PackageVersionText}-win-x64.msi");
        string stage = target + ".part";
        using (var source = OpenPackageStream() ?? throw new InvalidOperationException("пакет не найден"))
        using (var file = new FileStream(stage, FileMode.Create, FileAccess.Write, FileShare.None))
            source.CopyTo(file);
        if (File.Exists(target)) File.Delete(target);
        File.Move(stage, target);
        return target;
    }

    private static string PackageCacheRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GET-PDF", "Package");

    private static void TryDeletePackageCache(string? keep)
    {
        try
        {
            if (!Directory.Exists(PackageCacheRoot)) return;
            string? keepFolder = keep is null ? null : Path.GetDirectoryName(keep);
            foreach (string dir in Directory.GetDirectories(PackageCacheRoot))
                if (!string.Equals(dir, keepFolder, StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void SetStage(Stage stage)
    {
        _stage = stage;
        WelcomePanel.Visibility = stage == Stage.Welcome ? Visibility.Visible : Visibility.Collapsed;
        ProgressPanel.Visibility = stage == Stage.Busy ? Visibility.Visible : Visibility.Collapsed;
        DonePanel.Visibility = stage is Stage.Done or Stage.Failed ? Visibility.Visible : Visibility.Collapsed;
        RemoveButton.Visibility = stage == Stage.Welcome && _installed is not null && _mode != Mode.Install
            && _mode != Mode.Upgrade ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.IsEnabled = stage != Stage.Busy;
        SecondaryButton.IsEnabled = true;
        switch (stage)
        {
            case Stage.Busy:
                PrimaryButton.Visibility = Visibility.Collapsed;
                SecondaryButton.Content = "Отмена";
                break;
            case Stage.Done:
            case Stage.Failed:
                PrimaryButton.Visibility = Visibility.Visible;
                PrimaryButton.Content = stage == Stage.Done ? "Готово" : "Закрыть";
                SecondaryButton.Visibility = Visibility.Collapsed;
                break;
        }
        Step(Step1, true, stage == Stage.Welcome);
        Step(Step2, stage != Stage.Welcome, stage == Stage.Busy);
        Step(Step3, stage is Stage.Done or Stage.Failed, stage is Stage.Done or Stage.Failed);
    }

    private static void Step(System.Windows.Controls.TextBlock text, bool reached, bool current)
    {
        text.Opacity = reached ? 1 : 0.55;
        text.FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void ShowSuccess(bool rebootNeeded)
    {
        SetStage(Stage.Done);
        ResultBadge.Background = (Brush)FindResource("SuccessSoft");
        ResultIcon.Data = (Geometry)FindResource("IconCheck");
        ResultIcon.Stroke = new SolidColorBrush(Color.FromRgb(0x5E, 0x7A, 0x10));
        LogButton.Visibility = Visibility.Collapsed;
        if (_uninstalling)
        {
            DoneTitle.Text = "ГЭТ PDF удалён";
            DoneText.Text = "Программа удалена с компьютера. Ваши PDF-файлы остались на месте.";
            LaunchCheck.Visibility = Visibility.Collapsed;
            return;
        }
        DoneTitle.Text = _mode == Mode.Upgrade ? "ГЭТ PDF обновлён" : "ГЭТ PDF установлен";
        DoneText.Text = "Программа добавлена в меню «Пуск». " +
            (DefaultAppCheck.IsChecked == true
                ? "После нажатия «Готово» откроются параметры Windows: выберите ГЭТ PDF для файлов .pdf."
                : "PDF-файлы можно открывать через «Открыть с помощью» → ГЭТ PDF.") +
            (rebootNeeded ? " Для завершения обновления перезагрузите компьютер." : "");
    }

    private void ShowFailure(string title, string text, bool log)
    {
        SetStage(Stage.Failed);
        ResultBadge.Background = (Brush)FindResource("DangerSoft");
        ResultIcon.Data = (Geometry)FindResource("IconAlert");
        ResultIcon.Stroke = (Brush)FindResource("Red");
        DoneTitle.Text = title;
        DoneText.Text = text;
        LaunchCheck.Visibility = Visibility.Collapsed;
        LogButton.Visibility = log && File.Exists(_logPath) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e) => StartAsUser("notepad.exe", _logPath);

    private void FinishAndClose()
    {
        string exe = Path.Combine(InstalledFolder() ?? FolderBox.Text, "GetPdf.exe");
        if (DefaultAppCheck.IsChecked == true) StartAsUser("explorer.exe", DefaultAppsUri);
        if (LaunchCheck.IsChecked == true && File.Exists(exe)) StartAsUser("explorer.exe", exe);
        Close();
    }

    /// <summary>
    /// The wizard runs elevated; Explorer starts programs as the signed-in user, so ГЭТ PDF is not
    /// left running as administrator (which would break drag and drop from Explorer).
    /// </summary>
    private static void StartAsUser(string program, string argument)
    {
        try
        {
            Process.Start(new ProcessStartInfo(program, "\"" + argument + "\"") { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
    }
}

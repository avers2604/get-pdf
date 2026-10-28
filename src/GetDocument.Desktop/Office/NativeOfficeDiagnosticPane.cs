using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GetDocument.Office.EuroOfficeDesktop;

namespace GetDocument.Desktop.Office;

/// <summary>
/// Passive content for a future EuroOffice tab in the existing GET tab strip.
/// Does not launch Qt/CEF, access files or display an unverified editor.
/// It is intentionally not reachable from the normal production launcher.
/// </summary>
internal sealed class NativeOfficeDiagnosticPane : Border
{
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
        layout.Children.Add(_details);
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

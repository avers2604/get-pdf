using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GetDocument.Core.Workspace;

namespace GetDocument.Desktop.Preview;

/// <summary>New Office document draft. Saving creates an actual OOXML package, not only a tab record.</summary>
internal sealed class OfficeDraftPane : StackPanel
{
    public OfficeDraftPane(WorkspaceDocumentTab draft, Func<WorkspaceDocumentTab, Task> saveAsync)
    {
        Margin = new Thickness(32);
        Spacing = 14;
        Children.Add(new TextBlock
        {
            Text = draft.Title,
            Classes = { "title" },
            FontWeight = FontWeight.SemiBold
        });
        Children.Add(new TextBlock
        {
            Text = "Новый документ ещё не сохранён. Создайте файл для дальнейшего " +
                   "редактирования с использованием полноценного офисного движка.",
            TextWrapping = TextWrapping.Wrap
        });
        var save = new Button { Content = "Создать файл и сохранить как…" };
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            try { await saveAsync(draft); }
            finally { save.IsEnabled = true; }
        };
        Children.Add(save);
        Children.Add(new TextBlock
        {
            Text = "DOCX/XLSX/PPTX — реальные файлы. Встроенное редактирование " +
                   "с сохранением форматирования подключается отдельно.",
            TextWrapping = TextWrapping.Wrap
        });
    }
}

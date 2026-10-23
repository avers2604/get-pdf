using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using GetDocument.Office.Templates;

namespace GetDocument.Desktop.Templates;

/// <summary>
/// Templates stay in one tab, never open a new process, never send data to a service.
/// These are sample forms, not certified/approved GЭТ stationery.
/// </summary>
internal sealed class CorporateTemplatesPane : ScrollViewer
{
    private readonly Func<string, Task> _openResult;
    private readonly ComboBox _kind = new() { Width = 330, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _department = new() { Watermark = "Название подразделения" };
    private readonly TextBox _author = new() { Watermark = "ФИО и/или должность" };
    private readonly TextBox _recipient = new() { Watermark = "Кому предназначен документ" };
    private readonly TextBox _subject = new() { Watermark = "Тема документа" };
    private readonly TextBox _number = new() { Watermark = "Номер (при отсутствии будет б/н)" };
    private readonly TextBox _date = new() { Watermark = "ГГГГ-ММ-ДД" };
    private readonly TextBox _body = new()
    {
        Watermark = "Содержание",
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 120
    };
    private readonly TextBox _preview = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 200,
        MaxHeight = 360
    };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _generate = new() { Content = "Создать DOCX и открыть во вкладке" };

    public CorporateTemplatesPane(Func<string, Task> openResult)
    {
        _openResult = openResult;
        var layout = new StackPanel
        {
            Margin = new Thickness(28),
            Spacing = 12,
            MaxWidth = 900,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        layout.Children.Add(new TextBlock
        {
            Text = "Корпоративные шаблоны",
            FontSize = 27,
            FontWeight = FontWeight.SemiBold
        });
        layout.Children.Add(new TextBlock
        {
            Text = "Пилотные формы. Утверждённые реквизиты, логотип и фирменные DOCX будут " +
                   "подключены после проверки по официальному брендбуку.",
            TextWrapping = TextWrapping.Wrap
        });
        _kind.ItemsSource = CorporateTemplateService.All.Select(x => x.Name).ToArray();
        _kind.SelectedIndex = 0;
        layout.Children.Add(Field("Тип документа", _kind));
        layout.Children.Add(Field("Подразделение", _department));
        layout.Children.Add(Field("Составитель", _author));
        layout.Children.Add(Field("Адресат", _recipient));
        layout.Children.Add(Field("Тема", _subject));
        layout.Children.Add(Field("Номер", _number));
        _date.Text = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        layout.Children.Add(Field("Дата", _date));
        layout.Children.Add(Field("Основной текст", _body));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var render = new Button { Content = "Предпросмотр" };
        render.Click += (_, _) => Preview();
        actions.Children.Add(render);
        _generate.Click += async (_, _) => await GenerateAsync();
        actions.Children.Add(_generate);
        layout.Children.Add(actions);
        layout.Children.Add(_status);
        layout.Children.Add(new TextBlock { Text = "Предпросмотр текста документа", FontWeight = FontWeight.SemiBold });
        layout.Children.Add(_preview);
        Content = layout;
        _status.Text = "Данные обрабатываются локально и не отправляются на сервер.";
    }

    private static Control Field(string title, Control control)
    {
        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
                control
            }
        };
    }

    private CorporateTemplateData Data()
    {
        if (!DateOnly.TryParseExact(_date.Text, "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new ArgumentException("Введите дату в формате ГГГГ-ММ-ДД.");
        return new CorporateTemplateData(
            _department.Text ?? "", _author.Text ?? "", _recipient.Text ?? "",
            _subject.Text ?? "", _body.Text ?? "", _number.Text ?? "", date);
    }

    private CorporateTemplateId SelectedTemplate()
    {
        int index = _kind.SelectedIndex;
        if (index < 0 || index >= CorporateTemplateService.All.Count)
            throw new ArgumentException("Выберите тип документа.");
        return CorporateTemplateService.All[index].Id;
    }

    private void Preview()
    {
        try
        {
            _preview.Text = CorporateTemplateService.Preview(SelectedTemplate(), Data());
            _status.Text = "Предпросмотр сформирован локально. Документ ещё не сохранён.";
        }
        catch (Exception ex)
        {
            _preview.Text = "";
            _status.Text = "Проверьте поля: " + ex.Message;
        }
    }

    private async Task GenerateAsync()
    {
        CorporateTemplateId kind;
        CorporateTemplateData data;
        try
        {
            kind = SelectedTemplate();
            data = Data();
            // Fail on invalid input BEFORE showing a file picker.
            _preview.Text = CorporateTemplateService.Preview(kind, data);
        }
        catch (Exception ex)
        {
            _status.Text = "Нельзя создать документ: " + ex.Message;
            return;
        }

        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider is null) { _status.Text = "Системный диалог сохранения недоступен."; return; }
        IStorageFile? selected;
        try
        {
            selected = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Создать документ по шаблону",
                SuggestedFileName = CorporateTemplateService.All.First(x => x.Id == kind).Name + ".docx",
                DefaultExtension = "docx",
                ShowOverwritePrompt = false,
                FileTypeChoices = [new FilePickerFileType("Word DOCX") { Patterns = ["*.docx"] }]
            });
        }
        catch (Exception ex)
        {
            _status.Text = "Ошибка выбора файла: " + ex.Message;
            return;
        }
        if (selected is null) return;
        if (!selected.Path.IsFile)
        {
            _status.Text = "Выберите обычную папку или разрешённый сетевой каталог.";
            return;
        }

        _generate.IsEnabled = false;
        try
        {
            string target = selected.Path.LocalPath;
            await Task.Run(() => CorporateTemplateService.CreateDocx(kind, data, target));
            _status.Text = "Создана новая редакция DOCX. Исходные документы не изменены.";
            await _openResult(target);
        }
        catch (Exception ex)
        {
            _status.Text = "Создать документ не удалось: " + ex.Message;
        }
        finally { _generate.IsEnabled = true; }
    }
}

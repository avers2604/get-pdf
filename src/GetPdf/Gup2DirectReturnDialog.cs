using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;

namespace GetPdf;

/// <summary>Ephemeral code and local PDF picker; values are not persisted.</summary>
internal sealed class Gup2DirectReturnDialog : Window
{
    internal string OneTimeCode { get; private set; } = "";
    internal string PdfPath { get; private set; } = "";

    internal Gup2DirectReturnDialog()
    {
        Title = "ГЭТ PDF — защищённый возврат копии в GUP-2";
        Width = 650;
        Height = 355;
        MinWidth = 530;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(18) };
        Content = panel;
        panel.Children.Add(new TextBlock
        {
            Text = "Откройте карточку черновика в GUP-2, получите одноразовый код " +
                   "и вставьте его сюда. Код действует 5 минут, используется один раз. " +
                   "Нельзя возвращать исходный PDF без изменений.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });
        panel.Children.Add(new TextBlock { Text = "Одноразовый код возврата (43 символа)" });
        var secret = new PasswordBox
        {
            MaxLength = 43,
            Height = 34,
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 5, 0, 14)
        };
        System.Windows.Automation.AutomationProperties.SetName(
            secret, "Код возврата GUP-2 из карточки черновика");
        panel.Children.Add(secret);

        panel.Children.Add(new TextBlock { Text = "Подготовленная PDF-копия" });
        var path = new TextBox
        {
            IsReadOnly = true, Height = 34,
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 5, 0, 8)
        };
        panel.Children.Add(path);
        var choose = new Button
        {
            Content = "Выбрать PDF-копию…",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 6, 12, 6)
        };
        choose.Click += (_, _) =>
        {
            var picker = new OpenFileDialog
            {
                Title = "Выберите отредактированную PDF-копию, не оригинал",
                Filter = "PDF (*.pdf)|*.pdf",
                Multiselect = false,
                CheckFileExists = true
            };
            if (picker.ShowDialog(this) == true) path.Text = picker.FileName;
        };
        panel.Children.Add(choose);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        var send = new Button
        {
            Content = "Отправить через HTTPS",
            MinWidth = 170,
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 8, 0)
        };
        var cancel = new Button
        {
            Content = "Отмена", IsCancel = true,
            Padding = new Thickness(10, 6, 10, 6)
        };
        actions.Children.Add(send);
        actions.Children.Add(cancel);
        panel.Children.Add(actions);
        send.Click += (_, _) =>
        {
            if (!Gup2DesktopReturnClient.IsValidCode(secret.Password) ||
                string.IsNullOrWhiteSpace(path.Text) || !File.Exists(path.Text))
            {
                MessageBox.Show(this,
                    "Укажите действительный 43-символьный код и выберите отдельную PDF-копию.",
                    "ГЭТ PDF ↔ GUP-2", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            OneTimeCode = secret.Password;
            PdfPath = path.Text;
            DialogResult = true;
        };
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GetPdf;

/// <summary>
/// Modal preview of the exact paginator output, including stamps and markup.
/// Loads only the selected page; does not dispatch any printer job.
/// </summary>
internal sealed class PdfPrintPreviewWindow : Window
{
    private readonly PdfPagePrintPaginator _paginator;
    private readonly Rectangle _page;
    private readonly TextBlock _status;
    private readonly Button _previous;
    private readonly Button _next;
    private int _index;

    public PdfPrintPreviewWindow(PdfPagePrintPaginator paginator)
    {
        _paginator = paginator ?? throw new ArgumentNullException(nameof(paginator));
        if (paginator.PageCount < 1)
            throw new ArgumentException("Нет страниц для печати.", nameof(paginator));
        Title = "ГЭТ PDF — предварительный просмотр печати";
        Width = 940; Height = 780; MinWidth = 650; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel { LastChildFill = true };
        var toolbar = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);
        var actions = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(actions, Dock.Right);
        toolbar.Children.Add(actions);
        _previous = new Button { Content = "←", MinWidth = 42, Margin = new Thickness(2) };
        _next = new Button { Content = "→", MinWidth = 42, Margin = new Thickness(2) };
        var print = new Button { Content = "Отправить на печать", MinWidth = 170,
            Margin = new Thickness(12, 2, 2, 2) };
        var cancel = new Button { Content = "Отмена", MinWidth = 96, Margin = new Thickness(2) };
        actions.Children.Add(_previous); actions.Children.Add(_next);
        actions.Children.Add(print); actions.Children.Add(cancel);
        _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap };
        toolbar.Children.Add(_status);

        var scroll = new ScrollViewer { Background = Brushes.Gainsboro,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(scroll);
        var frame = new Border { Background = Brushes.White,
            BorderBrush = Brushes.DimGray, BorderThickness = new Thickness(1),
            Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center };
        var box = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
            MaxWidth = 760, MaxHeight = 650 };
        _page = new Rectangle { Width = paginator.PageSize.Width,
            Height = paginator.PageSize.Height, Fill = Brushes.White };
        box.Child = _page; frame.Child = box; scroll.Content = frame;
        Content = root;

        _previous.Click += (_, _) => { if (_index > 0) { _index--; ShowPage(); } };
        _next.Click += (_, _) => { if (_index + 1 < _paginator.PageCount) { _index++; ShowPage(); } };
        print.Click += (_, _) => { DialogResult = true; Close(); };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) { DialogResult = false; Close(); e.Handled = true; }
        };
        ShowPage();
    }

    private void ShowPage()
    {
        // PdfPagePrintPaginator lazily decodes only this staged raster source;
        // VisualBrush also includes all print overlays from the paginator.
        var page = _paginator.GetPage(_index);
        _page.Fill = new VisualBrush(page.Visual) { Stretch = Stretch.Fill };
        _status.Text = $"Страница {_index + 1} из {_paginator.PageCount}. " +
            "Предпросмотр; печать начнётся только после подтверждения.";
        _previous.IsEnabled = _index > 0;
        _next.IsEnabled = _index + 1 < _paginator.PageCount;
    }
}

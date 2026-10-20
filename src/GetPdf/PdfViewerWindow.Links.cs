using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace GetPdf;

public partial class PdfViewerWindow
{
    private IReadOnlyDictionary<int, IReadOnlyList<PdfInternalLinkReader.Link>>
        _pdfInternalLinks = new Dictionary<int, IReadOnlyList<PdfInternalLinkReader.Link>>();

    private void InternalLinks_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button owner || _plan is null ||
            _pageIndex < 0 || _pageIndex >= _plan.Pages.Count) return;
        int sourcePage = _plan.Pages[_pageIndex].SourcePage;
        var menu = new ContextMenu();
        if (_pdfInternalLinks.TryGetValue(sourcePage, out var links))
        {
            foreach (var link in links)
            {
                int originalTarget = link.SourceTargetPage;
                var item = new MenuItem { Header = link.Label };
                item.Click += (_, _) =>
                {
                    if (_loading || _closing || _plan is null) return;
                    int visible = PdfOutlineReader.VisibleIndex(_plan.Pages, originalTarget);
                    if (visible < 0)
                    {
                        StatusText.Text = "Страница назначения удалена или недоступна.";
                        return;
                    }
                    _pageIndex = visible;
                    RefreshList();
                };
                menu.Items.Add(item);
            }
        }
        if (menu.Items.Count == 0)
            menu.Items.Add(new MenuItem { Header = "Нет доступных внутренних ссылок",
                IsEnabled = false });
        owner.ContextMenu = menu;
        menu.PlacementTarget = owner;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}

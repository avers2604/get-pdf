using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace GetPdf;

public partial class PdfViewerWindow
{
    /// <summary>
    /// GET DS 4.1 toolbar: fit and page-layout choices share one "Вид" dropdown.
    /// Layout still goes through <see cref="LayoutModeBox"/> so preferences and
    /// <see cref="LayoutMode_SelectionChanged"/> keep a single code path.
    /// </summary>
    private void ViewMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button owner) return;
        var menu = new ContextMenu();
        var fitWidth = new MenuItem { Header = "По ширине страницы" };
        fitWidth.Click += FitWidth_Click;
        var fitPage = new MenuItem { Header = "Страница целиком" };
        fitPage.Click += FitPage_Click;
        menu.Items.Add(fitWidth);
        menu.Items.Add(fitPage);
        menu.Items.Add(new Separator());
        for (int i = 0; i < LayoutModeBox.Items.Count; i++)
        {
            int mode = i;
            var item = new MenuItem
            {
                Header = (LayoutModeBox.Items[i] as ComboBoxItem)?.Content,
                IsChecked = LayoutModeBox.SelectedIndex == mode
            };
            item.Click += (_, _) => LayoutModeBox.SelectedIndex = mode;
            menu.Items.Add(item);
        }
        owner.ContextMenu = menu;
        menu.PlacementTarget = owner;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}

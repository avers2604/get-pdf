using System.Windows;
using System.Windows.Media;

namespace GetPdf.Controls;

/// <summary>
/// GET DS 4.1 presentation hints read by App.xaml templates. Kept separate from
/// Content so code that rewrites a button caption never drops its icon.
/// </summary>
public static class Ui
{
    /// <summary>24×24 outline icon (stroke 1.75) from the GET DS functional set.</summary>
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(Geometry), typeof(Ui), new FrameworkPropertyMetadata(null));

    /// <summary>Toggle-like buttons show the DS "selected" state without becoming ToggleButtons.</summary>
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false));

    /// <summary>Hint text rendered by the TextBox template while the field is empty.</summary>
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null));

    public static Geometry? GetIcon(DependencyObject element) => (Geometry?)element.GetValue(IconProperty);
    public static void SetIcon(DependencyObject element, Geometry? value) => element.SetValue(IconProperty, value);
    public static bool GetIsActive(DependencyObject element) => (bool)element.GetValue(IsActiveProperty);
    public static void SetIsActive(DependencyObject element, bool value) => element.SetValue(IsActiveProperty, value);
    public static string? GetPlaceholder(DependencyObject element) => (string?)element.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject element, string? value) => element.SetValue(PlaceholderProperty, value);
}

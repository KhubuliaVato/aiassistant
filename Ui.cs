using System.Windows;

namespace AiAssistant
{
    /// <summary>
    /// Attached helpers for the shared styles.
    /// Ui.Hint = placeholder text shown inside an empty TextBox (see the TextBox style in Themes/Controls.xaml).
    /// </summary>
    public static class Ui
    {
        public static readonly DependencyProperty HintProperty =
            DependencyProperty.RegisterAttached("Hint", typeof(string), typeof(Ui), new PropertyMetadata(""));

        /// <summary>Colour of a sidebar icon tile (brush).</summary>
        public static readonly DependencyProperty IconColorProperty =
            DependencyProperty.RegisterAttached("IconColor", typeof(System.Windows.Media.Brush), typeof(Ui), new PropertyMetadata(null));

        public static System.Windows.Media.Brush? GetIconColor(DependencyObject d) => (System.Windows.Media.Brush?)d.GetValue(IconColorProperty);
        public static void SetIconColor(DependencyObject d, System.Windows.Media.Brush? value) => d.SetValue(IconColorProperty, value);

        /// <summary>Vector icon (Geometry) shown by the sidebar nav item.</summary>
        public static readonly DependencyProperty IconProperty =
            DependencyProperty.RegisterAttached("Icon", typeof(System.Windows.Media.Geometry), typeof(Ui), new PropertyMetadata(null));

        public static System.Windows.Media.Geometry? GetIcon(DependencyObject d) => (System.Windows.Media.Geometry?)d.GetValue(IconProperty);
        public static void SetIcon(DependencyObject d, System.Windows.Media.Geometry? value) => d.SetValue(IconProperty, value);

        public static string GetHint(DependencyObject d) => (string)d.GetValue(HintProperty);
        public static void SetHint(DependencyObject d, string value) => d.SetValue(HintProperty, value);
    }
}

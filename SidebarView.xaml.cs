using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace AiAssistant
{
    public partial class SidebarView : UserControl
    {
        public SidebarView()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                if (Window.GetWindow(this) is MainWindow w && w.MainTabControl != null)
                    // Keep the highlighted item in sync when the app switches tabs itself
                    // (e.g. "send to editor"), not only when the user clicks the sidebar.
                    w.MainTabControl.SelectionChanged += (_, e) =>
                    {
                        if (e.OriginalSource == w.MainTabControl) SyncSelection(w.MainTabControl.SelectedIndex);
                    };
                UpdateThemeButton();
            };
            ThemeManager.ThemeChanged += _ => UpdateThemeButton();
        }

        private void Nav_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb &&
                int.TryParse(rb.CommandParameter?.ToString(), out int index) &&
                Window.GetWindow(this) is MainWindow w && w.MainTabControl != null)
                w.MainTabControl.SelectedIndex = index;
        }

        private void SyncSelection(int tabIndex)
        {
            foreach (var rb in FindRadios(this))
                if (rb.CommandParameter?.ToString() == tabIndex.ToString())
                    rb.IsChecked = true;
        }

        private static System.Collections.Generic.IEnumerable<RadioButton> FindRadios(DependencyObject root)
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is RadioButton rb) yield return rb;
                foreach (var nested in FindRadios(child)) yield return nested;
            }
        }

        private void BtnTheme_Click(object sender, RoutedEventArgs e) => ThemeManager.Toggle();

        private void UpdateThemeButton()
        {
            bool dark = ThemeManager.Current == ThemeManager.Dark;
            ThemeIcon.Data  = (System.Windows.Media.Geometry)FindResource(dark ? "Icon.Sun" : "Icon.Moon");   // sun → switch to light, moon → switch to dark
            ThemeLabel.Text = dark ? "ღია თემა" : "მუქი თემა";
        }
    }
}

using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AiAssistant
{
    // Brand Kit tab: live 9:16 preview + preset chips (theme-aware)
    public partial class MainWindow
    {
        private string _pvWmPath = "";
        private BitmapImage? _pvWmImg;
        private string _pvOvPath = "";
        private BitmapImage? _pvOvImg;

        private static BitmapImage? LoadPreviewImage(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;       // don't keep the file locked
                bmp.DecodePixelWidth = 720;
                bmp.EndInit(); bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        private static SolidColorBrush Hex(string hex)
            => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

        private void BrandTab_Changed(object sender, RoutedEventArgs e)
        {
            if (BkLtEditor == null) return;
            BkLtEditor.Visibility = RbBkLt.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            BkWmEditor.Visibility = RbBkWm.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            BkIoEditor.Visibility = RbBkIo.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            UpdateBrandPreview();
        }

        private void BrandPreview_Changed(object sender, RoutedEventArgs e) => UpdateBrandPreview();

        private void BrandPreview_TextChanged(object sender, TextChangedEventArgs e) => UpdateBrandPreview();

        private void UpdateBrandPreview()
        {
            // called from many handlers, some of which fire while the window is still being built
            if (!IsLoaded || PvFrame == null || TmplDark == null || TmplMinimal == null || WmBR == null
                || SlWmSize == null || SlWmOpacity == null || TxtWmPath == null || CmbStingerTemplate == null
                || RbBkIo == null || TxtOverlayPath == null || TxtLTName == null) return;

            // ── lower third ─────────────────────────────────────────
            string tmpl = GetSelectedTemplate();
            bool custom = tmpl == "custom";
            (string bg, string bar) = tmpl switch
            {
                "dark"    => ("#DC0F163A", "#1464E6"),
                "red"     => ("#E0C0121C", "#FFFFFF"),
                "minimal" => ("#66000000", "#DCAF1E"),
                _         => ("#E61C1F26", "#FFFFFF")
            };
            PvLt.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;
            PvLt.Background = Hex(bg);
            PvLtBar.Fill = Hex(bar);

            string name = TxtLTName.Text.Trim();
            string desc = TxtLTDesc.Text.Trim();
            PvLtName.Text = name.Length > 0 ? name : "სახელი გვარი";
            PvLtName.Opacity = name.Length > 0 ? 1.0 : 0.55;
            PvLtDesc.Text = desc.Length > 0 ? desc : "თანამდებობა / აღწერა";
            PvLtDesc.Opacity = desc.Length > 0 ? 1.0 : 0.55;

            // ── custom full-frame overlay ───────────────────────────
            string ovPath = TxtOverlayPath.Text.Trim();
            if (ovPath != _pvOvPath) { _pvOvPath = ovPath; _pvOvImg = LoadPreviewImage(ovPath); }
            PvOverlay.Source = _pvOvImg;
            PvOverlay.Visibility = custom && _pvOvImg != null ? Visibility.Visible : Visibility.Collapsed;

            // ── watermark ───────────────────────────────────────────
            string wmPath = TxtWmPath.Text.Trim();
            if (wmPath != _pvWmPath) { _pvWmPath = wmPath; _pvWmImg = LoadPreviewImage(wmPath); }
            PvWm.Source = _pvWmImg;
            PvWm.Visibility = _pvWmImg != null ? Visibility.Visible : Visibility.Collapsed;
            PvWmEmpty.Visibility = _pvWmImg == null ? Visibility.Visible : Visibility.Collapsed;

            double w = Math.Max(20, PvFrame.Width * SlWmSize.Value / 100.0);
            PvWm.Width = w;
            PvWmEmpty.Width = w;
            PvWmEmpty.Height = w * 0.62;
            double op = SlWmOpacity.Value / 100.0;
            PvWm.Opacity = op;
            PvWmEmpty.Opacity = op;

            string pos = GetWmPosition();
            var ha = pos.EndsWith("left") ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            var va = pos.StartsWith("top") ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            PvWm.HorizontalAlignment = PvWmEmpty.HorizontalAlignment = ha;
            PvWm.VerticalAlignment = PvWmEmpty.VerticalAlignment = va;

            // ── intro / outro card (only on its own tab) ────────────
            bool io = RbBkIo.IsChecked == true;
            PvStinger.Visibility = io ? Visibility.Visible : Visibility.Collapsed;
            if (io)
            {
                int idx = CmbStingerTemplate.SelectedIndex;
                PvStingerBg.Background = Hex(idx switch { 0 => "#E5202E", 1 => "#000000", 2 => "#1C1F26", _ => "#151A24" });
                PvStLogo.Source = _pvWmImg;
                PvStLogo.Visibility = _pvWmImg != null ? Visibility.Visible : Visibility.Collapsed;
                PvStText.Visibility = _pvWmImg == null ? Visibility.Visible : Visibility.Collapsed;

                bool intro = ChkApplyIntro.IsChecked == true, outro = ChkApplyOutro.IsChecked == true;
                string where = intro && outro ? "INTRO · OUTRO" : intro ? "INTRO" : outro ? "OUTRO" : "—";
                PvStCaption.Text = idx == 3
                    ? "საკუთარი ფაილი · " + where
                    : $"{where} · {SlStingerDuration.Value:0.0} წმ";
            }
        }

        // ── preset chip (theme-aware, with a small delete button) ──
        private UIElement BuildBrandChip(string label, Action onApply, Action onDelete)
        {
            var chip = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 6, 6, 6),
                Margin = new Thickness(0, 0, 8, 8),
                Cursor = Cursors.Hand,
                ToolTip = "დააჭირე გამოსაყენებლად"
            };
            chip.SetResourceReference(Border.BackgroundProperty, "BgCard");
            chip.SetResourceReference(Border.BorderBrushProperty, "Border");

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var text = new TextBlock { Text = label, FontSize = 12.5, FontWeight = FontWeights.Medium,
                                       VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            row.Children.Add(text);

            var del = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11),
                                   Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand,
                                   Background = Brushes.Transparent, ToolTip = "პრეზეტის წაშლა" };
            var x = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 9,
                                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            x.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            del.Child = x;
            del.MouseEnter += (_, _) => { del.SetResourceReference(Border.BackgroundProperty, "DangerSoft"); x.SetResourceReference(TextBlock.ForegroundProperty, "Danger"); };
            del.MouseLeave += (_, _) => { del.Background = Brushes.Transparent; x.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted"); };
            del.MouseLeftButtonDown += (_, ev) => { ev.Handled = true; onDelete(); };
            row.Children.Add(del);

            chip.Child = row;
            chip.MouseEnter += (_, _) =>
            {
                chip.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
                chip.SetResourceReference(Border.BorderBrushProperty, "AccentBorder");
            };
            chip.MouseLeave += (_, _) =>
            {
                chip.SetResourceReference(Border.BackgroundProperty, "BgCard");
                chip.SetResourceReference(Border.BorderBrushProperty, "Border");
            };
            chip.MouseLeftButtonDown += (_, _) => onApply();
            return chip;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AiAssistant
{
    /// <summary>
    /// First-run (and "something is missing") screen: shows every missing component with its own
    /// progress bar while ToolManager downloads them one by one.
    /// </summary>
    public partial class SetupWindow : Window
    {
        private sealed class Row
        {
            public ToolItem Tool = null!;
            public Border Card = null!;
            public TextBlock Status = null!;
            public ProgressBar Bar = null!;
            public Border Dot = null!;
            public TextBlock DotGlyph = null!;
        }

        private readonly List<ToolItem> _items;
        private readonly Dictionary<string, Row> _rows = new();
        private CancellationTokenSource? _cts;
        private bool _running;

        public SetupWindow(List<ToolItem> items)
        {
            InitializeComponent();
            _items = items;
            BuildRows();
            Loaded += async (_, _) => await RunAsync();
            Closing += (_, _) => { try { _cts?.Cancel(); } catch { } };
        }

        private void Header_Drag(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } }
        }

        private void BuildRows()
        {
            ItemsPanel.Children.Clear();
            _rows.Clear();
            foreach (var t in _items)
            {
                var card = new Border { Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 10),
                                        CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1) };
                card.SetResourceReference(Border.BackgroundProperty, "BgCard");
                card.SetResourceReference(Border.BorderBrushProperty, "Border");

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var tile = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(12),
                                        Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top };
                tile.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
                var glyph = new TextBlock { Text = t.Glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 17,
                                            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                glyph.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
                tile.Child = glyph;
                grid.Children.Add(tile);

                var col = new StackPanel();
                var head = new Grid();
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var title = new TextBlock { Text = t.Title, FontSize = 14, FontWeight = FontWeights.SemiBold };
                title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
                head.Children.Add(title);
                var size = new TextBlock { Text = t.Size, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
                size.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
                Grid.SetColumn(size, 1);
                head.Children.Add(size);
                col.Children.Add(head);

                var desc = new TextBlock { Text = t.Description, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                                           Margin = new Thickness(0, 2, 0, 0) };
                desc.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
                col.Children.Add(desc);

                var bar = new ProgressBar { Height = 5, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 10, 0, 0), Value = 0 };
                col.Children.Add(bar);

                var status = new TextBlock { Text = "ელოდება რიგს", FontSize = 11.5, Margin = new Thickness(0, 6, 0, 0),
                                             TextTrimming = TextTrimming.CharacterEllipsis };
                status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                col.Children.Add(status);

                Grid.SetColumn(col, 1);
                grid.Children.Add(col);
                card.Child = grid;
                ItemsPanel.Children.Add(card);

                _rows[t.Id] = new Row { Tool = t, Card = card, Status = status, Bar = bar, Dot = tile, DotGlyph = glyph };
            }
        }

        private async Task RunAsync()
        {
            if (_running) return;
            _running = true;
            BtnRetry.Visibility = Visibility.Collapsed;
            BtnSkip.Content = "გამოტოვება";
            TxtTitle.Text = "ვამზადებ პროგრამას პირველი გამოყენებისთვის";
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            int failed = 0;
            foreach (var t in _items)
            {
                var row = _rows[t.Id];
                // an earlier retry may already have fixed it
                try { if (await t.IsInstalledAsync()) { MarkDone(row, "უკვე დაყენებულია"); continue; } } catch { }

                row.Status.Text = "მიმდინარეობს…";
                row.Bar.IsIndeterminate = true;
                var prog = new Progress<ToolProgress>(p =>
                {
                    row.Bar.IsIndeterminate = p.Percent < 0;
                    if (p.Percent >= 0) row.Bar.Value = p.Percent;
                    if (!string.IsNullOrEmpty(p.Text)) row.Status.Text = p.Text;
                });

                try
                {
                    var (ok, msg) = await t.InstallAsync(prog, ct);
                    if (ok) MarkDone(row, msg);
                    else { failed++; MarkFailed(row, msg); }
                }
                catch (OperationCanceledException)
                {
                    MarkFailed(row, "შეწყდა");
                    failed++;
                    break;
                }
                catch (Exception ex)
                {
                    failed++;
                    MarkFailed(row, ex.Message);
                }
            }

            _running = false;
            if (ct.IsCancellationRequested) { return; }

            if (failed == 0)
            {
                TxtTitle.Text = "ყველაფერი მზადაა ✓";
                TxtFooter.Text = "ვხსნი პროგრამას…";
                BtnSkip.IsEnabled = false;
                await Task.Delay(900);
                DialogResult = true;
            }
            else
            {
                TxtTitle.Text = "ზოგი კომპონენტი ვერ ჩამოიტვირთა";
                TxtFooter.Text = "შეამოწმე ინტერნეტი და სცადე თავიდან, ან გააგრძელე — ეს ფუნქციები მერე სეთინგებიდან დაყენდება.";
                BtnRetry.Visibility = Visibility.Visible;
                BtnSkip.Content = "გაგრძელება";
            }
        }

        private static void MarkDone(Row r, string msg)
        {
            r.Bar.IsIndeterminate = false;
            r.Bar.Value = 100;
            r.Status.Text = msg;
            r.Status.SetResourceReference(TextBlock.ForegroundProperty, "Success");
            r.DotGlyph.Text = "";
            r.DotGlyph.SetResourceReference(TextBlock.ForegroundProperty, "Success");
            r.Dot.SetResourceReference(Border.BackgroundProperty, "SuccessSoft");
        }

        private static void MarkFailed(Row r, string msg)
        {
            r.Bar.IsIndeterminate = false;
            r.Status.Text = msg;
            r.Status.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
            r.DotGlyph.Text = "";
            r.DotGlyph.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
            r.Dot.SetResourceReference(Border.BackgroundProperty, "DangerSoft");
        }

        private async void BtnRetry_Click(object sender, RoutedEventArgs e) => await RunAsync();

        private void BtnSkip_Click(object sender, RoutedEventArgs e)
        {
            try { _cts?.Cancel(); } catch { }
            DialogResult = false;
        }
    }
}

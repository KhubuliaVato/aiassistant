using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AiAssistant
{
    public partial class MediaLibraryView : UserControl
    {
        // ── wiring (set by MainWindow) ────────────────────────────────
        public event Action<string>? OpenInEditor;
        public event Action<string>? OpenInCrop;
        public event Action<string>? OpenInFaceBlur;
        public event Action<string>? OpenInVoiceOver;
        public event Action<string, bool>? OpenInAB;      // (path, asA)
        public event Action<string>? FileDeleted;
        public Func<List<MediaLibraryItem>>? Provider;     // exported / downloaded items the app already knows
        public Func<IEnumerable<(string Folder, string Source)>>? FoldersProvider;   // folders the app writes into
        public string CacheFolder { get; set; } = "";

        private static readonly string[] VideoExt = { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".mxf", ".m4v", ".ts" };

        private class Entry
        {
            public string Path = "";
            public string Title = "";
            public string Source = "";
            public DateTime When;
            public long Size;
            public long Stamp;                  // mtime ticks (cache key)
            public double Dur;
            public int W, H;
            public double Fps;
            public string Codec = "";
            public bool MetaTried;
            public BitmapImage? Thumb;
            // UI
            public Border? Card;
            public Image? Img;
            public TextBlock? MetaText;
            public TextBlock? DurText;
            public Border? DurBadge;

            public string Category => Source switch
            {
                "დამუშავებული" or "AI ფონი" or "სახის დაბუნდოვნება" or "ხმის ჩაწერა" => "processed",
                "ჩამოტვირთული" => "download",
                "ლაივ კლიპი" => "live",
                _ => "other"
            };
        }

        private List<Entry> _all = new();
        private readonly Dictionary<string, Entry> _byPath = new(StringComparer.OrdinalIgnoreCase);
        private Entry? _selected;
        private string _filter = "all";
        private string _sort = "new";
        private bool _refreshing, _again;
        private readonly SemaphoreSlim _metaGate = new(3);
        private readonly List<string> _userFolders = new();
        private readonly DispatcherTimer _playTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private bool _playing, _mediaReady, _seekFromTimer;

        private static string DataDir => AppPaths.DataDir;
        private static string FoldersFile => Path.Combine(DataDir, "library_folders.json");

        public MediaLibraryView()
        {
            InitializeComponent();
            LoadUserFolders();
            _playTimer.Tick += PlayTimer_Tick;
            DMedia.MouseLeftButtonDown += (_, _) => { if (_mediaReady) TogglePlay(); };
            // pause when the tab is left; refresh the list when it is shown again
            IsVisibleChanged += (_, _) =>
            {
                if (!IsVisible) StopPreview(); else Refresh();
            };
        }

        // ════════════════════════════════════════════════════════════
        // COLLECT
        // ════════════════════════════════════════════════════════════
        public async void Refresh()
        {
            if (_refreshing) { _again = true; return; }
            _refreshing = true;
            try
            {
                var provided = Provider?.Invoke() ?? new List<MediaLibraryItem>();
                var folders = (FoldersProvider?.Invoke() ?? Enumerable.Empty<(string, string)>())
                    .Concat(_userFolders.Select(f => (f, "საქაღალდე"))).ToList();

                var found = await Task.Run(() => Collect(provided, folders));
                _all = found;
                Rebuild();
            }
            catch { }
            finally
            {
                _refreshing = false;
                if (_again) { _again = false; Refresh(); }
            }
        }

        private List<Entry> Collect(List<MediaLibraryItem> provided, List<(string Folder, string Source)> folders)
        {
            var map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

            void Add(string path, string title, string source, DateTime? when)
            {
                try
                {
                    if (map.ContainsKey(path) || !File.Exists(path)) return;
                    var fi = new FileInfo(path);
                    if (!_byPath.TryGetValue(path, out var e) || e.Size != fi.Length || e.Stamp != fi.LastWriteTimeUtc.Ticks)
                        e = new Entry { Path = path, Size = fi.Length, Stamp = fi.LastWriteTimeUtc.Ticks };
                    e.Title = string.IsNullOrWhiteSpace(title) ? System.IO.Path.GetFileNameWithoutExtension(path) : title;
                    e.Source = source;
                    e.When = when ?? fi.LastWriteTime;
                    map[path] = e;
                }
                catch { }
            }

            foreach (var p in provided) Add(p.FilePath, TitleFromFile(p.FilePath, p.Title), p.Source, p.When);

            foreach (var (folder, source) in folders)
            {
                try
                {
                    if (!Directory.Exists(folder)) continue;
                    foreach (var f in Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly))
                    {
                        string ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                        if (!VideoExt.Contains(ext)) continue;
                        Add(f, "", source, null);
                    }
                }
                catch { }
            }

            lock (_byPath)
            {
                _byPath.Clear();
                foreach (var kv in map) _byPath[kv.Key] = kv.Value;
            }
            return map.Values.ToList();
        }

        private static string TitleFromFile(string path, string title)
            => !string.IsNullOrWhiteSpace(title) ? title : System.IO.Path.GetFileNameWithoutExtension(path);

        // ════════════════════════════════════════════════════════════
        // GRID
        // ════════════════════════════════════════════════════════════
        private void Rebuild()
        {
            string q = (TxtSearch.Text ?? "").Trim();
            IEnumerable<Entry> items = _all;
            if (_filter != "all") items = items.Where(i => i.Category == _filter);
            if (q.Length > 0)
                items = items.Where(i => i.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                                         || System.IO.Path.GetFileName(i.Path).Contains(q, StringComparison.OrdinalIgnoreCase));

            items = _sort switch
            {
                "old"  => items.OrderBy(i => i.When),
                "name" => items.OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase),
                "size" => items.OrderByDescending(i => i.Size),
                "dur"  => items.OrderByDescending(i => i.Dur),
                _      => items.OrderByDescending(i => i.When)
            };
            var list = items.ToList();

            CardsPanel.Children.Clear();
            foreach (var e in list) CardsPanel.Children.Add(BuildCard(e));

            long total = _all.Sum(i => i.Size);
            TxtStats.Text = _all.Count == 0 ? "ცარიელია" : $"{_all.Count} ფაილი  ·  {FormatSize(total)}";
            TxtCount.Text = (q.Length > 0 || _filter != "all") && _all.Count > 0 ? $"{list.Count} / {_all.Count}" : "";

            EmptyState.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (list.Count == 0)
            {
                bool nothing = _all.Count == 0;
                TxtEmptyTitle.Text = nothing ? "ბიბლიოთეკა ცარიელია" : "ვერაფერი მოიძებნა";
                TxtEmptySub.Text = nothing
                    ? "დაამუშავე, ჩამოტვირთე ან ჩაწერე ვიდეო — აქ გამოჩნდება. შეგიძლია ნებისმიერი საქაღალდეც დაამატო."
                    : "სცადე სხვა საძიებო სიტყვა ან ფილტრი.";
            }

            if (_selected != null && !_all.Any(a => string.Equals(a.Path, _selected.Path, StringComparison.OrdinalIgnoreCase)))
                Select(null);
            else if (_selected != null)
            {
                var again = _all.First(a => string.Equals(a.Path, _selected.Path, StringComparison.OrdinalIgnoreCase));
                _selected = again;
                HighlightSelection();
            }

            foreach (var e in list.Take(120)) _ = EnsureMetaAsync(e);
        }

        private Border BuildCard(Entry e)
        {
            var card = new Border
            {
                Width = 236, Margin = new Thickness(0, 0, 14, 14), Padding = new Thickness(8),
                CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1.5), Cursor = Cursors.Hand
            };
            card.SetResourceReference(Border.BackgroundProperty, "BgCard");
            card.SetResourceReference(Border.BorderBrushProperty, "Border");

            var stack = new StackPanel();

            var thumbBox = new Border { Height = 128, CornerRadius = new CornerRadius(9), ClipToBounds = true };
            thumbBox.SetResourceReference(Border.BackgroundProperty, "VideoBg");
            var grid = new Grid();
            var img = new Image { Stretch = Stretch.UniformToFill, Source = e.Thumb };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            grid.Children.Add(img);

            var src = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0B, 0x0D, 0x10)), CornerRadius = new CornerRadius(7),
                Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(8),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
            };
            var srcRow = new StackPanel { Orientation = Orientation.Horizontal };
            var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, e.Category switch
            {
                "processed" => "Success", "download" => "Info", "live" => "Danger", _ => "TextMuted"
            });
            srcRow.Children.Add(dot);
            srcRow.Children.Add(new TextBlock { Text = e.Source, FontSize = 10.5, FontWeight = FontWeights.SemiBold,
                                                Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE8, 0xEC)) });
            src.Child = srcRow;
            grid.Children.Add(src);

            var dur = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0B, 0x0D, 0x10)), CornerRadius = new CornerRadius(7),
                Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(8),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Visibility = e.Dur > 0 ? Visibility.Visible : Visibility.Collapsed
            };
            var durText = new TextBlock { Text = FormatDur(e.Dur), FontSize = 11, FontFamily = new FontFamily("Consolas"),
                                          FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF7)) };
            dur.Child = durText;
            grid.Children.Add(dur);
            thumbBox.Child = grid;
            stack.Children.Add(thumbBox);

            var title = new TextBlock { Text = e.Title, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                                        MaxHeight = 36, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 10, 4, 0),
                                        ToolTip = e.Path };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            stack.Children.Add(title);

            var meta = new TextBlock { FontSize = 11, Margin = new Thickness(4, 4, 4, 2), Text = MetaLine(e) };
            meta.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            stack.Children.Add(meta);

            card.Child = stack;
            e.Card = card; e.Img = img; e.MetaText = meta; e.DurText = durText; e.DurBadge = dur;

            card.MouseEnter += (_, _) => { if (_selected != e) card.SetResourceReference(Border.BorderBrushProperty, "BorderStrong"); };
            card.MouseLeave += (_, _) => { if (_selected != e) card.SetResourceReference(Border.BorderBrushProperty, "Border"); };
            card.MouseLeftButtonDown += (_, ev) =>
            {
                Select(e);
                if (ev.ClickCount >= 2) OpenInEditor?.Invoke(e.Path);
            };

            if (_selected != null && string.Equals(_selected.Path, e.Path, StringComparison.OrdinalIgnoreCase))
            {
                card.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
                card.SetResourceReference(Border.BorderBrushProperty, "Accent");
            }
            return card;
        }

        private static string MetaLine(Entry e)
        {
            string date = e.When.Date == DateTime.Today ? $"დღეს {e.When:HH:mm}"
                : e.When.Date == DateTime.Today.AddDays(-1) ? $"გუშინ {e.When:HH:mm}"
                : e.When.ToString("dd.MM.yyyy");
            return $"{date}  ·  {FormatSize(e.Size)}" + (e.H > 0 ? $"  ·  {e.H}p" : "");
        }

        private void HighlightSelection()
        {
            foreach (var e in _all)
            {
                if (e.Card == null) continue;
                bool sel = _selected != null && string.Equals(e.Path, _selected.Path, StringComparison.OrdinalIgnoreCase);
                e.Card.SetResourceReference(Border.BackgroundProperty, sel ? "AccentSoft" : "BgCard");
                e.Card.SetResourceReference(Border.BorderBrushProperty, sel ? "Accent" : "Border");
            }
        }

        // ════════════════════════════════════════════════════════════
        // METADATA + THUMBNAILS (lazy, a few at a time)
        // ════════════════════════════════════════════════════════════
        private string ThumbDir
        {
            get
            {
                string d = string.IsNullOrEmpty(CacheFolder) ? System.IO.Path.Combine(DataDir, "cache") : CacheFolder;
                return System.IO.Path.Combine(d, "libthumbs");
            }
        }

        private string ThumbPathFor(Entry e)
        {
            string key = e.Path.ToLowerInvariant() + "|" + e.Size + "|" + e.Stamp;
            string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16];
            return System.IO.Path.Combine(ThumbDir, hash + ".jpg");
        }

        private async Task EnsureMetaAsync(Entry e)
        {
            if (e.MetaTried && e.Thumb != null) return;
            if (e.MetaTried && e.Dur > 0 && File.Exists(ThumbPathFor(e))) { /* thumb may just need loading */ }
            await _metaGate.WaitAsync();
            try
            {
                if (!File.Exists(e.Path)) return;
                string thumb = ThumbPathFor(e);
                bool needProbe = !e.MetaTried;
                bool needThumb = e.Thumb == null;

                var result = await Task.Run(() =>
                {
                    double dur = e.Dur; int w = e.W, h = e.H; double fps = e.Fps; string codec = e.Codec;
                    if (needProbe) Probe(e.Path, out dur, out w, out h, out fps, out codec);

                    byte[]? bytes = null;
                    if (needThumb)
                    {
                        try
                        {
                            if (!File.Exists(thumb))
                            {
                                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(thumb)!);
                                double t = dur > 12 ? 3 : (dur > 2 ? 1 : 0);
                                RunFfmpeg(new[] { "-y", "-loglevel", "error", "-ss", t.ToString(CultureInfo.InvariantCulture),
                                                  "-i", e.Path, "-frames:v", "1", "-vf", "scale=640:-2", "-q:v", "4", thumb }, 25000);
                            }
                            if (File.Exists(thumb)) bytes = File.ReadAllBytes(thumb);
                        }
                        catch { }
                    }
                    return (dur, w, h, fps, codec, bytes);
                });

                e.MetaTried = true;
                e.Dur = result.dur; e.W = result.w; e.H = result.h; e.Fps = result.fps; e.Codec = result.codec;
                if (result.bytes != null && e.Thumb == null)
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.StreamSource = new MemoryStream(result.bytes);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit(); bmp.Freeze();
                    e.Thumb = bmp;
                }

                if (e.Img != null) e.Img.Source = e.Thumb;
                if (e.MetaText != null) e.MetaText.Text = MetaLine(e);
                if (e.DurText != null && e.Dur > 0)
                {
                    e.DurText.Text = FormatDur(e.Dur);
                    if (e.DurBadge != null) e.DurBadge.Visibility = Visibility.Visible;
                }
                if (_selected == e) FillDetails(e, keepPlayback: true);
            }
            catch { }
            finally { _metaGate.Release(); }
        }

        private static void Probe(string path, out double dur, out int w, out int h, out double fps, out string codec)
        {
            dur = 0; w = h = 0; fps = 0; codec = "";
            try
            {
                var psi = new ProcessStartInfo(AppPaths.Ffmpeg())
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
                    StandardErrorEncoding = Encoding.UTF8
                };
                psi.ArgumentList.Add("-hide_banner"); psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(path);
                using var p = Process.Start(psi);
                if (p == null) return;
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit(15000);

                var d = Regex.Match(err, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
                if (d.Success)
                    dur = int.Parse(d.Groups[1].Value) * 3600 + int.Parse(d.Groups[2].Value) * 60
                          + double.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture);
                foreach (var line in err.Split('\n'))
                {
                    if (!line.Contains("Video:")) continue;
                    var c = Regex.Match(line, @"Video:\s*([A-Za-z0-9_]+)");
                    if (c.Success) codec = c.Groups[1].Value;
                    var r = Regex.Match(line, @",\s*(\d{2,5})x(\d{2,5})");
                    if (r.Success) { w = int.Parse(r.Groups[1].Value); h = int.Parse(r.Groups[2].Value); }
                    var f = Regex.Match(line, @"(\d+(?:\.\d+)?)\s*fps");
                    if (!f.Success) f = Regex.Match(line, @"(\d+(?:\.\d+)?)\s*tbr");
                    if (f.Success) fps = double.Parse(f.Groups[1].Value, CultureInfo.InvariantCulture);
                    break;
                }
            }
            catch { }
        }

        private static bool RunFfmpeg(string[] args, int timeoutMs)
        {
            var si = new ProcessStartInfo(AppPaths.Ffmpeg()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var a in args) si.ArgumentList.Add(a);
            try
            {
                using var p = Process.Start(si);
                if (p == null) return false;
                p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return false; }
                return p.ExitCode == 0;
            }
            catch { return false; }
        }

        // ════════════════════════════════════════════════════════════
        // SELECTION / DETAILS / PREVIEW PLAYER
        // ════════════════════════════════════════════════════════════
        private void Select(Entry? e)
        {
            if (e != null && _selected != null && string.Equals(e.Path, _selected.Path, StringComparison.OrdinalIgnoreCase)) return;
            StopPreview();
            _selected = e;
            HighlightSelection();
            if (e == null)
            {
                DetailsPanel.Visibility = Visibility.Collapsed;
                DetailsEmpty.Visibility = Visibility.Visible;
                return;
            }
            DetailsEmpty.Visibility = Visibility.Collapsed;
            DetailsPanel.Visibility = Visibility.Visible;
            FillDetails(e, keepPlayback: false);
            _ = EnsureMetaAsync(e);
        }

        private void FillDetails(Entry e, bool keepPlayback)
        {
            TxtDTitle.Text = e.Title;
            TxtDPath.Text = e.Path;
            if (!keepPlayback) { DImg.Source = e.Thumb; }
            else if (!_playing && DMedia.Visibility != Visibility.Visible) DImg.Source = e.Thumb;
            TxtDTot.Text = FormatDur(e.Dur);

            DChips.Children.Clear();
            DChips.Children.Add(Chip(e.Source, e.Category switch
            {
                "processed" => "", "download" => "", "live" => "", _ => ""
            }));
            DChips.Children.Add(Chip(e.When.ToString("dd.MM.yyyy HH:mm"), ""));
            DChips.Children.Add(Chip(FormatSize(e.Size), ""));
            if (e.W > 0) DChips.Children.Add(Chip($"{e.W}×{e.H}", ""));
            if (e.Dur > 0) DChips.Children.Add(Chip(FormatDur(e.Dur), ""));
            if (e.Fps > 0) DChips.Children.Add(Chip($"{e.Fps.ToString("0.##", CultureInfo.InvariantCulture)} fps"));
            if (e.Codec.Length > 0) DChips.Children.Add(Chip(e.Codec.ToUpperInvariant()));
        }

        private Border Chip(string text, string glyph = "")
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            if (glyph.Length > 0)
            {
                var g = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11,
                                        Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Center };
                g.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
                sp.Children.Add(g);
            }
            var t = new TextBlock { Text = text, FontSize = 11.5 };
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            sp.Children.Add(t);
            var b = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 5, 10, 5),
                                 Margin = new Thickness(0, 0, 7, 7), BorderThickness = new Thickness(1), Child = sp };
            b.SetResourceReference(Border.BackgroundProperty, "BgCard");
            b.SetResourceReference(Border.BorderBrushProperty, "Border");
            return b;
        }

        private void BtnDPlay_Click(object sender, RoutedEventArgs e) => TogglePlay();

        private void TogglePlay()
        {
            if (_selected == null) return;
            if (DMedia.Source == null || !string.Equals(DMedia.Source.LocalPath, _selected.Path, StringComparison.OrdinalIgnoreCase))
            {
                _mediaReady = false;
                DMedia.Visibility = Visibility.Visible;
                DMedia.Source = new Uri(_selected.Path, UriKind.Absolute);
            }
            if (_playing)
            {
                DMedia.Pause(); _playing = false; _playTimer.Stop();
                BtnDPlay.Visibility = Visibility.Visible; BtnDPlay.Content = "";
            }
            else
            {
                DMedia.Play(); _playing = true; _playTimer.Start();
                BtnDPlay.Visibility = Visibility.Collapsed;
            }
        }

        private void StopPreview()
        {
            try { DMedia.Stop(); } catch { }
            DMedia.Source = null;
            DMedia.Visibility = Visibility.Collapsed;
            _playing = false; _mediaReady = false;
            _playTimer.Stop();
            if (BtnDPlay != null) { BtnDPlay.Visibility = Visibility.Visible; BtnDPlay.Content = ""; }
            if (SlDSeek != null) { _seekFromTimer = true; SlDSeek.Value = 0; _seekFromTimer = false; }
            if (TxtDCur != null) TxtDCur.Text = "0:00";
        }

        private void DMedia_Opened(object sender, RoutedEventArgs e)
        {
            _mediaReady = true;
            if (DMedia.NaturalDuration.HasTimeSpan)
            {
                SlDSeek.Maximum = Math.Max(0.1, DMedia.NaturalDuration.TimeSpan.TotalSeconds);
                TxtDTot.Text = FormatDur(DMedia.NaturalDuration.TimeSpan.TotalSeconds);
            }
        }

        private void DMedia_Ended(object sender, RoutedEventArgs e)
        {
            _playing = false; _playTimer.Stop();
            DMedia.Position = TimeSpan.Zero; DMedia.Pause();
            BtnDPlay.Visibility = Visibility.Visible; BtnDPlay.Content = "";
        }

        private void PlayTimer_Tick(object? sender, EventArgs e)
        {
            if (!_mediaReady) return;
            _seekFromTimer = true;
            SlDSeek.Value = Math.Min(SlDSeek.Maximum, DMedia.Position.TotalSeconds);
            _seekFromTimer = false;
            TxtDCur.Text = FormatDur(DMedia.Position.TotalSeconds);
        }

        private void SlDSeek_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_seekFromTimer || !_mediaReady) return;
            DMedia.Position = TimeSpan.FromSeconds(e.NewValue);
            TxtDCur.Text = FormatDur(e.NewValue);
        }

        // ════════════════════════════════════════════════════════════
        // TOOLBAR
        // ════════════════════════════════════════════════════════════
        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (IsLoaded) Rebuild();
        }

        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb) _filter = rb.Tag?.ToString() ?? "all";
            Rebuild();
        }

        private void Sort_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            _sort = (CmbSort.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "new";
            Rebuild();
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => Refresh();

        private void BtnAddFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "საქაღალდე ბიბლიოთეკაში დასამატებლად" };
            if (dlg.ShowDialog() != true) return;
            if (!_userFolders.Contains(dlg.FolderName, StringComparer.OrdinalIgnoreCase))
            {
                _userFolders.Add(dlg.FolderName);
                SaveUserFolders();
            }
            Refresh();
        }

        private void LoadUserFolders()
        {
            try
            {
                if (File.Exists(FoldersFile))
                {
                    var l = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FoldersFile));
                    if (l != null) { _userFolders.Clear(); _userFolders.AddRange(l.Where(Directory.Exists)); }
                }
            }
            catch { }
        }

        private void SaveUserFolders()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(FoldersFile, JsonSerializer.Serialize(_userFolders));
            }
            catch { }
        }

        // ════════════════════════════════════════════════════════════
        // ACTIONS
        // ════════════════════════════════════════════════════════════
        private void BtnEditor_Click(object sender, RoutedEventArgs e)
        { if (_selected != null) { StopPreview(); OpenInEditor?.Invoke(_selected.Path); } }

        private void BtnCrop_Click(object sender, RoutedEventArgs e)
        { if (_selected != null) { StopPreview(); OpenInCrop?.Invoke(_selected.Path); } }

        private void BtnVoiceOver_Click(object sender, RoutedEventArgs e)
        { if (_selected != null) { StopPreview(); OpenInVoiceOver?.Invoke(_selected.Path); } }

        private void BtnFaceBlur_Click(object sender, RoutedEventArgs e)
        { if (_selected != null) { StopPreview(); OpenInFaceBlur?.Invoke(_selected.Path); } }

        private void BtnAbA_Click(object sender, RoutedEventArgs e)
        { if (_selected != null) { StopPreview(); OpenInAB?.Invoke(_selected.Path, true); } }

        private void BtnAbB_Click(object sender, RoutedEventArgs e)
        { if (_selected != null) { StopPreview(); OpenInAB?.Invoke(_selected.Path, false); } }

        private void BtnPlayExternal_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            try { Process.Start(new ProcessStartInfo(_selected.Path) { UseShellExecute = true }); } catch { }
        }

        private void BtnReveal_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            try { Process.Start("explorer.exe", $"/select,\"{_selected.Path}\""); } catch { }
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            var sel = _selected;
            var r = MessageBox.Show($"წავშალო ფაილი?\n\n{System.IO.Path.GetFileName(sel.Path)}\n\nგადავა Recycle Bin-ში — საჭიროების შემთხვევაში იქიდან აღდგება.",
                "წაშლა", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;

            StopPreview();
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(sel.Path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            catch (Exception ex)
            {
                MessageBox.Show("ვერ წაიშალა: " + ex.Message, "შეცდომა", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            FileDeleted?.Invoke(sel.Path);
            _selected = null;
            DetailsPanel.Visibility = Visibility.Collapsed;
            DetailsEmpty.Visibility = Visibility.Visible;
            Refresh();
        }

        // ── helpers ──────────────────────────────────────────────────
        private static string FormatDur(double sec)
        {
            if (sec <= 0) return "0:00";
            var t = TimeSpan.FromSeconds(sec);
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }

        private static string FormatSize(long bytes)
        {
            double mb = bytes / 1048576.0;
            return mb >= 1024 ? $"{mb / 1024:0.0} GB" : mb >= 10 ? $"{mb:0} MB" : $"{mb:0.0} MB";
        }
    }
}

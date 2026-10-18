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
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shapes = System.Windows.Shapes;

namespace AiAssistant
{
    public partial class FaceBlurView : UserControl
    {
        public event Action<string, string>? OpenInAB;      // (original, result)
        public event Action<string, string>? Processed;     // (input, output)
        public event Action<string, string>? Notify;
        public string CacheFolder { get; set; } = "";

        private class Person
        {
            public int Id, First, Last, Count, BestFrame;
            public bool Minor;
            public double Size;
            public int No;                                   // number shown on the card / lane / frame (main people only)
            public Color Col;
            public List<(int a, int b)> Segs = new();        // frame ranges where the person is on screen
            public Dictionary<int, double[]> Boxes = new();  // frame → x,y,w,h (main people only)
            public int[] Sorted = Array.Empty<int>();        // frames that have a box
            public List<(int a, int b, bool inc)> Edits = new();   // manual: stretch (true) / cut away (false) a frame range
            public bool[] Cover = Array.Empty<bool>();       // frames that will really be blurred (auto + manual edits)
            public string Thumb = "";
            public CheckBox? Toggle;
        }

        private string? _input;
        private string? _output;
        private double _fps = 25, _dur;
        private int _frames;
        private string? _analysisPath;
        private readonly List<Person> _people = new();
        private int _vw = 1, _vh = 1;                  // video size in pixels (analysis boxes use it)
        private List<(int a, int b)> _crowdSegs = new();
        private bool _laneDrag;

        // one colour per person (the same one on the card, the timeline lane and the frame in the preview)
        private static readonly Color[] Palette =
        {
            Color.FromRgb(0x4C, 0x9A, 0xFF), Color.FromRgb(0xFF, 0xB0, 0x20), Color.FromRgb(0x2F, 0xD2, 0x7F),
            Color.FromRgb(0xB3, 0x6B, 0xFF), Color.FromRgb(0xFF, 0x6B, 0x9A), Color.FromRgb(0x22, 0xD3, 0xEE),
            Color.FromRgb(0xFF, 0x8A, 0x3D), Color.FromRgb(0xA3, 0xE6, 0x35), Color.FromRgb(0xF4, 0x3F, 0x5E),
            Color.FromRgb(0x5E, 0xEA, 0xD4), Color.FromRgb(0xFA, 0xCC, 0x15), Color.FromRgb(0x94, 0xA3, 0xB8)
        };
        private static readonly Color CrowdColor = Color.FromRgb(0x8A, 0x91, 0x9C);

        private CheckBox? _groupToggle;          // one switch for all the small / background faces
        private bool _busy;
        private Process? _proc;
        private bool _cancelled;

        private BitmapImage? _imgOrig, _imgRes;
        private int _previewVersion;
        private bool _suppressSlider;
        private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(380) };

        private string Quality => QFast.IsChecked == true ? "fast" : QMax.IsChecked == true ? "max" : "balanced";

        private static readonly string[] VideoExt = { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".mxf", ".m4v", ".ts" };

        public FaceBlurView()
        {
            InitializeComponent();
            _debounce.Tick += (_, _) => { _debounce.Stop(); _ = UpdatePreviewAsync(); };
            SetStep();
            IsVisibleChanged += (_, _) => { };
        }

        // ════════════════════════════════════════════════════════════
        // PATHS / PROCESS HELPERS
        // ════════════════════════════════════════════════════════════
        private string WorkDir
        {
            get
            {
                string root = !string.IsNullOrEmpty(CacheFolder) ? CacheFolder
                    : Path.Combine(AppPaths.DataDir, "cache");
                string d = Path.Combine(root, "faceblur");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        private static string? ScriptPath()
        {
            string b = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var c in new[]
            {
                Path.Combine(b, "face_blur.py"),
                Path.GetFullPath(Path.Combine(b, @"..\..\..\face_blur.py")),
                Path.GetFullPath(Path.Combine(b, @"..\..\..\..\face_blur.py"))
            })
                if (File.Exists(c)) return c;
            return null;
        }

        private static ProcessStartInfo MakePy(string script, params string[] args)
        {
            var si = new ProcessStartInfo("python")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            si.ArgumentList.Add(script);
            foreach (var a in args) si.ArgumentList.Add(a);
            si.Environment["PYTHONUTF8"] = "1";
            si.Environment["PYTHONIOENCODING"] = "utf-8";
            return si;
        }

        /// <summary>Runs face_blur.py, streaming its [PROGRESS]/[STATUS]/[SUCCESS] lines. Returns (exit code, last status).</summary>
        private async Task<(int code, string lastStatus, bool success)> RunScriptAsync(string[] args, Action<string>? onLine = null)
        {
            string? script = ScriptPath();
            if (script == null) return (-1, "face_blur.py ვერ მოიძებნა", false);

            string last = "";
            bool success = false;
            int code = await Task.Run(() =>
            {
                try
                {
                    using var p = new Process { StartInfo = MakePy(script, args) };
                    p.OutputDataReceived += (_, ev) =>
                    {
                        if (ev.Data == null) return;
                        if (ev.Data.StartsWith("[STATUS]")) last = ev.Data[8..].Trim();
                        if (ev.Data.StartsWith("[SUCCESS]")) success = true;
                        onLine?.Invoke(ev.Data);
                    };
                    var err = new StringBuilder();
                    p.ErrorDataReceived += (_, ev) => { if (ev.Data != null) err.AppendLine(ev.Data); };
                    _proc = p;
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    p.WaitForExit();
                    _proc = null;
                    if (p.ExitCode != 0 && last.Length == 0)
                    {
                        string e = err.ToString().Trim();
                        last = e.Length > 0 ? e.Split('\n').Last().Trim() : $"python კოდი {p.ExitCode}";
                    }
                    return p.ExitCode;
                }
                catch (Exception ex) { _proc = null; last = ex.Message; return -2; }
            });
            return (code, last, success);
        }

        private static async Task<bool> RunFfmpegAsync(string[] args, int timeoutMs = 20000)
        {
            return await Task.Run(() =>
            {
                var si = new ProcessStartInfo(AppPaths.Ffmpeg())
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
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
            });
        }

        private static (double dur, double fps, int w, int h) Probe(string path)
        {
            double dur = 0, fps = 25; int w = 0, h = 0;
            try
            {
                var si = new ProcessStartInfo(AppPaths.Ffmpeg())
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8 };
                si.ArgumentList.Add("-hide_banner"); si.ArgumentList.Add("-i"); si.ArgumentList.Add(path);
                using var p = Process.Start(si);
                string err = p!.StandardError.ReadToEnd();
                p.WaitForExit(15000);
                var d = Regex.Match(err, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
                if (d.Success)
                    dur = int.Parse(d.Groups[1].Value) * 3600 + int.Parse(d.Groups[2].Value) * 60
                          + double.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture);
                foreach (var line in err.Split('\n'))
                {
                    if (!line.Contains("Video:")) continue;
                    var r = Regex.Match(line, @",\s*(\d{2,5})x(\d{2,5})");
                    if (r.Success) { w = int.Parse(r.Groups[1].Value); h = int.Parse(r.Groups[2].Value); }
                    var f = Regex.Match(line, @"(\d+(?:\.\d+)?)\s*fps");
                    if (!f.Success) f = Regex.Match(line, @"(\d+(?:\.\d+)?)\s*tbr");
                    if (f.Success) fps = double.Parse(f.Groups[1].Value, CultureInfo.InvariantCulture);
                    break;
                }
            }
            catch { }
            return (dur, fps > 0 ? fps : 25, w, h);
        }

        // ════════════════════════════════════════════════════════════
        // SOURCE
        // ════════════════════════════════════════════════════════════
        private void BtnPick_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var ofd = new Microsoft.Win32.OpenFileDialog { Title = "ვიდეოს არჩევა", Filter = "Video|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.mxf;*.m4v|All|*.*" };
            if (ofd.ShowDialog() == true) _ = SetSourceAsync(ofd.FileName);
        }

        /// <summary>Public: other tabs can send a video here.</summary>
        public void LoadVideo(string path) => _ = SetSourceAsync(path);

        private async Task SetSourceAsync(string path)
        {
            if (_busy || !File.Exists(path)) return;
            _input = path;
            _output = null;
            _people.Clear();
            PeoplePanel.Children.Clear();
            _analysisPath = null;
            _imgOrig = _imgRes = null;
            _groupToggle = null; _crowdSegs = new(); RefreshVisuals();

            TxtFile.Text = Path.GetFileName(path);
            TxtStatus.Text = "";
            SetBusyVisual(false, "");
            DropZone.Visibility = Visibility.Collapsed;
            BtnChange.Visibility = Visibility.Visible;
            ScrubRow.Visibility = Visibility.Visible;
            BtnCompare.Visibility = BtnReveal.Visibility = Visibility.Collapsed;
            BtnAnalyze.Visibility = Visibility.Visible;
            BtnAnalyze.IsEnabled = false;
            BtnRender.Visibility = Visibility.Collapsed;
            ViewSwitch.Visibility = Visibility.Collapsed;
            RefreshPeopleHint();

            var info = await Task.Run(() => Probe(path));
            _dur = info.dur; _fps = info.fps;
            _frames = Math.Max(1, (int)Math.Round(_dur * _fps));
            TxtTotal.Text = Fmt(_dur);
            TxtStatus.Text = $"{info.w}×{info.h} · {_fps:0.##} fps · {Fmt(_dur)}";

            _suppressSlider = true;
            SlFrame.Maximum = Math.Max(1, _frames - 1);
            SlFrame.Value = Math.Min(_frames - 1, Math.Round(_fps * Math.Min(2, _dur / 3)));
            _suppressSlider = false;
            TxtTime.Text = Fmt(SlFrame.Value / _fps);

            // an analysis of this exact file may already exist → reuse it
            string cached = AnalysisFileFor(path);
            if (File.Exists(cached) && LoadAnalysis(cached)) { /* people listed */ }
            else BtnAnalyze.IsEnabled = true;

            SetStep();
            await UpdatePreviewAsync();
        }

        private string AnalysisFileFor(string path)
        {
            var fi = new FileInfo(path);
            string key = path.ToLowerInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks + "|v3|" + Quality;
            string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16];
            return Path.Combine(WorkDir, hash + ".json");
        }

        // ── drag & drop ──────────────────────────────────────────────
        private void Drag_Enter(object sender, DragEventArgs e)
        {
            bool ok = !_busy && e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            DragOverlay.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
            e.Handled = true;
        }

        private void Drag_Leave(object sender, DragEventArgs e) => DragOverlay.Visibility = Visibility.Collapsed;

        private void Drag_Drop(object sender, DragEventArgs e)
        {
            DragOverlay.Visibility = Visibility.Collapsed;
            if (_busy) return;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] f && f.Length > 0
                && VideoExt.Contains(Path.GetExtension(f[0]).ToLowerInvariant()))
                _ = SetSourceAsync(f[0]);
            e.Handled = true;
        }

        // ════════════════════════════════════════════════════════════
        // ANALYZE
        // ════════════════════════════════════════════════════════════
        private async void BtnAnalyze_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _input == null) return;
            string input = _input;
            string outJson = AnalysisFileFor(input);

            _cancelled = false;
            SetBusy(true);
            SetBusyVisual(true, "სახეების ძებნა…");
            TxtStatus.Text = "პირველად შეიძლება მოდელის ჩამოტვირთვას დასჭირდეს რამდენიმე წამი";

            var res = await RunScriptAsync(new[] { "analyze", input, outJson, Quality }, line =>
            {
                if (line.StartsWith("[PROGRESS]") && int.TryParse(line[10..].Trim(), out int pct))
                    Dispatcher.InvokeAsync(() => { Pb.Value = pct; TxtBusy.Text = $"სახეების ძებნა… {pct}%"; });
                else if (line.StartsWith("[STATUS]"))
                    Dispatcher.InvokeAsync(() => TxtStatus.Text = line[8..].Trim());
            });

            SetBusy(false);
            SetBusyVisual(false, "");
            if (_cancelled) { TxtStatus.Text = "გაჩერდა"; BtnAnalyze.IsEnabled = true; return; }

            if (!res.success || !File.Exists(outJson) || !LoadAnalysis(outJson))
            {
                TxtStatus.Text = "ანალიზი ვერ მოხერხდა: " + res.lastStatus;
                BtnAnalyze.IsEnabled = true;
                Notify?.Invoke("სახეების ძებნა ვერ მოხერხდა", "error");
                return;
            }

            SetStep();
            await UpdatePreviewAsync();
        }

        private bool LoadAnalysis(string path)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                var people = new List<Person>();
                int vw = root.TryGetProperty("width", out var jw) ? jw.GetInt32() : 1;
                int vh = root.TryGetProperty("height", out var jh) ? jh.GetInt32() : 1;
                int nFrames = root.TryGetProperty("frames", out var jf) ? jf.GetInt32() : 1;
                var crowd = new bool[Math.Max(1, nFrames)];
                foreach (var t in root.GetProperty("tracks").EnumerateArray())
                {
                    var person = new Person
                    {
                        Id = t.GetProperty("id").GetInt32(),
                        First = t.GetProperty("first").GetInt32(),
                        Last = t.GetProperty("last").GetInt32(),
                        Count = t.GetProperty("count").GetInt32(),
                        BestFrame = t.TryGetProperty("best_frame", out var bf) ? bf.GetInt32() : t.GetProperty("first").GetInt32(),
                        Thumb = t.TryGetProperty("thumb", out var th) ? th.GetString() ?? "" : "",
                        Minor = t.TryGetProperty("minor", out var mi) && mi.GetBoolean(),
                        Size = t.TryGetProperty("size", out var sz) ? sz.GetDouble() : 0
                    };
                    var frames = new List<int>();
                    foreach (var bp in t.GetProperty("boxes").EnumerateObject())
                    {
                        if (!int.TryParse(bp.Name, out int fr)) continue;
                        frames.Add(fr);
                        if (person.Minor) { if (fr >= 0 && fr < crowd.Length) crowd[fr] = true; }
                        else
                        {
                            var a = bp.Value;
                            person.Boxes[fr] = new[] { a[0].GetDouble(), a[1].GetDouble(), a[2].GetDouble(), a[3].GetDouble() };
                        }
                    }
                    if (!person.Minor) { person.Segs = ToSegments(frames); person.Sorted = person.Boxes.Keys.OrderBy(k => k).ToArray(); }
                    people.Add(person);
                }
                int no = 0;
                foreach (var pr in people.Where(x => !x.Minor).OrderBy(x => x.Id))
                {
                    pr.No = ++no;
                    pr.Col = Palette[(pr.No - 1) % Palette.Length];
                }
                var crowdFrames = new List<int>();
                for (int i = 0; i < crowd.Length; i++) if (crowd[i]) crowdFrames.Add(i);
                _crowdSegs = ToSegments(crowdFrames);
                _vw = Math.Max(1, vw); _vh = Math.Max(1, vh);
                _frames = Math.Max(_frames, nFrames);
                foreach (var pr in people.Where(x => !x.Minor)) RebuildCover(pr);
                _analysisPath = path;
                _people.Clear();
                _people.AddRange(people);
                BuildPeopleUi();
                BtnAnalyze.Visibility = Visibility.Collapsed;
                BtnRender.Visibility = Visibility.Visible;
                ViewSwitch.Visibility = Visibility.Visible;
                RefreshPeopleHint();
                TxtStatus.Text = _people.Count == 0
                    ? "სახე ვერ ვიპოვე ამ ვიდეოში"
                    : (_people.Any(x => x.Minor)
                        ? $"ნაპოვნია {_people.Count(x => !x.Minor)} ძირითადი ადამიანი + {_people.Count(x => x.Minor)} სხვა სახე · გადაახვიე და შეამოწმე"
                        : $"ნაპოვნია {_people.Count} ადამიანი · გადაახვიე და შეამოწმე, მერე დააჭირე „დაბუნდოვნება“");
                return true;
            }
            catch { return false; }
        }

        private void BuildPeopleUi()
        {
            PeoplePanel.Children.Clear();
            _groupToggle = null;
            foreach (var p in _people.Where(x => !x.Minor))
            {
                var card = new Border { Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 8),
                                        CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
                card.SetResourceReference(Border.BackgroundProperty, "BgCard");
                card.SetResourceReference(Border.BorderBrushProperty, "Border");

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var thumbBox = new Border { Width = 54, Height = 54, CornerRadius = new CornerRadius(27), ClipToBounds = true,
                                            BorderBrush = new SolidColorBrush(p.Col), BorderThickness = new Thickness(3),
                                            Cursor = Cursors.Hand,
                                            ToolTip = "დააჭირე — გადახვალ ამ ადამიანის კადრზე" };
                thumbBox.SetResourceReference(Border.BackgroundProperty, "VideoBg");
                if (File.Exists(p.Thumb))
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource = new Uri(p.Thumb, UriKind.Absolute);
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit(); bmp.Freeze();
                        thumbBox.Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill };
                    }
                    catch { }
                }
                int best = p.BestFrame;
                thumbBox.MouseLeftButtonDown += (_, _) => SeekTo(best);

                // portrait + number badge in the person's colour
                var portrait = new Grid { Width = 58, Height = 58, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
                portrait.Children.Add(thumbBox);
                portrait.Children.Add(MakeBadge(p, 20, 11, new Thickness(0), HorizontalAlignment.Right, VerticalAlignment.Bottom));
                grid.Children.Add(portrait);

                var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var name = new TextBlock { Text = $"ადამიანი {p.No}", FontSize = 13.5, FontWeight = FontWeights.SemiBold };
                name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
                mid.Children.Add(name);
                var range = new TextBlock
                {
                    Text = $"{Fmt(p.First / _fps)} – {Fmt(p.Last / _fps)}"
                           + ((p.Last - p.First) / _fps < 1.5 ? "  ·  მოკლე" : ""),
                    FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0)
                };
                range.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
                mid.Children.Add(range);
                Grid.SetColumn(mid, 1);
                grid.Children.Add(mid);

                var toggle = new CheckBox { IsChecked = true, Style = (Style)FindResource("ToggleSwitch"),
                                            VerticalAlignment = VerticalAlignment.Center, ToolTip = "ჩართული = სახე დაუბუნდოვნდება" };
                toggle.Click += (_, _) => { RequestPreview(); RefreshVisuals(); };
                p.Toggle = toggle;
                Grid.SetColumn(toggle, 2);
                grid.Children.Add(toggle);

                card.Child = grid;
                PeoplePanel.Children.Add(card);
            }

            // everything small / in the background: one card, one switch
            var minor = _people.Where(x => x.Minor).ToList();
            if (minor.Count > 0)
            {
                var card = new Border { Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 8),
                                        CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
                card.SetResourceReference(Border.BackgroundProperty, "BgCard");
                card.SetResourceReference(Border.BorderBrushProperty, "Border");
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var tile = new Border { Width = 54, Height = 54, CornerRadius = new CornerRadius(27), Margin = new Thickness(0, 0, 12, 0),
                                        Background = Brush(CrowdColor, 0.2), BorderBrush = Brush(CrowdColor), BorderThickness = new Thickness(3) };
                var glyph = new TextBlock { Text = "\uE716", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 22,
                                            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                glyph.Foreground = Brush(CrowdColor);
                tile.Child = glyph;
                grid.Children.Add(tile);

                var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var name = new TextBlock { Text = $"სხვა სახეები · {minor.Count}", FontSize = 13.5, FontWeight = FontWeights.SemiBold };
                name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
                mid.Children.Add(name);
                var sub = new TextBlock { Text = "ბრბო, პატარა და ფონის სახეები — ერთად", FontSize = 11.5,
                                          Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
                sub.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
                mid.Children.Add(sub);
                Grid.SetColumn(mid, 1);
                grid.Children.Add(mid);

                _groupToggle = new CheckBox { IsChecked = true, Style = (Style)FindResource("ToggleSwitch"),
                                              VerticalAlignment = VerticalAlignment.Center,
                                              ToolTip = "ჩართული = ყველა ეს სახე დაუბუნდოვნდება" };
                _groupToggle.Click += (_, _) => { RequestPreview(); RefreshVisuals(); };
                Grid.SetColumn(_groupToggle, 2);
                grid.Children.Add(_groupToggle);
                card.Child = grid;
                PeoplePanel.Children.Insert(0, card);      // the usual first decision: blur the crowd or not
            }
            RefreshVisuals();
        }

        // ════════════════════════════════════════════════════════════
        // PEOPLE COLOURS · TIMELINE LANES · PREVIEW FRAMES
        // ════════════════════════════════════════════════════════════
        private static List<(int a, int b)> ToSegments(List<int> frames, int maxGap = 2)
        {
            var res = new List<(int, int)>();
            if (frames.Count == 0) return res;
            frames.Sort();
            int a = frames[0], b = frames[0];
            foreach (int f in frames.Skip(1))
            {
                if (f - b <= maxGap + 1) b = f;
                else { res.Add((a, b)); a = b = f; }
            }
            res.Add((a, b));
            return res;
        }

        private static SolidColorBrush Brush(Color c, double opacity = 1)
        {
            var b = new SolidColorBrush(c) { Opacity = opacity };
            b.Freeze();
            return b;
        }

        /// <summary>Round number chip in the person's colour (dark text on the bright colours).</summary>
        private static Border MakeBadge(Person p, double w, double fontSize, Thickness margin, HorizontalAlignment ha, VerticalAlignment va)
        {
            var lum = (0.299 * p.Col.R + 0.587 * p.Col.G + 0.114 * p.Col.B) / 255.0;
            return new Border
            {
                Width = w, Height = w * (w > 15 ? 0.9 : 0.8), CornerRadius = new CornerRadius(w), Margin = margin,
                HorizontalAlignment = ha, VerticalAlignment = va, Background = Brush(p.Col),
                BorderBrush = Brush(Color.FromRgb(0x14, 0x17, 0x1C)), BorderThickness = new Thickness(1.5),
                Child = new TextBlock
                {
                    Text = p.No.ToString(), FontSize = fontSize, FontWeight = FontWeights.Bold,
                    Foreground = lum > 0.55 ? Brushes.Black : Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            };
        }

        /// <summary>Automatic frames + the user's manual edits → what is really covered; also the lane segments.</summary>
        private void RebuildCover(Person p)
        {
            var arr = new bool[Math.Max(1, _frames)];
            foreach (var (a, b) in p.Segs)
                for (int f = Math.Max(0, a); f <= b && f < arr.Length; f++) arr[f] = true;
            foreach (var (a, b, inc) in p.Edits)
                for (int f = Math.Max(0, a); f <= b && f < arr.Length; f++) arr[f] = inc;
            p.Cover = arr;
        }

        private static List<(int a, int b)> CoverSegments(bool[] cover)
        {
            var res = new List<(int, int)>();
            int start = -1;
            for (int i = 0; i < cover.Length; i++)
            {
                if (cover[i] && start < 0) start = i;
                else if (!cover[i] && start >= 0) { res.Add((start, i - 1)); start = -1; }
            }
            if (start >= 0) res.Add((start, cover.Length - 1));
            return res;
        }

        private static double[]? NearestBox(Person p, int frame)
        {
            if (p.Sorted.Length == 0) return null;
            int i = Array.BinarySearch(p.Sorted, frame);
            if (i >= 0) return p.Boxes[p.Sorted[i]];
            i = ~i;
            int lo = Math.Max(0, i - 1), hi = Math.Min(p.Sorted.Length - 1, i);
            int pick = Math.Abs(p.Sorted[lo] - frame) <= Math.Abs(p.Sorted[hi] - frame) ? lo : hi;
            return p.Boxes[p.Sorted[pick]];
        }

        private double[]? BoxAt(Person p, int frame)
        {
            if (frame < 0 || frame >= p.Cover.Length || !p.Cover[frame]) return null;
            return p.Boxes.TryGetValue(frame, out var b) ? b : NearestBox(p, frame);
        }

        private sealed class EdgeDrag { public Person P = null!; public int Lane; public bool Right; public int Orig; public int Seg; }
        private EdgeDrag? _edge;
        private readonly List<Person?> _laneOwner = new();         // lane index → person (null = crowd lane)
        private Shapes.Rectangle? _ghost;

        private int LaneAt(double y) { int i = (int)(y / (LaneH + LaneGap)); return i >= 0 && i < _laneOwner.Count && y - i * (LaneH + LaneGap) <= LaneH ? i : -1; }

        private EdgeDrag? EdgeAt(Point pt)
        {
            int lane = LaneAt(pt.Y);
            if (lane < 0 || _laneOwner[lane] is not Person p || _frames <= 0) return null;
            double fx = LaneCanvas.ActualWidth / Math.Max(1, _frames);
            var segs = CoverSegments(p.Cover);
            for (int i = 0; i < segs.Count; i++)
            {
                double xl = segs[i].a * fx, xr = (segs[i].b + 1) * fx;
                if (Math.Abs(pt.X - xr) <= 6) return new EdgeDrag { P = p, Lane = lane, Right = true, Orig = segs[i].b + 1, Seg = i };
                if (Math.Abs(pt.X - xl) <= 6) return new EdgeDrag { P = p, Lane = lane, Right = false, Orig = segs[i].a, Seg = i };
            }
            return null;
        }

        private int FrameAtX(double x) => Math.Clamp((int)Math.Round(x / Math.Max(1, LaneCanvas.ActualWidth) * _frames), 0, Math.Max(0, _frames));

        private bool IsOn(Person p) => p.Minor ? _groupToggle?.IsChecked == true : p.Toggle?.IsChecked == true;

        private void RefreshVisuals()
        {
            RedrawLanes();
            DrawOverlay();
        }

        private const double LaneH = 16, LaneGap = 5;

        private void RedrawLanes()
        {
            LaneCanvas.Children.Clear();
            LaneBadges.Children.Clear();
            _laneOwner.Clear();
            _ghost = null;
            var main = _people.Where(x => !x.Minor).OrderBy(x => x.No).ToList();
            bool hasCrowd = _crowdSegs.Count > 0;
            int lanes = main.Count + (hasCrowd ? 1 : 0);
            LaneScroll.Visibility = lanes > 0 && _analysisPath != null ? Visibility.Visible : Visibility.Collapsed;
            double w = LaneCanvas.ActualWidth;
            LaneCanvas.Height = Math.Max(0, lanes * (LaneH + LaneGap) - LaneGap);
            if (lanes == 0 || w <= 0 || _frames <= 0) return;

            double fx = w / Math.Max(1, _frames);
            double y = 0;
            void Lane(List<(int a, int b)> segs, Color col, bool on, UIElement badge)
            {
                var track = new Shapes.Rectangle { Width = w, Height = LaneH, RadiusX = 3, RadiusY = 3 };
                track.SetResourceReference(Shapes.Shape.FillProperty, "BgInput");
                Canvas.SetTop(track, y);
                LaneCanvas.Children.Add(track);
                var brush = Brush(col, on ? 0.95 : 0.25);
                foreach (var (a, b) in segs)
                {
                    var r = new Shapes.Rectangle { Width = Math.Max(2.5, (b - a + 1) * fx), Height = LaneH, RadiusX = 3, RadiusY = 3, Fill = brush };
                    Canvas.SetLeft(r, a * fx);
                    Canvas.SetTop(r, y);
                    LaneCanvas.Children.Add(r);
                }
                if (badge is FrameworkElement fe) { fe.Margin = new Thickness(0, 0, 0, LaneGap); LaneBadges.Children.Add(fe); }
                y += LaneH + LaneGap;
            }
            foreach (var p in main)
            {
                var badge = MakeBadge(p, 24, 10.5, new Thickness(0), HorizontalAlignment.Right, VerticalAlignment.Center);
                badge.Height = LaneH;
                badge.CornerRadius = new CornerRadius(LaneH / 2);
                _laneOwner.Add(p);
                Lane(CoverSegments(p.Cover), p.Col, IsOn(p), badge);
            }
            if (hasCrowd)
            {
                var star = new Border { Width = 24, Height = LaneH, CornerRadius = new CornerRadius(LaneH / 2), Background = Brush(CrowdColor),
                                        HorizontalAlignment = HorizontalAlignment.Right,
                                        ToolTip = "ბრბო / სხვა სახეები",
                                        Child = new TextBlock { Text = "∗", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Brushes.Black,
                                                                 HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                _laneOwner.Add(null);
                Lane(_crowdSegs, CrowdColor, _groupToggle?.IsChecked == true, star);
            }
            // playhead
            var head = new Shapes.Rectangle { Width = 2, Height = LaneCanvas.Height, Name = "Playhead", IsHitTestVisible = false };
            head.SetResourceReference(Shapes.Shape.FillProperty, "TextPrimary");
            Canvas.SetTop(head, 0);
            LaneCanvas.Children.Add(head);
            UpdatePlayhead();
        }

        private void UpdatePlayhead()
        {
            foreach (var ch in LaneCanvas.Children)
                if (ch is Shapes.Rectangle r && r.Name == "Playhead")
                {
                    double w = LaneCanvas.ActualWidth;
                    Canvas.SetLeft(r, Math.Clamp(SlFrame.Value / Math.Max(1, _frames) * w, 0, Math.Max(0, w - 2)));
                    return;
                }
        }

        private void Lanes_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawLanes();

        private void SeekFromLane(MouseEventArgs e)
        {
            double w = LaneCanvas.ActualWidth;
            if (w <= 0 || _input == null) return;
            double x = Math.Clamp(e.GetPosition(LaneCanvas).X, 0, w);
            SeekTo((int)Math.Round(x / w * Math.Max(1, _frames - 1)));
        }

        private void Lanes_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var pt = e.GetPosition(LaneCanvas);
            _edge = EdgeAt(pt);
            LaneCanvas.CaptureMouse();
            if (_edge == null) { _laneDrag = true; SeekFromLane(e); }
        }

        private void Lanes_MouseMove(object sender, MouseEventArgs e)
        {
            var pt = e.GetPosition(LaneCanvas);
            if (_edge != null)
            {
                // ghost between the old edge and the pointer: how the segment will change
                double fx = LaneCanvas.ActualWidth / Math.Max(1, _frames);
                int cur = FrameAtX(pt.X);
                double x0 = Math.Min(_edge.Orig, cur) * fx, x1 = Math.Max(_edge.Orig, cur) * fx;
                if (_ghost == null)
                {
                    _ghost = new Shapes.Rectangle { Height = LaneH, RadiusX = 3, RadiusY = 3, IsHitTestVisible = false,
                                                    Stroke = Brush(_edge.P.Col), StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 2, 2 },
                                                    Fill = Brush(_edge.P.Col, 0.35) };
                    Canvas.SetTop(_ghost, _edge.Lane * (LaneH + LaneGap));
                    LaneCanvas.Children.Add(_ghost);
                }
                _ghost.Width = Math.Max(1, x1 - x0);
                Canvas.SetLeft(_ghost, x0);
                SlFrame.Value = Math.Clamp(cur, 0, (int)SlFrame.Maximum);   // the player follows the edge: you see the exact frame
                return;
            }
            if (_laneDrag) { SeekFromLane(e); return; }
            LaneCanvas.Cursor = EdgeAt(pt) != null ? Cursors.SizeWE : Cursors.Hand;
        }

        private void Lanes_MouseUp(object sender, MouseButtonEventArgs e)
        {
            LaneCanvas.ReleaseMouseCapture();
            _laneDrag = false;
            var ed = _edge;
            _edge = null;
            if (ed == null) return;
            int cur = FrameAtX(e.GetPosition(LaneCanvas).X);
            if (cur != ed.Orig)
            {
                int hi = (int)Math.Max(0, _frames - 1);
                if (ed.Right)
                {
                    if (cur > ed.Orig) ed.P.Edits.Add((ed.Orig, Math.Min(hi, cur - 1), true));        // stretch to the right
                    else ed.P.Edits.Add((Math.Max(0, cur), ed.Orig - 1, false));                       // shorten
                }
                else
                {
                    if (cur < ed.Orig) ed.P.Edits.Add((Math.Max(0, cur), ed.Orig - 1, true));          // stretch to the left
                    else ed.P.Edits.Add((ed.Orig, Math.Min(hi, cur - 1), false));                       // shorten
                }
                RebuildCover(ed.P);
                RequestPreview();
            }
            RefreshVisuals();
        }

        /// <summary>Right click on a person's lane = forget the manual edits of that person.</summary>
        private void Lanes_RightUp(object sender, MouseButtonEventArgs e)
        {
            int lane = LaneAt(e.GetPosition(LaneCanvas).Y);
            if (lane < 0 || _laneOwner[lane] is not Person p || p.Edits.Count == 0) return;
            p.Edits.Clear();
            RebuildCover(p);
            RequestPreview();
            RefreshVisuals();
            Notify?.Invoke($"ადამიანი {p.No}: ხელით ცვლილებები გაუქმდა", "success");
        }
        private void Preview_SizeChanged(object sender, SizeChangedEventArgs e) => DrawOverlay();

        /// <summary>Coloured frame + number for every person on screen at the current frame.</summary>
        private void DrawOverlay()
        {
            PvOverlay.Children.Clear();
            if (_people.Count == 0 || _analysisPath == null || _input == null) return;
            double aw = PvOverlay.ActualWidth, ah = PvOverlay.ActualHeight;
            if (aw <= 0 || ah <= 0) return;
            double sc = Math.Min(aw / _vw, ah / _vh);
            double ox = (aw - _vw * sc) / 2, oy = (ah - _vh * sc) / 2;
            int frame = (int)SlFrame.Value;
            bool original = RbOriginal.IsChecked == true;

            foreach (var p in _people.Where(x => !x.Minor))
            {
                var b = BoxAt(p, frame);
                if (b == null) continue;
                bool on = IsOn(p);
                double x = ox + b[0] * sc, y = oy + b[1] * sc, w = b[2] * sc, h = b[3] * sc;
                if (original)
                {
                    var r = new Shapes.Rectangle
                    {
                        Width = Math.Max(4, w), Height = Math.Max(4, h), RadiusX = 8, RadiusY = 8,
                        Stroke = Brush(p.Col, on ? 1 : 0.55), StrokeThickness = 2.5,
                        StrokeDashArray = on ? null : new DoubleCollection { 3, 2 }
                    };
                    Canvas.SetLeft(r, x); Canvas.SetTop(r, y);
                    PvOverlay.Children.Add(r);
                }
                var badge = MakeBadge(p, 20, 11, new Thickness(0), HorizontalAlignment.Left, VerticalAlignment.Top);
                badge.Opacity = on ? 1 : 0.6;
                Canvas.SetLeft(badge, Math.Max(0, x - 4)); Canvas.SetTop(badge, Math.Max(0, y - 22));
                PvOverlay.Children.Add(badge);
            }
        }

        private bool AnyBlurTarget => _people.Any(p => p.Toggle?.IsChecked == true) || _groupToggle?.IsChecked == true;

        private void RefreshPeopleHint()
        {
            bool analyzed = _analysisPath != null;
            BtnAll.Visibility = BtnNone.Visibility = analyzed && _people.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtPeopleHint.Visibility = analyzed && _people.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            TxtPeopleHint.Text = _input == null
                ? "ჯერ აირჩიე ვიდეო."
                : analyzed
                    ? "ამ ვიდეოში სახე ვერ ვიპოვე. სცადე სხვა ვიდეო ან უფრო ახლო კადრი."
                    : "ახლა დააჭირე „სახეების ძებნა“ — აქ გამოჩნდება ვიდეოში ნანახი თითოეული ადამიანი. რომელსაც ჩართული აქვს ჩამრთველი, მას სახე დაუბუნდოვნდება.";
        }

        private void BtnAll_Click(object sender, RoutedEventArgs e) { foreach (var p in _people) if (p.Toggle != null) p.Toggle.IsChecked = true; if (_groupToggle != null) _groupToggle.IsChecked = true; RequestPreview(); }
        private void BtnNone_Click(object sender, RoutedEventArgs e) { foreach (var p in _people) if (p.Toggle != null) p.Toggle.IsChecked = false; if (_groupToggle != null) _groupToggle.IsChecked = false; RequestPreview(); }

        // ════════════════════════════════════════════════════════════
        // PREVIEW
        // ════════════════════════════════════════════════════════════
        private void SlFrame_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressSlider || !IsLoaded) return;
            TxtTime.Text = Fmt(e.NewValue / _fps);
            UpdatePlayhead(); DrawOverlay();
            RequestPreview();
        }

        private void SeekTo(int frame)
        {
            _suppressSlider = true;
            SlFrame.Value = Math.Clamp(frame, 0, (int)SlFrame.Maximum);
            _suppressSlider = false;
            TxtTime.Text = Fmt(SlFrame.Value / _fps);
            UpdatePlayhead(); DrawOverlay();
            RequestPreview();
        }

        private void RequestPreview()
        {
            if (_input == null) return;
            _debounce.Stop();
            _debounce.Start();
        }

        private void View_Changed(object sender, RoutedEventArgs e) { ShowCurrentPreview(); DrawOverlay(); }

        private void Style_Changed(object sender, RoutedEventArgs e) => RequestPreview();

        /// <summary>Another search accuracy → the existing result no longer matches: offer a new search (cached results are reused).</summary>
        private void Quality_Changed(object sender, RoutedEventArgs e)
        {
            if (_busy || _input == null) return;
            string cached = AnalysisFileFor(_input);
            _people.Clear();
            PeoplePanel.Children.Clear();
            _groupToggle = null;
            _analysisPath = null;
            _imgRes = null;
            _crowdSegs = new(); RefreshVisuals();
            BtnRender.Visibility = Visibility.Collapsed;
            ViewSwitch.Visibility = Visibility.Collapsed;
            BtnCompare.Visibility = BtnReveal.Visibility = Visibility.Collapsed;
            BtnAnalyze.Visibility = Visibility.Visible;
            BtnAnalyze.IsEnabled = true;
            RefreshPeopleHint();
            if (File.Exists(cached) && LoadAnalysis(cached)) { }
            SetStep();
            ShowCurrentPreview();
        }

        private void Param_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtStrength == null || TxtPad == null) return;
            TxtStrength.Text = $"{(int)SlStrength.Value}%";
            TxtPad.Text = $"{(int)SlPad.Value}%";
            if (IsLoaded) RequestPreview();
        }

        private void ShowCurrentPreview()
        {
            var img = (RbResult.IsChecked == true && _imgRes != null) ? _imgRes : _imgOrig;
            if (img != null) PvImage.Source = img;
        }

        private async Task UpdatePreviewAsync()
        {
            if (_input == null) return;
            int version = ++_previewVersion;
            int frame = (int)SlFrame.Value;
            double t = frame / _fps;
            string input = _input;
            string dir = WorkDir;
            string origPath = Path.Combine(dir, $"pv_orig_{version % 4}.jpg");
            string resPath = Path.Combine(dir, $"pv_res_{version % 4}.jpg");
            bool haveAnalysis = _analysisPath != null && _people.Count > 0;

            string optFile = Path.Combine(dir, "options_preview.json");
            if (haveAnalysis) File.WriteAllText(optFile, BuildOptions(), new UTF8Encoding(false));

            var origTask = RunFfmpegAsync(new[] { "-y", "-loglevel", "error", "-ss", t.ToString("0.###", CultureInfo.InvariantCulture),
                                                  "-i", input, "-frames:v", "1", "-vf", "scale=1280:-2", "-q:v", "3", origPath });
            Task<(int code, string lastStatus, bool success)>? resTask = null;
            if (haveAnalysis)
                resTask = RunScriptAsync(new[] { "preview", input, _analysisPath!, frame.ToString(), optFile, resPath });

            bool okOrig = await origTask;
            if (version != _previewVersion) return;
            if (okOrig) _imgOrig = LoadImage(origPath);

            if (resTask != null)
            {
                var r = await resTask;
                if (version != _previewVersion) return;
                _imgRes = r.success ? LoadImage(resPath) : null;
            }
            else _imgRes = null;

            ShowCurrentPreview();
        }

        private static BitmapImage? LoadImage(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var bytes = File.ReadAllBytes(path);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit(); bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        private string BuildOptions()
        {
            string style = StPixel.IsChecked == true ? "pixel" : StBlack.IsChecked == true ? "black" : "blur";
            bool groupOn = _groupToggle?.IsChecked == true;
            var ids = _people.Where(p => p.Minor ? groupOn : p.Toggle?.IsChecked == true).Select(p => p.Id).ToArray();
            return JsonSerializer.Serialize(new
            {
                style,
                strength = (int)SlStrength.Value,
                pad = (int)SlPad.Value,
                shape = ShRect.IsChecked == true ? "rect" : "ellipse",
                edits = _people.Where(x => !x.Minor && x.Edits.Count > 0)
                               .ToDictionary(x => x.Id.ToString(), x => x.Edits.Select(ed => new[] { ed.a, ed.b, ed.inc ? 1 : 0 }).ToArray()),
                ids
            });
        }

        // ════════════════════════════════════════════════════════════
        // RENDER
        // ════════════════════════════════════════════════════════════
        private async void BtnRender_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _input == null || _analysisPath == null) return;
            if (!AnyBlurTarget)
            {
                Notify?.Invoke("ჩართე ჩამრთველი ვისთვისმე — ვისაც უნდა დაუბუნდოვნდეს სახე", "error");
                return;
            }

            string input = _input;
            string dir = Path.GetDirectoryName(input)!;
            string name = Path.GetFileNameWithoutExtension(input);
            string output = Path.Combine(dir, $"{name}_blur_{DateTime.Now:HHmmss}.mp4");
            string optFile = Path.Combine(WorkDir, "options_render.json");
            File.WriteAllText(optFile, BuildOptions(), new UTF8Encoding(false));

            _cancelled = false;
            SetBusy(true);
            SetBusyVisual(true, "დაბუნდოვნება… 0%");
            TxtStatus.Text = "ვიდეო მუშავდება, ხმა უცვლელი რჩება";

            var res = await RunScriptAsync(new[] { "process", input, _analysisPath, output, optFile }, line =>
            {
                if (line.StartsWith("[PROGRESS]") && int.TryParse(line[10..].Trim(), out int pct))
                    Dispatcher.InvokeAsync(() => { Pb.Value = pct; TxtBusy.Text = $"დაბუნდოვნება… {pct}%"; });
            });

            SetBusy(false);
            SetBusyVisual(false, "");

            if (_cancelled)
            {
                try { if (File.Exists(output)) File.Delete(output); } catch { }
                TxtStatus.Text = "გაჩერდა";
                return;
            }
            if (!res.success || !File.Exists(output))
            {
                TxtStatus.Text = "ვერ მოხერხდა: " + res.lastStatus;
                Notify?.Invoke("დაბუნდოვნება ვერ მოხერხდა", "error");
                return;
            }

            _output = output;
            long size = new FileInfo(output).Length;
            TxtStatus.Text = $"მზადაა: {Path.GetFileName(output)} · {size / 1048576.0:0.#} MB";
            BtnCompare.Visibility = BtnReveal.Visibility = Visibility.Visible;
            Notify?.Invoke($"დაბუნდოვნებული ვიდეო მზადაა: {Path.GetFileName(output)}", "success");
            Processed?.Invoke(input, output);
            SetStep(done: true);
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            _cancelled = true;
            try { _proc?.Kill(true); } catch { }
        }

        private void BtnCompare_Click(object sender, RoutedEventArgs e)
        {
            if (_input != null && _output != null) OpenInAB?.Invoke(_input, _output);
        }

        private void BtnReveal_Click(object sender, RoutedEventArgs e)
        {
            if (_output == null || !File.Exists(_output)) return;
            try { Process.Start("explorer.exe", $"/select,\"{_output}\""); } catch { }
        }

        // ════════════════════════════════════════════════════════════
        // UI STATE
        // ════════════════════════════════════════════════════════════
        private void SetBusy(bool busy)
        {
            _busy = busy;
            BtnCancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (busy)
            {
                BtnAnalyze.Visibility = Visibility.Collapsed;
                BtnRender.Visibility = Visibility.Collapsed;
            }
            else
            {
                BtnAnalyze.Visibility = _analysisPath == null && _input != null ? Visibility.Visible : Visibility.Collapsed;
                BtnRender.Visibility = _analysisPath != null ? Visibility.Visible : Visibility.Collapsed;
            }
            BtnChange.IsEnabled = !busy;
        }

        private void SetBusyVisual(bool on, string text)
        {
            Busy.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            Pb.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            Pb.Value = 0;
            TxtBusy.Text = text;
        }

        private void SetStep(bool done = false)
        {
            TxtStep.Text = _input == null ? "1 · აირჩიე ვიდეო"
                : done ? "✓ მზადაა"
                : _analysisPath == null ? "2 · მოძებნე სახეები"
                : "3 · აირჩიე ვის დაუბუნდოვნო";
        }

        private static string Fmt(double sec)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, sec));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }
    }
}

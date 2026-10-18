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
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AiAssistant
{
    /// <summary>
    /// "AI ფონი": pick the video → say what stays (all people / chosen people / hand-marked things) → choose the new background → render.
    /// The stage shows the result, the original, the mask and a "what will change" highlight, so the principle is always visible.
    /// </summary>
    public partial class AiBackgroundView : UserControl
    {
        public event Action<string, string>? Notify;          // (message, kind)
        public event Action<string, string>? Processed;       // (input, output)
        public event Action<string, string>? OpenInAB;        // (original, result)
        public string CacheFolder { get; set; } = "";

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly string[] VideoExt = { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".mxf", ".m4v", ".ts" };

        private string _input = "";
        private double _dur, _fps = 25, _t;
        private int _frames, _vw = 1, _vh = 1;
        private bool _suppressTime;
        private string _bgFile = "";
        private string _lastOutput = "";

        public AiBackgroundView()
        {
            InitializeComponent();
            _previewTimer.Tick += async (_, _) => { _previewTimer.Stop(); await RunPreviewAsync(); };
            BuildSwatches();
            LoadPresets();
            SyncSubjectUi();
            PreviewKeyDown += OnKeys;
            Loaded += (_, _) => _ = CheckAiAsync();
        }

        // ═══════════════════════════════════════════════════════════
        // helpers
        // ═══════════════════════════════════════════════════════════
        private string WorkDir
        {
            get
            {
                string root = !string.IsNullOrEmpty(CacheFolder) ? CacheFolder : Path.Combine(AppPaths.DataDir, "cache");
                string d = Path.Combine(root, "aibg");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        private static string? Script(string name)
        {
            string b = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var c in new[] { Path.Combine(b, name), Path.GetFullPath(Path.Combine(b, @"..\..\..\" + name)),
                                      Path.GetFullPath(Path.Combine(b, @"..\..\..\..\" + name)) })
                if (File.Exists(c)) return c;
            return null;
        }

        private static ProcessStartInfo MakePy(string script, params string[] args)
        {
            var si = new ProcessStartInfo("python")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            if (!string.IsNullOrEmpty(script)) si.ArgumentList.Add(script);
            foreach (var a in args) si.ArgumentList.Add(a);
            si.Environment["PYTHONUTF8"] = "1";
            si.Environment["PYTHONIOENCODING"] = "utf-8";
            return si;
        }

        private static string FmtSec(double sec)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, sec));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }

        private static BitmapImage? LoadImage(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(File.ReadAllBytes(path));
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit(); bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        private void Toast(string msg, string kind = "success") => Notify?.Invoke(msg, kind);

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
                    dur = int.Parse(d.Groups[1].Value) * 3600 + int.Parse(d.Groups[2].Value) * 60 + double.Parse(d.Groups[3].Value, Inv);
                foreach (var line in err.Split('\n'))
                {
                    if (!line.Contains("Video:")) continue;
                    var r = Regex.Match(line, @",\s*(\d{2,5})x(\d{2,5})");
                    if (r.Success) { w = int.Parse(r.Groups[1].Value); h = int.Parse(r.Groups[2].Value); }
                    var f = Regex.Match(line, @"(\d+(?:\.\d+)?)\s*fps");
                    if (!f.Success) f = Regex.Match(line, @"(\d+(?:\.\d+)?)\s*tbr");
                    if (f.Success) fps = double.Parse(f.Groups[1].Value, Inv);
                    break;
                }
            }
            catch { }
            return (dur, fps > 0 ? fps : 25, w, h);
        }

        private static async Task<bool> RunFfmpegAsync(string[] args, int timeoutMs = 15000)
        {
            return await Task.Run(() =>
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
            });
        }

        // ═══════════════════════════════════════════════════════════
        // AI availability
        // ═══════════════════════════════════════════════════════════
        private async Task CheckAiAsync()
        {
            string level = "";
            await Task.Run(() =>
            {
                try
                {
                    var si = MakePy("", "-c", "import cv2, numpy\ntry:\n import onnxruntime; print('hq')\nexcept Exception:\n import mediapipe; print('std')");
                    using var p = Process.Start(si);
                    if (p == null) return;
                    string o = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (p.WaitForExit(40000) && p.ExitCode == 0) level = o.Trim();
                }
                catch { }
            });
            bool ok = level == "hq" || level == "std";
            TxtAi.Text = level == "hq" ? "AI მზადაა · მაღალი ხარისხი" : level == "std" ? "AI მზადაა · სტანდარტული" : "AI არ არის დაყენებული";
            DotAi.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, ok ? "Success" : "Danger");
            if (!ok) TxtStatus.Text = "AI-ისთვის საჭიროა Python — პარამეტრებიდან დააყენე „AI (ფონი, სახე)“.";
        }

        // ═══════════════════════════════════════════════════════════
        // source video
        // ═══════════════════════════════════════════════════════════
        private void BtnPick_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog { Title = "ვიდეოს არჩევა", Filter = "Video|*.mp4;*.mov;*.mxf;*.avi;*.mkv;*.webm|All|*.*" };
            if (ofd.ShowDialog() == true) _ = SetSourceAsync(ofd.FileName);
        }

        /// <summary>Other tabs can send a video here.</summary>
        public void LoadVideo(string path) => _ = SetSourceAsync(path);

        private async Task SetSourceAsync(string path)
        {
            if (!File.Exists(path) || _rendering) return;
            _input = path;
            _lastOutput = "";
            ResetSubjects();
            _off.Clear();
            DropZone.Visibility = Visibility.Collapsed;
            Toolbar.Visibility = TimelineBox.Visibility = Visibility.Visible;
            BtnRefresh.IsEnabled = BtnRender.IsEnabled = true;
            BtnCompare.Visibility = BtnReveal.Visibility = Visibility.Collapsed;
            ViewResult.IsChecked = true;
            PvImage.Source = null;
            TxtFile.Text = Path.GetFileName(path);
            TxtStatus.Text = "ვიდეო იტვირთება…";

            var info = await Task.Run(() => Probe(path));
            if (_input != path) return;
            _dur = Math.Max(0.1, info.dur); _fps = info.fps; _vw = Math.Max(1, info.w); _vh = Math.Max(1, info.h);
            _frames = Math.Max(1, (int)Math.Round(_dur * _fps));
            long size = new FileInfo(path).Length;
            TxtMeta.Text = $"{_vw}×{_vh} · {_fps:0.##} fps · {FmtSec(_dur)} · {size / 1048576.0:0.#} MB";
            TxtTotal.Text = FmtSec(_dur);

            _suppressTime = true;
            SlTime.Maximum = _dur;
            SlTime.Value = _dur > 1 ? _dur / 2 : 0;
            _suppressTime = false;
            _t = SlTime.Value;
            TxtTime.Text = FmtSec(_t);

            string cached = PeopleFileFor(path);
            if (File.Exists(cached)) LoadPeople(cached);
            RefreshVisuals();
            TxtStatus.Text = "აირჩიე რა დარჩეს კადრში და რით შევცვალოთ ფონი";
            await ShowFrameAsync();
            SchedulePreview();
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e) { if (!string.IsNullOrEmpty(_input)) _ = RunPreviewAsync(); }

        // ── drag & drop ──
        private static bool Dropped(DragEventArgs e, out string path)
        {
            path = "";
            if (e.Data.GetData(DataFormats.FileDrop) is string[] f && f.Length > 0 && VideoExt.Contains(Path.GetExtension(f[0]).ToLowerInvariant()))
            { path = f[0]; return true; }
            return false;
        }
        private void Drag_Enter(object sender, DragEventArgs e)
        {
            bool ok = !_rendering && Dropped(e, out _);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            DragOverlay.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
            e.Handled = true;
        }
        private void Drag_Leave(object sender, DragEventArgs e) => DragOverlay.Visibility = Visibility.Collapsed;
        private void Drag_Drop(object sender, DragEventArgs e)
        {
            DragOverlay.Visibility = Visibility.Collapsed;
            if (Dropped(e, out string p)) _ = SetSourceAsync(p);
            e.Handled = true;
        }

        // ═══════════════════════════════════════════════════════════
        // options: background, quality, refine, presets
        // ═══════════════════════════════════════════════════════════
        private static readonly string[] SwatchColors = { "#E5202E", "#15191F", "#0B2A5B", "#2F3A4A", "#F2F3F5", "#00B140" };

        private void BuildSwatches()
        {
            foreach (var hex in SwatchColors)
            {
                var b = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 8, 6), Cursor = Cursors.Hand,
                                     Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!), BorderThickness = new Thickness(1), Tag = hex,
                                     ToolTip = hex == "#00B140" ? "მწვანე (Chroma)" : hex };
                b.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");
                b.MouseLeftButtonDown += (s, _) => TxtColor.Text = (string)((Border)s).Tag;
                Swatches.Children.Add(b);
            }
        }

        private void BgMode_Changed(object sender, RoutedEventArgs e)
        {
            if (PanelBlur == null) return;
            PanelBlur.Visibility = BgBlur.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelColor.Visibility = BgColor.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            bool file = BgImage.IsChecked == true || BgVideo.IsChecked == true;
            PanelFile.Visibility = file ? Visibility.Visible : Visibility.Collapsed;
            TxtFileLabel.Text = BgImage.IsChecked == true ? "ფონის სურათი" : "ფონის ვიდეო (მეორდება ციკლურად)";
            if (file && !string.IsNullOrEmpty(_bgFile))
            {
                bool isImg = new[] { ".png", ".jpg", ".jpeg", ".bmp", ".webp" }.Contains(Path.GetExtension(_bgFile).ToLowerInvariant());
                if ((BgImage.IsChecked == true) != isImg) { _bgFile = ""; TxtBgFile.Text = "ფაილი არ არის არჩეული…"; }
            }
            SchedulePreview();
        }

        private void BtnPickBgFile_Click(object sender, RoutedEventArgs e)
        {
            bool img = BgImage.IsChecked == true;
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Title = img ? "ფონის სურათი" : "ფონის ვიდეო",
                Filter = img ? "Image|*.png;*.jpg;*.jpeg;*.bmp;*.webp|All|*.*" : "Video|*.mp4;*.mov;*.mkv;*.avi|All|*.*"
            };
            if (ofd.ShowDialog() != true) return;
            _bgFile = ofd.FileName;
            TxtBgFile.Text = Path.GetFileName(_bgFile);
            SchedulePreview();
        }

        private void TxtColor_Changed(object sender, TextChangedEventArgs e)
        {
            try
            {
                string hex = TxtColor.Text.Trim();
                if (!hex.StartsWith("#")) hex = "#" + hex;
                ColorSwatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
                SchedulePreview();
            }
            catch { }
        }

        private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SlFeather == null || TxtFeather == null) return;                 // still building
            TxtBlur.Text = $"{(int)SlBlur.Value}";
            TxtSolid.Text = $"{(int)SlSolid.Value}";
            TxtDefr.Text = $"{(int)SlDefr.Value}";
            TxtSmooth.Text = $"{(int)SlSmooth.Value}";
            TxtGrow.Text = $"{(int)SlGrow.Value:+0;-0;0}";
            TxtFeather.Text = $"{(int)SlFeather.Value}";
            SchedulePreview();
        }

        private void Option_Changed(object sender, RoutedEventArgs e) => SchedulePreview();

        private void BtnResetRefine_Click(object sender, RoutedEventArgs e)
        {
            SlSolid.Value = 60; SlDefr.Value = 60; SlSmooth.Value = 30; SlGrow.Value = 0; SlFeather.Value = 0;
            QBal.IsChecked = true;
            SchedulePreview();
        }

        private string Quality => QFast.IsChecked == true ? "fast" : QMax.IsChecked == true ? "max" : "balanced";

        private void BgSpec(out string mode, out string param)
        {
            if (BgBlur.IsChecked == true) { mode = "blur"; param = ((int)SlBlur.Value).ToString(); }
            else if (BgColor.IsChecked == true) { mode = "color"; param = TxtColor.Text.Trim(); }
            else { mode = BgImage.IsChecked == true ? "image" : "video"; param = _bgFile; }
        }

        private bool BgReady(out string why)
        {
            BgSpec(out string mode, out string param);
            why = "";
            if ((mode == "image" || mode == "video") && (string.IsNullOrEmpty(param) || !File.Exists(param)))
            { why = "ჯერ აირჩიე ფონის ფაილი"; return false; }
            return true;
        }

        // ── presets ──
        private sealed class BgPreset
        {
            public string Name { get; set; } = "";
            public string Mode { get; set; } = "blur";
            public string Param { get; set; } = "";
            public string? Quality { get; set; }
            public int? Solid { get; set; }
            public int? Defr { get; set; }
            public int? Smooth { get; set; }
            public int? Grow { get; set; }
            public int? Feather { get; set; }
        }
        private readonly List<BgPreset> _presets = new();
        private static string PresetFile => Path.Combine(AppPaths.DataDir, "vbg_presets.json");

        private void LoadPresets()
        {
            try
            {
                if (File.Exists(PresetFile))
                {
                    var l = JsonSerializer.Deserialize<List<BgPreset>>(File.ReadAllText(PresetFile));
                    if (l != null) { _presets.Clear(); _presets.AddRange(l); }
                }
            }
            catch { }
            RefreshPresetList();
        }

        private void RefreshPresetList()
        {
            LstPresets.Items.Clear();
            foreach (var p in _presets)
                LstPresets.Items.Add($"{p.Name}  ·  {(p.Mode switch { "blur" => "Blur", "color" => "ფერი", "image" => "სურათი", "video" => "ვიდეო", _ => p.Mode })}");
        }

        private void BtnSavePreset_Click(object sender, RoutedEventArgs e)
        {
            string name = TxtPresetName.Text.Trim();
            if (name.Length == 0) { Toast("ჩაწერე პრეზეტის სახელი", "error"); return; }
            BgSpec(out string mode, out string param);
            _presets.RemoveAll(p => p.Name == name);
            _presets.Insert(0, new BgPreset { Name = name, Mode = mode, Param = param, Quality = Quality, Solid = (int)SlSolid.Value, Defr = (int)SlDefr.Value,
                                              Smooth = (int)SlSmooth.Value, Grow = (int)SlGrow.Value, Feather = (int)SlFeather.Value });
            try { Directory.CreateDirectory(Path.GetDirectoryName(PresetFile)!); File.WriteAllText(PresetFile, JsonSerializer.Serialize(_presets)); } catch { }
            RefreshPresetList();
            TxtPresetName.Text = "";
            Toast($"პრეზეტი შენახულია: {name}");
        }

        private void LstPresets_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int i = LstPresets.SelectedIndex;
            if (i < 0 || i >= _presets.Count) return;
            var p = _presets[i];
            switch (p.Mode)
            {
                case "blur": BgBlur.IsChecked = true; if (int.TryParse(p.Param, out int bv)) SlBlur.Value = Math.Clamp(bv, 11, 71); break;
                case "color": BgColor.IsChecked = true; TxtColor.Text = p.Param; break;
                case "image": BgImage.IsChecked = true; _bgFile = p.Param; TxtBgFile.Text = Path.GetFileName(p.Param); break;
                case "video": BgVideo.IsChecked = true; _bgFile = p.Param; TxtBgFile.Text = Path.GetFileName(p.Param); break;
            }
            if (p.Quality == "fast") QFast.IsChecked = true; else if (p.Quality == "max") QMax.IsChecked = true; else if (p.Quality != null) QBal.IsChecked = true;
            if (p.Solid.HasValue) SlSolid.Value = p.Solid.Value;
            if (p.Defr.HasValue) SlDefr.Value = p.Defr.Value;
            if (p.Smooth.HasValue) SlSmooth.Value = p.Smooth.Value;
            if (p.Grow.HasValue) SlGrow.Value = p.Grow.Value;
            if (p.Feather.HasValue) SlFeather.Value = p.Feather.Value;
            BgMode_Changed(sender, e);
            LstPresets.SelectedIndex = -1;
        }

        // ── live camera ──
        private Process? _liveProc;
        private void BtnLive_Click(object sender, RoutedEventArgs e)
        {
            if (_liveProc != null && !_liveProc.HasExited)
            { try { _liveProc.Kill(true); } catch { } _liveProc = null; BtnLive.Content = "გაშვება"; return; }
            if (!BgReady(out string why)) { Toast(why, "error"); return; }
            string? script = Script("virtual_bg.py");
            if (script == null) { Toast("virtual_bg.py ვერ მოიძებნა", "error"); return; }
            BgSpec(out string mode, out string param);
            var si = MakePy(script, "webcam", mode, param, "", ChkInvert.IsChecked == true ? "1" : "0");
            si.RedirectStandardOutput = si.RedirectStandardError = false;
            si.StandardOutputEncoding = si.StandardErrorEncoding = null;
            try
            {
                var p = Process.Start(si);
                _liveProc = p;
                BtnLive.Content = "გათიშვა";
                if (p != null)
                {
                    p.EnableRaisingEvents = true;
                    p.Exited += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        BtnLive.Content = "გაშვება";
                        if (p.ExitCode != 0) Toast("კამერა ვერ გაიხსნა", "error");
                    }));
                }
            }
            catch (Exception ex) { Toast(ex.Message, "error"); }
        }

        // ── batch ──
        private readonly List<string> _batch = new();
        private void BtnAddBatch_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog { Title = "ვიდეოების დამატება", Multiselect = true, Filter = "Video|*.mp4;*.mov;*.mxf;*.avi;*.mkv|All|*.*" };
            if (ofd.ShowDialog() != true) return;
            foreach (var f in ofd.FileNames) if (!_batch.Contains(f)) _batch.Add(f);
            RefreshBatch();
        }

        private void RefreshBatch()
        {
            BatchPanel.Children.Clear();
            BtnBatch.IsEnabled = _batch.Count > 0;
            BtnBatch.Content = _batch.Count > 0 ? $"ყველას დამუშავება ({_batch.Count})" : "ყველას დამუშავება";
            foreach (var path in _batch.ToList())
            {
                var row = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 6, 6, 6), Margin = new Thickness(0, 0, 0, 6) };
                row.SetResourceReference(Border.BackgroundProperty, "BgInput");
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var name = new TextBlock { Text = Path.GetFileName(path), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
                var rm = new Button { Content = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10, Padding = new Thickness(8, 7, 8, 7), ToolTip = "სიიდან ამოღება" };
                rm.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");
                string captured = path;
                rm.Click += (_, _) => { _batch.Remove(captured); RefreshBatch(); };
                Grid.SetColumn(rm, 1);
                g.Children.Add(name); g.Children.Add(rm);
                row.Child = g;
                BatchPanel.Children.Add(row);
            }
        }

        // ═══════════════════════════════════════════════════════════
        // project file for bg_studio.py
        // ═══════════════════════════════════════════════════════════
        private Dictionary<string, object?> BuildProject(string input, string output, bool withSubjects, bool withOff, string? frameOut = null)
        {
            BgSpec(out string mode, out string param);
            var proj = new Dictionary<string, object?>
            {
                ["input"] = input,
                ["output"] = output,
                ["background"] = new Dictionary<string, object> { ["mode"] = mode, ["param"] = param },
                ["invert"] = ChkInvert.IsChecked == true,
                ["quality"] = Quality,
                ["refine"] = new Dictionary<string, object>
                {
                    ["solidify"] = (int)SlSolid.Value, ["defringe"] = (int)SlDefr.Value, ["smooth"] = (int)SlSmooth.Value,
                    ["grow"] = (int)SlGrow.Value, ["feather"] = (int)SlFeather.Value
                },
                ["off"] = withOff ? _off.Select(r => new[] { Math.Round(r.a, 3), Math.Round(r.b, 3) }).ToArray() : Array.Empty<double[]>(),
                ["mode"] = withSubjects && ModeSel.IsChecked == true ? "selected" : "all",
                ["people"] = withSubjects && ModeSel.IsChecked == true ? PeopleOption() : null,
                ["marks"] = withSubjects ? MarksOption() : Array.Empty<object>(),
                ["frame_out"] = frameOut
            };
            return proj;
        }

        private string WriteProject(Dictionary<string, object?> proj, string name)
        {
            string path = Path.Combine(WorkDir, name);
            File.WriteAllText(path, JsonSerializer.Serialize(proj), new UTF8Encoding(false));
            return path;
        }

        // ═══════════════════════════════════════════════════════════
        // stage: original frame + processed preview
        // ═══════════════════════════════════════════════════════════
        private BitmapImage? _imgOrig, _imgPv;
        private int _pvVersion;
        private bool _pvRunning, _pvDirty;
        private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(520) };
        private string _viewKind = "result";

        private string CurrentView => ViewOriginal.IsChecked == true ? "original" : ViewHighlight.IsChecked == true ? "highlight"
                                      : ViewMatte.IsChecked == true ? "matte" : "result";

        private void View_Changed(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_input)) return;
            if (CurrentView == "original" && _imgOrig != null) PvImage.Source = _imgOrig;
            else SchedulePreview(100);
            DrawOverlay();
        }

        private async Task ShowFrameAsync()
        {
            if (string.IsNullOrEmpty(_input)) return;
            string path = Path.Combine(WorkDir, $"orig_{Guid.NewGuid():N}.jpg");
            bool ok = await RunFfmpegAsync(new[] { "-y", "-loglevel", "error", "-ss", Math.Max(0, _t).ToString("F3", Inv), "-i", _input,
                                                   "-frames:v", "1", "-vf", "scale='min(1600,iw)':-2", "-q:v", "3", path });
            if (!ok) return;
            _imgOrig = LoadImage(path);
            try { File.Delete(path); } catch { }
            if (_imgOrig != null && (PvImage.Source == null || CurrentView == "original")) PvImage.Source = _imgOrig;
        }

        private void SchedulePreview(int ms = 520)
        {
            if (string.IsNullOrEmpty(_input)) return;
            _previewTimer.Interval = TimeSpan.FromMilliseconds(ms);
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        private void SetBusy(bool on, string text = "")
        {
            BusyChip.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            TxtBusy.Text = text;
            if (on) BusyRot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
            else BusyRot.BeginAnimation(RotateTransform.AngleProperty, null);
        }

        private async Task RunPreviewAsync()
        {
            if (string.IsNullOrEmpty(_input) || !File.Exists(_input)) return;
            if (CurrentView == "original") { if (_imgOrig != null) PvImage.Source = _imgOrig; return; }
            if (_pvRunning) { _pvDirty = true; return; }
            if (!BgReady(out string why)) { TxtStatus.Text = why; return; }
            string? script = Script("bg_studio.py");
            if (script == null) { TxtStatus.Text = "bg_studio.py ვერ მოიძებნა"; return; }

            _pvRunning = true; _pvDirty = false;
            int version = ++_pvVersion;
            string view = CurrentView;
            double t = _t;
            string proj = WriteProject(BuildProject(_input, "", true, true), "project_preview.json");
            string outJpg = Path.Combine(WorkDir, $"pv_{Guid.NewGuid():N}.jpg");
            SetBusy(true, "AI ანალიზი…");
            bool ok = false; string err = "";

            await Task.Run(() =>
            {
                try
                {
                    var si = MakePy(script, "preview", proj, outJpg, view, t.ToString("F3", Inv));
                    using var p = Process.Start(si)!;
                    var eTask = p.StandardError.ReadToEndAsync();
                    var oTask = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(180000)) { try { p.Kill(true); } catch { } }
                    string o = oTask.Result;
                    ok = File.Exists(outJpg) && o.Contains("[SUCCESS]");
                    if (!ok)
                    {
                        var st = o.Split('\n').Where(l => l.StartsWith("[STATUS]")).Select(l => l[8..].Trim());
                        err = string.Join("\n", st);
                        if (string.IsNullOrWhiteSpace(err)) { var le = eTask.Result.Trim().Split('\n'); err = le.Length > 0 ? le[^1].Trim() : ""; }
                    }
                }
                catch (System.ComponentModel.Win32Exception) { err = "'python' ვერ გაეშვა — დარწმუნდი, რომ Python დაყენებულია"; }
                catch (Exception ex) { err = ex.Message; }
            });

            SetBusy(false);
            _pvRunning = false;
            if (version == _pvVersion && CurrentView == view)
            {
                if (ok)
                {
                    _imgPv = LoadImage(outJpg);
                    if (_imgPv != null) PvImage.Source = _imgPv;
                    TxtStatus.Text = view == "highlight" ? "წითლად ჩანს ის, რაც ახალ ფონზე გადავა"
                                   : view == "matte" ? "თეთრი რჩება, შავი იცვლება"
                                   : "გადახედვა მზადაა — თუ მოგწონს, დააჭირე „ფონის შეცვლა“";
                }
                else TxtStatus.Text = string.IsNullOrWhiteSpace(err) ? "გადახედვა ვერ შედგა." : err;
            }
            try { File.Delete(outJpg); } catch { }
            if (_pvDirty) SchedulePreview(150);
        }

        // ═══════════════════════════════════════════════════════════
        // render (+ the animated progress layer)
        // ═══════════════════════════════════════════════════════════
        private bool _rendering;
        private Process? _renderProc;
        private bool _renderCancelled;
        private string _renderOutput = "";

        private void StartProgressAnimations()
        {
            ArcRot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.3)) { RepeatBehavior = RepeatBehavior.Forever });
            var sh = new DoubleAnimation(-90, 700, TimeSpan.FromSeconds(1.7)) { RepeatBehavior = RepeatBehavior.Forever };
            ShimmerMove.BeginAnimation(TranslateTransform.XProperty, sh);
        }

        private void StopProgressAnimations()
        {
            ArcRot.BeginAnimation(RotateTransform.AngleProperty, null);
            ShimmerMove.BeginAnimation(TranslateTransform.XProperty, null);
        }

        private void SetProgress(int pct, string eta = "")
        {
            TxtPctBig.Text = $"{pct}%";
            if (BarFill.Parent is FrameworkElement host && host.ActualWidth > 0)
                BarFill.BeginAnimation(WidthProperty, new DoubleAnimation(host.ActualWidth * pct / 100.0, TimeSpan.FromMilliseconds(380))
                                       { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            TxtPEta.Text = eta;
        }

        private void ShowProgressLayer(string title, string sub)
        {
            ProgressLayer.Visibility = Visibility.Visible;
            ProgressLayer.Opacity = 0;
            ProgressLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)));
            TxtPTitle.Text = title; TxtPSub.Text = sub; TxtPLeft.Text = "იწყება…";
            TxtDoneMark.Visibility = Visibility.Collapsed; TxtPctBig.Visibility = Visibility.Visible; SpinArc.Visibility = Visibility.Visible;
            BtnPCancel.Visibility = Visibility.Visible;
            BtnPClose.Visibility = BtnPCompare.Visibility = BtnPReveal.Visibility = Visibility.Collapsed;
            LiveImg.Source = null;
            BarFill.BeginAnimation(WidthProperty, null); BarFill.Width = 0;
            SetProgress(0);
            StartProgressAnimations();
        }

        private void FinishProgressLayer(bool ok, string message)
        {
            StopProgressAnimations();
            BtnPCancel.Visibility = Visibility.Collapsed;
            BtnPClose.Visibility = Visibility.Visible;
            SpinArc.Visibility = Visibility.Collapsed;
            if (ok)
            {
                SetProgress(100);
                TxtPctBig.Visibility = Visibility.Collapsed; TxtDoneMark.Visibility = Visibility.Visible;
                TxtPTitle.Text = "მზადაა!"; TxtPSub.Text = message; TxtPLeft.Text = "დასრულდა"; TxtPEta.Text = "";
                BtnPCompare.Visibility = BtnPReveal.Visibility = Visibility.Visible;
                var pop = new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 } };
                var sc = new ScaleTransform(1, 1);
                TxtDoneMark.RenderTransformOrigin = new Point(0.5, 0.5);
                TxtDoneMark.RenderTransform = sc;
                sc.BeginAnimation(ScaleTransform.ScaleXProperty, pop); sc.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            }
            else
            {
                TxtPctBig.Visibility = Visibility.Collapsed; TxtDoneMark.Text = ""; TxtDoneMark.Foreground = (Brush)FindResource("Danger");
                TxtDoneMark.Visibility = Visibility.Visible;
                TxtPTitle.Text = "ვერ მოხერხდა"; TxtPSub.Text = message; TxtPLeft.Text = ""; TxtPEta.Text = "";
            }
        }

        private void BtnPClose_Click(object sender, RoutedEventArgs e)
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
            fade.Completed += (_, _) => { ProgressLayer.Visibility = Visibility.Collapsed; TxtDoneMark.Text = ""; TxtDoneMark.SetResourceReference(TextBlock.ForegroundProperty, "Success"); };
            ProgressLayer.BeginAnimation(OpacityProperty, fade);
        }

        private void BtnPCancel_Click(object sender, RoutedEventArgs e)
        {
            _renderCancelled = true;
            try { _renderProc?.Kill(true); } catch { }
        }

        private async void BtnRender_Click(object sender, RoutedEventArgs e)
        {
            if (_rendering || string.IsNullOrEmpty(_input) || !File.Exists(_input)) return;
            if (!BgReady(out string why)) { Toast(why, "error"); return; }
            string dir = Path.GetDirectoryName(_input) ?? "";
            string output = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(_input)}_virtualbg_{DateTime.Now:HHmmss}.mp4");
            ShowProgressLayer("ფონი იცვლება", "AI კადრ-კადრ ამოჭრის და ახალ ფონზე აწყობს");
            bool ok = await RenderOneAsync(_input, output, true, null);
            if (_renderCancelled) { try { if (File.Exists(output)) File.Delete(output); } catch { } ProgressLayer.Visibility = Visibility.Collapsed; TxtStatus.Text = "გაჩერდა"; return; }
            if (ok && File.Exists(output))
            {
                _lastOutput = output;
                long size = new FileInfo(output).Length;
                FinishProgressLayer(true, $"{Path.GetFileName(output)} · {size / 1048576.0:0.#} MB");
                BtnCompare.Visibility = BtnReveal.Visibility = Visibility.Visible;
                TxtStatus.Text = $"დასრულდა: {Path.GetFileName(output)}";
                Toast($"ვირტუალური ფონი დასრულდა: {Path.GetFileName(output)}");
                Processed?.Invoke(_input, output);
            }
            else FinishProgressLayer(false, string.IsNullOrWhiteSpace(_renderError) ? "დამუშავება ვერ შედგა." : _renderError);
        }

        private string _renderError = "";

        /// <summary>One video through bg_studio.py render, streaming progress into the animated layer.</summary>
        private async Task<bool> RenderOneAsync(string input, string output, bool withSubjects, string? batchLabel)
        {
            string? script = Script("bg_studio.py");
            if (script == null) { _renderError = "bg_studio.py ვერ მოიძებნა"; return false; }
            _rendering = true; _renderCancelled = false; _renderError = ""; _renderOutput = output;
            string live = Path.Combine(WorkDir, $"live_{Guid.NewGuid():N}.jpg");
            string proj = WriteProject(BuildProject(input, output, withSubjects, withSubjects, live), $"project_render_{Guid.NewGuid():N}.json");
            BtnRender.IsEnabled = false;
            bool ok = false;
            var err = new StringBuilder();

            await Task.Run(() =>
            {
                try
                {
                    var si = MakePy(script, "render", proj);
                    using var p = new Process { StartInfo = si };
                    p.OutputDataReceived += (_, ev) =>
                    {
                        if (ev.Data == null) return;
                        string line = ev.Data;
                        if (line.StartsWith("[PROGRESS]") && int.TryParse(line[10..].Trim(), out int pct))
                            Dispatcher.InvokeAsync(() => { SetProgress(pct); TxtPLeft.Text = batchLabel ?? "კადრები მუშავდება…"; });
                        else if (line.StartsWith("[ETA]") && int.TryParse(line[5..].Trim(), out int eta))
                            Dispatcher.InvokeAsync(() => TxtPEta.Text = eta > 0 ? $"დარჩა დაახლოებით {FmtSec(eta)}" : "");
                        else if (line.StartsWith("[FRAME]"))
                            Dispatcher.InvokeAsync(() => { var im = LoadImage(live); if (im != null) LiveImg.Source = im; });
                        else if (line.StartsWith("[STATUS]"))
                        { string st = line[8..].Trim(); err.AppendLine(st); Dispatcher.InvokeAsync(() => TxtPLeft.Text = st); }
                        else if (line.StartsWith("[SUCCESS]")) ok = true;
                    };
                    p.ErrorDataReceived += (_, ev) => { };
                    _renderProc = p;
                    p.Start();
                    p.BeginOutputReadLine(); p.BeginErrorReadLine();
                    p.WaitForExit();
                    _renderProc = null;
                    ok = ok && p.ExitCode == 0;
                }
                catch (System.ComponentModel.Win32Exception) { err.AppendLine("'python' ვერ გაეშვა — დარწმუნდი, რომ Python დაყენებულია"); }
                catch (Exception ex) { err.AppendLine(ex.Message); }
            });

            _rendering = false;
            BtnRender.IsEnabled = true;
            _renderError = err.ToString().Trim();
            try { File.Delete(live); } catch { }
            return ok;
        }

        private async void BtnBatch_Click(object sender, RoutedEventArgs e)
        {
            if (_batch.Count == 0 || _rendering) return;
            if (!BgReady(out string why)) { Toast(why, "error"); return; }
            var files = _batch.ToList();
            ShowProgressLayer("ვიდეოები მუშავდება", $"{files.Count} ფაილი ერთი და იგივე პარამეტრებით");
            int done = 0;
            for (int i = 0; i < files.Count; i++)
            {
                string input = files[i];
                string output = Path.Combine(Path.GetDirectoryName(input) ?? "", $"{Path.GetFileNameWithoutExtension(input)}_vbg_{DateTime.Now:HHmmss}.mp4");
                TxtPTitle.Text = $"ვიდეო {i + 1} / {files.Count}";
                TxtPSub.Text = Path.GetFileName(input);
                BarFill.BeginAnimation(WidthProperty, null); BarFill.Width = 0;
                bool ok = await RenderOneAsync(input, output, false, $"{i + 1} / {files.Count}");
                if (_renderCancelled) { try { if (File.Exists(output)) File.Delete(output); } catch { } break; }
                if (ok && File.Exists(output)) { done++; Processed?.Invoke(input, output); }
            }
            FinishProgressLayer(done == files.Count && !_renderCancelled, $"დამუშავდა {done} / {files.Count} ვიდეო");
            Toast($"Batch დასრულდა: {done}/{files.Count} ფაილი", done == files.Count ? "success" : "error");
        }

        private void BtnCompare_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_input) && File.Exists(_lastOutput)) OpenInAB?.Invoke(_input, _lastOutput);
        }

        private void BtnReveal_Click(object sender, RoutedEventArgs e)
        {
            string p = File.Exists(_lastOutput) ? _lastOutput : _renderOutput;
            if (File.Exists(p)) try { Process.Start("explorer.exe", $"/select,\"{p}\""); } catch { }
        }
    }
}

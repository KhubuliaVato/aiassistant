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
            public string Thumb = "";
            public CheckBox? Toggle;
        }

        private string? _input;
        private string? _output;
        private double _fps = 25, _dur;
        private int _frames;
        private string? _analysisPath;
        private readonly List<Person> _people = new();
        private bool _busy;
        private Process? _proc;
        private bool _cancelled;

        private BitmapImage? _imgOrig, _imgRes;
        private int _previewVersion;
        private bool _suppressSlider;
        private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(380) };

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
            string key = path.ToLowerInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
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

            var res = await RunScriptAsync(new[] { "analyze", input, outJson }, line =>
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
                foreach (var t in root.GetProperty("tracks").EnumerateArray())
                {
                    people.Add(new Person
                    {
                        Id = t.GetProperty("id").GetInt32(),
                        First = t.GetProperty("first").GetInt32(),
                        Last = t.GetProperty("last").GetInt32(),
                        Count = t.GetProperty("count").GetInt32(),
                        BestFrame = t.TryGetProperty("best_frame", out var bf) ? bf.GetInt32() : t.GetProperty("first").GetInt32(),
                        Thumb = t.TryGetProperty("thumb", out var th) ? th.GetString() ?? "" : ""
                    });
                }
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
                    : $"ნაპოვნია {_people.Count} ადამიანი · გადაახვიე და შეამოწმე, მერე დააჭირე „დაბუნდოვნება“";
                return true;
            }
            catch { return false; }
        }

        private void BuildPeopleUi()
        {
            PeoplePanel.Children.Clear();
            foreach (var p in _people)
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
                                            Margin = new Thickness(0, 0, 12, 0), Cursor = Cursors.Hand,
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
                grid.Children.Add(thumbBox);

                var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var name = new TextBlock { Text = $"ადამიანი {p.Id}", FontSize = 13.5, FontWeight = FontWeights.SemiBold };
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
                toggle.Click += (_, _) => RequestPreview();
                p.Toggle = toggle;
                Grid.SetColumn(toggle, 2);
                grid.Children.Add(toggle);

                card.Child = grid;
                PeoplePanel.Children.Add(card);
            }
        }

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

        private void BtnAll_Click(object sender, RoutedEventArgs e) { foreach (var p in _people) if (p.Toggle != null) p.Toggle.IsChecked = true; RequestPreview(); }
        private void BtnNone_Click(object sender, RoutedEventArgs e) { foreach (var p in _people) if (p.Toggle != null) p.Toggle.IsChecked = false; RequestPreview(); }

        // ════════════════════════════════════════════════════════════
        // PREVIEW
        // ════════════════════════════════════════════════════════════
        private void SlFrame_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressSlider || !IsLoaded) return;
            TxtTime.Text = Fmt(e.NewValue / _fps);
            RequestPreview();
        }

        private void SeekTo(int frame)
        {
            _suppressSlider = true;
            SlFrame.Value = Math.Clamp(frame, 0, (int)SlFrame.Maximum);
            _suppressSlider = false;
            TxtTime.Text = Fmt(SlFrame.Value / _fps);
            RequestPreview();
        }

        private void RequestPreview()
        {
            if (_input == null) return;
            _debounce.Stop();
            _debounce.Start();
        }

        private void View_Changed(object sender, RoutedEventArgs e) => ShowCurrentPreview();

        private void Style_Changed(object sender, RoutedEventArgs e) => RequestPreview();

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
            var ids = _people.Where(p => p.Toggle?.IsChecked == true).Select(p => p.Id).ToArray();
            return JsonSerializer.Serialize(new
            {
                style,
                strength = (int)SlStrength.Value,
                pad = (int)SlPad.Value,
                shape = ShRect.IsChecked == true ? "rect" : "ellipse",
                ids
            });
        }

        // ════════════════════════════════════════════════════════════
        // RENDER
        // ════════════════════════════════════════════════════════════
        private async void BtnRender_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _input == null || _analysisPath == null) return;
            if (!_people.Any(p => p.Toggle?.IsChecked == true))
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

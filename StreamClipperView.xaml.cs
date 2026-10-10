using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AiAssistant
{
    public partial class StreamClipperView : UserControl
    {
        public event Action<string>?        SendToEditor;
        public event Action<string>?        SendToCrop;
        public event Action<string,string>? Notify;

        // ── recording session ─────────────────────────────────────────
        // The stream is written by ffmpeg as small rolling .ts segments (seg_000123.ts) + a csv list with their
        // real durations. Old segments are deleted, so the buffer never grows beyond the chosen window and a
        // clip only has to touch the last few segments instead of copying one huge file.
        private Process?  _ffmpegProc;
        private string    _sessionDir = "";
        private string    _csvPath = "";
        private bool      _hasSession;        // a buffer exists (live or dropped)
        private bool      _procAlive;
        private bool      _stopRequested;
        private DateTime  _recStart;
        private TimeSpan  _elapsedFrozen;
        private string    _streamTitle = "";
        private readonly HashSet<string> _deleted = new(StringComparer.OrdinalIgnoreCase);

        private int       _clipSec = 30;
        private int       _bufferMin = 10;
        private string    _quality = "best";
        private DateTime? _markAt;
        private bool      _clipBusy;
        private bool      _previewBusy;
        private DateTime  _lastPreview = DateTime.MinValue;
        private double    _availableSec;
        private string    _lastError = "";

        private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromSeconds(1) };

        // ── output folder / persisted settings ────────────────────────
        public string OutputFolder { get; set; } = "";
        public string CurrentClipFolder => ClipFolder;

        public void SetOutputFolder(string folder)
        {
            OutputFolder = folder;
            UpdateFolderText();
            SaveSettings();
        }
        private string ClipFolder =>
            string.IsNullOrEmpty(OutputFolder)
                ? AppPaths.VideosFolder("1Flow Live Clips", "FlowSocial Live Clips")
                : OutputFolder;

        private static string SettingsFile =>
            Path.Combine(AppPaths.DataDir, "clipper_settings.json");

        public StreamClipperView()
        {
            InitializeComponent();
            LoadSettings();
            UpdateFolderText();
            RefreshUi();

            _uiTimer.Tick += UiTimer_Tick;
            _uiTimer.Start();

            Loaded += (_, _) =>
            {
                if (Window.GetWindow(this) is Window w)
                {
                    w.PreviewKeyDown -= Win_PreviewKeyDown;
                    w.PreviewKeyDown += Win_PreviewKeyDown;
                }
            };

            // never leave ffmpeg running (or the buffer on disk) when the app closes
            Dispatcher.ShutdownStarted += (_, _) =>
            {
                _stopRequested = true;
                try { _ffmpegProc?.Kill(entireProcessTree: true); } catch { }
                try { if (Directory.Exists(_sessionDir)) Directory.Delete(_sessionDir, true); } catch { }
            };
        }

        // ════════════════════════════════════════════════════════════
        // SETTINGS
        // ════════════════════════════════════════════════════════════
        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return;
                using var doc = JsonDocument.Parse(File.ReadAllText(SettingsFile));
                var r = doc.RootElement;
                if (r.TryGetProperty("Folder", out var f)) OutputFolder = f.GetString() ?? "";
                if (r.TryGetProperty("Quality", out var q)) _quality = q.GetString() ?? "best";
                if (r.TryGetProperty("BufferMin", out var b)) _bufferMin = b.GetInt32();
                if (r.TryGetProperty("ClipSec", out var c)) _clipSec = c.GetInt32();
                if (r.TryGetProperty("AutoVertical", out var av)) ChkAutoVertical.IsChecked = av.GetBoolean();
                if (r.TryGetProperty("AutoEditor", out var ae)) ChkAutoEditor.IsChecked = ae.GetBoolean();
                if (r.TryGetProperty("Url", out var u)) TxtStreamUrl.Text = u.GetString() ?? "";
            }
            catch { }

            foreach (var rb in Radios())
            {
                string tag = rb.Tag?.ToString() ?? "";
                if (rb.GroupName == "LiveQ") rb.IsChecked = tag == _quality;
                else if (rb.GroupName == "LiveBuf") rb.IsChecked = tag == _bufferMin.ToString();
                else if (rb.GroupName == "ClipDur") rb.IsChecked = tag == _clipSec.ToString();
            }
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
                File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new
                {
                    Folder = OutputFolder, Quality = _quality, BufferMin = _bufferMin, ClipSec = _clipSec,
                    AutoVertical = ChkAutoVertical.IsChecked == true, AutoEditor = ChkAutoEditor.IsChecked == true,
                    Url = TxtStreamUrl.Text.Trim()
                }));
            }
            catch { }
        }

        private IEnumerable<RadioButton> Radios()
        {
            var stack = new Stack<DependencyObject>();
            stack.Push(this);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
                {
                    var c = VisualTreeHelper.GetChild(d, i);
                    if (c is RadioButton rb) yield return rb;
                    stack.Push(c);
                }
            }
        }

        private void Dur_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && int.TryParse(rb.Tag?.ToString(), out int s)) { _clipSec = s; RefreshUi(); SaveSettings(); }
        }

        private void Buffer_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && int.TryParse(rb.Tag?.ToString(), out int m)) { _bufferMin = m; SaveSettings(); }
        }

        private void Quality_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb) { _quality = rb.Tag?.ToString() ?? "best"; SaveSettings(); }
        }

        private void Option_Changed(object sender, RoutedEventArgs e) => SaveSettings();

        private void UpdateFolderText() => TxtClipFolder.Text = ClipFolder;

        private void BtnChooseFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "საქაღალდე კლიპებისთვის", InitialDirectory = ClipFolder };
            if (dlg.ShowDialog() != true) return;
            OutputFolder = dlg.FolderName;
            UpdateFolderText();
            SaveSettings();
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try { Directory.CreateDirectory(ClipFolder); Process.Start("explorer.exe", ClipFolder); } catch { }
        }

        private void BtnPasteUrl_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string t = Clipboard.GetText().Trim();
                if (t.Length > 0) TxtStreamUrl.Text = t.Split('\n')[0].Trim();
            }
            catch { }
        }

        // ════════════════════════════════════════════════════════════
        // UI STATE
        // ════════════════════════════════════════════════════════════
        private void RefreshUi()
        {
            bool live = _hasSession && _procAlive;
            bool dropped = _hasSession && !_procAlive;

            BtnStartMonitor.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
            BtnStopMonitor.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
            TxtStreamUrl.IsEnabled = !live;

            LiveBadge.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
            TitleBadge.Visibility = _hasSession && _streamTitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtStreamTitle.Text = _streamTitle;
            PlaceholderLive.Visibility = _hasSession && ImgLive.Source != null ? Visibility.Collapsed : Visibility.Visible;

            if (live)
            {
                DotLive.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Danger");
                TxtLiveChip.Text = "იწერება";
            }
            else if (dropped)
            {
                DotLive.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Warning");
                TxtLiveChip.Text = "შეწყდა";
            }
            else
            {
                DotLive.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextMuted");
                TxtLiveChip.Text = "გამორთულია";
            }

            string lbl = _clipSec >= 60 && _clipSec % 60 == 0 ? $"{_clipSec / 60} წუთი" : $"{_clipSec} წამი";
            TxtClipLabel.Text = _markAt.HasValue ? "კლიპი მონიშვნიდან ახლამდე" : $"კლიპი — ბოლო {lbl}";
            TxtMarkLabel.Text = _markAt.HasValue ? "მონიშვნის გაუქმება" : "მონიშვნა";

            bool canClip = _hasSession && _availableSec >= 4 && !_clipBusy;
            BtnClip.IsEnabled = canClip;
            BtnMark.IsEnabled = _hasSession && _procAlive;
            MarkChip.Visibility = _markAt.HasValue ? Visibility.Visible : Visibility.Collapsed;

        }

        private void SetPlaceholder(string title, string sub)
        {
            PlaceholderLive.Visibility = Visibility.Visible;
            ImgLive.Source = null;
            TxtPlaceholderTitle.Text = title;
            TxtPlaceholderSub.Text = sub;
        }

        // ════════════════════════════════════════════════════════════
        // START
        // ════════════════════════════════════════════════════════════
        private void TxtStreamUrl_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) BtnStartMonitor_Click(sender, new RoutedEventArgs());
        }

        private async void BtnStartMonitor_Click(object sender, RoutedEventArgs e)
        {
            string url = TxtStreamUrl.Text.Trim();
            if (url.Length == 0) { TxtStreamUrl.Focus(); return; }
            if (!url.Contains("://")) url = "https://" + url;

            // restarting after a dropped connection: discard the old buffer first
            if (_hasSession) DiscardSession();

            BtnStartMonitor.IsEnabled = false;
            SetPlaceholder("ლინკი მოწმდება…", "stream-ის მისამართი იძებნება, ერთი წამი…");
            TxtLiveChip.Text = "მოწმდება";

            string quality = _quality;
            var res = await Task.Run(() => ResolveStreamUrl(url, quality));
            BtnStartMonitor.IsEnabled = true;

            if (string.IsNullOrEmpty(res.url))
            {
                SetPlaceholder("ვერ მოიძებნა", FriendlyError(res.err));
                TxtLiveChip.Text = "გამორთულია";
                return;
            }

            string root = Path.Combine(Path.GetTempPath(), "1FlowStream");
            CleanOldSessions(root);
            _sessionDir = Path.Combine(root, "sess_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(_sessionDir);
            _csvPath = Path.Combine(_sessionDir, "segments.csv");
            _deleted.Clear();

            var si = new ProcessStartInfo(AppPaths.Ffmpeg())
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8
            };
            void A(params string[] a) { foreach (var s in a) si.ArgumentList.Add(s); }
            A("-y", "-hide_banner", "-loglevel", "error");
            if (!res.isLive) A("-re");                         // a normal video: read it at real-time speed
            if (res.url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                A("-reconnect", "1", "-reconnect_streamed", "1", "-reconnect_delay_max", "5");
            A("-i", res.url, "-map", "0:v:0?", "-map", "0:a:0?", "-c", "copy",
              "-f", "segment", "-segment_time", "4", "-segment_format", "mpegts",
              "-segment_list", _csvPath, "-segment_list_type", "csv",
              Path.Combine(_sessionDir, "seg_%06d.ts"));

            var err = new StringBuilder();
            var p = new Process { StartInfo = si, EnableRaisingEvents = true };
            p.ErrorDataReceived += (_, ev) => { if (ev.Data != null) lock (err) err.AppendLine(ev.Data); };
            p.Exited += (_, _) => Dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_ffmpegProc, p)) return;
                _procAlive = false;
                _elapsedFrozen = DateTime.Now - _recStart;
                if (!_stopRequested)
                {
                    lock (err) _lastError = err.ToString().Trim();
                    TxtLiveChip.Text = "შეწყდა";
                    Notify?.Invoke("ლაივის ჩაწერა შეწყდა (კავშირი გაწყდა?)", "error");
                }
                RefreshUi();
            });

            try
            {
                _stopRequested = false;
                _ffmpegProc = p;
                p.Start();
                p.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                SetPlaceholder("ffmpeg ვერ გაეშვა", ex.Message);
                _ffmpegProc = null;
                return;
            }

            _hasSession = true;
            _procAlive = true;
            _recStart = DateTime.Now;
            _elapsedFrozen = TimeSpan.Zero;
            _streamTitle = res.title;
            _availableSec = 0;
            _markAt = null;
            _lastPreview = DateTime.MinValue;
            TxtPlaceholderTitle.Text = "ჩაწერა დაიწყო";
            TxtPlaceholderSub.Text = res.isLive ? "პირველი კადრი რამდენიმე წამში გამოჩნდება…"
                                                : "ეს ჩვეულებრივი ვიდეოა და არა ლაივი — ჩაიწერება რეალური სიჩქარით";
            SaveSettings();
            RefreshUi();
        }

        private static void CleanOldSessions(string root)
        {
            try
            {
                if (!Directory.Exists(root)) return;
                foreach (var d in Directory.GetDirectories(root, "sess_*"))
                    if (Directory.GetCreationTime(d) < DateTime.Now.AddHours(-12))
                        try { Directory.Delete(d, true); } catch { }
                // old single-file buffers from the previous version
                foreach (var f in Directory.GetFiles(root, "live_*.ts*"))
                    try { File.Delete(f); } catch { }
            }
            catch { }
        }

        private static string FriendlyError(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "ლინკი ვერ ამოვიცანი. შეამოწმე, რომ ეს კონკრეტული ლაივის ან ვიდეოს ლინკია.";
            string low = raw.ToLowerInvariant();
            string last = raw.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR")) ?? raw.Trim();
            if (low.Contains("yt-dlp ვერ")) return raw;
            if (low.Contains("sign in to confirm") || low.Contains("not a bot")) return last + "\n\nYouTube-მა ავტორიზაცია მოითხოვა — სცადე მოგვიანებით ან განაახლე yt-dlp.";
            if (low.Contains("will begin") || low.Contains("premieres")) return last + "\n\nლაივი ჯერ არ დაწყებულა — სცადე დაწყების მერე.";
            return last.Length > 220 ? last[..220] + "…" : last;
        }

        // ════════════════════════════════════════════════════════════
        // STOP
        // ════════════════════════════════════════════════════════════
        private void BtnStopMonitor_Click(object sender, RoutedEventArgs e)
        {
            DiscardSession();
            SetPlaceholder("ჩაწერა გაჩერდა", "ბუფერი გასუფთავდა. ახალი ლაივისთვის ჩასვი ლინკი.");
            RefreshUi();
        }

        private void DiscardSession()
        {
            _stopRequested = true;
            var p = _ffmpegProc;
            _ffmpegProc = null;
            _procAlive = false;
            _hasSession = false;
            _markAt = null;
            _availableSec = 0;
            _streamTitle = "";
            try { p?.Kill(entireProcessTree: true); } catch { }

            string dir = _sessionDir;
            Task.Run(async () =>
            {
                for (int i = 0; i < 6; i++)
                {
                    try { if (Directory.Exists(dir)) Directory.Delete(dir, true); return; }
                    catch { await Task.Delay(600); }   // ffmpeg may hold the files for a moment
                }
            });

            TxtRecordedTime.Text = "0:00:00";
            TxtBufferTime.Text = "0:00";
            TxtBufferSize.Text = "0 MB";
            ImgLive.Source = null;
        }

        // ════════════════════════════════════════════════════════════
        // SEGMENTS
        // ════════════════════════════════════════════════════════════
        private record Seg(string Path, double Start, double End)
        {
            public double Dur => Math.Max(0, End - Start);
        }

        private List<Seg> ReadSegments()
        {
            var list = new List<Seg>();
            try
            {
                if (!File.Exists(_csvPath)) return list;
                using var fs = new FileStream(_csvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    var parts = line.Split(',');
                    if (parts.Length < 3) continue;
                    if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double s)) continue;
                    if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double en)) continue;
                    string path = Path.Combine(_sessionDir, parts[0].Trim());
                    if (_deleted.Contains(path)) continue;
                    list.Add(new Seg(path, s, en));
                }
            }
            catch { }
            return list;
        }

        /// <summary>The segment ffmpeg is writing right now (a seg_*.ts that is not in the csv yet).</summary>
        private string? GrowingSegment(List<Seg> done)
        {
            try
            {
                var known = new HashSet<string>(done.Select(s => s.Path), StringComparer.OrdinalIgnoreCase);
                return Directory.GetFiles(_sessionDir, "seg_*.ts")
                    .Where(f => !known.Contains(f) && !_deleted.Contains(f))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .LastOrDefault();
            }
            catch { return null; }
        }

        // ════════════════════════════════════════════════════════════
        // 1-SECOND TICK
        // ════════════════════════════════════════════════════════════
        private void UiTimer_Tick(object? sender, EventArgs e)
        {
            if (!_hasSession) return;

            var elapsed = _procAlive ? DateTime.Now - _recStart : _elapsedFrozen;
            TxtRecordedTime.Text = $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";

            var segs = ReadSegments();

            // keep only the chosen window (+ a little slack)
            double total = segs.Sum(s => s.Dur);
            double limit = _bufferMin * 60 + 20;
            int idx = 0;
            while (total > limit && idx < segs.Count - 2)
            {
                var old = segs[idx++];
                try { File.Delete(old.Path); _deleted.Add(old.Path); total -= old.Dur; }
                catch { break; }
            }
            if (idx > 0) segs = segs.Skip(idx).ToList();

            // the segment being written counts too (its length is estimated from the wall clock)
            double growing = 0;
            if (_procAlive && GrowingSegment(segs) != null)
            {
                double lastEnd = segs.Count > 0 ? segs[^1].End : 0;
                growing = Math.Clamp(elapsed.TotalSeconds - lastEnd, 0, 12);
            }
            _availableSec = total + growing;

            TxtBufferTime.Text = FormatMmSs(_availableSec);
            long bytes = 0;
            try { foreach (var f in Directory.GetFiles(_sessionDir, "seg_*.ts")) bytes += new FileInfo(f).Length; } catch { }
            TxtBufferSize.Text = FormatSize(bytes);

            if (_markAt.HasValue)
                TxtMarkChip.Text = "მონიშვნიდან " + FormatMmSs((DateTime.Now - _markAt.Value).TotalSeconds);

            bool canClip = _availableSec >= 4 && !_clipBusy;
            if (BtnClip.IsEnabled != canClip) BtnClip.IsEnabled = canClip;

            if (_procAlive && _availableSec >= 4 && TxtLiveChip.Text != "იწერება") TxtLiveChip.Text = "იწერება";

            // live preview: grab the newest frame of the growing segment every few seconds
            if (_procAlive && !_previewBusy && (DateTime.Now - _lastPreview).TotalSeconds >= 4 && bytes > 300_000)
            {
                string? g = GrowingSegment(segs) ?? segs.LastOrDefault()?.Path;
                if (g != null) _ = UpdatePreviewAsync(g);
            }
        }

        private async Task UpdatePreviewAsync(string segPath)
        {
            _previewBusy = true;
            _lastPreview = DateTime.Now;
            try
            {
                string jpg = Path.Combine(_sessionDir, "preview.jpg");
                bool ok = await Task.Run(() =>
                    RunFfmpeg(new[] { "-y", "-loglevel", "error", "-sseof", "-2", "-i", segPath, "-frames:v", "1",
                                      "-vf", "scale=1280:-2", "-q:v", "5", jpg }, 15000).ok);
                if (ok && File.Exists(jpg) && _hasSession)
                {
                    var bytes = await File.ReadAllBytesAsync(jpg);
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.StreamSource = new MemoryStream(bytes);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit(); bmp.Freeze();
                    ImgLive.Source = bmp;
                    PlaceholderLive.Visibility = Visibility.Collapsed;
                }
            }
            catch { }
            finally { _previewBusy = false; }
        }

        // ════════════════════════════════════════════════════════════
        // MARK / CLIP
        // ════════════════════════════════════════════════════════════
        private void BtnMark_Click(object sender, RoutedEventArgs e)
        {
            if (!_hasSession || !_procAlive) return;
            _markAt = _markAt.HasValue ? null : DateTime.Now;
            if (_markAt.HasValue) TxtMarkChip.Text = "მონიშვნიდან 0:00";
            RefreshUi();
        }

        private void Win_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!IsVisible) return;
            if (Keyboard.FocusedElement is TextBox) return;
            if (Keyboard.Modifiers != ModifierKeys.None) return;
            if (e.Key == Key.C && BtnClip.IsEnabled) { BtnClip_Click(this, new RoutedEventArgs()); e.Handled = true; }
            else if (e.Key == Key.M && BtnMark.IsEnabled) { BtnMark_Click(this, new RoutedEventArgs()); e.Handled = true; }
        }

        private async void BtnClip_Click(object sender, RoutedEventArgs e)
        {
            if (!_hasSession || _clipBusy) return;

            int sec = _clipSec;
            bool fromMark = _markAt.HasValue;
            if (fromMark) sec = (int)Math.Ceiling((DateTime.Now - _markAt!.Value).TotalSeconds) + 1;
            sec = Math.Max(3, Math.Min(sec, (int)Math.Max(3, _availableSec)));

            _clipBusy = true;
            BtnClip.IsEnabled = false;
            TxtLiveChip.Text = "ჭრა…";

            // snapshot of what to cut: newest segments until we have enough seconds (+ margin for keyframe alignment)
            var segs = ReadSegments();
            string? growing = GrowingSegment(segs);
            var files = new List<string>();
            double have = 0;
            string? growSnap = null;
            if (growing != null)
            {
                growSnap = Path.Combine(_sessionDir, $"grow_{DateTime.Now:HHmmssfff}.ts");
                try
                {
                    await Task.Run(() =>
                    {
                        using var src = new FileStream(growing, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var dst = File.Create(growSnap);
                        src.CopyTo(dst);
                    });
                    files.Add(growSnap);
                    double lastEnd = segs.Count > 0 ? segs[^1].End : 0;
                    have += Math.Clamp((_procAlive ? (DateTime.Now - _recStart).TotalSeconds : _elapsedFrozen.TotalSeconds) - lastEnd, 0, 12);
                }
                catch { growSnap = null; }
            }
            for (int i = segs.Count - 1; i >= 0 && have < sec + 8; i--)
            {
                files.Insert(0, segs[i].Path);
                have += segs[i].Dur;
            }

            string title = SanitizeFileName(_streamTitle);
            Directory.CreateDirectory(ClipFolder);
            string outPath = Path.Combine(ClipFolder, $"{title}_{DateTime.Now:yyyyMMdd_HHmmss}_{sec}s.mp4");
            string work = _sessionDir;

            bool ok = await Task.Run(() => CutClip(files, work, sec, outPath));

            if (growSnap != null) try { File.Delete(growSnap); } catch { }
            _clipBusy = false;
            if (fromMark) _markAt = null;

            if (!ok || !File.Exists(outPath))
            {
                TxtLiveChip.Text = _procAlive ? "იწერება" : "შეწყდა";
                Notify?.Invoke("კლიპი ვერ გადაიჭრა — ბუფერი ჯერ ძალიან მოკლეა?", "error");
                RefreshUi();
                return;
            }

            TxtLiveChip.Text = _procAlive ? "იწერება" : "შეწყდა";
            AddClipCard(outPath, sec);
            Notify?.Invoke($"კლიპი მზადაა: {Path.GetFileName(outPath)}", "success");
            RefreshUi();

            if (ChkAutoEditor.IsChecked == true) SendToEditor?.Invoke(outPath);
            else if (ChkAutoVertical.IsChecked == true) SendToCrop?.Invoke(outPath);
        }

        private static bool CutClip(List<string> files, string workDir, int sec, string output)
        {
            if (files.Count == 0) return false;
            string listPath = Path.Combine(workDir, $"concat_{Guid.NewGuid():N}.txt");
            string joined = Path.Combine(workDir, $"join_{Guid.NewGuid():N}.ts");
            try
            {
                var sb = new StringBuilder();
                foreach (var f in files)
                    sb.AppendLine("file '" + f.Replace("\\", "/").Replace("'", "'\\''") + "'");
                File.WriteAllText(listPath, sb.ToString(), new UTF8Encoding(false));

                var r1 = RunFfmpeg(new[] { "-y", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", listPath,
                                           "-c", "copy", joined }, 120000);
                if (!r1.ok || !File.Exists(joined)) return false;

                var r2 = RunFfmpeg(new[] { "-y", "-loglevel", "error", "-sseof", $"-{sec}", "-i", joined,
                                           "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
                                           "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart", output }, 240000);
                return r2.ok;
            }
            catch { return false; }
            finally
            {
                try { File.Delete(listPath); } catch { }
                try { File.Delete(joined); } catch { }
            }
        }

        private static (bool ok, string err) RunFfmpeg(string[] args, int timeoutMs)
        {
            var si = new ProcessStartInfo(AppPaths.Ffmpeg())
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var a in args) si.ArgumentList.Add(a);
            try
            {
                using var p = Process.Start(si);
                if (p == null) return (false, "start");
                var err = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (false, "timeout"); }
                return (p.ExitCode == 0, err.Result);
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        // ════════════════════════════════════════════════════════════
        // CLIP CARDS
        // ════════════════════════════════════════════════════════════
        private void AddClipCard(string clipPath, int sec)
        {
            TxtNoClips.Visibility = Visibility.Collapsed;

            var card = new Border { Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 8),
                                    CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
            card.SetResourceReference(Border.BackgroundProperty, "BgCard");
            card.SetResourceReference(Border.BorderBrushProperty, "Border");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var thumbBox = new Border { Width = 112, Height = 63, CornerRadius = new CornerRadius(8),
                                        Margin = new Thickness(0, 0, 12, 0), ClipToBounds = true };
            thumbBox.SetResourceReference(Border.BackgroundProperty, "VideoBg");
            var img = new Image { Stretch = Stretch.UniformToFill };
            thumbBox.Child = img;
            grid.Children.Add(thumbBox);

            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { Text = Path.GetFileName(clipPath), FontSize = 12.5, FontWeight = FontWeights.Medium,
                                       TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = clipPath };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            right.Children.Add(name);

            long size = 0; try { size = new FileInfo(clipPath).Length; } catch { }
            var meta = new TextBlock { Text = $"{DateTime.Now:HH:mm:ss}  ·  {FormatMmSs(sec)}  ·  {FormatSize(size)}",
                                       FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };
            meta.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            right.Children.Add(meta);

            var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            btns.Children.Add(ClipBtn("", "მინი მონტაჟში", () => SendToEditor?.Invoke(clipPath)));
            btns.Children.Add(ClipBtn("", "გავერტიკალურება", () => SendToCrop?.Invoke(clipPath)));
            btns.Children.Add(ClipBtn("", "დაკვრა", () =>
            {
                try { Process.Start(new ProcessStartInfo(clipPath) { UseShellExecute = true }); } catch { }
            }));
            btns.Children.Add(ClipBtn("", "საქაღალდეში ჩვენება", () =>
            {
                try { Process.Start("explorer.exe", $"/select,\"{clipPath}\""); } catch { }
            }));
            right.Children.Add(btns);
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);

            card.Child = grid;
            ClipsPanel.Children.Insert(0, card);

            _ = LoadClipThumbAsync(clipPath, img);
        }

        private Button ClipBtn(string glyph, string tip, Action action)
        {
            var b = new Button { Content = glyph, Style = (Style)FindResource("ToolButton"), Width = 32, Height = 32,
                                 FontSize = 13, ToolTip = tip, Margin = new Thickness(0, 0, 6, 0) };
            b.Click += (_, _) => action();
            return b;
        }

        private static async Task LoadClipThumbAsync(string clipPath, Image target)
        {
            try
            {
                string jpg = Path.Combine(Path.GetTempPath(), $"flow_clip_{Guid.NewGuid():N}.jpg");
                bool ok = await Task.Run(() =>
                    RunFfmpeg(new[] { "-y", "-loglevel", "error", "-ss", "1", "-i", clipPath, "-frames:v", "1",
                                      "-vf", "scale=224:-2", jpg }, 20000).ok);
                if (!ok || !File.Exists(jpg)) return;
                var bytes = await File.ReadAllBytesAsync(jpg);
                try { File.Delete(jpg); } catch { }
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit(); bmp.Freeze();
                target.Source = bmp;
            }
            catch { }
        }

        // ════════════════════════════════════════════════════════════
        // yt-dlp: resolve the direct media URL of a live page
        // ════════════════════════════════════════════════════════════
        private static (string url, string title, bool isLive, string err) ResolveStreamUrl(string pageUrl, string quality)
        {
            // already a direct media/playlist address → no resolving needed
            string low = pageUrl.ToLowerInvariant();
            if (low.Contains(".m3u8") || low.Contains(".mpd") || low.StartsWith("rtmp") || low.StartsWith("rtsp"))
                return (pageUrl, "Live Stream", true, "");

            string fmt = quality switch
            {
                "1080" => "b[height<=1080]/b/best",
                "720"  => "b[height<=720]/b/best",
                "480"  => "b[height<=480]/b/best",
                _      => "b/best"
            };

            try
            {
                var args = YtDlpTool.CommonArgs().Concat(new[]
                {
                    "-f", fmt, "--print", "%(title)s", "--print", "%(is_live)s", "--print", "%(urls)s", pageUrl
                }).ToArray();
                var si = YtDlpTool.Make(args);
                if (si == null) return ("", "", true, "yt-dlp ვერ მოიძებნა — დააყენე: pip install yt-dlp");

                using var p = Process.Start(si);
                if (p == null) return ("", "", true, "yt-dlp ვერ გაეშვა");
                var errT = p.StandardError.ReadToEndAsync();
                string stdout = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(60000)) { try { p.Kill(true); } catch { } return ("", "", true, "დრო ამოიწურა"); }
                string stderr = errT.Result;

                var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                  .Select(l => l.Trim()).ToList();
                if (p.ExitCode != 0 || lines.Count < 3) return ("", "", true, stderr);

                string title = lines[0];
                bool isLive = !string.Equals(lines[1], "False", StringComparison.OrdinalIgnoreCase);
                string url = lines.Skip(2).FirstOrDefault(l => l.StartsWith("http", StringComparison.OrdinalIgnoreCase)) ?? "";
                return (url, title, isLive, url.Length == 0 ? stderr : "");
            }
            catch (Exception ex) { return ("", "", true, ex.Message); }
        }

        // ── helpers ──────────────────────────────────────────────────
        private static string FormatMmSs(double sec)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, sec));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }

        private static string FormatSize(long bytes)
        {
            double mb = bytes / 1048576.0;
            return mb >= 1024 ? $"{mb / 1024:0.0} GB" : mb >= 10 ? $"{mb:0} MB" : $"{mb:0.0} MB";
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "live_clip";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            name = name.Trim().TrimEnd('.');
            if (name.Length == 0) return "live_clip";
            return name.Length > 40 ? name[..40].TrimEnd() : name;
        }
    }
}

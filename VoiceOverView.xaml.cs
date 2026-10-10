using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NAudio.Wave;

namespace AiAssistant
{
    public partial class VoiceOverView : UserControl
    {
        public event Action<string, string>? Notify;
        public event Action<string, string>? Processed;      // (input, output)
        public event Action<string, string>? OpenInAB;
        public string CacheFolder { get; set; } = "";

        private class Take
        {
            public int Id;
            public string Name = "";
            public string Path = "";
            public double Start;           // seconds on the video timeline
            public double Dur;
            public double Gain = 1.0;      // 0..1.5
            public bool Muted;
            public float[] Peaks = Array.Empty<float>();
        }

        // ── project state ─────────────────────────────────────────────
        private string? _video;
        private double _dur;
        private double _fps = 25;
        private bool _hasAudio = true;
        private readonly List<Take> _takes = new();
        private int _nextId = 1;
        private string? _lastOutput;

        // ── playback ──────────────────────────────────────────────────
        private bool _playing;
        private bool _suppressSlider;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
        private readonly Dictionary<int, MediaPlayer> _players = new();
        private readonly HashSet<int> _started = new();

        // ── recording ─────────────────────────────────────────────────
        private WaveInEvent? _wave;
        private WaveFileWriter? _writer;
        private string _recPath = "";
        private bool _recording;
        private bool _countingDown;
        private double _recStartPos;
        private readonly Stopwatch _recWatch = new();
        private WaveInEvent? _testWave;

        // ── timeline interaction ──────────────────────────────────────
        private Take? _drag;
        private double _dragOffset;
        private Rectangle? _playhead;

        private static readonly string[] VideoExt = { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".mxf", ".m4v", ".ts" };

        public VoiceOverView()
        {
            InitializeComponent();
            _timer.Tick += Timer_Tick;
            Loaded += (_, _) =>
            {
                if (Window.GetWindow(this) is Window w)
                {
                    w.PreviewKeyDown -= Win_PreviewKeyDown;
                    w.PreviewKeyDown += Win_PreviewKeyDown;
                }
                if (CmbMic.Items.Count == 0) LoadMics();
            };
            IsVisibleChanged += (_, _) => { if (!IsVisible) { StopPlayback(); StopMicTest(); } };
            RefreshTakesUi();
            Mix_Changed(this, new RoutedEventArgs());
        }

        // ════════════════════════════════════════════════════════════
        // WORK FOLDER
        // ════════════════════════════════════════════════════════════
        private string WorkDir
        {
            get
            {
                string root = !string.IsNullOrEmpty(CacheFolder) ? CacheFolder
                    : System.IO.Path.Combine(AppPaths.DataDir, "cache");
                string d = System.IO.Path.Combine(root, "voiceover");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        // ════════════════════════════════════════════════════════════
        // MICROPHONES
        // ════════════════════════════════════════════════════════════
        private void LoadMics()
        {
            CmbMic.Items.Clear();
            try
            {
                int n = WaveInEvent.DeviceCount;
                for (int i = 0; i < n; i++)
                {
                    var caps = WaveInEvent.GetCapabilities(i);
                    CmbMic.Items.Add(new ComboBoxItem { Content = caps.ProductName, Tag = i });
                }
            }
            catch { }

            if (CmbMic.Items.Count > 0)
            {
                CmbMic.SelectedIndex = 0;
                TxtMicStatus.Text = "ჩაწერისას ყურსასმენი გამოიყენე, რომ ვიდეოს ხმა მიკროფონში არ მოხვდეს.";
            }
            else
            {
                CmbMic.Items.Add(new ComboBoxItem { Content = "მიკროფონი ვერ მოიძებნა", Tag = -1 });
                CmbMic.SelectedIndex = 0;
                TxtMicStatus.Text = "მიკროფონი არ ჩანს. შეაერთე და სეთინგებიდან გადატვირთე ეს ტაბი, ან გამოიყენე „აუდიო ფაილიდან“.";
            }
            UpdateRecordEnabled();
        }

        private int SelectedMic => (CmbMic.SelectedItem as ComboBoxItem)?.Tag is int i ? i : -1;

        private void CmbMic_Changed(object sender, SelectionChangedEventArgs e)
        {
            StopMicTest();
            UpdateRecordEnabled();
        }

        private void BtnMicTest_Click(object sender, RoutedEventArgs e)
        {
            if (_testWave != null) { StopMicTest(); return; }
            if (SelectedMic < 0 || _recording) return;
            try
            {
                _testWave = new WaveInEvent { DeviceNumber = SelectedMic, WaveFormat = new WaveFormat(48000, 16, 1), BufferMilliseconds = 50 };
                _testWave.DataAvailable += (_, a) => ShowLevel(a.Buffer, a.BytesRecorded);
                _testWave.StartRecording();
                BtnMicTest.Content = "გაჩერება";
                TxtMicStatus.Text = "თქვი რამე — ზოლი უნდა მოძრაობდეს.";
            }
            catch (Exception ex)
            {
                _testWave = null;
                TxtMicStatus.Text = "მიკროფონი ვერ გაიხსნა: " + ex.Message;
            }
        }

        private void StopMicTest()
        {
            try { _testWave?.StopRecording(); _testWave?.Dispose(); } catch { }
            _testWave = null;
            if (BtnMicTest != null) BtnMicTest.Content = "შემოწმება";
            if (PbLevel != null && !_recording) PbLevel.Value = 0;
        }

        private void ShowLevel(byte[] buffer, int bytes)
        {
            int peak = 0;
            for (int i = 0; i + 1 < bytes; i += 2)
            {
                int s = Math.Abs((short)(buffer[i] | (buffer[i + 1] << 8)));
                if (s > peak) peak = s;
            }
            double db = peak <= 0 ? -90 : 20 * Math.Log10(peak / 32768.0);
            double v = Math.Clamp((db + 60) / 60.0 * 100.0, 0, 100);
            Dispatcher.InvokeAsync(() => PbLevel.Value = v);
        }

        private void UpdateRecordEnabled()
        {
            if (BtnRecord == null) return;
            BtnRecord.IsEnabled = _video != null && SelectedMic >= 0 && !_countingDown;
        }

        // ════════════════════════════════════════════════════════════
        // VIDEO
        // ════════════════════════════════════════════════════════════
        private void BtnPick_Click(object sender, RoutedEventArgs e)
        {
            if (_recording) return;
            var ofd = new Microsoft.Win32.OpenFileDialog
            { Title = "ვიდეოს არჩევა", Filter = "Video|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.mxf;*.m4v|All|*.*" };
            if (ofd.ShowDialog() == true) LoadVideo(ofd.FileName);
        }

        /// <summary>Public: other tabs can send a video here.</summary>
        public void LoadVideo(string path)
        {
            if (_recording || !File.Exists(path)) return;
            StopPlayback();
            _video = path;
            _lastOutput = null;
            _takes.Clear();
            RefreshTakesUi();

            var info = ProbeVideo(path);
            _dur = info.dur; _fps = info.fps; _hasAudio = info.hasAudio;

            DropZone.Visibility = Visibility.Collapsed;
            BtnChange.Visibility = Visibility.Visible;
            Media.Source = new Uri(path, UriKind.Absolute);
            Media.Volume = 1;
            Media.Play(); Media.Pause();

            SlPos.IsEnabled = BtnPlay.IsEnabled = BtnRewind.IsEnabled = true;
            _suppressSlider = true;
            SlPos.Maximum = Math.Max(0.1, _dur);
            SlPos.Value = 0;
            _suppressSlider = false;
            UpdatePosText(0);
            BtnCompare.Visibility = Visibility.Collapsed;
            TxtExportStatus.Text = _hasAudio ? "" : "ვიდეოს საკუთარი ხმა არ აქვს — ახალი ხმა დაემატება პირდაპირ.";
            UpdateRecordEnabled();
            RedrawTimeline();
        }

        private static (double dur, double fps, bool hasAudio) ProbeVideo(string path)
        {
            double dur = 0, fps = 25; bool audio = false;
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
                var f = Regex.Match(err, @"(\d+(?:\.\d+)?)\s*fps");
                if (f.Success) fps = double.Parse(f.Groups[1].Value, CultureInfo.InvariantCulture);
                audio = err.Contains("Audio:");
            }
            catch { }
            return (dur, fps > 0 ? fps : 25, audio);
        }

        private void Media_Opened(object sender, RoutedEventArgs e)
        {
            if (Media.NaturalDuration.HasTimeSpan)
            {
                _dur = Media.NaturalDuration.TimeSpan.TotalSeconds;
                _suppressSlider = true;
                SlPos.Maximum = Math.Max(0.1, _dur);
                _suppressSlider = false;
                UpdatePosText(Media.Position.TotalSeconds);
                RedrawTimeline();
            }
        }

        private void Media_Failed(object? sender, ExceptionRoutedEventArgs e)
            => TxtExportStatus.Text = "ვიდეო ვერ გაიხსნა: " + e.ErrorException.Message;

        private void Media_Ended(object sender, RoutedEventArgs e)
        {
            if (_recording) _ = StopRecordingAsync();
            else StopPlayback();
        }

        // ── drag & drop ──────────────────────────────────────────────
        private void Drag_Enter(object sender, DragEventArgs e)
        {
            bool ok = !_recording && e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            DragOverlay.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
            e.Handled = true;
        }

        private void Drag_Leave(object sender, DragEventArgs e) => DragOverlay.Visibility = Visibility.Collapsed;

        private void Drag_Drop(object sender, DragEventArgs e)
        {
            DragOverlay.Visibility = Visibility.Collapsed;
            if (_recording) return;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] f && f.Length > 0)
            {
                string ext = System.IO.Path.GetExtension(f[0]).ToLowerInvariant();
                if (VideoExt.Contains(ext)) LoadVideo(f[0]);
                else if (_video != null) _ = AddAudioFileAsync(f[0]);
            }
            e.Handled = true;
        }

        // ════════════════════════════════════════════════════════════
        // TRANSPORT / PLAYBACK (preview with the new voice)
        // ════════════════════════════════════════════════════════════
        private double Pos => Media.Position.TotalSeconds;

        private void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            if (_recording || _video == null) return;
            if (_playing) StopPlayback(); else StartPlayback();
        }

        private void BtnRewind_Click(object sender, RoutedEventArgs e)
        {
            if (_recording) return;
            bool was = _playing;
            StopPlayback();
            SeekTo(0);
            if (was) StartPlayback();
        }

        private void StartPlayback()
        {
            if (_video == null) return;
            if (Pos >= _dur - 0.1) SeekTo(0);

            _players.Clear();
            _started.Clear();
            foreach (var t in _takes.Where(t => !t.Muted && File.Exists(t.Path)))
            {
                var p = new MediaPlayer();
                p.Open(new Uri(t.Path, UriKind.Absolute));
                p.Volume = Math.Clamp(t.Gain, 0, 1);
                _players[t.Id] = p;
            }

            ApplyOriginalVolume(Pos);
            Media.Play();
            _playing = true;
            BtnPlay.Content = "";
            _timer.Start();
        }

        private void StopPlayback()
        {
            _timer.Stop();
            if (_playing || _recording) { try { Media.Pause(); } catch { } }
            _playing = false;
            foreach (var p in _players.Values) { try { p.Stop(); p.Close(); } catch { } }
            _players.Clear();
            _started.Clear();
            if (BtnPlay != null) BtnPlay.Content = "";
        }

        private void SeekTo(double sec)
        {
            sec = Math.Clamp(sec, 0, Math.Max(0, _dur));
            Media.Position = TimeSpan.FromSeconds(sec);
            _suppressSlider = true;
            SlPos.Value = sec;
            _suppressSlider = false;
            UpdatePosText(sec);
            UpdatePlayhead(sec);
            // players re-sync at their next window
            foreach (var p in _players.Values) { try { p.Stop(); } catch { } }
            _started.Clear();
        }

        private void SlPos_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressSlider || _recording) return;
            SeekTo(e.NewValue);
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            double pos = Pos;

            if (_recording)
            {
                TxtRecTime.Text = "REC " + FmtShort(_recWatch.Elapsed.TotalSeconds);
            }
            else if (_playing)
            {
                foreach (var t in _takes)
                {
                    if (t.Muted || _started.Contains(t.Id) || !_players.TryGetValue(t.Id, out var p)) continue;
                    if (pos >= t.Start && pos < t.Start + t.Dur - 0.05)
                    {
                        try { p.Position = TimeSpan.FromSeconds(pos - t.Start); p.Play(); } catch { }
                        _started.Add(t.Id);
                    }
                }
                ApplyOriginalVolume(pos);
            }

            _suppressSlider = true;
            SlPos.Value = Math.Min(SlPos.Maximum, pos);
            _suppressSlider = false;
            UpdatePosText(pos);
            UpdatePlayhead(pos);
        }

        /// <summary>How loud the original track is at time t (fixed level, or ducked only while a take is speaking).</summary>
        private double OriginalLevel(double t)
        {
            double lvl = SlOrig.Value / 100.0;
            if (RbDuckAuto.IsChecked != true) return lvl;
            double mask = 0;
            foreach (var tk in _takes.Where(x => !x.Muted))
            {
                double a = tk.Start - 0.2, b = tk.Start + tk.Dur + 0.4;
                double m = Math.Clamp(Math.Min((t - a) / 0.15, (b - t) / 0.35), 0, 1);
                if (m > mask) mask = m;
            }
            return 1 - (1 - lvl) * mask;
        }

        private void ApplyOriginalVolume(double t)
        {
            double v = OriginalLevel(t);
            // while recording the video is only a cue for the reader — keep it at the "monitor" level
            if (_recording) v = SlMonitor.Value / 100.0;
            Media.Volume = Math.Clamp(v, 0, 1);
        }

        private void UpdatePosText(double sec)
            => TxtPos.Text = $"{FmtShort(sec)} / {FmtShort(_dur)}";

        // ════════════════════════════════════════════════════════════
        // RECORDING
        // ════════════════════════════════════════════════════════════
        private async void BtnRecord_Click(object sender, RoutedEventArgs e)
        {
            if (_countingDown) return;
            if (_recording) await StopRecordingAsync();
            else await StartRecordingAsync();
        }

        private async Task StartRecordingAsync()
        {
            if (_video == null || _recording || SelectedMic < 0) return;
            StopPlayback();
            StopMicTest();

            double startPos = Pos;
            if (startPos >= _dur - 0.3) { SeekTo(0); startPos = 0; }

            if (ChkCountdown.IsChecked == true)
            {
                _countingDown = true;
                UpdateRecordEnabled();
                CountdownBox.Visibility = Visibility.Visible;
                for (int i = 3; i >= 1; i--)
                {
                    TxtCountdown.Text = i.ToString();
                    await Task.Delay(750);
                    if (!IsVisible) break;
                }
                CountdownBox.Visibility = Visibility.Collapsed;
                _countingDown = false;
            }

            try
            {
                _recPath = System.IO.Path.Combine(WorkDir, $"take_{DateTime.Now:yyyyMMdd_HHmmss}_{_nextId}.wav");
                var fmt = new WaveFormat(48000, 16, 1);
                _writer = new WaveFileWriter(_recPath, fmt);
                _wave = new WaveInEvent { DeviceNumber = SelectedMic, WaveFormat = fmt, BufferMilliseconds = 40 };
                _wave.DataAvailable += (_, a) =>
                {
                    try { _writer?.Write(a.Buffer, 0, a.BytesRecorded); } catch { }
                    ShowLevel(a.Buffer, a.BytesRecorded);
                };
                _wave.RecordingStopped += (_, _) =>
                {
                    try { _writer?.Dispose(); } catch { }
                    _writer = null;
                };
                _wave.StartRecording();
            }
            catch (Exception ex)
            {
                _wave = null; _writer = null;
                UpdateRecordEnabled();
                TxtMicStatus.Text = "ჩაწერა ვერ დაიწყო: " + ex.Message;
                Notify?.Invoke("მიკროფონი ვერ გაიხსნა", "error");
                return;
            }

            _recording = true;
            _recStartPos = startPos;
            _recWatch.Restart();
            Media.Volume = Math.Clamp(SlMonitor.Value / 100.0, 0, 1);
            Media.Play();
            _timer.Start();

            RecBadge.Visibility = Visibility.Visible;
            TxtRecBtn.Text = "გაჩერება";
            RecIcon.Fill = Brushes.White;
            DotRec.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Danger");
            TxtRecChip.Text = "იწერება";
            BtnPlay.IsEnabled = BtnRewind.IsEnabled = SlPos.IsEnabled = false;
            BtnExport.IsEnabled = false;
        }

        private async Task StopRecordingAsync()
        {
            if (!_recording) return;
            _recording = false;
            _recWatch.Stop();
            double elapsed = _recWatch.Elapsed.TotalSeconds;

            _timer.Stop();
            try { Media.Pause(); } catch { }
            try { _wave?.StopRecording(); } catch { }
            await Task.Delay(180);                 // let the writer flush
            try { _wave?.Dispose(); } catch { }
            _wave = null;

            RecBadge.Visibility = Visibility.Collapsed;
            TxtRecBtn.Text = "ჩაწერის დაწყება (ამ ადგილიდან)";
            DotRec.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextMuted");
            TxtRecChip.Text = "მზადაა";
            PbLevel.Value = 0;
            BtnPlay.IsEnabled = BtnRewind.IsEnabled = SlPos.IsEnabled = true;

            if (elapsed < 0.4 || !File.Exists(_recPath))
            {
                try { if (File.Exists(_recPath)) File.Delete(_recPath); } catch { }
                UpdateRecordEnabled();
                UpdateExportEnabled();
                return;
            }

            double latency = SlLatency.Value / 1000.0;
            var take = new Take
            {
                Id = _nextId++,
                Name = $"ჩანაწერი {_takes.Count + 1}",
                Path = _recPath,
                Start = Math.Max(0, _recStartPos - latency),
                Dur = elapsed
            };
            // use the real file length
            try { using var r = new WaveFileReader(_recPath); take.Dur = r.TotalTime.TotalSeconds; } catch { }
            take.Peaks = await Task.Run(() => ComputePeaks(take.Path));
            _takes.Add(take);
            RefreshTakesUi();
            RedrawTimeline();
            SeekTo(take.Start);
            UpdateRecordEnabled();
            UpdateExportEnabled();
            Notify?.Invoke($"{take.Name} დაემატა ({FmtShort(take.Dur)})", "success");
        }

        // ════════════════════════════════════════════════════════════
        // ADD AUDIO FROM A FILE
        // ════════════════════════════════════════════════════════════
        private async void BtnAddAudio_Click(object sender, RoutedEventArgs e)
        {
            if (_video == null)
            {
                Notify?.Invoke("ჯერ აირჩიე ვიდეო", "error");
                return;
            }
            var ofd = new Microsoft.Win32.OpenFileDialog
            { Title = "აუდიო ფაილი", Filter = "Audio|*.wav;*.mp3;*.m4a;*.aac;*.flac;*.ogg|All|*.*" };
            if (ofd.ShowDialog() == true) await AddAudioFileAsync(ofd.FileName);
        }

        public async Task AddAudioFileAsync(string path)
        {
            if (_video == null || !File.Exists(path)) return;
            double dur = 0;
            float[] peaks = await Task.Run(() =>
            {
                try { using var r = new AudioFileReader(path); dur = r.TotalTime.TotalSeconds; } catch { }
                return ComputePeaks(path);
            });
            if (dur <= 0)
            {
                Notify?.Invoke("აუდიო ფაილი ვერ წავიკითხე", "error");
                return;
            }
            var take = new Take
            {
                Id = _nextId++,
                Name = System.IO.Path.GetFileNameWithoutExtension(path),
                Path = path,
                Start = Math.Min(Pos, Math.Max(0, _dur - 0.5)),
                Dur = dur,
                Peaks = peaks
            };
            _takes.Add(take);
            RefreshTakesUi();
            RedrawTimeline();
            UpdateExportEnabled();
        }

        private static float[] ComputePeaks(string path, int buckets = 240)
        {
            try
            {
                using var r = new AudioFileReader(path);
                long total = r.Length / (r.WaveFormat.BitsPerSample / 8);
                if (total <= 0) return new float[buckets];
                long per = Math.Max(1, total / buckets);
                var peaks = new float[buckets];
                var buf = new float[4096];
                long idx = 0;
                int read;
                while ((read = r.Read(buf, 0, buf.Length)) > 0)
                {
                    for (int i = 0; i < read; i++, idx++)
                    {
                        int b = (int)Math.Min(buckets - 1, idx / per);
                        float v = Math.Abs(buf[i]);
                        if (v > peaks[b]) peaks[b] = v;
                    }
                }
                float max = peaks.Max();
                if (max > 0.0001f) for (int i = 0; i < peaks.Length; i++) peaks[i] = Math.Min(1f, peaks[i] / max);
                return peaks;
            }
            catch { return new float[buckets]; }
        }

        // ════════════════════════════════════════════════════════════
        // TIMELINE
        // ════════════════════════════════════════════════════════════
        private double PxPerSec => _dur > 0 && Timeline.ActualWidth > 0 ? Timeline.ActualWidth / _dur : 1;

        private void Timeline_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawTimeline();

        private void RedrawTimeline()
        {
            if (Timeline == null) return;
            Timeline.Children.Clear();
            _playhead = null;
            double w = Timeline.ActualWidth, h = Timeline.ActualHeight;
            if (w <= 0 || h <= 0) return;
            TxtTimelineHint.Visibility = _takes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_dur <= 0) return;

            double pps = w / _dur;

            // ruler
            double step = _dur <= 20 ? 1 : _dur <= 60 ? 5 : _dur <= 300 ? 15 : 60;
            for (double t = 0; t <= _dur + 0.001; t += step)
            {
                double x = t * pps;
                var tick = new Rectangle { Width = 1, Height = 9 };
                tick.SetResourceReference(Shape.FillProperty, "BorderStrong");
                Canvas.SetLeft(tick, x); Canvas.SetTop(tick, 0);
                Timeline.Children.Add(tick);
                var lbl = new TextBlock { Text = FmtShort(t), FontSize = 10, FontFamily = new FontFamily("Consolas") };
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
                Canvas.SetLeft(lbl, x + 4); Canvas.SetTop(lbl, 1);
                Timeline.Children.Add(lbl);
            }

            // takes
            foreach (var t in _takes)
            {
                double bw = Math.Max(8, t.Dur * pps);
                double bh = h - 34;
                var block = new Border
                {
                    Width = bw, Height = bh, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1.5),
                    Cursor = Cursors.SizeWE, Tag = t, Opacity = t.Muted ? 0.45 : 1,
                    ToolTip = $"{t.Name} · {FmtShort(t.Start)} – {FmtShort(t.Start + t.Dur)}"
                };
                block.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
                block.SetResourceReference(Border.BorderBrushProperty, "Accent");

                var grid = new Grid { ClipToBounds = true };
                if (t.Peaks.Length > 0)
                {
                    var geo = new StreamGeometry();
                    using (var ctx = geo.Open())
                    {
                        double mid = bh / 2;
                        for (int i = 0; i < t.Peaks.Length; i++)
                        {
                            double x = (i + 0.5) / t.Peaks.Length * (bw - 4) + 2;
                            double a = Math.Max(1, t.Peaks[i] * (bh / 2 - 6));
                            ctx.BeginFigure(new Point(x, mid - a), false, false);
                            ctx.LineTo(new Point(x, mid + a), true, false);
                        }
                    }
                    geo.Freeze();
                    var path = new System.Windows.Shapes.Path { Data = geo, StrokeThickness = 1.4, IsHitTestVisible = false };
                    path.SetResourceReference(Shape.StrokeProperty, "Accent");
                    grid.Children.Add(path);
                }
                var name = new TextBlock { Text = t.Name, FontSize = 10.5, FontWeight = FontWeights.SemiBold,
                                           Margin = new Thickness(6, 3, 6, 0), IsHitTestVisible = false,
                                           TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Top };
                name.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
                grid.Children.Add(name);
                block.Child = grid;

                Canvas.SetLeft(block, t.Start * pps);
                Canvas.SetTop(block, 26);
                Timeline.Children.Add(block);
            }

            // playhead
            _playhead = new Rectangle { Width = 2, Height = h, IsHitTestVisible = false };
            _playhead.SetResourceReference(Shape.FillProperty, "Danger");
            Canvas.SetTop(_playhead, 0);
            Timeline.Children.Add(_playhead);
            UpdatePlayhead(_video != null ? Pos : 0);
        }

        private void UpdatePlayhead(double sec)
        {
            if (_playhead == null) return;
            Canvas.SetLeft(_playhead, Math.Clamp(sec * PxPerSec - 1, 0, Math.Max(0, Timeline.ActualWidth - 2)));
        }

        private void Timeline_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_recording || _dur <= 0) return;
            var p = e.GetPosition(Timeline);
            var hit = e.OriginalSource as FrameworkElement;
            while (hit != null && hit != Timeline && hit.Tag is not Take) hit = hit.Parent as FrameworkElement;
            if (hit?.Tag is Take t)
            {
                _drag = t;
                _dragOffset = p.X / PxPerSec - t.Start;
                Timeline.CaptureMouse();
            }
            else
            {
                bool was = _playing;
                if (was) StopPlayback();
                SeekTo(p.X / PxPerSec);
                if (was) StartPlayback();
            }
        }

        private void Timeline_MouseMove(object sender, MouseEventArgs e)
        {
            if (_drag == null) return;
            double x = e.GetPosition(Timeline).X;
            _drag.Start = Math.Clamp(x / PxPerSec - _dragOffset, 0, Math.Max(0, _dur - _drag.Dur));
            RedrawTimeline();
        }

        private void Timeline_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_drag == null) return;
            _drag = null;
            Timeline.ReleaseMouseCapture();
            RefreshTakesUi();
        }

        // ════════════════════════════════════════════════════════════
        // TAKES LIST
        // ════════════════════════════════════════════════════════════
        private Button SmallBtn(string content, string tip, Action a, bool glyph = true, double w = 32)
        {
            var b = new Button
            {
                Content = content, ToolTip = tip, Width = w, Height = 30, FontSize = glyph ? 12 : 11.5,
                Style = (Style)FindResource("ToolButton"), Margin = new Thickness(0, 0, 4, 0)
            };
            if (!glyph) b.FontFamily = new FontFamily("Segoe UI");
            b.Click += (_, _) => a();
            return b;
        }

        private void RefreshTakesUi()
        {
            if (TakesPanel == null) return;
            TakesPanel.Children.Clear();
            TxtNoTakes.Visibility = _takes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtTakeCount.Text = _takes.Count == 0 ? "" : _takes.Count.ToString();

            foreach (var t in _takes)
            {
                var take = t;
                var card = new Border { Padding = new Thickness(12, 10, 10, 10), Margin = new Thickness(0, 0, 0, 8),
                                        CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
                card.SetResourceReference(Border.BackgroundProperty, "BgCard");
                card.SetResourceReference(Border.BorderBrushProperty, "Border");

                var sp = new StackPanel();
                var head = new Grid();
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var title = new StackPanel();
                var nm = new TextBlock { Text = t.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
                nm.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
                var tm = new TextBlock { Text = $"{FmtShort(t.Start)} – {FmtShort(t.Start + t.Dur)}  ·  {FmtShort(t.Dur)}", FontSize = 11.5,
                                         Margin = new Thickness(0, 2, 0, 0), FontFamily = new FontFamily("Consolas") };
                tm.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
                title.Children.Add(nm); title.Children.Add(tm);
                head.Children.Add(title);

                var btns = new StackPanel { Orientation = Orientation.Horizontal };
                btns.Children.Add(SmallBtn("−", "0.1 წმ ადრე", () => { take.Start = Math.Max(0, take.Start - 0.1); AfterTakeChange(); }, false));
                btns.Children.Add(SmallBtn("+", "0.1 წმ გვიან", () => { take.Start = Math.Min(Math.Max(0, _dur - take.Dur), take.Start + 0.1); AfterTakeChange(); }, false));
                btns.Children.Add(SmallBtn("", "მოძრავი ხაზის ადგილას გადატანა", () => { take.Start = Math.Clamp(Pos, 0, Math.Max(0, _dur - take.Dur)); AfterTakeChange(); }));
                btns.Children.Add(SmallBtn(t.Muted ? "" : "", t.Muted ? "ხმის ჩართვა" : "გაჩუმება", () => { take.Muted = !take.Muted; AfterTakeChange(); }));
                btns.Children.Add(SmallBtn("", "წაშლა", () => DeleteTake(take)));
                Grid.SetColumn(btns, 1);
                head.Children.Add(btns);
                sp.Children.Add(head);

                var vol = new Grid { Margin = new Thickness(0, 8, 0, 0) };
                vol.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
                vol.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                vol.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
                var vl = new TextBlock { Text = "სიმაღლე", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                vl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                var sl = new Slider { Minimum = 0, Maximum = 150, Value = t.Gain * 100, VerticalAlignment = VerticalAlignment.Center };
                var vt = new TextBlock { Text = $"{(int)(t.Gain * 100)}%", FontSize = 12, FontWeight = FontWeights.SemiBold,
                                         HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                vt.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
                sl.ValueChanged += (_, ev) =>
                {
                    take.Gain = ev.NewValue / 100.0;
                    vt.Text = $"{(int)ev.NewValue}%";
                    if (_players.TryGetValue(take.Id, out var pl)) pl.Volume = Math.Clamp(take.Gain, 0, 1);
                };
                Grid.SetColumn(sl, 1); Grid.SetColumn(vt, 2);
                vol.Children.Add(vl); vol.Children.Add(sl); vol.Children.Add(vt);
                sp.Children.Add(vol);

                card.Child = sp;
                if (t.Muted) card.Opacity = 0.6;
                TakesPanel.Children.Add(card);
            }
        }

        private void AfterTakeChange()
        {
            RefreshTakesUi();
            RedrawTimeline();
        }

        private void DeleteTake(Take t)
        {
            bool was = _playing;
            if (was) StopPlayback();
            _takes.Remove(t);
            try { if (t.Path.StartsWith(WorkDir, StringComparison.OrdinalIgnoreCase) && File.Exists(t.Path)) File.Delete(t.Path); } catch { }
            AfterTakeChange();
            UpdateExportEnabled();
        }

        private void UpdateExportEnabled()
            => BtnExport.IsEnabled = _video != null && _takes.Any(t => !t.Muted) && !_recording;

        // ════════════════════════════════════════════════════════════
        // OPTIONS
        // ════════════════════════════════════════════════════════════
        private void SlMonitor_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtMonitor != null) TxtMonitor.Text = $"{(int)SlMonitor.Value}%";
            if (_recording) ApplyOriginalVolume(Pos);
        }

        private void SlLatency_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtLatency != null) TxtLatency.Text = $"{(int)SlLatency.Value} მს";
        }

        private void SlOrig_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtOrig != null) TxtOrig.Text = $"{(int)SlOrig.Value}%";
        }

        private void Mix_Changed(object sender, RoutedEventArgs e)
        {
            if (LblOrig == null) return;
            bool auto = RbDuckAuto.IsChecked == true;
            LblOrig.Text = auto ? "ორიგინალი საუბრისას" : "ორიგინალი ხმა";
            TxtMixHint.Text = auto
                ? "ავტო: ორიგინალი ხმა სრულ დონეზეა, მაგრამ შენი ხმის დროს თავისით ჩუმდება და მერე ისევ იზრდება."
                : "ფიქსირებული: ორიგინალი ხმა მთელ ვიდეოზე ერთნაირი დონით იქნება.";
        }

        private void Win_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!IsVisible || Keyboard.Modifiers != ModifierKeys.None) return;
            if (Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is ComboBox) return;
            if (e.Key == Key.R && BtnRecord.IsEnabled) { BtnRecord_Click(this, new RoutedEventArgs()); e.Handled = true; }
            else if (e.Key == Key.Space && BtnPlay.IsEnabled && !_recording) { BtnPlay_Click(this, new RoutedEventArgs()); e.Handled = true; }
        }

        // ════════════════════════════════════════════════════════════
        // EXPORT (ffmpeg mix)
        // ════════════════════════════════════════════════════════════
        private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private string BuildExpression(IEnumerable<Take> takes, double lvl)
        {
            // volume(t) = 1 - (1-lvl) * max(mask_i(t)); mask_i ramps in over 0.15 s and out over 0.35 s around each take
            var masks = takes.Select(t =>
            {
                double a = t.Start - 0.2, b = t.Start + t.Dur + 0.4;
                return $"clip(min((t-{F(a)})/0.15,({F(b)}-t)/0.35),0,1)";
            }).ToList();
            if (masks.Count == 0) return F(lvl);
            string m = masks[0];
            for (int i = 1; i < masks.Count; i++) m = $"max({m},{masks[i]})";
            return $"1-(1-{F(lvl)})*{m}";
        }

        private async void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            if (_video == null || _recording) return;
            var active = _takes.Where(t => !t.Muted && File.Exists(t.Path)).ToList();
            if (active.Count == 0) return;

            string dir = System.IO.Path.GetDirectoryName(_video)!;
            string name = System.IO.Path.GetFileNameWithoutExtension(_video);
            string output = System.IO.Path.Combine(dir, $"{name}_voice_{DateTime.Now:HHmmss}.mp4");
            bool auto = RbDuckAuto.IsChecked == true;
            double lvl = SlOrig.Value / 100.0;

            BtnExport.IsEnabled = false;
            BtnRecord.IsEnabled = false;
            PbExport.Visibility = Visibility.Visible;
            PbExport.Value = 0;
            TxtExportStatus.Text = "ვიდეო ინახება ახალი ხმით…";
            BtnCompare.Visibility = Visibility.Collapsed;

            bool ok = false;
            string err = "";
            foreach (bool normalizeOpt in new[] { true, false })       // older ffmpeg has no amix "normalize" option
            {
                var r = await RunExportAsync(_video, active, output, auto, lvl, normalizeOpt);
                ok = r.ok; err = r.err;
                if (ok || !err.Contains("normalize", StringComparison.OrdinalIgnoreCase)) break;
            }

            PbExport.Visibility = Visibility.Collapsed;
            UpdateRecordEnabled();
            UpdateExportEnabled();

            if (!ok || !File.Exists(output))
            {
                try { if (File.Exists(output)) File.Delete(output); } catch { }
                TxtExportStatus.Text = "შენახვა ვერ მოხერხდა: " + (err.Length > 160 ? err[^160..] : err);
                Notify?.Invoke("ხმის დამატება ვერ მოხერხდა", "error");
                return;
            }

            _lastOutput = output;
            double mb = new FileInfo(output).Length / 1048576.0;
            TxtExportStatus.Text = $"მზადაა: {System.IO.Path.GetFileName(output)} · {mb:0.#} MB";
            BtnCompare.Visibility = Visibility.Visible;
            Notify?.Invoke($"ვიდეო ახალი ხმით მზადაა: {System.IO.Path.GetFileName(output)}", "success");
            Processed?.Invoke(_video, output);
        }

        private async Task<(bool ok, string err)> RunExportAsync(string video, List<Take> takes, string output,
            bool auto, double lvl, bool useNormalizeOpt)
        {
            var si = new ProcessStartInfo(AppPaths.Ffmpeg())
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8
            };
            void A(params string[] a) { foreach (var s in a) si.ArgumentList.Add(s); }

            A("-y", "-hide_banner", "-loglevel", "error", "-stats", "-i", video);
            foreach (var t in takes) A("-i", t.Path);

            var chains = new List<string>();
            var voLabels = new List<string>();
            for (int i = 0; i < takes.Count; i++)
            {
                int ms = (int)Math.Round(takes[i].Start * 1000);
                string lab = $"v{i}";
                chains.Add($"[{i + 1}:a]aresample=48000,aformat=channel_layouts=mono,volume={F(takes[i].Gain)},adelay={ms}:all=1[{lab}]");
                voLabels.Add($"[{lab}]");
            }

            string norm = useNormalizeOpt ? ":normalize=0" : "";
            string voOut;
            if (voLabels.Count == 1) voOut = voLabels[0];
            else
            {
                chains.Add(string.Concat(voLabels) + $"amix=inputs={voLabels.Count}:duration=longest{norm}[vo]");
                voOut = "[vo]";
            }

            if (_hasAudio)
            {
                string expr = auto ? BuildExpression(takes, lvl) : F(lvl);
                string volFilter = auto ? $"volume='{expr}':eval=frame" : $"volume={expr}";
                chains.Add($"[0:a]aresample=48000,aformat=channel_layouts=mono,{volFilter}[o]");
                chains.Add($"[o]{voOut}amix=inputs=2:duration=first{norm}[aout]");
            }
            else
            {
                chains.Add($"{voOut}anull[aout]");
            }

            A("-filter_complex", string.Join(";", chains));
            A("-map", "0:v:0", "-map", "[aout]", "-c:v", "copy", "-c:a", "aac", "-b:a", "192k",
              "-shortest", "-movflags", "+faststart", output);

            var errBuf = new StringBuilder();
            double total = Math.Max(1, _dur);
            return await Task.Run(() =>
            {
                try
                {
                    using var p = new Process { StartInfo = si };
                    p.ErrorDataReceived += (_, ev) =>
                    {
                        if (string.IsNullOrEmpty(ev.Data)) return;
                        var m = Regex.Match(ev.Data, @"time=(\d+):(\d+):(\d+(?:\.\d+)?)");
                        if (m.Success)
                        {
                            double sec = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60
                                         + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                            Dispatcher.InvokeAsync(() => PbExport.Value = Math.Min(99, sec / total * 100));
                        }
                        else errBuf.AppendLine(ev.Data);
                    };
                    p.Start();
                    p.BeginErrorReadLine();
                    p.WaitForExit();
                    return (p.ExitCode == 0, errBuf.ToString().Trim());
                }
                catch (Exception ex) { return (false, ex.Message); }
            });
        }

        private void BtnCompare_Click(object sender, RoutedEventArgs e)
        {
            if (_video != null && _lastOutput != null) OpenInAB?.Invoke(_video, _lastOutput);
        }

        // ── helpers ──────────────────────────────────────────────────
        private static string FmtShort(double sec)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, sec));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                                     : $"{t.Minutes}:{t.Seconds:00}.{t.Milliseconds / 100}";
        }
    }
}

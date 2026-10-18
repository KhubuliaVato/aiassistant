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
using System.Windows.Threading;
using NAudio.Wave;

namespace AiAssistant
{
    /// <summary>
    /// Voice-over studio: watch the video, read the script (teleprompter), record, punch-in anywhere, polish the voice
    /// (denoise / EQ / de-ess / compress / loudness) and save the video with the new voice.
    /// </summary>
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
            public double Start;           // where it begins on the video timeline (s)
            public double Offset;          // how far into the file the audible part begins (s)
            public double Dur;             // audible length (s)
            public double FileDur;         // length of the whole file (s)
            public double Gain = 1.0;      // 0..1.5
            public bool Muted;
            public float[] Peaks = Array.Empty<float>();   // peaks of the WHOLE file
            public Take Clone() => (Take)MemberwiseClone();
            public double End => Start + Dur;
        }

        // ── project state ─────────────────────────────────────────────
        private string? _video;
        private double _dur;
        private double _fps = 25;
        private bool _hasAudio = true;
        private List<Take> _takes = new();
        private int _nextId = 1;
        private string? _lastOutput;
        private Take? _selTake;
        private readonly List<List<Take>> _undo = new();

        // ── playback ──────────────────────────────────────────────────
        private bool _playing;
        private bool _suppressSlider;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(30) };
        private bool _built;                                   // false while the XAML is being created (sliders fire events then)
        private readonly Dictionary<int, MediaPlayer> _players = new();
        private readonly HashSet<int> _started = new();

        private static readonly string[] VideoExt = { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".mxf", ".m4v", ".ts" };

        public VoiceOverView()
        {
            InitializeComponent();
            _built = true;
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
            LoadFxSettings();
            LoadScript();
            RefreshTakesUi();
            Mix_Changed(this, new RoutedEventArgs());
            Rec_Slider_Changed(this, null!);
            SetMeter(0, -90);
        }

        // ════════════════════════════════════════════════════════════
        // WORK FOLDER · helpers
        // ════════════════════════════════════════════════════════════
        private string WorkDir
        {
            get
            {
                string root = !string.IsNullOrEmpty(CacheFolder) ? CacheFolder : System.IO.Path.Combine(AppPaths.DataDir, "cache");
                string d = System.IO.Path.Combine(root, "voiceover");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private static string FmtShort(double sec)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, sec));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}.{t.Milliseconds / 100}";
        }

        private void Toast(string msg, string kind = "success") => Notify?.Invoke(msg, kind);

        // ════════════════════════════════════════════════════════════
        // VIDEO
        // ════════════════════════════════════════════════════════════
        private void BtnPick_Click(object sender, RoutedEventArgs e)
        {
            if (_recording) return;
            var ofd = new Microsoft.Win32.OpenFileDialog { Title = "ვიდეოს არჩევა", Filter = "Video|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.mxf;*.m4v|All|*.*" };
            if (ofd.ShowDialog() == true) LoadVideo(ofd.FileName);
        }

        /// <summary>Public: other tabs can send a video here.</summary>
        public void LoadVideo(string path)
        {
            if (_recording || !File.Exists(path)) return;
            StopPlayback();
            _video = path;
            _lastOutput = null;
            _takes.Clear(); _undo.Clear(); _selTake = null;
            _punchIn = _punchOut = null;
            _origPeaks = Array.Empty<float>();
            RefreshTakesUi();

            var info = ProbeVideo(path);
            _dur = info.dur; _fps = info.fps; _hasAudio = info.hasAudio;

            DropZone.Visibility = Visibility.Collapsed;
            BtnChange.Visibility = Visibility.Visible;
            Media.Source = new Uri(path, UriKind.Absolute);
            Media.Volume = 1;
            Media.Play(); Media.Pause();

            SlPos.IsEnabled = BtnPlay.IsEnabled = BtnRewind.IsEnabled = BtnBack5.IsEnabled = BtnFwd5.IsEnabled = true;
            _suppressSlider = true;
            SlPos.Maximum = Math.Max(0.1, _dur);
            SlPos.Value = 0;
            _suppressSlider = false;
            UpdatePosText(0);
            BtnCompare.Visibility = Visibility.Collapsed;
            TxtExportStatus.Text = _hasAudio ? "" : "ვიდეოს საკუთარი ხმა არ აქვს — ახალი ხმა დაემატება პირდაპირ.";
            UpdatePunchText();
            UpdateRecordEnabled();
            UpdateExportEnabled();
            RedrawTimeline();
            _ = LoadOriginalPeaksAsync(path);
        }

        private static (double dur, double fps, bool hasAudio) ProbeVideo(string path)
        {
            double dur = 0, fps = 25; bool audio = false;
            try
            {
                var si = new ProcessStartInfo(AppPaths.Ffmpeg()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8 };
                si.ArgumentList.Add("-hide_banner"); si.ArgumentList.Add("-i"); si.ArgumentList.Add(path);
                using var p = Process.Start(si);
                string err = p!.StandardError.ReadToEnd();
                p.WaitForExit(15000);
                var d = Regex.Match(err, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
                if (d.Success)
                    dur = int.Parse(d.Groups[1].Value) * 3600 + int.Parse(d.Groups[2].Value) * 60 + double.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture);
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
            try { Media.Position = TimeSpan.FromMilliseconds(40); } catch { }       // shows the first frame instead of black
        }

        private void Media_Failed(object? sender, ExceptionRoutedEventArgs e) => TxtExportStatus.Text = "ვიდეო ვერ გაიხსნა: " + e.ErrorException.Message;

        private void Media_Ended(object sender, RoutedEventArgs e)
        {
            if (_recording) _ = StopRecordingAsync();
            else StopPlayback();
        }

        // ── drag & drop ──
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
        // TRANSPORT / PLAYBACK
        // ════════════════════════════════════════════════════════════
        private double Pos => Media.Position.TotalSeconds;

        private void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            if (_recording || _video == null) return;
            if (_playing) StopPlayback(); else _ = StartPlaybackAsync();
        }

        private void BtnRewind_Click(object sender, RoutedEventArgs e)
        {
            if (_recording) return;
            bool was = _playing;
            StopPlayback();
            SeekTo(0);
            if (was) _ = StartPlaybackAsync();
        }

        private void BtnSkip_Click(object sender, RoutedEventArgs e)
        {
            if (_recording || sender is not FrameworkElement fe || !double.TryParse(fe.Tag as string, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return;
            bool was = _playing;
            if (was) StopPlayback();
            SeekTo(Pos + d);
            if (was) _ = StartPlaybackAsync();
        }

        private bool ListeningToVoice => ListenOrig.IsChecked != true;

        private void Listen_Changed(object sender, RoutedEventArgs e)
        {
            if (ListenFx.IsChecked == true)
            {
                if (!FxActive) { Toast("ჯერ აირჩიე დამუშავება მარჯვნივ (მე-5 ნაბიჯი)", "error"); ListenRaw.IsChecked = true; return; }
                _ = EnsureProcessedAsync(true);
            }
            if (_playing) { bool pos = true; StopPlayback(); if (pos) _ = StartPlaybackAsync(); }
        }

        private async Task StartPlaybackAsync()
        {
            if (_video == null) return;
            if (ListenFx.IsChecked == true && FxActive && !await EnsureProcessedAsync(false)) return;
            if (Pos >= _dur - 0.1) SeekTo(0);

            _players.Clear(); _started.Clear();
            if (ListeningToVoice)
                foreach (var t in _takes.Where(t => !t.Muted && File.Exists(t.Path)))
                {
                    var p = new MediaPlayer();
                    p.Open(new Uri(PlayPath(t), UriKind.Absolute));
                    p.Volume = 0;
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
            if (!_recording) _timer.Stop();
            if (_playing || _recording) { try { Media.Pause(); } catch { } }
            _playing = false;
            foreach (var p in _players.Values) { try { p.Stop(); p.Close(); } catch { } }
            _players.Clear(); _started.Clear();
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

            if (_recording) RecordingTick(pos);
            else if (_playing)
            {
                foreach (var t in _takes)
                {
                    if (t.Muted || !_players.TryGetValue(t.Id, out var p)) continue;
                    bool inside = pos >= t.Start && pos < t.End - 0.03;
                    if (inside && !_started.Contains(t.Id))
                    {
                        try { p.Position = TimeSpan.FromSeconds(t.Offset + (pos - t.Start)); p.Play(); } catch { }
                        _started.Add(t.Id);
                    }
                    else if (!inside && _started.Contains(t.Id))
                    {
                        try { p.Pause(); } catch { }
                        _started.Remove(t.Id);
                    }
                    if (inside)
                    {
                        double fade = Math.Min(1, Math.Min((pos - t.Start) / 0.02, (t.End - pos) / 0.04));
                        try { p.Volume = Math.Clamp(t.Gain, 0, 1) * Math.Max(0, fade); } catch { }
                    }
                }
                ApplyOriginalVolume(pos);
            }

            _suppressSlider = true;
            SlPos.Value = Math.Min(SlPos.Maximum, pos);
            _suppressSlider = false;
            UpdatePosText(pos);
            UpdatePlayhead(pos);
            if (_recording) RedrawLiveRec();
        }

        /// <summary>How loud the original track is at time t (fixed level, or ducked only while a take is speaking).</summary>
        private double OriginalLevel(double t)
        {
            if (!ListeningToVoice) return 1;
            double lvl = SlOrig.Value / 100.0;
            if (RbDuckAuto.IsChecked != true) return lvl;
            double mask = 0;
            foreach (var tk in _takes.Where(x => !x.Muted))
            {
                double a = tk.Start - 0.2, b = tk.End + 0.4;
                double m = Math.Clamp(Math.Min((t - a) / 0.15, (b - t) / 0.35), 0, 1);
                if (m > mask) mask = m;
            }
            return 1 - (1 - lvl) * mask;
        }

        private void ApplyOriginalVolume(double t)
        {
            double v = OriginalLevel(t);
            if (_recording) v = ChkMonitor.IsChecked == true ? SlMonitor.Value / 100.0 : 0;   // while recording the video is only a cue — silent unless switched on
            Media.Volume = Math.Clamp(v, 0, 1);
        }

        private void UpdatePosText(double sec) => TxtPos.Text = $"{FmtShort(sec)} / {FmtShort(_dur)}";

        // ════════════════════════════════════════════════════════════
        // TAKES: model, undo, carve, split
        // ════════════════════════════════════════════════════════════
        private void PushUndo()
        {
            _undo.Add(_takes.Select(t => t.Clone()).ToList());
            if (_undo.Count > 40) _undo.RemoveAt(0);
            BtnUndo.IsEnabled = true;
        }

        private void BtnUndo_Click(object sender, RoutedEventArgs e) => Undo();

        private void Undo()
        {
            if (_recording || _undo.Count == 0) return;
            bool was = _playing; if (was) StopPlayback();
            _takes = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
            _selTake = null;
            BtnUndo.IsEnabled = _undo.Count > 0;
            AfterTakeChange();
        }

        /// <summary>A new take wins over older ones on the part where they overlap (punch-in).</summary>
        private void CarveTakes(double a, double b, Take keep)
        {
            var result = new List<Take>();
            foreach (var t in _takes)
            {
                if (t == keep || t.End <= a + 0.001 || t.Start >= b - 0.001) { result.Add(t); continue; }
                if (t.Start < a)                                    // the left part survives
                {
                    var left = t.Clone(); left.Id = _nextId++; left.Dur = a - t.Start; left.Name = t.Name;
                    result.Add(left);
                }
                if (t.End > b)                                      // the right part survives
                {
                    var right = t.Clone(); right.Id = _nextId++;
                    right.Offset = t.Offset + (b - t.Start); right.Start = b; right.Dur = t.End - b; right.Name = t.Name;
                    result.Add(right);
                }
            }
            _takes = result;
        }

        private void BtnSplit_Click(object sender, RoutedEventArgs e) => SplitAt(Pos);

        private void SplitAt(double sec)
        {
            if (_recording) return;
            var t = _takes.FirstOrDefault(x => sec > x.Start + 0.05 && sec < x.End - 0.05);
            if (t == null) { Toast("გასაჭრელად დააყენე მოძრავი ხაზი ჩანაწერის შიგნით", "error"); return; }
            PushUndo();
            var right = t.Clone(); right.Id = _nextId++;
            right.Offset = t.Offset + (sec - t.Start); right.Start = sec; right.Dur = t.End - sec;
            t.Dur = sec - t.Start;
            _takes.Add(right);
            _selTake = right;
            AfterTakeChange();
        }

        private void AfterTakeChange()
        {
            RefreshTakesUi();
            RedrawTimeline();
            UpdateExportEnabled();
            InvalidateFxInfo();
        }

        private void DeleteTake(Take t)
        {
            if (_recording) return;
            bool was = _playing; if (was) StopPlayback();
            PushUndo();
            _takes.Remove(t);
            if (_selTake == t) _selTake = null;
            AfterTakeChange();
        }

        private void UpdateExportEnabled()
        {
            bool any = _video != null && _takes.Any(t => !t.Muted) && !_recording;
            BtnExport.IsEnabled = any;
            BtnExportWav.IsEnabled = any;
            BtnFxPreview.IsEnabled = _takes.Count > 0 && FxActive;
        }

        // ════════════════════════════════════════════════════════════
        // ADD AUDIO FROM A FILE
        // ════════════════════════════════════════════════════════════
        private async void BtnAddAudio_Click(object sender, RoutedEventArgs e)
        {
            if (_video == null) { Toast("ჯერ აირჩიე ვიდეო", "error"); return; }
            var ofd = new Microsoft.Win32.OpenFileDialog { Title = "აუდიო ფაილი", Filter = "Audio|*.wav;*.mp3;*.m4a;*.aac;*.flac;*.ogg|All|*.*" };
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
            if (dur <= 0) { Toast("აუდიო ფაილი ვერ წავიკითხე", "error"); return; }
            PushUndo();
            var take = new Take
            {
                Id = _nextId++, Name = System.IO.Path.GetFileNameWithoutExtension(path), Path = path,
                Start = Math.Min(Pos, Math.Max(0, _dur - 0.5)), Dur = dur, FileDur = dur, Peaks = peaks
            };
            _takes.Add(take);
            _selTake = take;
            AfterTakeChange();
        }

        private static float[] ComputePeaks(string path, int buckets = 400)
        {
            try
            {
                using var r = new AudioFileReader(path);
                long total = r.Length / (r.WaveFormat.BitsPerSample / 8);
                if (total <= 0) return new float[buckets];
                long per = Math.Max(1, total / buckets);
                var peaks = new float[buckets];
                var buf = new float[4096];
                long idx = 0; int read;
                while ((read = r.Read(buf, 0, buf.Length)) > 0)
                    for (int i = 0; i < read; i++, idx++)
                    {
                        int b = (int)Math.Min(buckets - 1, idx / per);
                        float v = Math.Abs(buf[i]);
                        if (v > peaks[b]) peaks[b] = v;
                    }
                float max = peaks.Max();
                if (max > 0.0001f) for (int i = 0; i < peaks.Length; i++) peaks[i] = Math.Min(1f, peaks[i] / max);
                return peaks;
            }
            catch { return new float[buckets]; }
        }

        // ════════════════════════════════════════════════════════════
        // TAKES LIST
        // ════════════════════════════════════════════════════════════
        private Button SmallBtn(string content, string tip, Action a, bool glyph = true, double w = 32)
        {
            var b = new Button { Content = content, ToolTip = tip, Width = w, Height = 30, FontSize = glyph ? 12 : 11.5, Style = (Style)FindResource("ToolButton"), Margin = new Thickness(0, 0, 4, 0) };
            if (!glyph) b.FontFamily = new FontFamily("Segoe UI");
            b.Click += (_, _) => a();
            return b;
        }

        private void RefreshTakesUi()
        {
            if (TakesPanel == null) return;
            TakesPanel.Children.Clear();
            TxtNoTakes.Visibility = _takes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            BtnUndo.IsEnabled = _undo.Count > 0;

            foreach (var t in _takes.OrderBy(x => x.Start))
            {
                var take = t;
                var card = new Border { Padding = new Thickness(12, 10, 10, 10), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1.5), Cursor = Cursors.Hand };
                card.SetResourceReference(Border.BackgroundProperty, "BgCard");
                if (t == _selTake) card.SetResourceReference(Border.BorderBrushProperty, "Accent"); else card.SetResourceReference(Border.BorderBrushProperty, "Border");
                card.MouseLeftButtonDown += (_, ev) => { if (ev.OriginalSource is TextBox || ev.OriginalSource is Slider) return; _selTake = take; RefreshTakesUi(); RedrawTimeline(); };

                var sp = new StackPanel();
                var head = new Grid();
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var title = new StackPanel();
                var nm = new TextBox { Text = t.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0), ToolTip = "სახელის შეცვლა" };
                nm.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
                nm.LostFocus += (_, _) => { take.Name = nm.Text.Trim().Length > 0 ? nm.Text.Trim() : take.Name; RedrawTimeline(); };
                var tm = new TextBlock { Text = $"{FmtShort(t.Start)} – {FmtShort(t.End)}  ·  {FmtShort(t.Dur)}", FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0), FontFamily = new FontFamily("Consolas") };
                tm.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
                title.Children.Add(nm); title.Children.Add(tm);
                head.Children.Add(title);

                var btns = new StackPanel { Orientation = Orientation.Horizontal };
                btns.Children.Add(SmallBtn("−", "0.1 წმ ადრე", () => { PushUndo(); take.Start = Math.Max(0, take.Start - 0.1); AfterTakeChange(); }, false));
                btns.Children.Add(SmallBtn("+", "0.1 წმ გვიან", () => { PushUndo(); take.Start = Math.Min(Math.Max(0, _dur - take.Dur), take.Start + 0.1); AfterTakeChange(); }, false));
                btns.Children.Add(SmallBtn("", "მოძრავი ხაზის ადგილას გადატანა", () => { PushUndo(); take.Start = Math.Clamp(Pos, 0, Math.Max(0, _dur - take.Dur)); AfterTakeChange(); }));
                btns.Children.Add(SmallBtn(t.Muted ? "" : "", t.Muted ? "ხმის ჩართვა (M)" : "გაჩუმება (M)", () => { PushUndo(); take.Muted = !take.Muted; AfterTakeChange(); }));
                btns.Children.Add(SmallBtn("", "წაშლა (Del)", () => DeleteTake(take)));
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
                var vt = new TextBlock { Text = $"{(int)(t.Gain * 100)}%", FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                vt.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
                sl.ValueChanged += (_, ev) => { take.Gain = ev.NewValue / 100.0; vt.Text = $"{(int)ev.NewValue}%"; };
                Grid.SetColumn(sl, 1); Grid.SetColumn(vt, 2);
                vol.Children.Add(vl); vol.Children.Add(sl); vol.Children.Add(vt);
                sp.Children.Add(vol);

                card.Child = sp;
                if (t.Muted) card.Opacity = 0.6;
                TakesPanel.Children.Add(card);
            }
        }

        // ════════════════════════════════════════════════════════════
        // OPTIONS · keyboard
        // ════════════════════════════════════════════════════════════
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
            if (!IsVisible) return;
            if (e.Key == Key.Escape && (_recording || _countingDown)) { if (_recording) _ = StopRecordingAsync(); else _cancelCount = true; e.Handled = true; return; }
            if (Keyboard.FocusedElement is TextBox || (Keyboard.FocusedElement is ComboBox cb && cb.IsDropDownOpen)) return;
            if (_recording && e.Key == Key.Space) { _ = StopRecordingAsync(); e.Handled = true; return; }
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (ctrl && e.Key == Key.Z) { Undo(); e.Handled = true; return; }
            if (Keyboard.Modifiers != ModifierKeys.None) return;
            switch (e.Key)
            {
                case Key.R when BtnRecord.IsEnabled: BtnRecord_Click(this, new RoutedEventArgs()); e.Handled = true; break;
                case Key.Space when BtnPlay.IsEnabled && !_recording: BtnPlay_Click(this, new RoutedEventArgs()); e.Handled = true; break;
                case Key.I when _video != null && !_recording: BtnSetIn_Click(this, new RoutedEventArgs()); e.Handled = true; break;
                case Key.O when _video != null && !_recording: BtnSetOut_Click(this, new RoutedEventArgs()); e.Handled = true; break;
                case Key.S when _video != null && !_recording: SplitAt(Pos); e.Handled = true; break;
                case Key.Home when _video != null && !_recording: BtnRewind_Click(this, new RoutedEventArgs()); e.Handled = true; break;
                case Key.Left when _video != null && !_recording: SeekTo(Pos - 5); e.Handled = true; break;
                case Key.Right when _video != null && !_recording: SeekTo(Pos + 5); e.Handled = true; break;
                case Key.Delete when _selTake != null && !_recording: DeleteTake(_selTake); e.Handled = true; break;
                case Key.M when _selTake != null && !_recording: PushUndo(); _selTake.Muted = !_selTake.Muted; AfterTakeChange(); e.Handled = true; break;
            }
        }

        private void BtnCompare_Click(object sender, RoutedEventArgs e)
        {
            if (_video != null && _lastOutput != null) OpenInAB?.Invoke(_video, _lastOutput);
        }
    }
}

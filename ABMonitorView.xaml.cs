using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AiAssistant
{
    public partial class ABMonitorView : UserControl
    {
        // ── what ffmpeg tells us about a file ─────────────────────────
        private sealed class VInfo
        {
            public double Duration;
            public int W, H;
            public double Fps;
            public string Codec = "";
            public int Kbps;
            public long Size;
            public bool HasAudio;
        }

        private string? _pathA, _pathB;
        private VInfo? _infoA, _infoB;
        private bool _aReady, _bReady;

        private bool _playing;
        private bool _loop = true;
        private double _speed = 1.0;
        private double _offsetMs;                 // B is shown at (A position + offset)
        private int _mode;                        // 0 side-by-side, 1 wipe, 2 flip
        private bool _flipShowA = true;
        private double _wipeFrac = 0.5;
        private bool _updatingSlider;

        // zoom / pan shared by both videos
        private readonly ScaleTransform _sc = new(1, 1);
        private readonly TranslateTransform _tr = new(0, 0);
        private double _scale = 1;
        private bool _dragWipe, _panning;
        private Point _panStart;
        private double _panTx0, _panTy0;

        private CancellationTokenSource? _analyzeCts;

        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };

        public ABMonitorView()
        {
            InitializeComponent();

            var tg = new TransformGroup();
            tg.Children.Add(_sc);
            tg.Children.Add(_tr);
            MediaA.RenderTransform = tg;
            MediaB.RenderTransform = tg;

            _timer.Tick += Timer_Tick;
            _timer.Start();

            Loaded += (_, _) =>
            {
                if (Window.GetWindow(this) is Window w)
                {
                    w.PreviewKeyDown -= Win_PreviewKeyDown;
                    w.PreviewKeyDown += Win_PreviewKeyDown;
                }
            };
            // leaving the tab pauses playback so the sound doesn't keep running in the background
            IsVisibleChanged += (_, _) => { if (!IsVisible && _playing) SetPlaying(false); };
        }

        // ════════════════════════════════════════════════════════════
        // FILE LOADING
        // ════════════════════════════════════════════════════════════
        private const string VideoFilter = "Video|*.mp4;*.mov;*.mxf;*.avi;*.mkv;*.webm|All|*.*";

        private void BtnPickA_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog { Title = "A — ორიგინალი ვიდეო", Filter = VideoFilter };
            if (ofd.ShowDialog() == true) LoadA(ofd.FileName);
        }

        private void BtnPickB_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog { Title = "B — დამუშავებული ვიდეო", Filter = VideoFilter };
            if (ofd.ShowDialog() == true) LoadB(ofd.FileName);
        }

        /// <summary>Public API: other tabs can push a before/after pair in here.</summary>
        public void LoadPair(string a, string b)
        {
            LoadA(a);
            LoadB(b);
        }

        public void LoadA(string path)
        {
            if (!File.Exists(path)) return;
            SetPlaying(false);
            _pathA = path;
            _aReady = false;
            _infoA = null;
            TxtPathA.Text = Path.GetFileName(path);
            TxtMetaA.Text = "იტვირთება…";
            PlaceholderA.Visibility = Visibility.Collapsed;
            MediaA.Source = new Uri(path, UriKind.Absolute);
            MediaA.Play(); MediaA.Pause();            // a Manual MediaElement only opens after a Play/Pause
            ResetAnalysis();
            _ = ProbeAsync(path, isA: true);
            UpdateUi();
        }

        public void LoadB(string path)
        {
            if (!File.Exists(path)) return;
            SetPlaying(false);
            _pathB = path;
            _bReady = false;
            _infoB = null;
            TxtPathB.Text = Path.GetFileName(path);
            TxtMetaB.Text = "იტვირთება…";
            PlaceholderB.Visibility = Visibility.Collapsed;
            MediaB.Source = new Uri(path, UriKind.Absolute);
            MediaB.Play(); MediaB.Pause();
            ResetAnalysis();
            _ = ProbeAsync(path, isA: false);
            UpdateUi();
        }

        private void MediaA_Opened(object sender, RoutedEventArgs e)
        {
            _aReady = true;
            MediaA.SpeedRatio = _speed;
            ApplyAudio();
            MediaA.Position = TimeSpan.Zero;
            UpdateUi();
        }

        private void MediaB_Opened(object sender, RoutedEventArgs e)
        {
            _bReady = true;
            MediaB.SpeedRatio = _speed;
            ApplyAudio();
            MediaB.Position = TimeSpan.FromMilliseconds(Math.Max(0, _offsetMs));
            UpdateUi();
        }

        private void MediaA_Failed(object? sender, ExceptionRoutedEventArgs e)
        {
            _aReady = false;
            TxtMetaA.Text = "ვერ გაიხსნა: " + e.ErrorException.Message;
            PlaceholderA.Visibility = Visibility.Visible;
            UpdateUi();
        }

        private void MediaB_Failed(object? sender, ExceptionRoutedEventArgs e)
        {
            _bReady = false;
            TxtMetaB.Text = "ვერ გაიხსნა: " + e.ErrorException.Message;
            PlaceholderB.Visibility = Visibility.Visible;
            UpdateUi();
        }

        private void BtnSwap_Click(object sender, RoutedEventArgs e)
        {
            string? a = _pathA, b = _pathB;
            if (a == null && b == null) return;
            if (b != null) LoadA(b); else ClearA();
            if (a != null) LoadB(a); else ClearB();
            _offsetMs = -_offsetMs;
            TxtOffset.Text = FmtOffset();
        }

        private void ClearA()
        {
            SetPlaying(false);
            _pathA = null; _infoA = null; _aReady = false;
            MediaA.Source = null;
            TxtPathA.Text = "ორიგინალი ვიდეო"; TxtMetaA.Text = "ფაილი არ არის არჩეული";
            PlaceholderA.Visibility = Visibility.Visible;
            UpdateUi();
        }

        private void ClearB()
        {
            SetPlaying(false);
            _pathB = null; _infoB = null; _bReady = false;
            MediaB.Source = null;
            TxtPathB.Text = "დამუშავებული ვიდეო"; TxtMetaB.Text = "ფაილი არ არის არჩეული";
            PlaceholderB.Visibility = Visibility.Visible;
            UpdateUi();
        }

        // ── drag & drop ──────────────────────────────────────────────
        private void Pane_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                DropHint.Visibility = Visibility.Visible;
            }
            else e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        private void Pane_DragLeave(object sender, DragEventArgs e) => DropHint.Visibility = Visibility.Collapsed;

        private static string? FirstDropped(DragEventArgs e)
            => e.Data.GetData(DataFormats.FileDrop) is string[] f && f.Length > 0 ? f[0] : null;

        private void PaneA_Drop(object sender, DragEventArgs e)
        {
            DropHint.Visibility = Visibility.Collapsed;
            // dropping two files at once → A and B
            if (e.Data.GetData(DataFormats.FileDrop) is string[] f && f.Length >= 2) { LoadPair(f[0], f[1]); e.Handled = true; return; }
            var p = FirstDropped(e);
            if (p != null) LoadA(p);
            e.Handled = true;
        }

        private void PaneB_Drop(object sender, DragEventArgs e)
        {
            DropHint.Visibility = Visibility.Collapsed;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] f && f.Length >= 2) { LoadPair(f[0], f[1]); e.Handled = true; return; }
            var p = FirstDropped(e);
            if (p != null) LoadB(p);
            e.Handled = true;
        }

        // ════════════════════════════════════════════════════════════
        // FILE INFO (ffmpeg -i)
        // ════════════════════════════════════════════════════════════
        private async Task ProbeAsync(string path, bool isA)
        {
            VInfo? info = await Task.Run(() => Probe(path));
            // the user may have loaded something else in the meantime
            if (isA && !string.Equals(path, _pathA, StringComparison.OrdinalIgnoreCase)) return;
            if (!isA && !string.Equals(path, _pathB, StringComparison.OrdinalIgnoreCase)) return;

            if (isA) _infoA = info; else _infoB = info;
            (isA ? TxtMetaA : TxtMetaB).Text = info == null ? "ინფორმაცია ვერ წაიკითხა" : Describe(info);
            UpdateDiff();
            UpdateUi();
        }

        private static VInfo? Probe(string path)
        {
            try
            {
                var psi = new ProcessStartInfo(AppPaths.Ffmpeg())
                {
                    RedirectStandardError = true, RedirectStandardOutput = true,
                    UseShellExecute = false, CreateNoWindow = true,
                    StandardErrorEncoding = Encoding.UTF8
                };
                psi.ArgumentList.Add("-hide_banner");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(path);
                using var p = Process.Start(psi);
                if (p == null) return null;
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit(15000);

                var info = new VInfo { Size = new FileInfo(path).Length };
                var d = Regex.Match(err, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
                if (d.Success)
                    info.Duration = int.Parse(d.Groups[1].Value) * 3600 + int.Parse(d.Groups[2].Value) * 60
                                    + double.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture);
                var br = Regex.Match(err, @"bitrate:\s*(\d+)\s*kb/s");
                if (br.Success) info.Kbps = int.Parse(br.Groups[1].Value);

                foreach (var line in err.Split('\n'))
                {
                    if (line.Contains("Video:") && info.W == 0)
                    {
                        var c = Regex.Match(line, @"Video:\s*([A-Za-z0-9_]+)");
                        if (c.Success) info.Codec = c.Groups[1].Value;
                        var r = Regex.Match(line, @",\s*(\d{2,5})x(\d{2,5})");
                        if (r.Success) { info.W = int.Parse(r.Groups[1].Value); info.H = int.Parse(r.Groups[2].Value); }
                        var f = Regex.Match(line, @"(\d+(?:\.\d+)?)\s*fps");
                        if (!f.Success) f = Regex.Match(line, @"(\d+(?:\.\d+)?)\s*tbr");
                        if (f.Success) info.Fps = double.Parse(f.Groups[1].Value, CultureInfo.InvariantCulture);
                    }
                    else if (line.Contains("Audio:")) info.HasAudio = true;
                }
                return info.W > 0 ? info : null;
            }
            catch { return null; }
        }

        private static string Describe(VInfo i)
        {
            var sb = new StringBuilder();
            sb.Append($"{i.W}×{i.H}");
            if (i.Fps > 0) sb.Append($" · {i.Fps.ToString("0.##", CultureInfo.InvariantCulture)} fps");
            if (i.Codec.Length > 0) sb.Append(" · ").Append(i.Codec.ToUpperInvariant());
            if (i.Kbps > 0) sb.Append($" · {(i.Kbps / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)} Mbps");
            sb.Append(" · ").Append(FmtSize(i.Size));
            if (i.Duration > 0) sb.Append(" · ").Append(FmtShort(TimeSpan.FromSeconds(i.Duration)));
            return sb.ToString();
        }

        private void UpdateDiff()
        {
            if (_infoA == null || _infoB == null) { DiffCard.Visibility = Visibility.Collapsed; return; }
            var lines = new StringBuilder();

            if (_infoA.Size > 0)
            {
                double pct = (1.0 - (double)_infoB.Size / _infoA.Size) * 100.0;
                if (Math.Abs(pct) < 0.5) lines.AppendLine("ზომა: პრაქტიკულად ერთნაირია");
                else if (pct > 0) lines.AppendLine($"ზომა: B {pct:0}%-ით პატარაა ({FmtSize(_infoA.Size)} → {FmtSize(_infoB.Size)})");
                else lines.AppendLine($"ზომა: B {-pct:0}%-ით დიდია ({FmtSize(_infoA.Size)} → {FmtSize(_infoB.Size)})");
            }
            lines.AppendLine(_infoA.W == _infoB.W && _infoA.H == _infoB.H
                ? $"რეზოლუცია: ერთნაირია ({_infoA.W}×{_infoA.H})"
                : $"რეზოლუცია: {_infoA.W}×{_infoA.H} → {_infoB.W}×{_infoB.H}");
            if (_infoA.Fps > 0 && _infoB.Fps > 0 && Math.Abs(_infoA.Fps - _infoB.Fps) > 0.05)
                lines.AppendLine($"კადრი/წმ: {_infoA.Fps:0.##} → {_infoB.Fps:0.##}");
            if (_infoA.Duration > 0 && _infoB.Duration > 0 && Math.Abs(_infoA.Duration - _infoB.Duration) > 0.2)
                lines.AppendLine($"ხანგრძლივობა: {FmtShort(TimeSpan.FromSeconds(_infoA.Duration))} → {FmtShort(TimeSpan.FromSeconds(_infoB.Duration))}");
            if (_infoA.HasAudio != _infoB.HasAudio)
                lines.AppendLine(_infoB.HasAudio ? "B-ს ხმა აქვს, A-ს არა" : "B-ს ხმა არ აქვს!");

            TxtDiff.Text = lines.ToString().TrimEnd();
            DiffCard.Visibility = Visibility.Visible;
        }

        // ════════════════════════════════════════════════════════════
        // PLAYBACK
        // ════════════════════════════════════════════════════════════
        private bool AnyReady => _aReady || _bReady;

        private MediaElement? Master => _aReady ? MediaA : (_bReady ? MediaB : null);

        private TimeSpan Duration
        {
            get
            {
                if (_aReady && MediaA.NaturalDuration.HasTimeSpan) return MediaA.NaturalDuration.TimeSpan;
                if (_bReady && MediaB.NaturalDuration.HasTimeSpan) return MediaB.NaturalDuration.TimeSpan;
                return TimeSpan.Zero;
            }
        }

        private double Fps => (_infoA?.Fps > 0 ? _infoA.Fps : (_infoB?.Fps > 0 ? _infoB.Fps : 25.0));

        private void SetPlaying(bool play)
        {
            if (play && !AnyReady) return;
            _playing = play;
            if (play)
            {
                if (_aReady) MediaA.Play();
                if (_bReady) MediaB.Play();
                BtnABPlay.Content = "";
            }
            else
            {
                if (_aReady) MediaA.Pause();
                if (_bReady) MediaB.Pause();
                BtnABPlay.Content = "";
            }
        }

        private void BtnABPlay_Click(object sender, RoutedEventArgs e) => SetPlaying(!_playing);

        private void BtnABRewind_Click(object sender, RoutedEventArgs e) => SeekTo(TimeSpan.Zero);

        private void BtnABPrev_Click(object sender, RoutedEventArgs e) => Step(-1, false);

        private void BtnABNext_Click(object sender, RoutedEventArgs e) => Step(+1, false);

        private void BtnABLoop_Click(object sender, RoutedEventArgs e) => ToggleLoop();

        private void ToggleLoop()
        {
            _loop = !_loop;
            BtnABLoop.Tag = _loop ? "on" : null;
        }

        private void Step(int dir, bool bigStep)
        {
            if (!AnyReady) return;
            SetPlaying(false);
            var master = Master!;
            var delta = bigStep ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(1.0 / Fps);
            SeekTo(master.Position + (dir < 0 ? -delta : delta));
        }

        private void SeekTo(TimeSpan t)
        {
            var dur = Duration;
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            if (dur > TimeSpan.Zero && t > dur) t = dur;
            if (_aReady) MediaA.Position = t;
            if (_bReady)
            {
                var tb = t + TimeSpan.FromMilliseconds(_offsetMs);
                MediaB.Position = tb < TimeSpan.Zero ? TimeSpan.Zero : tb;
            }
            UpdateSeekUi(t);
        }

        private void SlABSeek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded || _updatingSlider || !AnyReady) return;
            SeekTo(TimeSpan.FromSeconds(e.NewValue));
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            var master = Master;
            if (master == null) return;

            var pos = master.Position;
            var dur = Duration;

            if (_playing && dur > TimeSpan.Zero && pos >= dur - TimeSpan.FromMilliseconds(60))
            {
                if (_loop) { SeekTo(TimeSpan.Zero); return; }
                SetPlaying(false);
                SeekTo(dur);
                return;
            }

            // keep B locked to A (+ offset): re-seek only when it drifts noticeably
            if (_playing && _aReady && _bReady)
            {
                var target = pos + TimeSpan.FromMilliseconds(_offsetMs);
                if (target < TimeSpan.Zero) target = TimeSpan.Zero;
                if (Math.Abs((MediaB.Position - target).TotalMilliseconds) > 140)
                    MediaB.Position = target;
            }

            UpdateSeekUi(pos);
        }

        private void UpdateSeekUi(TimeSpan pos)
        {
            var dur = Duration;
            _updatingSlider = true;
            try
            {
                SlABSeek.Maximum = Math.Max(0.01, dur.TotalSeconds);
                SlABSeek.Value = Math.Min(SlABSeek.Maximum, pos.TotalSeconds);
            }
            finally { _updatingSlider = false; }

            TxtABCurrent.Text = FmtTime(pos);
            TxtABTotal.Text = FmtTime(dur);
            if (_aReady && _bReady)
            {
                long frame = (long)Math.Round(pos.TotalSeconds * Fps);
                long total = (long)Math.Round(dur.TotalSeconds * Fps);
                TxtABStatus.Text = $"კადრი {frame} / {total}  ·  {Fps:0.##} fps";
            }
        }

        private void UpdateUi()
        {
            bool any = AnyReady;
            BtnABPlay.IsEnabled = BtnABRewind.IsEnabled = BtnABPrev.IsEnabled = BtnABNext.IsEnabled = any;
            BtnABSnap.IsEnabled = any;
            SlABSeek.IsEnabled = any;
            BtnAnalyze.IsEnabled = _aReady && _bReady && _infoA != null && _infoB != null && _analyzeCts == null;

            if (!any) TxtABStatus.Text = "ორივე ვიდეო აირჩიე შესადარებლად";
            else if (!_aReady) TxtABStatus.Text = "B ჩატვირთულია — დაამატე A (ორიგინალი)";
            else if (!_bReady) TxtABStatus.Text = "A ჩატვირთულია — დაამატე B (დამუშავებული)";
            else UpdateSeekUi(Master!.Position);
        }

        // ── speed / audio ────────────────────────────────────────────
        private void Speed_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && double.TryParse(rb.Tag?.ToString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double s))
            {
                _speed = s;
                if (_aReady) MediaA.SpeedRatio = s;
                if (_bReady) MediaB.SpeedRatio = s;
            }
        }

        private void Audio_Changed(object sender, RoutedEventArgs e) => ApplyAudio();

        private void ApplyAudio()
        {
            MediaA.Volume = RbAudA.IsChecked == true ? 1.0 : 0.0;
            MediaB.Volume = RbAudB.IsChecked == true ? 1.0 : 0.0;
        }

        // ── B offset ─────────────────────────────────────────────────
        private void Offset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b) return;
            string tag = b.Tag?.ToString() ?? "";
            double frameMs = 1000.0 / Fps;
            double delta = tag.EndsWith("f")
                ? (tag.StartsWith("-") ? -frameMs : frameMs)
                : double.Parse(tag, CultureInfo.InvariantCulture);
            _offsetMs = Math.Clamp(Math.Round(_offsetMs + delta), -10000, 10000);
            TxtOffset.Text = FmtOffset();
            ResetAnalysis();
            if (AnyReady) SeekTo(Master!.Position);
        }

        private void Offset_Reset(object sender, RoutedEventArgs e)
        {
            _offsetMs = 0;
            TxtOffset.Text = FmtOffset();
            ResetAnalysis();
            if (AnyReady) SeekTo(Master!.Position);
        }

        private string FmtOffset() => $"{_offsetMs.ToString("+0;-0;0", CultureInfo.InvariantCulture)} მს";

        // ════════════════════════════════════════════════════════════
        // VIEW MODES
        // ════════════════════════════════════════════════════════════
        private void Mode_Changed(object sender, RoutedEventArgs e)
        {
            _mode = RbModeWipe.IsChecked == true ? 1 : RbModeFlip.IsChecked == true ? 2 : 0;
            ApplyMode();
        }

        private void SetMode(int mode)
        {
            _mode = mode;
            RbModeSide.IsChecked = mode == 0;
            RbModeWipe.IsChecked = mode == 1;
            RbModeFlip.IsChecked = mode == 2;
            ApplyMode();
        }

        private void ApplyMode()
        {
            switch (_mode)
            {
                case 0: // side by side
                    Grid.SetColumn(PaneA, 0); Grid.SetColumnSpan(PaneA, 1);
                    Grid.SetColumn(PaneB, 2); Grid.SetColumnSpan(PaneB, 1);
                    PaneA.Clip = null;
                    PaneA.Visibility = PaneB.Visibility = Visibility.Visible;
                    Panel.SetZIndex(PaneA, 0); Panel.SetZIndex(PaneB, 0);
                    WipeLayer.Visibility = Visibility.Collapsed;
                    break;

                case 1: // wipe: B underneath, A on top clipped to the left of the divider
                    Grid.SetColumn(PaneA, 0); Grid.SetColumnSpan(PaneA, 3);
                    Grid.SetColumn(PaneB, 0); Grid.SetColumnSpan(PaneB, 3);
                    PaneA.Visibility = PaneB.Visibility = Visibility.Visible;
                    Panel.SetZIndex(PaneA, 2); Panel.SetZIndex(PaneB, 1);
                    WipeLayer.Visibility = Visibility.Visible;
                    ApplyWipe();
                    break;

                default: // flip: same place, one at a time
                    Grid.SetColumn(PaneA, 0); Grid.SetColumnSpan(PaneA, 3);
                    Grid.SetColumn(PaneB, 0); Grid.SetColumnSpan(PaneB, 3);
                    PaneA.Clip = null;
                    WipeLayer.Visibility = Visibility.Collapsed;
                    Panel.SetZIndex(PaneA, 2); Panel.SetZIndex(PaneB, 1);
                    PaneA.Visibility = _flipShowA ? Visibility.Visible : Visibility.Hidden;
                    PaneB.Visibility = Visibility.Visible;
                    break;
            }
            ClampPan();
        }

        private void Flip()
        {
            if (_mode != 2) SetMode(2);
            else
            {
                _flipShowA = !_flipShowA;
                PaneA.Visibility = _flipShowA ? Visibility.Visible : Visibility.Hidden;
            }
        }

        private void ApplyWipe()
        {
            double w = Stage.ActualWidth, h = Stage.ActualHeight;
            if (w <= 0 || h <= 0) return;
            double x = _wipeFrac * w;
            PaneA.Clip = new RectangleGeometry(new Rect(0, 0, x, h));
            Canvas.SetLeft(WipeLine, x - 1);
            WipeLine.Height = h;
            Canvas.SetLeft(WipeGrip, x - WipeGrip.Width / 2);
            Canvas.SetTop(WipeGrip, h / 2 - WipeGrip.Height / 2);
        }

        private void Stage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_mode == 1) ApplyWipe();
            ClampPan();
        }

        // ── mouse on the stage: wipe divider, pan, zoom ──────────────
        private void Stage_MouseDown(object sender, MouseButtonEventArgs e)
        {
            Focus();
            var p = e.GetPosition(Stage);
            if (_mode == 1)
            {
                double split = _wipeFrac * Stage.ActualWidth;
                if (_scale <= 1.001 || Math.Abs(p.X - split) < 26)
                {
                    _dragWipe = true;
                    _wipeFrac = Math.Clamp(p.X / Math.Max(1, Stage.ActualWidth), 0.01, 0.99);
                    ApplyWipe();
                    Stage.CaptureMouse();
                    e.Handled = true;
                    return;
                }
            }
            if (_scale > 1.001)
            {
                _panning = true;
                _panStart = p;
                _panTx0 = _tr.X; _panTy0 = _tr.Y;
                Stage.CaptureMouse();
                Stage.Cursor = Cursors.SizeAll;
                e.Handled = true;
            }
        }

        private void Stage_MouseMove(object sender, MouseEventArgs e)
        {
            var p = e.GetPosition(Stage);
            if (_dragWipe)
            {
                _wipeFrac = Math.Clamp(p.X / Math.Max(1, Stage.ActualWidth), 0.01, 0.99);
                ApplyWipe();
            }
            else if (_panning)
            {
                _tr.X = _panTx0 + (p.X - _panStart.X);
                _tr.Y = _panTy0 + (p.Y - _panStart.Y);
                ClampPan();
            }
            else if (_mode == 1)
            {
                double split = _wipeFrac * Stage.ActualWidth;
                Stage.Cursor = (_scale <= 1.001 || Math.Abs(p.X - split) < 26) ? Cursors.SizeWE : Cursors.Arrow;
            }
        }

        private void Stage_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragWipe || _panning)
            {
                _dragWipe = _panning = false;
                Stage.ReleaseMouseCapture();
                Stage.Cursor = Cursors.Arrow;
            }
        }

        private void Stage_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!AnyReady) return;
            var refPane = (_mode == 0 && e.GetPosition(Stage).X > PaneB.TranslatePoint(new Point(0, 0), Stage).X)
                ? PaneB : PaneA;
            var p = e.GetPosition(refPane);
            double factor = e.Delta > 0 ? 1.25 : 0.8;
            ZoomAround(p, _scale * factor);
            e.Handled = true;
        }

        private void Stage_ResetZoom(object sender, MouseButtonEventArgs e)
        {
            SetZoom(1, new Point(0, 0));
            e.Handled = true;
        }

        private void ZoomPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && double.TryParse(rb.Tag?.ToString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double s))
                ZoomAround(new Point(PaneA.ActualWidth / 2, PaneA.ActualHeight / 2), s);
        }

        private void ZoomAround(Point p, double newScale)
        {
            newScale = Math.Clamp(newScale, 1.0, 16.0);
            double k = newScale / _scale;
            _tr.X = p.X - (p.X - _tr.X) * k;
            _tr.Y = p.Y - (p.Y - _tr.Y) * k;
            SetZoom(newScale, null);
        }

        private void SetZoom(double scale, Point? _)
        {
            _scale = scale;
            if (scale <= 1.001) { _scale = 1; _tr.X = 0; _tr.Y = 0; }
            _sc.ScaleX = _sc.ScaleY = _scale;
            ClampPan();

            ZoomChip.Visibility = _scale > 1.001 ? Visibility.Visible : Visibility.Collapsed;
            TxtZoomChip.Text = $"{_scale * 100:0}%";
            foreach (var rb in new[] { RbZoom1 })
                rb.IsChecked = _scale <= 1.001;
            SyncZoomRadios();
        }

        private void SyncZoomRadios()
        {
            // tick the preset that matches the current zoom, if any
            if (Content is not Grid root) return;
            foreach (var rb in FindRadios(root))
                if (rb.GroupName == "AbZoom" && double.TryParse(rb.Tag?.ToString(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double s))
                    rb.IsChecked = Math.Abs(s - _scale) < 0.01;
        }

        private static System.Collections.Generic.IEnumerable<RadioButton> FindRadios(DependencyObject root)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var c = VisualTreeHelper.GetChild(root, i);
                if (c is RadioButton rb) yield return rb;
                foreach (var n in FindRadios(c)) yield return n;
            }
        }

        private void ClampPan()
        {
            double w = PaneA.ActualWidth > 0 ? PaneA.ActualWidth : PaneB.ActualWidth;
            double h = PaneA.ActualHeight > 0 ? PaneA.ActualHeight : PaneB.ActualHeight;
            if (_mode != 0) { w = Stage.ActualWidth; h = Stage.ActualHeight; }
            if (_scale <= 1.001) { _tr.X = 0; _tr.Y = 0; return; }
            _tr.X = Math.Clamp(_tr.X, w * (1 - _scale), 0);
            _tr.Y = Math.Clamp(_tr.Y, h * (1 - _scale), 0);
        }

        // ════════════════════════════════════════════════════════════
        // KEYBOARD
        // ════════════════════════════════════════════════════════════
        private void Win_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!IsVisible) return;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;

            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            switch (e.Key)
            {
                case Key.Space: SetPlaying(!_playing); break;
                case Key.Left: Step(-1, shift); break;
                case Key.Right: Step(+1, shift); break;
                case Key.Home: if (AnyReady) SeekTo(TimeSpan.Zero); break;
                case Key.X: Flip(); break;
                case Key.L: ToggleLoop(); break;
                case Key.S: BtnABSnap_Click(this, new RoutedEventArgs()); break;
                case Key.D1: SetMode(0); break;
                case Key.D2: SetMode(1); break;
                case Key.D3: SetMode(2); break;
                default: return;
            }
            e.Handled = true;
        }

        // ════════════════════════════════════════════════════════════
        // SNAPSHOT
        // ════════════════════════════════════════════════════════════
        private void BtnABSnap_Click(object sender, RoutedEventArgs e)
        {
            if (!AnyReady || StageFrame.ActualWidth < 4) return;
            var sfd = new SaveFileDialog
            {
                Title = "შედარების კადრის შენახვა",
                Filter = "PNG|*.png",
                FileName = $"AB_{DateTime.Now:yyyyMMdd_HHmmss}.png"
            };
            if (sfd.ShowDialog() != true) return;
            SaveSnapshot(sfd.FileName);
        }

        /// <summary>Renders the comparison stage (both videos, current view mode and zoom) to a PNG.</summary>
        public bool SaveSnapshot(string path)
        {
            try
            {
                var dpi = VisualTreeHelper.GetDpi(StageFrame);
                int w = (int)Math.Ceiling(StageFrame.ActualWidth * dpi.DpiScaleX);
                int h = (int)Math.Ceiling(StageFrame.ActualHeight * dpi.DpiScaleY);
                var rtb = new RenderTargetBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                rtb.Render(StageFrame);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using var fs = File.Create(path);
                enc.Save(fs);
                TxtABStatus.Text = "კადრი შენახულია: " + Path.GetFileName(path);
                return true;
            }
            catch (Exception ex)
            {
                TxtABStatus.Text = "შენახვა ვერ მოხერხდა: " + ex.Message;
                return false;
            }
        }

        // ════════════════════════════════════════════════════════════
        // QUALITY ANALYSIS (ffmpeg ssim + psnr)
        // ════════════════════════════════════════════════════════════
        private void ResetAnalysis()
        {
            AnalyzeResult.Visibility = Visibility.Collapsed;
            PbAnalyze.Visibility = Visibility.Collapsed;
        }

        private async void BtnAnalyze_Click(object sender, RoutedEventArgs e)
        {
            // second click while running = cancel
            if (_analyzeCts != null) { _analyzeCts.Cancel(); return; }
            if (_infoA == null || _infoB == null || _pathA == null || _pathB == null) return;

            _analyzeCts = new CancellationTokenSource();
            var ct = _analyzeCts.Token;
            TxtAnalyzeBtn.Text = "შეჩერება";
            PbAnalyze.Value = 0;
            PbAnalyze.Visibility = Visibility.Visible;
            AnalyzeResult.Visibility = Visibility.Collapsed;

            string pathA = _pathA, pathB = _pathB;
            var a = _infoA; var b = _infoB;
            double offSec = _offsetMs / 1000.0;

            // work at A's resolution (capped at 1280 wide — keeps the measurement fast)
            int w = Math.Min(a.W, 1280); w -= w % 2;
            int h = (int)Math.Round(a.H * (double)w / a.W / 2.0) * 2;
            double fps = a.Fps > 0 ? a.Fps : 25;
            double ssA = Math.Max(0, -offSec), ssB = Math.Max(0, offSec);
            double total = Math.Max(0.5, Math.Min(a.Duration - ssA, b.Duration - ssB));

            var result = await Task.Run(() => RunAnalysis(pathA, pathB, w, h, fps, ssA, ssB, total, ct,
                pct => Dispatcher.BeginInvoke(() => PbAnalyze.Value = pct)));

            bool cancelled = ct.IsCancellationRequested;
            _analyzeCts.Dispose();
            _analyzeCts = null;
            TxtAnalyzeBtn.Text = "გაზომვა";
            PbAnalyze.Visibility = Visibility.Collapsed;
            UpdateUi();

            if (cancelled) return;
            if (result == null)
            {
                TxtVerdict.Text = "გაზომვა ვერ მოხერხდა — ffmpeg შეცდომა.";
                TxtVerdict.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
                TxtSsim.Text = TxtPsnr.Text = "—";
                AnalyzeResult.Visibility = Visibility.Visible;
                return;
            }

            var (ssim, psnr) = result.Value;
            TxtSsim.Text = ssim.ToString("0.000", CultureInfo.InvariantCulture);
            TxtPsnr.Text = double.IsInfinity(psnr) ? "∞" : psnr.ToString("0.0", CultureInfo.InvariantCulture) + " dB";

            string verdict; string brush;
            if (ssim >= 0.99) { verdict = "პრაქტიკულად იდენტურია"; brush = "Success"; }
            else if (ssim >= 0.97) { verdict = "ძალიან მაღალი ხარისხი — განსხვავება თვალით თითქმის არ ჩანს"; brush = "Success"; }
            else if (ssim >= 0.93) { verdict = "კარგია — უმნიშვნელო ცვლილებებია"; brush = "Success"; }
            else if (ssim >= 0.85) { verdict = "შესამჩნევი ცვლილებაა (შეკუმშვა ან ეფექტი)"; brush = "Warning"; }
            else { verdict = "B მნიშვნელოვნად განსხვავდება A-სგან"; brush = "Danger"; }
            TxtVerdict.Text = verdict;
            TxtVerdict.SetResourceReference(TextBlock.ForegroundProperty, brush);
            AnalyzeResult.Visibility = Visibility.Visible;
            AnalyzeResult.BringIntoView();
        }

        private static (double ssim, double psnr)? RunAnalysis(string pathA, string pathB, int w, int h, double fps,
            double ssA, double ssB, double total, CancellationToken ct, Action<double> progress)
        {
            try
            {
                var psi = new ProcessStartInfo(AppPaths.Ffmpeg())
                {
                    RedirectStandardError = true, RedirectStandardOutput = true,
                    UseShellExecute = false, CreateNoWindow = true,
                    StandardErrorEncoding = Encoding.UTF8
                };
                void Add(params string[] a) { foreach (var s in a) psi.ArgumentList.Add(s); }
                string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

                Add("-hide_banner", "-nostdin");
                if (ssA > 0) Add("-ss", F(ssA));
                Add("-i", pathA);
                if (ssB > 0) Add("-ss", F(ssB));
                Add("-i", pathB);
                Add("-t", F(total));
                string norm = $"scale={w}:{h}:flags=bicubic,setsar=1,fps={F(fps)},format=yuv420p,split=2";
                Add("-filter_complex",
                    $"[0:v]{norm}[a1][a2];[1:v]{norm}[b1][b2];[b1][a1]ssim[s];[b2][a2]psnr[p]");
                Add("-map", "[s]", "-f", "null", "-", "-map", "[p]", "-f", "null", "-");

                double ssim = double.NaN, psnr = double.NaN;
                using var p = new Process { StartInfo = psi };
                p.ErrorDataReceived += (_, ev) =>
                {
                    var line = ev.Data;
                    if (string.IsNullOrEmpty(line)) return;
                    var t = Regex.Match(line, @"time=(\d+):(\d+):(\d+(?:\.\d+)?)");
                    if (t.Success)
                    {
                        double sec = int.Parse(t.Groups[1].Value) * 3600 + int.Parse(t.Groups[2].Value) * 60
                                     + double.Parse(t.Groups[3].Value, CultureInfo.InvariantCulture);
                        progress(Math.Min(99, sec / total * 100));
                    }
                    var s = Regex.Match(line, @"SSIM\s.*All:(\d+(?:\.\d+)?)");
                    if (s.Success) ssim = double.Parse(s.Groups[1].Value, CultureInfo.InvariantCulture);
                    var pm = Regex.Match(line, @"PSNR\s.*average:(inf|\d+(?:\.\d+)?)");
                    if (pm.Success)
                        psnr = pm.Groups[1].Value == "inf" ? double.PositiveInfinity
                             : double.Parse(pm.Groups[1].Value, CultureInfo.InvariantCulture);
                };
                p.Start();
                p.BeginErrorReadLine();
                p.StandardOutput.ReadToEnd();

                while (!p.WaitForExit(200))
                {
                    if (ct.IsCancellationRequested)
                    {
                        try { p.Kill(true); } catch { }
                        return null;
                    }
                }
                p.WaitForExit();    // flush async stderr
                if (double.IsNaN(ssim) || double.IsNaN(psnr)) return null;
                return (ssim, psnr);
            }
            catch { return null; }
        }

        // ════════════════════════════════════════════════════════════
        // FORMATTING
        // ════════════════════════════════════════════════════════════
        private static string FmtTime(TimeSpan t) =>
            $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 10:00}";

        private static string FmtShort(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

        private static string FmtSize(long bytes)
        {
            double mb = bytes / 1048576.0;
            return mb >= 1024 ? $"{mb / 1024:0.0} GB" : mb >= 10 ? $"{mb:0} MB" : $"{mb:0.0} MB";
        }
    }
}

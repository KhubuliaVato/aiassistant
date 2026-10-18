using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NAudio.Wave;

namespace AiAssistant
{
    // microphone, level meter, countdown / pre-roll, recording, punch-in range, room test
    public partial class VoiceOverView
    {
        private WaveInEvent? _wave;
        private WaveFileWriter? _writer;
        private string _recPath = "";
        private bool _recording;
        private bool _countingDown;
        private double _recBeginPos, _recInPos, _recOutPos;
        private readonly Stopwatch _recWatch = new();
        private WaveInEvent? _testWave;
        private bool _roomTesting;
        private bool _cancelCount;

        private readonly List<float> _livePeaks = new();          // one linear peak per ~40 ms buffer while recording
        private int _clipCount;
        private double _peakHold;
        private DateTime _peakHoldAt = DateTime.MinValue;

        private double? _punchIn, _punchOut;

        private double InputGain => SlGain.Value / 100.0;

        // ════════════════════════════════════════════════════════════
        // MICROPHONES
        // ════════════════════════════════════════════════════════════
        private void LoadMics()
        {
            CmbMic.Items.Clear();
            try
            {
                for (int i = 0; i < WaveInEvent.DeviceCount; i++)
                    CmbMic.Items.Add(new ComboBoxItem { Content = WaveInEvent.GetCapabilities(i).ProductName, Tag = i });
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
                TxtMicStatus.Text = "მიკროფონი არ ჩანს. შეაერთე და სეთინგებიდან გადატვირთე ეს ტაბი, ან გამოიყენე „ფაილიდან“.";
            }
            UpdateRecordEnabled();
        }

        private int SelectedMic => (CmbMic.SelectedItem as ComboBoxItem)?.Tag is int i ? i : -1;

        private void BtnRescanMics_Click(object sender, RoutedEventArgs e)
        {
            if (_recording) return;
            StopMicTest();
            LoadMics();
            Toast("მიკროფონები გადამოწმდა: " + (SelectedMic >= 0 ? $"{CmbMic.Items.Count} ნაპოვნია" : "ვერაფერი ვიპოვე"), SelectedMic >= 0 ? "success" : "error");
        }

        private void CmbMic_Changed(object sender, SelectionChangedEventArgs e) { StopMicTest(); UpdateRecordEnabled(); }

        private void UpdateRecordEnabled()
        {
            if (BtnRecord == null) return;
            BtnRecord.IsEnabled = _recording || (_video != null && SelectedMic >= 0 && !_countingDown && !_roomTesting);
        }

        private void Rec_Changed(object sender, RoutedEventArgs e) { }

        private void Monitor_Changed(object sender, RoutedEventArgs e)
        {
            MonitorRow.Visibility = ChkMonitor.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            if (_recording) ApplyOriginalVolume(Pos);
        }

        private void Rec_Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_built) return;
            TxtGain.Text = $"{(int)SlGain.Value}%";
            TxtMonitor.Text = $"{(int)SlMonitor.Value}%";
            TxtLatency.Text = $"{(int)SlLatency.Value} მს";
            TxtWpm.Text = $"{(int)SlWpm.Value}";
            TxtFont.Text = $"{(int)SlFont.Value}";
            if (PrompterText != null) { PrompterText.FontSize = SlFont.Value; PrompterText.LineHeight = SlFont.Value * 1.42; }
            if (_recording) ApplyOriginalVolume(Pos);
        }

        // ── mic test (level only) ──
        private void BtnMicTest_Click(object sender, RoutedEventArgs e)
        {
            if (_testWave != null) { StopMicTest(); return; }
            if (SelectedMic < 0 || _recording || _roomTesting) return;
            try
            {
                _testWave = new WaveInEvent { DeviceNumber = SelectedMic, WaveFormat = new WaveFormat(48000, 16, 1), BufferMilliseconds = 40 };
                _testWave.DataAvailable += (_, a) => { ApplyGain(a.Buffer, a.BytesRecorded); ShowLevel(a.Buffer, a.BytesRecorded); };
                _testWave.StartRecording();
                BtnMicTest.Content = "გაჩერება";
                TxtMicStatus.Text = "თქვი რამე — ზოლი უნდა მოძრაობდეს და წითელს არ უნდა მიაღწიოს.";
            }
            catch (Exception ex) { _testWave = null; TxtMicStatus.Text = "მიკროფონი ვერ გაიხსნა: " + ex.Message; }
        }

        private void StopMicTest()
        {
            try { _testWave?.StopRecording(); _testWave?.Dispose(); } catch { }
            _testWave = null;
            if (BtnMicTest != null) BtnMicTest.Content = "დონის შემოწმება";
            if (!_recording) SetMeter(0, 0);
        }

        // ── level meter ──
        private void Meter_SizeChanged(object sender, SizeChangedEventArgs e) { if (MeterFill != null) MeterFill.Width = Math.Max(1, MeterHost.ActualWidth); }

        private void ApplyGain(byte[] buf, int bytes)
        {
            double g = 1;
            Dispatcher.Invoke(() => g = InputGain);
            if (Math.Abs(g - 1) < 0.01) return;
            for (int i = 0; i + 1 < bytes; i += 2)
            {
                int s = (short)(buf[i] | (buf[i + 1] << 8));
                s = (int)Math.Clamp(s * g, short.MinValue, short.MaxValue);
                buf[i] = (byte)(s & 0xFF); buf[i + 1] = (byte)((s >> 8) & 0xFF);
            }
        }

        private float ShowLevel(byte[] buffer, int bytes)
        {
            int peak = 0;
            for (int i = 0; i + 1 < bytes; i += 2)
            {
                int s = Math.Abs((short)(buffer[i] | (buffer[i + 1] << 8)));
                if (s > peak) peak = s;
            }
            double lin = peak / 32768.0;
            double db = peak <= 0 ? -90 : 20 * Math.Log10(lin);
            double frac = Math.Clamp((db + 60) / 60.0, 0, 1);
            bool clip = peak >= 32300;
            Dispatcher.InvokeAsync(() => SetMeter(frac, db, clip));
            return (float)lin;
        }

        private void SetMeter(double frac, double db, bool clip = false)
        {
            if (MeterClip == null) return;
            double w = MeterHost.ActualWidth;
            MeterClip.Width = w * frac;
            var now = DateTime.UtcNow;
            if (frac >= _peakHold || (now - _peakHoldAt).TotalMilliseconds > 900) { _peakHold = frac; _peakHoldAt = now; }
            else _peakHold = Math.Max(frac, _peakHold - 0.02);
            MeterPeak.Margin = new Thickness(Math.Max(0, w * _peakHold - 3), 0, 0, 0);
            TxtDb.Text = db <= -89 ? "−∞ dB" : $"{db:0} dB";
            TxtDb.Foreground = clip ? Brushes.OrangeRed : (Brush)FindResource("TextSecondary");
            if (clip && _recording) _clipCount++;
        }

        // ════════════════════════════════════════════════════════════
        // PUNCH RANGE (I / O)
        // ════════════════════════════════════════════════════════════
        private void BtnSetIn_Click(object sender, RoutedEventArgs e)
        {
            if (_video == null || _recording) return;
            _punchIn = Pos;
            if (_punchOut != null && _punchOut <= _punchIn + 0.2) _punchOut = null;
            UpdatePunchText(); RedrawTimeline();
        }

        private void BtnSetOut_Click(object sender, RoutedEventArgs e)
        {
            if (_video == null || _recording) return;
            if (_punchIn != null && Pos <= _punchIn + 0.2) { Toast("დასასრული დასაწყისზე გვიან უნდა იყოს", "error"); return; }
            _punchOut = Pos;
            UpdatePunchText(); RedrawTimeline();
        }

        private void BtnClearPunch_Click(object sender, RoutedEventArgs e)
        {
            _punchIn = _punchOut = null;
            UpdatePunchText(); RedrawTimeline();
        }

        private void UpdatePunchText()
        {
            if (TxtPunch == null) return;
            TxtPunch.Text = _punchIn == null && _punchOut == null ? "გადაწერის მონაკვეთი: არ არის მონიშნული"
                : _punchOut == null ? $"დასაწყისი: {FmtShort(_punchIn ?? 0)} · დასასრული: ვიდეოს ბოლომდე / გაჩერებამდე"
                : $"გადაწერა: {FmtShort(_punchIn ?? 0)} – {FmtShort(_punchOut.Value)}  ({FmtShort(_punchOut.Value - (_punchIn ?? 0))})";
        }

        // ════════════════════════════════════════════════════════════
        // RECORDING
        // ════════════════════════════════════════════════════════════
        private async void BtnRecord_Click(object sender, RoutedEventArgs e)
        {
            if (_countingDown) return;
            if (_recording) await StopRecordingAsync();
            else await StartRecordingAsync();
        }

        private double PreRollSeconds => Pre2.IsChecked == true ? 2 : Pre3.IsChecked == true ? 3 : Pre5.IsChecked == true ? 5 : 0;

        private async Task StartRecordingAsync()
        {
            if (_video == null || _recording || SelectedMic < 0) return;
            StopPlayback(); StopMicTest();

            double inPos = _punchIn ?? Pos;
            double outPos = _punchOut ?? _dur;
            if (inPos >= _dur - 0.3) inPos = 0;
            double begin = Math.Max(0, inPos - PreRollSeconds);
            SeekTo(begin);
            await Task.Delay(120);                       // the frame has to be on screen before the counting starts

            if (ChkCountdown.IsChecked == true)
            {
                _countingDown = true;
                UpdateRecordEnabled();
                CountdownBox.Visibility = Visibility.Visible;
                _cancelCount = false;
                for (int i = 3; i >= 1 && !_cancelCount; i--)
                {
                    TxtCountdown.Text = i.ToString();
                    CountScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.35, 1, TimeSpan.FromMilliseconds(650)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                    CountScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.35, 1, TimeSpan.FromMilliseconds(650)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                    await Task.Delay(800);
                    if (!IsVisible) break;
                }
                CountdownBox.Visibility = Visibility.Collapsed;
                _countingDown = false;
                UpdateRecordEnabled();
                if (_cancelCount) { _cancelCount = false; return; }
            }

            try
            {
                _recPath = System.IO.Path.Combine(WorkDir, $"take_{DateTime.Now:yyyyMMdd_HHmmss}_{_nextId}.wav");
                var fmt = new WaveFormat(48000, 16, 1);
                _writer = new WaveFileWriter(_recPath, fmt);
                _wave = new WaveInEvent { DeviceNumber = SelectedMic, WaveFormat = fmt, BufferMilliseconds = 40 };
                _wave.DataAvailable += (_, a) =>
                {
                    ApplyGain(a.Buffer, a.BytesRecorded);
                    try { _writer?.Write(a.Buffer, 0, a.BytesRecorded); } catch { }
                    float lin = ShowLevel(a.Buffer, a.BytesRecorded);
                    lock (_livePeaks) _livePeaks.Add(lin);
                };
                _wave.RecordingStopped += (_, _) => { try { _writer?.Dispose(); } catch { } _writer = null; };
                lock (_livePeaks) _livePeaks.Clear();
                _clipCount = 0;
                _wave.StartRecording();
            }
            catch (Exception ex)
            {
                _wave = null; _writer = null;
                UpdateRecordEnabled();
                TxtMicStatus.Text = "ჩაწერა ვერ დაიწყო: " + ex.Message;
                Toast("მიკროფონი ვერ გაიხსნა", "error");
                return;
            }

            _recording = true;
            BtnStopRec.Visibility = Visibility.Visible;
            CmbMic.IsEnabled = false;
            UpdateRecordEnabled();
            _recBeginPos = begin; _recInPos = inPos; _recOutPos = outPos;
            _recWatch.Restart();
            Media.Volume = ChkMonitor.IsChecked == true ? Math.Clamp(SlMonitor.Value / 100.0, 0, 1) : 0;
            Media.Play();
            _timer.Start();

            TxtRecBtn.Text = "გაჩერება";
            RecIcon.Fill = Brushes.White;
            DotRec.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Danger");
            TxtRecChip.Text = "იწერება";
            BtnPlay.IsEnabled = BtnRewind.IsEnabled = BtnBack5.IsEnabled = BtnFwd5.IsEnabled = SlPos.IsEnabled = false;
            BtnExport.IsEnabled = BtnExportWav.IsEnabled = false;
            if (HasScript) { _prompterOffByUser = false; SetPrompter(true); PrompterReset(); }          // recording always shows the text
        }

        private void RecordingTick(double pos)
        {
            if (pos < _recInPos - 0.02)
            {
                PreRollBadge.Visibility = Visibility.Visible; RecBadge.Visibility = RecFrame.Visibility = Visibility.Collapsed;
                TxtPreRoll.Text = $"ჩაწერა დაიწყება {Math.Max(0, _recInPos - pos):0.0} წმ-ში — მზად იყავი";
            }
            else
            {
                PreRollBadge.Visibility = Visibility.Collapsed; RecBadge.Visibility = RecFrame.Visibility = Visibility.Visible;
                TxtRecTime.Text = "REC " + FmtShort(Math.Max(0, pos - _recInPos));
                bool on = (int)(_recWatch.Elapsed.TotalSeconds * 2) % 2 == 0;
                RecBlink.Opacity = on ? 1 : 0.25;
                RecFrame.Opacity = on ? 1 : 0.55;
                if (_prompterOn) PrompterTick(Math.Max(0, pos - _recInPos));
            }
            if (pos >= _recOutPos - 0.01) _ = StopRecordingAsync();
        }

        private async Task StopRecordingAsync()
        {
            if (!_recording) return;
            _recording = false;
            _recWatch.Stop();
            _timer.Stop();
            try { Media.Pause(); } catch { }
            try { _wave?.StopRecording(); } catch { }
            await Task.Delay(180);                 // let the writer flush
            try { _wave?.Dispose(); } catch { }
            _wave = null;

            PreRollBadge.Visibility = RecBadge.Visibility = RecFrame.Visibility = Visibility.Collapsed;
            BtnStopRec.Visibility = Visibility.Collapsed;
            CmbMic.IsEnabled = true;
            if (_pWords.Count > 0) PrompterReset();
            TxtRecBtn.Text = "ჩაწერის დაწყება";
            DotRec.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextMuted");
            TxtRecChip.Text = "მზადაა";
            SetMeter(0, -90);
            BtnPlay.IsEnabled = BtnRewind.IsEnabled = BtnBack5.IsEnabled = BtnFwd5.IsEnabled = SlPos.IsEnabled = true;

            double fileDur = 0;
            try { using var r = new WaveFileReader(_recPath); fileDur = r.TotalTime.TotalSeconds; } catch { }
            double offset = Math.Max(0, _recInPos - _recBeginPos);
            double audible = fileDur - offset;
            if (fileDur <= 0 || audible < 0.4 || !File.Exists(_recPath))
            {
                try { if (File.Exists(_recPath)) File.Delete(_recPath); } catch { }
                UpdateRecordEnabled(); UpdateExportEnabled(); RedrawTimeline();
                return;
            }

            double latency = SlLatency.Value / 1000.0;
            double start = _recInPos - latency;
            if (start < 0) { offset += -start; audible -= -start; start = 0; }
            if (start + audible > _dur) audible = Math.Max(0.1, _dur - start);

            var take = new Take { Id = _nextId++, Name = $"ჩანაწერი {_takes.Count + 1}", Path = _recPath, Start = start, Offset = offset, Dur = audible, FileDur = fileDur };
            take.Peaks = await Task.Run(() => ComputePeaks(take.Path));
            PushUndo();
            bool replaced = _takes.Any(t => t.End > take.Start + 0.05 && t.Start < take.End - 0.05);
            CarveTakes(take.Start, take.End, take);
            _takes.Add(take);
            _selTake = take;
            lock (_livePeaks) _livePeaks.Clear();
            AfterTakeChange();
            SeekTo(take.Start);
            UpdateRecordEnabled();
            UpdatePunchText();
            string msg = $"{take.Name} დაემატა ({FmtShort(take.Dur)})";
            if (replaced) msg += " · ძველი ამ მონაკვეთში ჩანაცვლდა (Ctrl+Z — გაუქმება)";
            if (_clipCount > 2) msg += " · ⚠ ჩანაწერში იყო გადატვირთვა — შეამცირე მიკროფონის სიძლიერე";
            Toast(msg, _clipCount > 2 ? "error" : "success");
            _ = ReportLoudnessAsync(take);
        }

        // ════════════════════════════════════════════════════════════
        // ROOM TEST: 2 s of silence + 3 s of speech → noise level, speech level, automatic input gain
        // ════════════════════════════════════════════════════════════
        private async void BtnRoom_Click(object sender, RoutedEventArgs e)
        {
            if (_roomTesting || _recording || SelectedMic < 0) return;
            StopMicTest();
            _roomTesting = true; UpdateRecordEnabled();
            BtnRoom.IsEnabled = BtnMicTest.IsEnabled = false;
            var quiet = new List<short>(); var speech = new List<short>();
            var sw = Stopwatch.StartNew();
            WaveInEvent? w = null;
            try
            {
                w = new WaveInEvent { DeviceNumber = SelectedMic, WaveFormat = new WaveFormat(48000, 16, 1), BufferMilliseconds = 40 };
                w.DataAvailable += (_, a) =>
                {
                    double t = sw.Elapsed.TotalSeconds;
                    ShowLevel(a.Buffer, a.BytesRecorded);
                    var list = t < 2.2 ? quiet : speech;
                    if (t < 0.3) return;
                    lock (list) for (int i = 0; i + 1 < a.BytesRecorded; i += 2) list.Add((short)(a.Buffer[i] | (a.Buffer[i + 1] << 8)));
                };
                w.StartRecording();
                TxtMicStatus.Text = "🤫 ჩუმად იყავი… (2 წმ)";
                await Task.Delay(2200);
                TxtMicStatus.Text = "🎙 ახლა თქვი რამე ჩვეულებრივად, როგორც ჩაწერისას… (3 წმ)";
                await Task.Delay(3000);
            }
            catch (Exception ex) { TxtMicStatus.Text = "ტესტი ვერ შედგა: " + ex.Message; }
            finally { try { w?.StopRecording(); w?.Dispose(); } catch { } }
            SetMeter(0, -90);

            double Db(double lin) => lin <= 1e-6 ? -90 : 20 * Math.Log10(lin);
            double Rms(List<short> l) { lock (l) { if (l.Count == 0) return 0; double s = 0; foreach (var v in l) s += (v / 32768.0) * (v / 32768.0); return Math.Sqrt(s / l.Count); } }
            double Peak(List<short> l) { lock (l) return l.Count == 0 ? 0 : l.Max(v => Math.Abs((int)v)) / 32768.0; }
            double noiseDb = Db(Rms(quiet)), speechPeakDb = Db(Peak(speech)), speechRmsDb = Db(Rms(speech));

            var sb = new System.Text.StringBuilder();
            if (speechPeakDb < -40) sb.Append("ხმა ვერ გავიგე — შეამოწმე, სწორი მიკროფონია თუ არა არჩეული. ");
            else
            {
                double factor = Math.Pow(10, (-10 - speechPeakDb) / 20.0);          // aim for peaks around −10 dBFS
                double newGain = Math.Clamp(SlGain.Value * factor, 25, 400);
                SlGain.Value = newGain;
                sb.Append($"სიძლიერე დავაყენე {(int)newGain}%-ზე. ");
                double snr = speechRmsDb - noiseDb;
                sb.Append(noiseDb <= -60 ? "ოთახი მშვენივრად ჩუმია ✓ " : noiseDb <= -50 ? "ოთახი ჩუმია ✓ " :
                          noiseDb <= -40 ? "ოდნავ ხმაურია — „ბუნებრივი“ დამუშავება გაასწორებს. " : "ხმაურია — მიკროფონი ახლოს დაიჭირე ან ჩართე „სტუდიური“ დამუშავება. ");
                sb.Append($"(ხმა ხმაურზე {Math.Max(0, snr):0} dB-ით ძლიერია)");
                if (noiseDb > -50 && FxOff.IsChecked == true) { FxNatural.IsChecked = true; FxPreset_Click(FxNatural, new RoutedEventArgs()); }
            }
            TxtMicStatus.Text = sb.ToString();
            BtnRoom.IsEnabled = BtnMicTest.IsEnabled = true;
            _roomTesting = false; UpdateRecordEnabled();
        }
    }
}

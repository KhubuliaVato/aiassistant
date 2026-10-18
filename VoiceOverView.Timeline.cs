using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Shapes = System.Windows.Shapes;

namespace AiAssistant
{
    // timeline: ruler · the video's own sound · my voice (takes, trim, split, punch range, live waveform)
    public partial class VoiceOverView
    {
        private const double RulerH = 22, OrigTop = 26, OrigH = 36, VoiceTop = 68, VoiceH = 78;

        private float[] _origPeaks = Array.Empty<float>();
        private Shapes.Rectangle? _playhead;
        private Shapes.Path? _livePath;

        private enum TlDrag { None, Scrub, Move, TrimL, TrimR }
        private TlDrag _tlDrag = TlDrag.None;
        private Take? _dragTake;
        private double _dragX0, _dragStart0, _dragOffset0, _dragDur0;
        private bool _dragUndone;

        private double PxPerSec => _dur > 0 && Timeline.ActualWidth > 0 ? Timeline.ActualWidth / _dur : 1;

        private void Timeline_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawTimeline();

        // ── the video's own sound, so you can see where the speaker talks ──
        private async Task LoadOriginalPeaksAsync(string path)
        {
            const int buckets = 1600;
            var peaks = await Task.Run(() =>
            {
                try
                {
                    var si = new ProcessStartInfo(AppPaths.Ffmpeg()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (var a in new[] { "-v", "error", "-i", path, "-vn", "-ac", "1", "-ar", "8000", "-f", "s16le", "-" }) si.ArgumentList.Add(a);
                    using var p = Process.Start(si)!;
                    p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
                    var res = new float[buckets];
                    long per = Math.Max(1, (long)(Math.Max(1, _dur) * 8000 / buckets));
                    var buf = new byte[16384]; long idx = 0; int carry = 0; int read;
                    var st = p.StandardOutput.BaseStream;
                    while ((read = st.Read(buf, carry, buf.Length - carry)) > 0)
                    {
                        int n = carry + read;
                        int i = 0;
                        for (; i + 1 < n; i += 2, idx++)
                        {
                            float v = Math.Abs((short)(buf[i] | (buf[i + 1] << 8))) / 32768f;
                            int b = (int)Math.Min(buckets - 1, idx / per);
                            if (v > res[b]) res[b] = v;
                        }
                        carry = n - i; if (carry > 0) buf[0] = buf[i];
                    }
                    float max = res.Max();
                    if (max > 0.001f) for (int i = 0; i < res.Length; i++) res[i] = (float)Math.Pow(res[i] / max, 0.7);
                    return res;
                }
                catch { return Array.Empty<float>(); }
            });
            if (_video == path) { _origPeaks = peaks; RedrawTimeline(); }
        }

        private static Shapes.Path WavePath(IList<float> peaks, int from, int to, double w, double h, string brushKey, double thick = 1.3)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                double mid = h / 2;
                int n = Math.Max(1, to - from);
                for (int i = from; i < to; i++)
                {
                    double x = (i - from + 0.5) / n * w;
                    double a = Math.Max(0.8, peaks[Math.Clamp(i, 0, peaks.Count - 1)] * (h / 2 - 3));
                    ctx.BeginFigure(new Point(x, mid - a), false, false);
                    ctx.LineTo(new Point(x, mid + a), true, false);
                }
            }
            geo.Freeze();
            var path = new Shapes.Path { Data = geo, StrokeThickness = thick, IsHitTestVisible = false };
            path.SetResourceReference(Shapes.Shape.StrokeProperty, brushKey);
            return path;
        }

        private void RedrawTimeline()
        {
            if (Timeline == null) return;
            Timeline.Children.Clear();
            _playhead = null; _livePath = null;
            double w = Timeline.ActualWidth, h = Timeline.ActualHeight;
            if (w <= 0 || h <= 0) return;
            TxtTimelineHint.Visibility = _takes.Count == 0 && !_recording ? Visibility.Visible : Visibility.Collapsed;
            if (_dur <= 0) return;
            double pps = w / _dur;

            // lanes
            foreach (var (top, ht) in new[] { (OrigTop, OrigH), (VoiceTop, VoiceH) })
            {
                var lane = new Shapes.Rectangle { Width = w, Height = ht, RadiusX = 6, RadiusY = 6, IsHitTestVisible = false };
                lane.SetResourceReference(Shapes.Shape.FillProperty, "BgInput");
                Canvas.SetTop(lane, top); Timeline.Children.Add(lane);
            }

            // ruler
            double step = _dur <= 20 ? 1 : _dur <= 60 ? 5 : _dur <= 300 ? 15 : 60;
            for (double t = 0; t <= _dur + 0.001; t += step)
            {
                double x = t * pps;
                var tick = new Shapes.Rectangle { Width = 1, Height = 8, IsHitTestVisible = false };
                tick.SetResourceReference(Shapes.Shape.FillProperty, "BorderStrong");
                Canvas.SetLeft(tick, x); Timeline.Children.Add(tick);
                var lbl = new TextBlock { Text = FmtShort(t), FontSize = 10, FontFamily = new FontFamily("Consolas"), IsHitTestVisible = false };
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
                Canvas.SetLeft(lbl, x + 4); Canvas.SetTop(lbl, 1); Timeline.Children.Add(lbl);
            }

            // the video's own sound
            if (_origPeaks.Length > 0)
            {
                var wp = WavePath(_origPeaks, 0, _origPeaks.Length, w, OrigH, "TextMuted", 1.1);
                wp.Opacity = 0.7;
                Canvas.SetTop(wp, OrigTop); Timeline.Children.Add(wp);
            }

            // punch range
            if (_punchIn != null || _punchOut != null)
            {
                double a = (_punchIn ?? 0) * pps, b = (_punchOut ?? _dur) * pps;
                var reg = new Shapes.Rectangle { Width = Math.Max(2, b - a), Height = VoiceTop + VoiceH - OrigTop + 6, RadiusX = 6, RadiusY = 6, IsHitTestVisible = false };
                reg.SetResourceReference(Shapes.Shape.FillProperty, "AccentSoft");
                reg.SetResourceReference(Shapes.Shape.StrokeProperty, "Accent");
                reg.StrokeThickness = 1; reg.StrokeDashArray = new DoubleCollection { 3, 2 };
                Canvas.SetLeft(reg, a); Canvas.SetTop(reg, OrigTop - 3); Timeline.Children.Add(reg);
                foreach (var (flag, x) in new[] { ("I", _punchIn), ("O", _punchOut) }.Where(f => f.Item2 != null).Select(f => (f.Item1, f.Item2!.Value * pps)))
                {
                    var tag = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0, 5, 1), IsHitTestVisible = false,
                                           Child = new TextBlock { Text = flag, FontSize = 10, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Consolas"), Foreground = Brushes.White } };
                    tag.SetResourceReference(Border.BackgroundProperty, "Accent");
                    Canvas.SetLeft(tag, flag == "I" ? x : x - 17); Canvas.SetTop(tag, 5); Timeline.Children.Add(tag);
                }
            }

            // takes
            foreach (var t in _takes)
            {
                double bw = Math.Max(8, t.Dur * pps);
                bool sel = t == _selTake;
                var block = new Border { Width = bw, Height = VoiceH, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(sel ? 2.4 : 1.5), Opacity = t.Muted ? 0.4 : 1,
                                         ToolTip = $"{t.Name} · {FmtShort(t.Start)} – {FmtShort(t.End)}", IsHitTestVisible = false };
                block.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
                block.SetResourceReference(Border.BorderBrushProperty, sel ? "TextPrimary" : "Accent");
                var grid = new Grid { ClipToBounds = true };
                if (t.Peaks.Length > 0 && t.FileDur > 0)
                {
                    int i0 = (int)Math.Floor(t.Offset / t.FileDur * t.Peaks.Length), i1 = (int)Math.Ceiling((t.Offset + t.Dur) / t.FileDur * t.Peaks.Length);
                    i0 = Math.Clamp(i0, 0, t.Peaks.Length - 1); i1 = Math.Clamp(i1, i0 + 1, t.Peaks.Length);
                    var wp = WavePath(t.Peaks, i0, i1, bw - 4, VoiceH, "Accent", 1.5);
                    wp.Margin = new Thickness(2, 0, 0, 0);
                    grid.Children.Add(wp);
                }
                var name = new TextBlock { Text = t.Name, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(7, 3, 6, 0), IsHitTestVisible = false,
                                           TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Top };
                name.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
                grid.Children.Add(name);
                if (sel && bw > 46)                                   // a delete button on the selected take
                {
                    var bin = new Border { Width = 24, Height = 22, CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                                           Margin = new Thickness(0, 3, 8, 0), Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x14, 0x17, 0x1C)), IsHitTestVisible = false,
                                           Child = new TextBlock { Text = "\uE74D", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12, Foreground = Brushes.White,
                                                                   HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, ToolTip = "წაშლა" };
                    grid.Children.Add(bin);
                }
                // trim handles
                foreach (var left in new[] { true, false })
                {
                    var hd = new Border { Width = 5, CornerRadius = new CornerRadius(3), HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right, Margin = new Thickness(1, 12, 1, 12), Opacity = 0.55 };
                    hd.SetResourceReference(Border.BackgroundProperty, "Accent");
                    grid.Children.Add(hd);
                }
                block.Child = grid;
                Canvas.SetLeft(block, t.Start * pps); Canvas.SetTop(block, VoiceTop);
                Timeline.Children.Add(block);
            }

            // live recording waveform
            _livePath = new Shapes.Path { StrokeThickness = 1.6, IsHitTestVisible = false };
            _livePath.Stroke = new SolidColorBrush(Color.FromRgb(0xF4, 0x3F, 0x5E));
            Canvas.SetTop(_livePath, VoiceTop);
            Timeline.Children.Add(_livePath);

            // playhead
            _playhead = new Shapes.Rectangle { Width = 2, Height = h, IsHitTestVisible = false };
            _playhead.SetResourceReference(Shapes.Shape.FillProperty, "Danger");
            Timeline.Children.Add(_playhead);
            UpdatePlayhead(_video != null ? Pos : 0);
        }

        private void RedrawLiveRec()
        {
            if (_livePath == null || Timeline.ActualWidth <= 0 || _dur <= 0) return;
            float[] snap;
            lock (_livePeaks) snap = _livePeaks.ToArray();
            double pps = PxPerSec, lat = SlLatency.Value / 1000.0;
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                double mid = VoiceH / 2;
                for (int i = 0; i < snap.Length; i++)
                {
                    double t = _recBeginPos + i * 0.04 - lat;
                    if (t < _recInPos - lat) continue;                         // pre-roll is not part of the take
                    double x = t * pps;
                    double a = Math.Max(1, Math.Min(1, snap[i] * 1.6) * (VoiceH / 2 - 4));
                    ctx.BeginFigure(new Point(x, mid - a), false, false);
                    ctx.LineTo(new Point(x, mid + a), true, false);
                }
            }
            geo.Freeze();
            _livePath.Data = geo;
        }

        private void UpdatePlayhead(double sec)
        {
            if (_playhead == null) return;
            Canvas.SetLeft(_playhead, Math.Clamp(sec * PxPerSec - 1, 0, Math.Max(0, Timeline.ActualWidth - 2)));
        }

        // ── mouse ──
        private (Take? take, int edge) HitTake(Point p)
        {
            if (p.Y < VoiceTop - 4 || p.Y > VoiceTop + VoiceH + 4) return (null, 0);
            double pps = PxPerSec;
            foreach (var t in _takes.AsEnumerable().Reverse())
            {
                double a = t.Start * pps, b = t.End * pps;
                if (p.X < a - 2 || p.X > b + 2) continue;
                int edge = (p.X - a < 7) ? -1 : (b - p.X < 7) ? 1 : 0;
                return (t, edge);
            }
            return (null, 0);
        }

        private void Timeline_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_recording || _dur <= 0) return;
            Keyboard.ClearFocus(); Focus();                     // so Delete / M / S reach the timeline even after the microphone list had focus
            var p = e.GetPosition(Timeline);
            if (_selTake != null && _selTake.Dur * PxPerSec > 46)    // the little bin on the selected take
            {
                double bx = _selTake.End * PxPerSec - 32, by = VoiceTop + 3;
                if (p.X >= bx && p.X <= bx + 24 && p.Y >= by && p.Y <= by + 22) { DeleteTake(_selTake); return; }
            }
            var (t, edge) = HitTake(p);
            _dragX0 = p.X; _dragUndone = false;
            if (t != null)
            {
                _selTake = t; _dragTake = t;
                _dragStart0 = t.Start; _dragOffset0 = t.Offset; _dragDur0 = t.Dur;
                _tlDrag = edge < 0 ? TlDrag.TrimL : edge > 0 ? TlDrag.TrimR : TlDrag.Move;
                Timeline.CaptureMouse();
                RefreshTakesUi(); RedrawTimeline();
            }
            else
            {
                _tlDrag = TlDrag.Scrub;
                Timeline.CaptureMouse();
                bool was = _playing; if (was) StopPlayback();
                SeekTo(p.X / PxPerSec);
                if (was) _ = StartPlaybackAsync();
            }
        }

        private double Snap(double sec, Take self)
        {
            double tol = 7 / PxPerSec;
            var targets = new List<double> { 0, Pos };
            foreach (var o in _takes.Where(x => x != self)) { targets.Add(o.Start); targets.Add(o.End); }
            if (_punchIn != null) targets.Add(_punchIn.Value);
            if (_punchOut != null) targets.Add(_punchOut.Value);
            double best = sec, bd = tol;
            foreach (var tg in targets) { double d = Math.Abs(tg - sec); if (d < bd) { bd = d; best = tg; } }
            return best;
        }

        private void Timeline_MouseMove(object sender, MouseEventArgs e)
        {
            var p = e.GetPosition(Timeline);
            if (_tlDrag == TlDrag.None)
            {
                var (t, edge) = HitTake(p);
                Timeline.Cursor = t == null ? Cursors.Arrow : edge != 0 ? Cursors.SizeWE : Cursors.SizeAll;
                return;
            }
            if (_tlDrag == TlDrag.Scrub) { SeekTo(p.X / PxPerSec); return; }
            var tk = _dragTake; if (tk == null) return;
            if (!_dragUndone && Math.Abs(p.X - _dragX0) > 2) { _dragUndone = true; var keep = (tk.Start, tk.Offset, tk.Dur); PushUndoWith(tk, _dragStart0, _dragOffset0, _dragDur0); }
            double dt = (p.X - _dragX0) / PxPerSec;
            switch (_tlDrag)
            {
                case TlDrag.Move:
                    {
                        double s = Math.Clamp(_dragStart0 + dt, 0, Math.Max(0, _dur - tk.Dur));
                        double s2 = Snap(s, tk); double e2 = Snap(s + tk.Dur, tk) - tk.Dur;
                        s = Math.Abs(s2 - s) <= Math.Abs(e2 - s) && Math.Abs(s2 - s) < 7 / PxPerSec ? s2 : (Math.Abs(e2 - s) < 7 / PxPerSec ? e2 : s);
                        tk.Start = Math.Clamp(s, 0, Math.Max(0, _dur - tk.Dur));
                        break;
                    }
                case TlDrag.TrimL:
                    {
                        double ns = Math.Clamp(Snap(_dragStart0 + dt, tk), _dragStart0 - _dragOffset0, _dragStart0 + _dragDur0 - 0.1);
                        double delta = ns - _dragStart0;
                        tk.Start = ns; tk.Offset = _dragOffset0 + delta; tk.Dur = _dragDur0 - delta;
                        break;
                    }
                case TlDrag.TrimR:
                    {
                        double ne = Math.Clamp(Snap(_dragStart0 + _dragDur0 + dt, tk), _dragStart0 + 0.1, Math.Min(_dur, _dragStart0 + (tk.FileDur - _dragOffset0)));
                        tk.Dur = ne - tk.Start;
                        break;
                    }
            }
            RedrawTimeline();
        }

        /// <summary>The undo snapshot has to hold the take as it was BEFORE the drag started.</summary>
        private void PushUndoWith(Take t, double start, double offset, double dur)
        {
            var snap = _takes.Select(x => x.Clone()).ToList();
            var orig = snap.FirstOrDefault(x => x.Id == t.Id);
            if (orig != null) { orig.Start = start; orig.Offset = offset; orig.Dur = dur; }
            _undo.Add(snap);
            if (_undo.Count > 40) _undo.RemoveAt(0);
            BtnUndo.IsEnabled = true;
        }

        private void Timeline_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_tlDrag == TlDrag.None) return;
            var was = _tlDrag; _tlDrag = TlDrag.None;
            Timeline.ReleaseMouseCapture();
            if (was != TlDrag.Scrub && _dragTake != null) { _dragTake = null; AfterTakeChange(); }
        }

        /// <summary>Right click on a take = cut it right there.</summary>
        private void Timeline_RightUp(object sender, MouseButtonEventArgs e)
        {
            if (_recording) return;
            var p = e.GetPosition(Timeline);
            var (t, _) = HitTake(p);
            if (t != null) SplitAt(p.X / PxPerSec);
        }
    }
}

using System;
using System.IO;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using Shp = System.Windows.Shapes;

namespace AiAssistant
{
    public partial class EditorView : UserControl
    {
        // ── Public API (used by MainWindow) ───────────────────────────
        public event Action<string, string, TimeSpan>? ClipReadyForCrop;
        // Fired for "task completed" style events — host (MainWindow) shows a toast.
        public event Action<string, string>? Notify;
        public string CacheFolder { get; set; } = "";
        private string CacheDir
        {
            get
            {
                var root = string.IsNullOrEmpty(CacheFolder) ? Path.GetTempPath() : CacheFolder;
                return Path.Combine(root, ".flow_cache");
            }
        }

        // ════════════════════════════════════════════════════════════
        // TIMELINE GEOMETRY — two video lanes (V1 bottom, V2 on top)
        // ════════════════════════════════════════════════════════════
        private const int    TRACKS   = 2;
        private const double RULER_H  = 26;
        private const double LANE_H   = 62;
        private const double LANE_GAP = 6;
        private const double LANE_TOP = 5;
        private const double CANVAS_H = RULER_H + LANE_TOP + TRACKS * (LANE_H + LANE_GAP) + 3;   // keep in sync with EditorView.xaml
        private const double EDGE_PX  = 7;      // grab zone for trimming a clip edge
        private const double MIN_CLIP = 0.2;    // shortest clip (seconds)
        private const double PH_HW    = 6;
        private const double TR_R     = 11;
        private const double MARKER_Y = 15;     // transition marker height inside its lane

        private static double LaneY(int track) => RULER_H + LANE_TOP + (TRACKS - 1 - track) * (LANE_H + LANE_GAP);
        private static int TrackAtY(double y)
        {
            for (int t = 0; t < TRACKS; t++)
            {
                double y0 = LaneY(t);
                if (y >= y0 - LANE_GAP / 2 && y <= y0 + LANE_H + LANE_GAP / 2) return t;
            }
            return -1;
        }

        // Theme-aware colours for the code-drawn timeline (re-read on every Redraw).
        private Brush Br(string key) => (Brush)FindResource(key);

        // ════════════════════════════════════════════════════════════
        // DATA MODEL — free-form timeline
        // Every clip has an absolute position (TlStart, seconds) on a lane. Gaps are allowed
        // (they render as black), clips can overlap on DIFFERENT lanes (the upper lane wins),
        // and dropping a clip on top of another one on the SAME lane overwrites that part.
        // ════════════════════════════════════════════════════════════
        private class TClip
        {
            public int    Id;
            public string SourcePath = "";
            public double Start;     // in-point inside the source file (s)
            public double End;       // out-point inside the source file (s)
            public double TlStart;   // position on the timeline (s)
            public int    Track;     // 0 = V1, 1 = V2
            public double Dur   => End - Start;
            public double TlEnd => TlStart + Dur;
        }

        private readonly List<TClip> _clips = new();
        private int _nextClipId = 1;
        private int _selClipId  = -1;

        private double TimelineEnd()   => _clips.Count == 0 ? 0 : _clips.Max(c => c.TlEnd);
        private double TimelineBegin() => _clips.Count == 0 ? 0 : _clips.Min(c => c.TlStart);
        private TClip? ClipById(int id) => _clips.FirstOrDefault(c => c.Id == id);
        private IEnumerable<TClip> TrackClips(int track) => _clips.Where(c => c.Track == track).OrderBy(c => c.TlStart);
        private double EndOfTrack(int track) { var l = _clips.Where(c => c.Track == track).ToList(); return l.Count == 0 ? 0 : l.Max(c => c.TlEnd); }

        /// <summary>Topmost clip covering time t (upper lane wins), or null in a gap.</summary>
        private TClip? ActiveClipAt(double t)
        {
            TClip? best = null;
            foreach (var c in _clips)
                if (t >= c.TlStart - 1e-6 && t < c.TlEnd - 1e-6 && (best == null || c.Track > best.Track)) best = c;
            return best;
        }

        // Two clips on the same lane that touch each other form a "join" (transitions live there).
        private IEnumerable<(TClip A, TClip B)> AdjacentPairs()
        {
            for (int tr = 0; tr < TRACKS; tr++)
            {
                var list = TrackClips(tr).ToList();
                for (int i = 0; i < list.Count - 1; i++)
                    if (Math.Abs(list[i].TlEnd - list[i + 1].TlStart) <= 0.05) yield return (list[i], list[i + 1]);
            }
        }
        private TClip? NextAdjacent(TClip c) =>
            TrackClips(c.Track).FirstOrDefault(x => x.Id != c.Id && Math.Abs(x.TlStart - c.TlEnd) <= 0.05);

        // ── Per-source caches (duration probe + waveform + media info) ─
        private readonly Dictionary<string, TimeSpan> _sourceDurations = new();
        private readonly Dictionary<string, float[]>  _waveforms       = new();
        private record MediaInfo(int W, int H, double Fps, bool HasAudio);
        private readonly Dictionary<string, MediaInfo> _mediaInfo = new();
        private const int WAVEFORM_BUCKETS = 4000;

        // ── Scale ─────────────────────────────────────────────────────
        private double _pps = 20;            // pixels per second
        private bool   _userZoomed = false;  // false = timeline auto-fits the window
        private const double MIN_PPS = 0.5, MAX_CANVAS_W = 150000;

        private double X(double t) => t * _pps;
        private double T(double x) => x / _pps;
        private double ViewportW => TimelineScroller.ViewportWidth > 0 ? TimelineScroller.ViewportWidth : TimelineScroller.ActualWidth;
        private double CanvasW   => Math.Max(1, TimelineCanvas.ActualWidth > 0 ? TimelineCanvas.ActualWidth : TimelineCanvas.Width);
        private double VisL      => TimelineScroller.HorizontalOffset - 150;
        private double VisR      => TimelineScroller.HorizontalOffset + Math.Max(ViewportW, 300) + 150;

        // ── Playback engine ───────────────────────────────────────────
        private bool       _playing = false;
        private double     _curT    = 0;                     // playhead (timeline seconds)
        private readonly Stopwatch _clock = new();           // wall clock while playing (also drives gaps)
        private double     _clockBaseT = 0;
        private int        _activeId = -1;                   // clip currently shown in the player
        private string?    _loadedSourcePath = null;
        private double?    _pendingSeek = null;              // source-time to seek to once the media has opened
        private bool       _pendingResume = false;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
        private readonly List<UIElement> _tlEls = new();

        // ── Interaction state ─────────────────────────────────────────
        private enum DragMode { None, Scrub, Playhead, Move, TrimL, TrimR }
        private DragMode _mode = DragMode.None;
        private TClip?   _dClip;
        private double   _dStartX, _dStartY;
        private double   _dOrigTl, _dOrigStart, _dOrigEnd;
        private double   _dNewStart;
        private int      _dNewTrack;
        private bool     _dMoved;
        private UState?  _dUndo;
        private double?  _snapGuide;
        private bool     _snap = true;

        // ── Transitions — keyed by (FromClipId, ToClipId) for two joined clips ──
        public enum TrType { None, CrossDissolve, DipToBlack, WhiteFlash, Fade }
        private record Tr(TrType Type, double Dur = 0.5);
        private readonly Dictionary<(int, int), Tr> _transitions = new();

        // ── Project save/load data model (.flowproj = JSON) ───────────
        public class FlowProject
        {
            public int Version { get; set; } = 2;
            public List<ClipEntry> Clips { get; set; } = new();
            public List<TransitionEntry> Transitions { get; set; } = new();
            public double Zoom { get; set; } = 1.0;
            public DateTime SavedAt { get; set; }
        }
        public class ClipEntry
        {
            public int Id { get; set; }
            public string SourcePath { get; set; } = "";
            public double StartSec { get; set; }
            public double EndSec { get; set; }
            public double TlStartSec { get; set; }
            public int Track { get; set; }
        }
        public class TransitionEntry
        {
            public int From { get; set; }
            public int To { get; set; }
            public string Type { get; set; } = "";
            public double Dur { get; set; }
        }

        // ── Undo / Redo ──────────────────────────────────────────────
        private record UState(
            List<(int Id, string Src, double St, double En, double Tl, int Trk)> Clips,
            int Sel, Dictionary<(int, int), Tr> Trans);
        private readonly Stack<UState> _undo = new();
        private readonly Stack<UState> _redo = new();

        // ── Transition preview state ────────────────────────────────
        private bool _fadeInRunning = false;

        public EditorView()
        {
            InitializeComponent();
            _timer.Tick += Timer_Tick;
            _timer.Start();
            PlayerFrame.SizeChanged += (_, e) =>
                PlayerFrame.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 14, 14);
            // only the visible part of the timeline is drawn → redraw while scrolling
            TimelineScroller.ScrollChanged += (_, e) => { if (e.HorizontalChange != 0) Redraw(); };
            // code-drawn timeline / clip list must be rebuilt when the theme flips
            ThemeManager.ThemeChanged += _ => Dispatcher.BeginInvoke(new Action(() => { Redraw(); UpdateStrip(); }));
            Loaded += (_, _) => Refresh();
        }

        // Called by MainWindow with an ACTION NAME — see MainWindow's shortcut dictionary.
        public void HandleAction(string action)
        {
            if (_clips.Count == 0) return;
            switch (action)
            {
                case "Play/Pause":    TogglePlay();            break;
                case "Add Cut":       CutAtPlayhead();         break;
                case "Del Segment":   DeleteCurrent(false);    break;
                case "Ripple Delete": DeleteCurrent(true);     break;
                case "Undo":          Undo();                  break;
                case "Redo":          Redo();                  break;
                case "Toggle Snap":   ToggleSnap();            break;
                case "Rewind":        SeekTo(0);               break;
                case "-5 seconds":    SeekTo(_curT - 5);       break;
                case "+5 seconds":    SeekTo(_curT + 5);       break;
                case "Prev Frame":    SeekTo(_curT - 1.0 / FrameRate()); break;
                case "Next Frame":    SeekTo(_curT + 1.0 / FrameRate()); break;
                case "Cut Only":      BtnEditorCutOnly_Click(null!, null!);    break;
                case "Cut + Crop":    BtnEditorCutAndCrop_Click(null!, null!); break;
            }
        }

        private double FrameRate()
        {
            var c = ActiveClipAt(_curT) ?? _clips.FirstOrDefault();
            return c != null && _mediaInfo.TryGetValue(c.SourcePath, out var mi) && mi.Fps > 1 ? mi.Fps : 25;
        }

        // ════════════════════════════════════════════════════════════
        // UNDO / REDO
        // ════════════════════════════════════════════════════════════
        private UState SnapshotState() => new UState(
            _clips.Select(c => (c.Id, c.SourcePath, c.Start, c.End, c.TlStart, c.Track)).ToList(),
            _selClipId,
            new Dictionary<(int, int), Tr>(_transitions));

        private void RestoreState(UState s)
        {
            _clips.Clear();
            foreach (var (id, src, st, en, tl, trk) in s.Clips)
                _clips.Add(new TClip { Id = id, SourcePath = src, Start = st, End = en, TlStart = tl, Track = trk });
            _selClipId = s.Sel;
            _transitions.Clear();
            foreach (var kv in s.Trans) _transitions[kv.Key] = kv.Value;
            Refresh();
        }

        private void SaveUndo() { _undo.Push(SnapshotState()); _redo.Clear(); }
        private void Undo() { if (_undo.Count == 0) return; _redo.Push(SnapshotState()); RestoreState(_undo.Pop()); }
        private void Redo() { if (_redo.Count == 0) return; _undo.Push(SnapshotState()); RestoreState(_redo.Pop()); }

        // ════════════════════════════════════════════════════════════
        // EDIT OPERATIONS
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// Overwrite: whatever sits in [a,b) on this lane (except clip `exceptId`) is cut away —
        /// fully covered clips disappear, partly covered ones are trimmed, a clip that spans the
        /// whole range is split in two.
        /// </summary>
        private void OverwriteRange(int track, double a, double b, int exceptId)
        {
            foreach (var o in _clips.Where(c => c.Track == track && c.Id != exceptId).ToList())
            {
                if (o.TlStart >= b - 1e-6 || o.TlEnd <= a + 1e-6) continue;       // no overlap

                if (o.TlStart >= a - 1e-6 && o.TlEnd <= b + 1e-6)                  // fully covered
                { _clips.Remove(o); DropTransitionsOf(o.Id); continue; }

                if (o.TlStart < a - 1e-6 && o.TlEnd > b + 1e-6)                    // spans the range → split
                {
                    var right = new TClip
                    {
                        Id = _nextClipId++, SourcePath = o.SourcePath, Track = o.Track,
                        Start = o.Start + (b - o.TlStart), End = o.End, TlStart = b
                    };
                    RemapTransitionFrom(o.Id, right.Id);
                    o.End = o.Start + (a - o.TlStart);
                    _clips.Add(right);
                    continue;
                }

                if (o.TlStart < a - 1e-6)                                           // overlaps on its right side
                {
                    o.End = o.Start + (a - o.TlStart);
                    DropTransitionsFrom(o.Id);
                }
                else                                                                // overlaps on its left side
                {
                    double d = b - o.TlStart;
                    o.Start += d; o.TlStart = b;
                    DropTransitionsTo(o.Id);
                }
            }
            // drop slivers that are too short to be useful
            foreach (var s in _clips.Where(c => c.Dur < 0.05).ToList()) { _clips.Remove(s); DropTransitionsOf(s.Id); }
        }

        private void DropTransitionsOf(int id)   { foreach (var k in _transitions.Keys.Where(k => k.Item1 == id || k.Item2 == id).ToList()) _transitions.Remove(k); }
        private void DropTransitionsFrom(int id) { foreach (var k in _transitions.Keys.Where(k => k.Item1 == id).ToList()) _transitions.Remove(k); }
        private void DropTransitionsTo(int id)   { foreach (var k in _transitions.Keys.Where(k => k.Item2 == id).ToList()) _transitions.Remove(k); }
        private void RemapTransitionFrom(int oldId, int newId)
        {
            foreach (var k in _transitions.Keys.Where(k => k.Item1 == oldId).ToList())
            { var v = _transitions[k]; _transitions.Remove(k); _transitions[(newId, k.Item2)] = v; }
        }

        private void SplitClipAt(TClip clip, double t)
        {
            if (t <= clip.TlStart + 0.1 || t >= clip.TlEnd - 0.1) return;
            SaveUndo();
            var right = new TClip
            {
                Id = _nextClipId++, SourcePath = clip.SourcePath, Track = clip.Track,
                Start = clip.Start + (t - clip.TlStart), End = clip.End, TlStart = t
            };
            RemapTransitionFrom(clip.Id, right.Id);
            clip.End = clip.Start + (t - clip.TlStart);
            _clips.Add(right);
            Refresh();
        }

        private void CutAtPlayhead()
        {
            var clip = ClipById(_selClipId);
            if (clip == null || _curT <= clip.TlStart || _curT >= clip.TlEnd) clip = ActiveClipAt(_curT);
            if (clip != null) SplitClipAt(clip, _curT);
        }

        // Delete the selected clip (or the one under the playhead). ripple = also close the gap on its lane.
        private void DeleteCurrent(bool ripple)
        {
            var c = ClipById(_selClipId) ?? ActiveClipAt(_curT);
            if (c != null) DeleteClip(c, ripple);
        }

        private void DeleteClip(TClip c, bool ripple)
        {
            SaveUndo();
            double cut = c.TlEnd, d = c.Dur;
            _clips.Remove(c);
            DropTransitionsOf(c.Id);
            if (ripple)
                foreach (var o in _clips.Where(x => x.Track == c.Track && x.TlStart >= cut - 1e-6)) o.TlStart -= d;
            _selClipId = -1;
            Refresh();
        }

        private void ToggleSnap()
        {
            _snap = !_snap;
            if (BtnMagnet != null) BtnMagnet.Tag = _snap ? "on" : null;
        }

        // Magnetic snapping of a clip's start/end to other clips' edges, the playhead and 0.
        private double SnapMove(double start, double dur, int exceptId, out double? guide)
        {
            guide = null;
            if (!_snap) return start;
            double th = 9 / _pps, best = double.MaxValue, delta = 0;
            var targets = new List<double> { 0, _curT };
            foreach (var c in _clips) if (c.Id != exceptId) { targets.Add(c.TlStart); targets.Add(c.TlEnd); }
            foreach (var g in targets)
            {
                double d1 = g - start, d2 = g - (start + dur);
                if (Math.Abs(d1) < th && Math.Abs(d1) < best) { best = Math.Abs(d1); delta = d1; guide = g; }
                if (Math.Abs(d2) < th && Math.Abs(d2) < best) { best = Math.Abs(d2); delta = d2; guide = g; }
            }
            return best < double.MaxValue ? start + delta : start;
        }

        private double SnapEdge(double t, int exceptId, out double? guide)
        {
            guide = null;
            if (!_snap) return t;
            double th = 9 / _pps, best = double.MaxValue, res = t;
            var targets = new List<double> { 0, _curT };
            foreach (var c in _clips) if (c.Id != exceptId) { targets.Add(c.TlStart); targets.Add(c.TlEnd); }
            foreach (var g in targets) if (Math.Abs(g - t) < th && Math.Abs(g - t) < best) { best = Math.Abs(g - t); res = g; guide = g; }
            return res;
        }

        // ════════════════════════════════════════════════════════════
        // PLAYBACK — a wall clock drives the playhead, the MediaElement follows the active clip.
        // Gaps simply play black; the upper lane wins where lanes overlap.
        // ════════════════════════════════════════════════════════════
        private void SetPlayhead(double t)
        {
            _curT = Math.Max(0, t);
            EditorCurrentTime.Text = FormatTime(_curT);
            SetPlayheadX(X(_curT));
        }

        private void SetPlayheadX(double x)
        {
            Canvas.SetLeft(TimelinePlayhead, x - 1);
            Canvas.SetLeft(PlayheadTriangle, x);
            Canvas.SetTop(PlayheadTriangle, RULER_H - 11);
        }

        private void TogglePlay()
        {
            if (_clips.Count == 0) return;
            if (_playing) { PausePlayback(); return; }
            if (_curT >= TimelineEnd() - 0.05) SetPlayhead(TimelineBegin());
            _clockBaseT = _curT; _clock.Restart(); _playing = true;
            BtnEditorPlay.Content = "";
            LoadActive(_curT, resumePlay: true);
        }

        private void PausePlayback()
        {
            _playing = false; _clock.Stop();
            EditorMediaElement.Pause();
            BtnEditorPlay.Content = "";
            StopFade();
        }

        private void SeekTo(double t)
        {
            if (_clips.Count == 0) { SetPlayhead(0); return; }
            t = Math.Clamp(t, 0, Math.Max(TimelineEnd(), 0));
            SetPlayhead(t);
            StopFade();
            if (_playing) { _clockBaseT = t; _clock.Restart(); }
            LoadActive(t, _playing);
        }

        /// <summary>Make the player show / play whatever is at timeline time t.</summary>
        private void LoadActive(double t, bool resumePlay)
        {
            var c = ActiveClipAt(t);
            if (c == null)
            {
                _activeId = -1;
                GapOverlay.Visibility = Visibility.Visible;
                EditorMediaElement.Pause();
                return;
            }
            GapOverlay.Visibility = Visibility.Collapsed;
            _activeId = c.Id;
            double srcT = c.Start + (t - c.TlStart);

            if (_loadedSourcePath != c.SourcePath)
            {
                _loadedSourcePath = c.SourcePath;
                _pendingSeek   = srcT;
                _pendingResume = resumePlay;
                EditorMediaElement.Source = new Uri(c.SourcePath);
                if (resumePlay) EditorMediaElement.Play(); else EditorMediaElement.Pause();   // Manual mode: a call is needed to open the file
            }
            else
            {
                EditorMediaElement.Position = TimeSpan.FromSeconds(srcT);
                if (resumePlay) EditorMediaElement.Play(); else EditorMediaElement.Pause();   // paused + ScrubbingEnabled = frame follows
            }
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_clips.Count == 0) return;
            if (!_playing) { if (FadeOverlay.Opacity > 0 && !_fadeInRunning) FadeOverlay.Opacity = 0; return; }

            double t = _clockBaseT + _clock.Elapsed.TotalSeconds;
            double end = TimelineEnd();
            if (t >= end)
            {
                PausePlayback();
                SetPlayhead(end);
                return;
            }

            _curT = t;
            var c = ActiveClipAt(t);
            int newId = c?.Id ?? -1;
            if (newId != _activeId)
            {
                // crossed a clip boundary (or entered / left a gap)
                var prev = ClipById(_activeId);
                if (prev != null && c != null && _transitions.TryGetValue((prev.Id, c.Id), out var trIn)
                    && trIn.Type != TrType.None && Math.Abs(prev.TlEnd - c.TlStart) <= 0.05)
                    StartFadeIn(trIn);
                else if (!_fadeInRunning) FadeOverlay.Opacity = 0;
                LoadActive(t, true);
            }
            else if (c != null && !_pendingSeek.HasValue)
            {
                // keep the media in step with the clock
                double expected = c.Start + (t - c.TlStart);
                if (Math.Abs(EditorMediaElement.Position.TotalSeconds - expected) > 0.4)
                    EditorMediaElement.Position = TimeSpan.FromSeconds(expected);

                // fade OUT while approaching a transition at the end of this clip
                if (!_fadeInRunning)
                {
                    var nxt = NextAdjacent(c);
                    if (nxt != null && _transitions.TryGetValue((c.Id, nxt.Id), out var tr) && tr.Type != TrType.None)
                    {
                        double left = c.TlEnd - t;
                        if (left >= 0 && left < tr.Dur)
                        {
                            FadeOverlay.Background = OverlayBrush(tr.Type);
                            FadeOverlay.Opacity = (1.0 - left / tr.Dur) * OverlayMaxOpacity(tr.Type);
                        }
                    }
                }
            }

            EditorCurrentTime.Text = FormatTime(t);
            double px = X(t);
            SetPlayheadX(px);
            EnsurePlayheadVisible(px);
        }

        private void StartFadeIn(Tr tr)
        {
            _fadeInRunning = true;
            FadeOverlay.Background = OverlayBrush(tr.Type);
            double maxOp = OverlayMaxOpacity(tr.Type);
            FadeOverlay.Opacity = maxOp;
            var anim = new DoubleAnimation(maxOp, 0, new Duration(TimeSpan.FromSeconds(tr.Dur)))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            anim.Completed += (_, _) => { _fadeInRunning = false; FadeOverlay.Opacity = 0; };
            FadeOverlay.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        private static Brush OverlayBrush(TrType t) => t == TrType.WhiteFlash ? Brushes.White : Brushes.Black;

        private static double OverlayMaxOpacity(TrType t) => t switch
        {
            TrType.DipToBlack    => 1.0,
            TrType.WhiteFlash    => 1.0,
            TrType.CrossDissolve => 0.5,
            TrType.Fade          => 0.65,
            _                    => 0.5
        };

        private void StopFade()
        {
            _fadeInRunning = false;
            FadeOverlay.BeginAnimation(UIElement.OpacityProperty, null);
            FadeOverlay.Opacity = 0;
        }

        private void EditorMedia_Opened(object s, RoutedEventArgs e)
        {
            if (!EditorMediaElement.NaturalDuration.HasTimeSpan) return;
            if (!string.IsNullOrEmpty(_loadedSourcePath))
                _sourceDurations[_loadedSourcePath] = EditorMediaElement.NaturalDuration.TimeSpan;

            if (_pendingSeek.HasValue)
            {
                EditorMediaElement.Position = TimeSpan.FromSeconds(_pendingSeek.Value);
                _pendingSeek = null;
                if (_pendingResume && _playing) EditorMediaElement.Play();
                else ShowFrameWhenReady();
            }
            _pendingResume = false;
        }

        // MediaElement paints a frame only after it has actually played for a moment:
        // play briefly (muted), then seek + pause on the wanted position.
        private async void ShowFrameWhenReady()
        {
            var wanted = EditorMediaElement.Position;
            string? src = _loadedSourcePath;
            bool wasMuted = EditorMediaElement.IsMuted;
            EditorMediaElement.IsMuted = true;
            EditorMediaElement.Play();
            await Task.Delay(160);
            EditorMediaElement.IsMuted = wasMuted;
            if (_playing || src != _loadedSourcePath) return;
            EditorMediaElement.Position = wanted + TimeSpan.FromMilliseconds(10);
            EditorMediaElement.Pause();
        }

        private void EditorMedia_Ended(object s, RoutedEventArgs e) { /* the wall clock decides when playback ends */ }

        // ════════════════════════════════════════════════════════════
        // REFRESH / LAYOUT
        // ════════════════════════════════════════════════════════════
        private double FitPps()
        {
            double vw = ViewportW; if (vw < 100) vw = Math.Max(300, ActualWidth - 120);
            double span = Math.Max(TimelineEnd() * 1.06, 12);
            return Math.Max(MIN_PPS, (vw - 30) / span);
        }

        private void ApplyCanvasWidth()
        {
            double vw = ViewportW;
            double content = Math.Min(MAX_CANVAS_W, TimelineEnd() * _pps + 70);
            TimelineCanvas.Width = Math.Max(vw > 0 ? vw : 0, content);
            TimelineScroller.HorizontalScrollBarVisibility =
                content > vw + 2 ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
        }

        private void Refresh()
        {
            if (!_userZoomed) _pps = FitPps();
            _pps = Math.Min(_pps, MAX_CANVAS_W / Math.Max(TimelineEnd(), 1));
            ApplyCanvasWidth();

            bool has = _clips.Count > 0;
            EditorPlaceholder.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            BtnEditorPlay.IsEnabled   = has;
            BtnEditorRewind.IsEnabled = has;
            if (BtnSaveProject != null) BtnSaveProject.IsEnabled = has;
            EditorTotalTime.Text = FormatTime(TimelineEnd());

            Redraw(); UpdateBtns();
            if (has) { if (_curT > TimelineEnd()) SetPlayhead(TimelineEnd()); LoadActive(_curT, _playing); }
            else { _activeId = -1; _loadedSourcePath = null; GapOverlay.Visibility = Visibility.Collapsed; }
        }

        // ════════════════════════════════════════════════════════════
        // DRAW TIMELINE
        // ════════════════════════════════════════════════════════════
        private static Shp.Rectangle Rect(double w, double h, Brush fill) => new() { Width = Math.Max(0, w), Height = Math.Max(0, h), Fill = fill };

        private void AddEl(UIElement el, double x, double y)
        {
            Canvas.SetLeft(el, x); Canvas.SetTop(el, y);
            TimelineCanvas.Children.Add(el); _tlEls.Add(el);
        }

        private void Redraw()
        {
            foreach (var el in _tlEls) TimelineCanvas.Children.Remove(el);
            _tlEls.Clear();
            double w = Math.Max(CanvasW, TimelineCanvas.Width > 0 ? TimelineCanvas.Width : 0);
            if (w < 4) return;

            // ruler band
            AddEl(Rect(w, RULER_H, Br("BgCard")), 0, 0);
            AddEl(Rect(w, 1, Br("Border")), 0, RULER_H);
            DrawRuler(w);

            // lanes
            for (int tr = 0; tr < TRACKS; tr++)
            {
                double y = LaneY(tr);
                var lane = Rect(w, LANE_H, Br("BgCard")); lane.RadiusX = lane.RadiusY = 8; lane.Opacity = 0.55;
                AddEl(lane, 0, y);
                AddEl(new TextBlock { Text = $"V{tr + 1}", FontSize = 10, FontWeight = FontWeights.Bold,
                                      Foreground = Br("TextDisabled"), IsHitTestVisible = false }, 6 + TimelineScroller.HorizontalOffset, y + 2);
            }

            if (_clips.Count == 0)
            {
                AddEl(new TextBlock { Text = "გადმოათრიე ვიდეო აქ ან დააჭირე „ვიდეოს გახსნა“", FontSize = 12.5,
                                      Foreground = Br("TextMuted"), IsHitTestVisible = false },
                      24, LaneY(0) + LANE_H / 2 - 9);
                SetPlayheadX(X(_curT));
                return;
            }

            bool moving = _mode == DragMode.Move && _dMoved && _dClip != null;

            // clips
            foreach (var c in _clips.OrderBy(c => c.Track).ThenBy(c => c.TlStart))
            {
                if (moving && _dClip!.Id == c.Id) continue;
                DrawClip(c, X(c.TlStart), LaneY(c.Track), c.Dur * _pps, _selClipId == c.Id, false);
            }

            // while dragging: show what the drop would overwrite, then the clip itself
            if (moving)
            {
                double ns = _dNewStart, ne = _dNewStart + _dClip!.Dur;
                foreach (var o in _clips.Where(c => c.Track == _dNewTrack && c.Id != _dClip.Id && c.TlStart < ne && c.TlEnd > ns))
                {
                    double ox1 = X(Math.Max(ns, o.TlStart)), ox2 = X(Math.Min(ne, o.TlEnd));
                    var warn = Rect(ox2 - ox1, LANE_H, Br("Danger")); warn.Opacity = 0.42; warn.RadiusX = warn.RadiusY = 6;
                    AddEl(warn, ox1, LaneY(_dNewTrack));
                }
                DrawClip(_dClip, X(ns), LaneY(_dNewTrack), _dClip.Dur * _pps, true, true);
            }

            // joins + transition markers
            foreach (var (a, b) in AdjacentPairs())
            {
                if (moving && (_dClip!.Id == a.Id || _dClip.Id == b.Id)) continue;
                DrawTransitionMarker(X(a.TlEnd), LaneY(a.Track), a.Id, b.Id);
            }

            // snap guide
            if (_snapGuide.HasValue)
            {
                var g = Rect(1.5, CANVAS_H - RULER_H, Br("Warning"));
                AddEl(g, X(_snapGuide.Value) - 0.75, RULER_H);
            }

            SetPlayheadX(X(_curT));
        }

        private void DrawRuler(double w)
        {
            double target = 95.0 / _pps;
            double[] nice = { 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600 };
            double interval = nice.FirstOrDefault(n => n >= target);
            if (interval == 0) interval = nice[^1];
            double minor = interval / 5;

            int k0 = (int)Math.Max(0, Math.Floor(T(VisL) / minor));
            int k1 = (int)Math.Ceiling(T(Math.Min(w, VisR)) / minor);
            for (int k = k0; k <= k1; k++)
            {
                double t = k * minor, x = X(t);
                bool major = k % 5 == 0;
                double tickH = major ? 9 : 4;
                AddEl(Rect(1, tickH, major ? Br("TextMuted") : Br("BorderStrong")), x, RULER_H - tickH);
                if (major)
                    AddEl(new TextBlock
                    {
                        Text = interval < 1 ? $"{t:0.0}s" : FormatTime(t),
                        FontSize = 10, IsHitTestVisible = false, Foreground = Br("TextMuted")
                    }, x + 3, 2);
            }
        }

        private void DrawClip(TClip c, double x, double y, double w, bool sel, bool ghost)
        {
            if (w < 1) return;
            double h = LANE_H;

            AddEl(new Shp.Rectangle { Width = w, Height = h, RadiusX = 9, RadiusY = 9, Fill = Br("BgSelected") }, x, y);
            if (sel)
                AddEl(new Shp.Rectangle { Width = w, Height = h, RadiusX = 9, RadiusY = 9, Fill = Br("AccentSoft") }, x, y);

            // waveform — one smooth mirrored shape, only for the visible part
            double a = Math.Max(x + 3, VisL), b = Math.Min(x + w - 3, VisR);
            if (b - a > 6 && w > 8)
            {
                double midY = y + h / 2 + 7, maxH = h * 0.27;
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    var xs = new List<double>();
                    for (double px = a; px < b; px += 2) xs.Add(px);
                    if (xs.Count > 1)
                    {
                        var amps = xs.Select(px =>
                        {
                            double frac = (px - x) / w;
                            return 1.5 + GetAmplitude(c.SourcePath, c.Start + Math.Clamp(frac, 0, 1) * c.Dur) * maxH;
                        }).ToList();
                        ctx.BeginFigure(new Point(xs[0], midY - amps[0]), true, true);
                        for (int i = 1; i < xs.Count; i++) ctx.LineTo(new Point(xs[i], midY - amps[i]), true, false);
                        for (int i = xs.Count - 1; i >= 0; i--) ctx.LineTo(new Point(xs[i], midY + amps[i]), true, false);
                    }
                }
                geo.Freeze();
                AddEl(new Shp.Path { Data = geo, Fill = sel ? Br("Accent") : Br("TextMuted"),
                                     Opacity = sel ? 0.55 : 0.32, IsHitTestVisible = false }, 0, 0);
            }

            // top strip + outline
            if (w > 24)
                AddEl(new Shp.Rectangle { Width = w - 18, Height = 3, RadiusX = 1.5, RadiusY = 1.5,
                                          Fill = sel ? Br("Accent") : Br("TextDisabled") }, x + 9, y + 5);
            var outline = new Shp.Rectangle
            {
                Width = w, Height = h, RadiusX = 9, RadiusY = 9, Fill = Brushes.Transparent,
                Stroke = sel ? Br("Accent") : Br("BorderStrong"), StrokeThickness = sel ? 2 : 1
            };
            if (ghost) outline.StrokeDashArray = new DoubleCollection { 3, 2 };
            AddEl(outline, x, y);

            // trim grips on the selected clip
            if (sel && w > 6 * EDGE_PX)
            {
                AddEl(new Shp.Rectangle { Width = 4, Height = 24, RadiusX = 2, RadiusY = 2, Fill = Br("Accent"), IsHitTestVisible = false }, x + 3, y + h / 2 - 12);
                AddEl(new Shp.Rectangle { Width = 4, Height = 24, RadiusX = 2, RadiusY = 2, Fill = Br("Accent"), IsHitTestVisible = false }, x + w - 7, y + h / 2 - 12);
            }

            // labels
            if (w > 56)
                AddEl(new TextBlock { Text = FormatTime(c.Dur), FontSize = 11, FontWeight = FontWeights.SemiBold,
                                      IsHitTestVisible = false, Foreground = Br("TextPrimary") }, x + 12, y + 11);
            if (w > 96)
                AddEl(new TextBlock { Text = Path.GetFileNameWithoutExtension(c.SourcePath), FontSize = 10.5,
                                      IsHitTestVisible = false, MaxWidth = w - 24, TextTrimming = TextTrimming.CharacterEllipsis,
                                      Foreground = Br("TextSecondary") }, x + 12, y + h - 21);
        }

        private void DrawTransitionMarker(double x, double laneY, int fromId, int toId)
        {
            _transitions.TryGetValue((fromId, toId), out var tr);
            var type = tr?.Type ?? TrType.None;
            (string colKey, string icon) = type switch
            {
                TrType.CrossDissolve => ("Info",        "≈"),
                TrType.DipToBlack    => ("Accent",      "■"),
                TrType.WhiteFlash    => ("TextPrimary", "☀"),
                TrType.Fade          => ("Warning",     "◑"),
                _                    => ("TextMuted",   "+")
            };
            double cy = laneY + MARKER_Y;          // near the top of the lane → the lower part of the edge stays free for trimming
            var bg = new Shp.Ellipse
            {
                Width = TR_R * 2, Height = TR_R * 2, Fill = Br("BgPanel"),
                Stroke = type == TrType.None ? Br("BorderStrong") : Br(colKey),
                StrokeThickness = 2, IsHitTestVisible = false
            };
            Canvas.SetZIndex(bg, 500);
            AddEl(bg, x - TR_R, cy - TR_R);
            var lbl = new TextBlock
            {
                Text = icon, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Br(colKey),
                IsHitTestVisible = false, TextAlignment = TextAlignment.Center, Width = TR_R * 2
            };
            Canvas.SetZIndex(lbl, 501);
            AddEl(lbl, x - TR_R, cy - 9);
        }

        private void CycleTransition(int fromId, int toId)
        {
            var key = (fromId, toId);
            SaveUndo();
            _transitions.TryGetValue(key, out var cur);
            var next = (cur?.Type ?? TrType.None) switch
            {
                TrType.None          => new Tr(TrType.CrossDissolve, 0.5),
                TrType.CrossDissolve => new Tr(TrType.DipToBlack,    0.4),
                TrType.DipToBlack    => new Tr(TrType.WhiteFlash,    0.3),
                TrType.WhiteFlash    => new Tr(TrType.Fade,          0.5),
                _                    => null
            };
            if (next == null) _transitions.Remove(key); else _transitions[key] = next;
            Refresh();
        }

        // ════════════════════════════════════════════════════════════
        // REAL AUDIO WAVEFORM — extracted once PER SOURCE FILE via ffmpeg, cached by path.
        // ════════════════════════════════════════════════════════════
        private async Task ExtractWaveformAsync(string sourcePath)
        {
            if (_waveforms.ContainsKey(sourcePath)) return;

            const int sampleRate = 4000;
            var si = new ProcessStartInfo
            {
                FileName  = FF(),
                UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var a in new[] { "-i", sourcePath, "-vn", "-ac", "1", "-ar", sampleRate.ToString(), "-f", "s16le", "-acodec", "pcm_s16le", "-loglevel", "quiet", "pipe:1" })
                si.ArgumentList.Add(a);

            byte[] pcm;
            try
            {
                using var p = Process.Start(si);
                if (p == null) return;
                using var ms = new MemoryStream();
                await p.StandardOutput.BaseStream.CopyToAsync(ms).ConfigureAwait(false);
                p.WaitForExit(15000);
                pcm = ms.ToArray();
            }
            catch { return; }

            if (pcm.Length < 4) return;
            int sampleCount = pcm.Length / 2;
            var buckets = new float[WAVEFORM_BUCKETS];
            double samplesPerBucket = Math.Max(1.0, (double)sampleCount / WAVEFORM_BUCKETS);
            for (int bk = 0; bk < WAVEFORM_BUCKETS; bk++)
            {
                int startSample = (int)(bk * samplesPerBucket);
                int endSample   = Math.Min(sampleCount, (int)((bk + 1) * samplesPerBucket));
                short peak = 0;
                for (int s = startSample; s < endSample; s++)
                {
                    int byteOff = s * 2;
                    if (byteOff + 1 >= pcm.Length) break;
                    short sample = (short)(pcm[byteOff] | (pcm[byteOff + 1] << 8));
                    short abs = (short)Math.Abs((int)sample);
                    if (abs > peak) peak = abs;
                }
                buckets[bk] = (float)Math.Sqrt(Math.Clamp(peak / 32768f, 0, 1));
            }

            _waveforms[sourcePath] = buckets;
            Dispatcher.Invoke(Redraw);
        }

        private float GetAmplitude(string sourcePath, double sourceTime)
        {
            if (!_waveforms.TryGetValue(sourcePath, out var wf) || wf == null) return 0.18f;
            if (!_sourceDurations.TryGetValue(sourcePath, out var dur) || dur.TotalSeconds <= 0) return 0.18f;
            double frac = Math.Clamp(sourceTime / dur.TotalSeconds, 0, 1);
            return wf[Math.Clamp((int)(frac * (WAVEFORM_BUCKETS - 1)), 0, WAVEFORM_BUCKETS - 1)];
        }

        // ════════════════════════════════════════════════════════════
        // CLIP LIST (right panel) + BUTTON STATES
        // ════════════════════════════════════════════════════════════
        private void UpdateStrip()
        {
            SegmentsStrip.Children.Clear();
            TxtNoSegments.Visibility = _clips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtTotalDur.Text = _clips.Count == 0 ? "—" : FormatTime(ExportDuration());

            int index = 0;
            foreach (var clip in _clips.OrderBy(c => c.TlStart).ThenBy(c => c.Track))
            {
                index++;
                bool sel = _selClipId == clip.Id;
                var card = new Border
                {
                    CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1.5),
                    Padding = new Thickness(11, 10, 8, 10), Margin = new Thickness(0, 0, 0, 8),
                    Cursor = Cursors.Hand, Tag = clip.Id
                };
                card.SetResourceReference(Border.BackgroundProperty,  sel ? "AccentSoft" : "BgCard");
                card.SetResourceReference(Border.BorderBrushProperty, sel ? "Accent"     : "Border");

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var num = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 12, 0) };
                num.SetResourceReference(Border.BackgroundProperty, sel ? "Accent" : "BgInput");
                var numText = new TextBlock
                {
                    Text = index.ToString(), FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                };
                numText.SetResourceReference(TextBlock.ForegroundProperty, sel ? "OnAccent" : "TextSecondary");
                num.Child = numText;
                Grid.SetColumn(num, 0);

                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var name = new TextBlock
                {
                    Text = Path.GetFileNameWithoutExtension(clip.SourcePath),
                    FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis
                };
                name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
                var range = new TextBlock
                {
                    Text = $"V{clip.Track + 1}  ·  {FormatTime(clip.TlStart)} – {FormatTime(clip.TlEnd)}  ·  {FormatTime(clip.Dur)}",
                    FontSize = 11.5, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis
                };
                range.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
                info.Children.Add(name); info.Children.Add(range);
                Grid.SetColumn(info, 1);

                var removeBtn = new Button
                {
                    Content = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11,
                    Padding = new Thickness(9, 8, 9, 8), Tag = clip.Id, ToolTip = "კლიპის წაშლა",
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0)
                };
                removeBtn.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");
                removeBtn.Click += (s, e) =>
                {
                    e.Handled = true;
                    var c = ClipById((int)((Button)s).Tag);
                    if (c != null) DeleteClip(c, false);
                };
                Grid.SetColumn(removeBtn, 2);

                grid.Children.Add(num); grid.Children.Add(info); grid.Children.Add(removeBtn);
                card.Child = grid;

                card.MouseEnter += (s, _) => { if ((int)((Border)s).Tag != _selClipId) ((Border)s).SetResourceReference(Border.BackgroundProperty, "BgHover"); };
                card.MouseLeave += (s, _) => { if ((int)((Border)s).Tag != _selClipId) ((Border)s).SetResourceReference(Border.BackgroundProperty, "BgCard"); };
                card.MouseLeftButtonDown += (s, _) =>
                {
                    int id = (int)((Border)s).Tag;
                    var c = ClipById(id);
                    if (c == null) return;
                    if (_selClipId == id) _selClipId = -1;
                    else { _selClipId = id; SeekTo(c.TlStart); }
                    Redraw(); UpdateStrip(); UpdateBtnsOnly();
                };
                SegmentsStrip.Children.Add(card);
            }

            EditorClipDuration.Text = _clips.Count == 0 ? "—" : $"{_clips.Count} კლიპი";
        }

        private void UpdateBtns()
        {
            UpdateBtnsOnly();
            UpdateStrip();
        }

        private void UpdateBtnsOnly()
        {
            bool hasClips = _clips.Count > 0;
            BtnEditorCutOnly.IsEnabled    = hasClips;
            BtnEditorCutAndCrop.IsEnabled = hasClips;
            if (BtnCutHere       != null) BtnCutHere.IsEnabled       = hasClips;
            if (BtnUndo          != null) BtnUndo.IsEnabled           = _undo.Count > 0;
            if (BtnRedo          != null) BtnRedo.IsEnabled           = _redo.Count > 0;
            if (BtnResetCuts     != null) BtnResetCuts.IsEnabled      = hasClips || _undo.Count > 0;
            if (BtnDeleteSegment != null) BtnDeleteSegment.IsEnabled  = hasClips;
            if (TxtCutInfo       != null) TxtCutInfo.Text = $"{_clips.Count} კლიპი";
        }

        // ════════════════════════════════════════════════════════════
        // TIMELINE MOUSE — select, move (free, between lanes), trim edges, scrub
        // ════════════════════════════════════════════════════════════
        public void PlayheadHandle_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_clips.Count == 0) return;
            _mode = DragMode.Playhead;
            TimelineCanvas.CaptureMouse(); e.Handled = true;
        }

        // The clip under the cursor and which part of it: -1 left edge, 0 body, +1 right edge
        private (TClip? clip, int part) HitTest(double x, double y)
        {
            int tr = TrackAtY(y);
            if (tr < 0) return (null, 0);
            TClip? edgeClip = null; int edgePart = 0; double bestD = EDGE_PX + 0.5;
            foreach (var c in TrackClips(tr))
            {
                double xs = X(c.TlStart), xe = X(c.TlEnd);
                if (x < xs - EDGE_PX || x > xe + EDGE_PX) continue;
                if (xe - xs <= 2.5 * EDGE_PX) continue;               // too narrow to trim: body only
                double dl = Math.Abs(x - xs), dr = Math.Abs(x - xe);
                if (dl <= EDGE_PX && dl < bestD) { bestD = dl; edgeClip = c; edgePart = -1; }
                if (dr <= EDGE_PX && dr < bestD) { bestD = dr; edgeClip = c; edgePart =  1; }
            }
            if (edgeClip != null) return (edgeClip, edgePart);
            return (TrackClips(tr).FirstOrDefault(c => x >= X(c.TlStart) && x <= X(c.TlEnd)), 0);
        }

        private void Timeline_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var pos = e.GetPosition(TimelineCanvas);
            double x = pos.X, y = pos.Y;

            // transition marker?
            foreach (var (a, b) in AdjacentPairs())
                if (Math.Abs(x - X(a.TlEnd)) <= TR_R && Math.Abs(y - (LaneY(a.Track) + MARKER_Y)) <= TR_R)
                { CycleTransition(a.Id, b.Id); e.Handled = true; return; }

            // ruler → scrub
            if (y <= RULER_H)
            {
                _mode = DragMode.Playhead;
                SeekTo(T(x));
                TimelineCanvas.CaptureMouse(); return;
            }

            var (clip, part) = HitTest(x, y);
            if (clip == null)
            {
                _selClipId = -1; _mode = DragMode.Scrub;
                SeekTo(T(x));
                Redraw(); UpdateStrip(); UpdateBtnsOnly();
                TimelineCanvas.CaptureMouse(); return;
            }

            _selClipId = clip.Id;
            _dClip = clip; _dStartX = x; _dStartY = y; _dMoved = false;
            _dOrigTl = clip.TlStart; _dOrigStart = clip.Start; _dOrigEnd = clip.End;
            _dNewStart = clip.TlStart; _dNewTrack = clip.Track;
            _dUndo = SnapshotState(); _snapGuide = null;
            _mode = part < 0 ? DragMode.TrimL : part > 0 ? DragMode.TrimR : DragMode.Move;
            TimelineCanvas.CaptureMouse();
            Redraw(); UpdateStrip(); UpdateBtnsOnly();
        }

        private void Timeline_MouseMove(object sender, MouseEventArgs e)
        {
            var pos = e.GetPosition(TimelineCanvas);
            double x = Math.Clamp(pos.X, 0, Math.Max(CanvasW, 1)), y = pos.Y;

            if (_mode == DragMode.None)
            {
                var (hc, hp) = HitTest(pos.X, pos.Y);           // hover cursor
                TimelineCanvas.Cursor = hc == null ? Cursors.Arrow : hp != 0 ? Cursors.SizeWE : Cursors.SizeAll;
                return;
            }

            switch (_mode)
            {
                case DragMode.Playhead:
                case DragMode.Scrub:
                    SeekTo(T(x));
                    break;

                case DragMode.Move when _dClip != null:
                {
                    if (!_dMoved && Math.Abs(x - _dStartX) < 4 && Math.Abs(y - _dStartY) < 8) break;
                    if (!_dMoved) { _dMoved = true; if (_playing) PausePlayback(); }
                    double ns = Math.Max(0, _dOrigTl + (x - _dStartX) / _pps);
                    ns = Math.Max(0, SnapMove(ns, _dClip.Dur, _dClip.Id, out _snapGuide));
                    int ntr = TrackAtY(y); if (ntr < 0) ntr = y < RULER_H + LANE_TOP ? TRACKS - 1 : _dNewTrack;
                    _dNewStart = ns; _dNewTrack = ntr;
                    Redraw();
                    break;
                }

                case DragMode.TrimL when _dClip != null:
                {
                    if (!_dMoved && Math.Abs(x - _dStartX) < 2) break;
                    _dMoved = true; if (_playing) PausePlayback();
                    double prevEnd = _clips.Where(c => c.Track == _dClip.Track && c.Id != _dClip.Id && c.TlEnd <= _dOrigTl + 1e-6)
                                           .Select(c => c.TlEnd).DefaultIfEmpty(0).Max();
                    double lo = Math.Max(prevEnd, _dOrigTl - _dOrigStart);          // can't reveal before the source starts
                    double hi = _dOrigTl + (_dOrigEnd - _dOrigStart) - MIN_CLIP;
                    double nt = SnapEdge(_dOrigTl + (x - _dStartX) / _pps, _dClip.Id, out _snapGuide);
                    nt = Math.Clamp(nt, lo, hi);
                    _dClip.TlStart = nt; _dClip.Start = _dOrigStart + (nt - _dOrigTl);
                    Redraw();
                    SeekTo(nt + 0.001);
                    break;
                }

                case DragMode.TrimR when _dClip != null:
                {
                    if (!_dMoved && Math.Abs(x - _dStartX) < 2) break;
                    _dMoved = true; if (_playing) PausePlayback();
                    double srcMax = _sourceDurations.TryGetValue(_dClip.SourcePath, out var sd) ? sd.TotalSeconds : double.MaxValue;
                    double nextStart = _clips.Where(c => c.Track == _dClip.Track && c.Id != _dClip.Id && c.TlStart >= _dOrigTl + 1e-6)
                                             .Select(c => c.TlStart).DefaultIfEmpty(double.MaxValue).Min();
                    double hiEnd = Math.Min(srcMax, nextStart == double.MaxValue ? double.MaxValue : _dOrigStart + (nextStart - _dOrigTl));
                    double wantTlEnd = SnapEdge(_dOrigTl + (_dOrigEnd - _dOrigStart) + (x - _dStartX) / _pps, _dClip.Id, out _snapGuide);
                    double ne = _dOrigStart + (wantTlEnd - _dOrigTl);
                    ne = Math.Clamp(ne, _dOrigStart + MIN_CLIP, hiEnd);
                    _dClip.End = ne;
                    Redraw();
                    SeekTo(_dClip.TlEnd - 0.04);
                    break;
                }
            }
        }

        private void Timeline_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            TimelineCanvas.ReleaseMouseCapture();
            var mode = _mode; _mode = DragMode.None; _snapGuide = null;

            if (mode == DragMode.Playhead || mode == DragMode.Scrub) { Redraw(); return; }
            if (_dClip == null) return;
            var clip = _dClip; _dClip = null;

            switch (mode)
            {
                case DragMode.Move:
                    if (_dMoved)
                    {
                        if (_dUndo != null) { _undo.Push(_dUndo); _redo.Clear(); }
                        clip.TlStart = _dNewStart; clip.Track = _dNewTrack;
                        OverwriteRange(clip.Track, clip.TlStart, clip.TlEnd, clip.Id);
                        DropTransitionsOf(clip.Id);            // a moved clip no longer joins its old neighbours
                        Refresh();
                    }
                    else
                    {
                        SeekTo(T(e.GetPosition(TimelineCanvas).X));      // plain click on a clip = seek + select
                        Redraw();
                    }
                    break;

                case DragMode.TrimL:
                case DragMode.TrimR:
                    if (_dMoved && _dUndo != null)
                    {
                        _undo.Push(_dUndo); _redo.Clear();
                        if (mode == DragMode.TrimL) DropTransitionsTo(clip.Id); else DropTransitionsFrom(clip.Id);   // trimming breaks the join on that side
                        Refresh();
                    }
                    break;
            }
            _dUndo = null; _dMoved = false;
            UpdateBtns();
        }

        private void Timeline_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_clips.Count == 0) return;
            var pos = e.GetPosition(TimelineCanvas);
            if (pos.Y <= RULER_H) { e.Handled = true; return; }
            var (clip, _) = HitTest(pos.X, pos.Y);
            if (clip != null) SplitClipAt(clip, T(pos.X));
            e.Handled = true;
        }

        private void Timeline_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_clips.Count == 0) return;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                ApplyZoom(e.Delta > 0 ? 1.3 : 1.0 / 1.3);                    // Ctrl + wheel = zoom
            else
                TimelineScroller.ScrollToHorizontalOffset(TimelineScroller.HorizontalOffset - e.Delta);   // wheel = pan
            e.Handled = true;
        }

        private void ApplyZoom(double factor)
        {
            if (_clips.Count == 0) return;
            _userZoomed = true;
            _pps = Math.Clamp(_pps * factor, MIN_PPS, 400);
            _pps = Math.Min(_pps, MAX_CANVAS_W / Math.Max(TimelineEnd(), 1));
            ApplyCanvasWidth();
            TimelineCanvas.UpdateLayout();
            TimelineScroller.ScrollToHorizontalOffset(Math.Max(0, X(_curT) - ViewportW / 2));
            Redraw();
            SetPlayheadX(X(_curT));
        }

        private void BtnZoomIn_Click(object s, RoutedEventArgs e)  => ApplyZoom(1.5);
        private void BtnZoomOut_Click(object s, RoutedEventArgs e) => ApplyZoom(1.0 / 1.5);
        private void BtnZoomFit_Click(object s, RoutedEventArgs e) { _userZoomed = false; TimelineScroller.ScrollToHorizontalOffset(0); Refresh(); }

        // keep the playhead in view while playing on a zoomed timeline
        private void EnsurePlayheadVisible(double x)
        {
            if (_mode != DragMode.None) return;
            double off = TimelineScroller.HorizontalOffset, vw = TimelineScroller.ViewportWidth;
            if (vw <= 0) return;
            if (x < off + 20 || x > off + vw - 40)
                TimelineScroller.ScrollToHorizontalOffset(Math.Max(0, x - vw * 0.3));
        }

        private void Timeline_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!IsLoaded) return;
            Refresh();
        }

        // ════════════════════════════════════════════════════════════
        // BUTTONS
        // ════════════════════════════════════════════════════════════
        private void BtnCutHere_Click(object s, RoutedEventArgs e)       => CutAtPlayhead();
        private void BtnDeleteSegment_Click(object s, RoutedEventArgs e) => DeleteCurrent(false);
        private void BtnUndo_Click(object s, RoutedEventArgs e)          => Undo();
        private void BtnRedo_Click(object s, RoutedEventArgs e)          => Redo();
        private void BtnMagnet_Click(object s, RoutedEventArgs e)        => ToggleSnap();
        private void BtnEditorPlay_Click(object s, RoutedEventArgs e)    => TogglePlay();
        private void BtnEditorRewind_Click(object s, RoutedEventArgs e)  => SeekTo(0);

        private void BtnResetCuts_Click(object s, RoutedEventArgs e)
        {
            if (MessageBox.Show("მთლიანი ტაიმლაინი გასუფთავდება. დარწმუნებული ხარ?",
                "გასუფთავება", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            ResetTimeline();
        }

        // ════════════════════════════════════════════════════════════
        // MEDIA IMPORT
        // ════════════════════════════════════════════════════════════
        private void BtnEditorOpenFile_Click(object s, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog { Title = "ვიდეოს გახსნა (იწყებს ახალ ტაიმლაინს)",
                Filter = "Video|*.mp4;*.mov;*.mxf;*.avi;*.mkv|All|*.*" };
            if (ofd.ShowDialog() != true) return;
            LoadVideo(ofd.FileName);
        }

        // "+ კლიპი" — appends files to the END of lane V1 without clearing the timeline.
        private void BtnAddClip_Click(object s, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog { Title = "კლიპის დამატება",
                Filter = "Video|*.mp4;*.mov;*.mxf;*.avi;*.mkv|All|*.*", Multiselect = true };
            if (ofd.ShowDialog() != true) return;
            _ = AddClipsAsync(ofd.FileNames);
        }

        // Public: start a fresh timeline with this single file (used by Downloader hand-off, etc.)
        public void LoadVideo(string path)
        {
            if (!File.Exists(path)) return;
            ResetTimeline();
            _ = AddClipsAsync(new[] { path });
        }

        // Public: append clip(s) to the end of lane V1.
        public Task AddClipsAsync(IEnumerable<string> paths) => AddClipsAtAsync(paths, 0, null);

        // Place clip(s) on a lane starting at `at` seconds (null = after the last clip on that lane).
        public async Task AddClipsAtAsync(IEnumerable<string> paths, int track, double? at)
        {
            bool wasEmpty = _clips.Count == 0;
            var list = paths.Where(File.Exists).ToList();
            if (list.Count == 0) return;
            if (!wasEmpty) SaveUndo();

            double t = at ?? EndOfTrack(track);
            int added = 0;
            foreach (var path in list)
            {
                var dur = await ProbeDurationAsync(path);
                if (dur <= TimeSpan.Zero) continue;
                await ProbeMediaInfoAsync(path);
                var clip = new TClip { Id = _nextClipId++, SourcePath = path, Start = 0, End = dur.TotalSeconds, TlStart = t, Track = track };
                OverwriteRange(track, clip.TlStart, clip.TlEnd, clip.Id);
                _clips.Add(clip);
                t += dur.TotalSeconds; added++;
                if (!_waveforms.ContainsKey(path)) _ = ExtractWaveformAsync(path);
            }
            if (added == 0) return;

            if (wasEmpty) { _curT = 0; _userZoomed = false; }
            Refresh();
            if (wasEmpty) SeekTo(0);
            if (!wasEmpty) Notify?.Invoke($"{_clips.Count} კლიპი ტაიმლაინზე", "success");
        }

        // ffmpeg-based probes (parse stderr of `ffmpeg -i`, no ffprobe needed).
        private async Task<string> RunProbe(string path)
        {
            var si = new ProcessStartInfo { FileName = FF(), UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
            si.ArgumentList.Add("-hide_banner"); si.ArgumentList.Add("-i"); si.ArgumentList.Add(path);
            return await Task.Run(() =>
            {
                try { using var p = Process.Start(si); string e = p!.StandardError.ReadToEnd(); p.WaitForExit(8000); return e; }
                catch { return ""; }
            });
        }

        private async Task<TimeSpan> ProbeDurationAsync(string path)
        {
            if (_sourceDurations.TryGetValue(path, out var cached)) return cached;
            string err = await RunProbe(path);
            var m = System.Text.RegularExpressions.Regex.Match(err, @"Duration:\s*(\d+):(\d+):(\d+\.\d+)");
            if (!m.Success) return TimeSpan.Zero;
            var d = TimeSpan.FromHours(int.Parse(m.Groups[1].Value))
                  + TimeSpan.FromMinutes(int.Parse(m.Groups[2].Value))
                  + TimeSpan.FromSeconds(double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
            _sourceDurations[path] = d;
            return d;
        }

        private async Task<MediaInfo> ProbeMediaInfoAsync(string path)
        {
            if (_mediaInfo.TryGetValue(path, out var cached)) return cached;
            string err = await RunProbe(path);
            int w = 1920, h = 1080; double fps = 25; bool audio = err.Contains(": Audio:");
            var v = System.Text.RegularExpressions.Regex.Match(err, @"Video:[^\r\n]*?,\s*(\d{2,5})x(\d{2,5})");
            if (v.Success) { w = int.Parse(v.Groups[1].Value); h = int.Parse(v.Groups[2].Value); }
            var f = System.Text.RegularExpressions.Regex.Match(err, @"(\d+(?:\.\d+)?)\s+fps");
            if (f.Success && double.TryParse(f.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fv) && fv > 1) fps = fv;
            var info = new MediaInfo(w, h, fps, audio);
            _mediaInfo[path] = info;
            return info;
        }

        // ════════════════════════════════════════════════════════════
        // PROJECT SAVE / RESUME  (.flowproj = JSON)
        // ════════════════════════════════════════════════════════════
        private void BtnSaveProject_Click(object s, RoutedEventArgs e)
        {
            if (_clips.Count == 0)
            {
                MessageBox.Show("ჯერ დაამატე კლიპი.", "პროექტი", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var sfd = new SaveFileDialog
            {
                Title = "პროექტის შენახვა", Filter = "1Flow Project|*.flowproj",
                FileName = Path.GetFileNameWithoutExtension(_clips.OrderBy(c => c.TlStart).First().SourcePath)
            };
            if (sfd.ShowDialog() != true) return;
            try
            {
                SaveProject(sfd.FileName);
                Notify?.Invoke($"პროექტი შენახულია: {Path.GetFileName(sfd.FileName)}", "success");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"ვერ შევინახე: {ex.Message}", "შეცდომა", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnLoadProject_Click(object s, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog { Title = "პროექტის გახსნა", Filter = "1Flow Project|*.flowproj|All|*.*" };
            if (ofd.ShowDialog() != true) return;
            _ = LoadProjectAsync(ofd.FileName);
        }

        public void SaveProject(string projectPath)
        {
            var proj = new FlowProject
            {
                Version = 2,
                Clips = _clips.Select(c => new ClipEntry
                {
                    Id = c.Id, SourcePath = c.SourcePath, StartSec = c.Start, EndSec = c.End,
                    TlStartSec = c.TlStart, Track = c.Track
                }).ToList(),
                Transitions = _transitions.Select(kv => new TransitionEntry
                {
                    From = kv.Key.Item1, To = kv.Key.Item2, Type = kv.Value.Type.ToString(), Dur = kv.Value.Dur
                }).ToList(),
                Zoom = _pps, SavedAt = DateTime.Now
            };
            File.WriteAllText(projectPath, JsonSerializer.Serialize(proj, new JsonSerializerOptions { WriteIndented = true }));
        }

        public async Task LoadProjectAsync(string projectPath)
        {
            FlowProject? proj;
            try { proj = JsonSerializer.Deserialize<FlowProject>(File.ReadAllText(projectPath)); }
            catch (Exception ex)
            {
                MessageBox.Show($"პროექტის წაკითხვა ვერ მოხერხდა: {ex.Message}", "შეცდომა", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (proj == null || proj.Clips.Count == 0) return;

            // missing source files → ask where they are now
            var relocate = new Dictionary<string, string>();
            foreach (var srcPath in proj.Clips.Select(c => c.SourcePath).Distinct())
            {
                if (File.Exists(srcPath)) continue;
                MessageBox.Show($"ვერ მოიძებნა:\n{srcPath}\n\nმიუთითეთ ახალი მდებარეობა.", "ვიდეო ვერ მოიძებნა",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                var ofd = new OpenFileDialog { Title = "მიუთითეთ ფაილის ახალი მდებარეობა",
                    Filter = "Video|*.mp4;*.mov;*.mxf;*.avi;*.mkv|All|*.*" };
                if (ofd.ShowDialog() != true) return;
                relocate[srcPath] = ofd.FileName;
            }

            ResetTimeline();
            int maxId = 0; double seqPos = 0;
            foreach (var ce in proj.Clips)
            {
                string actual = relocate.TryGetValue(ce.SourcePath, out var np) ? np : ce.SourcePath;
                var c = new TClip { Id = ce.Id, SourcePath = actual, Start = ce.StartSec, End = ce.EndSec, Track = Math.Clamp(ce.Track, 0, TRACKS - 1) };
                // old projects (v1) had no positions: lay the clips out one after another
                if (proj.Version < 2) { c.TlStart = seqPos; seqPos += c.Dur; } else c.TlStart = ce.TlStartSec;
                _clips.Add(c);
                maxId = Math.Max(maxId, ce.Id);
                await ProbeDurationAsync(actual);
                await ProbeMediaInfoAsync(actual);
                if (!_waveforms.ContainsKey(actual)) _ = ExtractWaveformAsync(actual);
            }
            _nextClipId = maxId + 1;
            foreach (var t in proj.Transitions)
                if (Enum.TryParse<TrType>(t.Type, out var type)) _transitions[(t.From, t.To)] = new Tr(type, t.Dur);

            _userZoomed = false; _curT = 0;
            Refresh(); SeekTo(0);
            Notify?.Invoke("პროექტი ჩაიტვირთა", "success");
        }

        // ════════════════════════════════════════════════════════════
        // DRAG & DROP FROM EXPLORER
        // Drop on the PLAYER → fresh timeline; drop on the TIMELINE → placed where you drop it.
        // ════════════════════════════════════════════════════════════
        private static readonly string[] VideoExts = { ".mp4", ".mov", ".mxf", ".avi", ".mkv", ".webm", ".m4v" };

        private static bool IsDroppedVideo(DragEventArgs e, out string[] paths)
        {
            paths = Array.Empty<string>();
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return false;
            var valid = files.Where(f => VideoExts.Contains(Path.GetExtension(f).ToLowerInvariant())).ToArray();
            if (valid.Length == 0) return false;
            paths = valid;
            return true;
        }

        private void VideoArea_DragEnter(object sender, DragEventArgs e)
        {
            if (IsDroppedVideo(e, out _)) { DragOverlay.Visibility = Visibility.Visible; e.Effects = DragDropEffects.Copy; }
            else e.Effects = DragDropEffects.None;
            e.Handled = true;
        }
        private void VideoArea_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = IsDroppedVideo(e, out _) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }
        private void VideoArea_DragLeave(object sender, DragEventArgs e) => DragOverlay.Visibility = Visibility.Collapsed;
        private void VideoArea_Drop(object sender, DragEventArgs e)
        {
            DragOverlay.Visibility = Visibility.Collapsed;
            if (IsDroppedVideo(e, out var paths)) LoadVideo(paths[0]);
            e.Handled = true;
        }

        private void TimelineArea_DragEnter(object sender, DragEventArgs e)
        {
            if (IsDroppedVideo(e, out _)) { TimelineDragOverlay.Visibility = Visibility.Visible; e.Effects = DragDropEffects.Copy; }
            else e.Effects = DragDropEffects.None;
            e.Handled = true;
        }
        private void TimelineArea_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = IsDroppedVideo(e, out _) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }
        private void TimelineArea_DragLeave(object sender, DragEventArgs e) => TimelineDragOverlay.Visibility = Visibility.Collapsed;
        private void TimelineArea_Drop(object sender, DragEventArgs e)
        {
            TimelineDragOverlay.Visibility = Visibility.Collapsed;
            if (IsDroppedVideo(e, out var paths))
            {
                var p = e.GetPosition(TimelineCanvas);
                int tr = TrackAtY(p.Y); if (tr < 0) tr = 0;
                double? at = _clips.Count == 0 ? 0 : Math.Max(0, T(p.X));
                _ = AddClipsAtAsync(paths, tr, at);
            }
            e.Handled = true;
        }

        private void ResetTimeline()
        {
            if (_playing) PausePlayback();
            _clips.Clear(); _nextClipId = 1; _selClipId = -1;
            _undo.Clear(); _redo.Clear();
            _transitions.Clear();
            _mode = DragMode.None; _dClip = null; _snapGuide = null;
            _userZoomed = false; _curT = 0;
            _activeId = -1; _loadedSourcePath = null; _pendingSeek = null; _pendingResume = false;
            GapOverlay.Visibility = Visibility.Collapsed;
            EditorCurrentTime.Text = EditorTotalTime.Text = "0:00:00";
            EditorClipDuration.Text = "—";
            TimelineScroller.ScrollToHorizontalOffset(0);
            Refresh();
        }

        // ════════════════════════════════════════════════════════════
        // EXPORT — the timeline is flattened into a list of segments (clip piece or black gap, upper
        // lane wins) and rendered in ONE frame-accurate pass: every piece is scaled/padded to the same
        // size and frame rate, clips without audio get silence, joins can carry xfade transitions.
        // ════════════════════════════════════════════════════════════
        private record Seg(TClip? Clip, double Src, double Dur);

        private List<Seg> Flatten()
        {
            var segs = new List<Seg>();
            if (_clips.Count == 0) return segs;
            var times = new SortedSet<double>();
            foreach (var c in _clips) { times.Add(Math.Round(c.TlStart, 4)); times.Add(Math.Round(c.TlEnd, 4)); }
            var ts = times.ToList();
            for (int i = 0; i < ts.Count - 1; i++)
            {
                double a = ts[i], b = ts[i + 1];
                if (b - a < 0.02) continue;
                var c = ActiveClipAt((a + b) / 2);
                var seg = c == null ? new Seg(null, 0, b - a) : new Seg(c, c.Start + (a - c.TlStart), b - a);
                // merge with the previous piece if it continues the same clip seamlessly
                if (segs.Count > 0 && segs[^1].Clip?.Id == seg.Clip?.Id &&
                    (seg.Clip == null || Math.Abs(segs[^1].Src + segs[^1].Dur - seg.Src) < 0.001))
                    segs[^1] = segs[^1] with { Dur = segs[^1].Dur + seg.Dur };
                else segs.Add(seg);
            }
            return segs;
        }

        // Transition between two flattened pieces (only real, touching joins on the same lane)
        private Tr? TransitionBetween(Seg a, Seg b)
        {
            if (a.Clip == null || b.Clip == null) return null;
            return _transitions.TryGetValue((a.Clip.Id, b.Clip.Id), out var tr) && tr.Type != TrType.None
                && a.Clip.Track == b.Clip.Track ? tr : null;
        }

        private double ExportDuration()
        {
            var segs = Flatten();
            if (segs.Count == 0) return 0;
            int first = segs[0].Clip == null && segs.Count > 1 ? 1 : 0;    // the export starts at the first clip
            double total = 0;
            for (int i = first; i < segs.Count; i++)
            {
                total += segs[i].Dur;
                if (i > first) { var tr = TransitionBetween(segs[i - 1], segs[i]); if (tr != null) total -= Math.Min(tr.Dur, Math.Min(segs[i - 1].Dur, segs[i].Dur) / 2); }
            }
            return total;
        }

        private static string Inv(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private async Task<(bool ok, string error)> RenderTimelineAsync(string outPath, RenderProgressDialog? dlg)
        {
            var segs = Flatten();
            if (segs.Count > 1 && segs[0].Clip == null) segs.RemoveAt(0);   // no leading black
            if (segs.Count == 0) return (false, "ტაიმლაინი ცარიელია");

            // target format = the first real clip
            var firstClip = segs.First(s => s.Clip != null).Clip!;
            var fi = await ProbeMediaInfoAsync(firstClip.SourcePath);
            int W = fi.W % 2 == 0 ? fi.W : fi.W + 1, H = fi.H % 2 == 0 ? fi.H : fi.H + 1;
            double fps = Math.Clamp(fi.Fps, 10, 60);
            string fpsS = Inv(fps);

            var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error" };
            var vf = new StringBuilder(); var af = new StringBuilder();
            int idx = 0;

            for (int i = 0; i < segs.Count; i++)
            {
                var sg = segs[i]; string d = Inv(sg.Dur);
                if (sg.Clip == null)
                {
                    args.AddRange(new[] { "-f", "lavfi", "-t", d, "-i", $"color=c=black:s={W}x{H}:r={fpsS}" });
                    int vi = idx++;
                    args.AddRange(new[] { "-f", "lavfi", "-t", d, "-i", "anullsrc=r=48000:cl=stereo" });
                    int ai = idx++;
                    vf.Append($"[{vi}:v]format=yuv420p,setsar=1,trim=duration={d},setpts=PTS-STARTPTS[v{i}];");
                    af.Append($"[{ai}:a]aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo,atrim=duration={d},asetpts=PTS-STARTPTS[a{i}];");
                }
                else
                {
                    var mi = await ProbeMediaInfoAsync(sg.Clip.SourcePath);
                    args.AddRange(new[] { "-ss", Inv(sg.Src), "-t", d, "-i", sg.Clip.SourcePath });
                    int vi = idx++;
                    vf.Append($"[{vi}:v]scale={W}:{H}:force_original_aspect_ratio=decrease,pad={W}:{H}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps={fpsS},format=yuv420p,trim=duration={d},setpts=PTS-STARTPTS[v{i}];");
                    if (mi.HasAudio)
                        af.Append($"[{vi}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,apad,atrim=duration={d},asetpts=PTS-STARTPTS[a{i}];");
                    else
                    {
                        args.AddRange(new[] { "-f", "lavfi", "-t", d, "-i", "anullsrc=r=48000:cl=stereo" });
                        int ai = idx++;
                        af.Append($"[{ai}:a]aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo,atrim=duration={d},asetpts=PTS-STARTPTS[a{i}];");
                    }
                }
            }

            // join the pieces (xfade where a transition exists, plain concat otherwise)
            string curV = "[v0]", curA = "[a0]"; double acc = segs[0].Dur;
            if (segs.Count == 1) { vf.Append("[v0]null[outv];"); af.Append("[a0]anull[outa];"); }
            for (int i = 1; i < segs.Count; i++)
            {
                bool last = i == segs.Count - 1;
                string outV = last ? "[outv]" : $"[xv{i}]", outA = last ? "[outa]" : $"[xa{i}]";
                var tr = TransitionBetween(segs[i - 1], segs[i]);
                if (tr != null)
                {
                    double td = Math.Min(tr.Dur, Math.Min(segs[i - 1].Dur, segs[i].Dur) / 2);
                    string xName = tr.Type switch
                    {
                        TrType.CrossDissolve => "dissolve", TrType.DipToBlack => "fadeblack",
                        TrType.WhiteFlash => "fadewhite",   _ => "fade"
                    };
                    vf.Append($"{curV}[v{i}]xfade=transition={xName}:duration={Inv(td)}:offset={Inv(acc - td)}{outV};");
                    af.Append($"{curA}[a{i}]acrossfade=d={Inv(td)}{outA};");
                    acc += segs[i].Dur - td;
                }
                else
                {
                    vf.Append($"{curV}[v{i}]concat=n=2:v=1:a=0{outV};");
                    af.Append($"{curA}[a{i}]concat=n=2:v=0:a=1{outA};");
                    acc += segs[i].Dur;
                }
                curV = outV; curA = outA;
            }
            double totalDur = acc;

            string graph = (vf.ToString() + af.ToString()).TrimEnd(';');
            string? scriptFile = null;
            if (graph.Length > 20000)       // keep the command line short: put the graph in a file
            {
                Directory.CreateDirectory(CacheDir);
                scriptFile = Path.Combine(CacheDir, $"graph_{DateTime.Now.Ticks}.txt");
                File.WriteAllText(scriptFile, graph, new UTF8Encoding(false));
                args.AddRange(new[] { "-/filter_complex", scriptFile });
            }
            else args.AddRange(new[] { "-filter_complex", graph });

            args.AddRange(new[]
            {
                "-map", "[outv]", "-map", "[outa]",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart",
                "-progress", "pipe:1", "-nostats", outPath
            });

            var si = new ProcessStartInfo { FileName = FF(), UseShellExecute = false, CreateNoWindow = true,
                                            RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) si.ArgumentList.Add(a);

            var err = new StringBuilder();
            try
            {
                return await Task.Run(() =>
                {
                    using var p = new Process { StartInfo = si };
                    p.ErrorDataReceived += (_, ev) => { if (ev.Data != null) lock (err) { err.AppendLine(ev.Data); if (err.Length > 4000) err.Remove(0, err.Length - 4000); } };
                    p.OutputDataReceived += (_, ev) =>
                    {
                        if (ev.Data == null || dlg == null || totalDur <= 0) return;
                        if (ev.Data.StartsWith("out_time_us=") &&
                            double.TryParse(ev.Data.AsSpan(12), NumberStyles.Float, CultureInfo.InvariantCulture, out var us))
                            dlg.SetProgress((int)Math.Clamp(us / 1e6 / totalDur * 100, 0, 99), "ვიდეო მუშავდება…");
                    };
                    p.Start(); p.BeginErrorReadLine(); p.BeginOutputReadLine();
                    p.WaitForExit();
                    bool ok = p.ExitCode == 0 && File.Exists(outPath);
                    return (ok, ok ? "" : err.ToString().Trim());
                });
            }
            catch (Exception ex) { return (false, ex.Message); }
            finally { if (scriptFile != null) try { File.Delete(scriptFile); } catch { } }
        }

        private async void BtnEditorCutOnly_Click(object s, RoutedEventArgs e)
        {
            if (_clips.Count == 0) return;
            if (_playing) PausePlayback();
            var first = _clips.OrderBy(c => c.TlStart).First();
            var sfd = new SaveFileDialog
            {
                Title = "ტაიმლაინის შენახვა", Filter = "MP4 video|*.mp4",
                InitialDirectory = Path.GetDirectoryName(first.SourcePath),
                FileName = $"{Path.GetFileNameWithoutExtension(first.SourcePath)}_edit.mp4"
            };
            if (sfd.ShowDialog() != true) return;

            BtnEditorCutOnly.IsEnabled = BtnEditorCutAndCrop.IsEnabled = false;
            var dlg = new RenderProgressDialog { Owner = Window.GetWindow(this) };
            dlg.SetProgress(0, "ვიდეო მზადდება…"); dlg.Show();
            var (ok, error) = await RenderTimelineAsync(sfd.FileName, dlg);
            if (ok) { dlg.SetProgress(100, "მზადაა"); await Task.Delay(500); }
            dlg.Close();
            UpdateBtns();
            if (ok) Notify?.Invoke($"ფაილი შენახულია: {Path.GetFileName(sfd.FileName)}", "success");
            else MessageBox.Show("ექსპორტი ვერ შესრულდა.\n\n" + error, "შეცდომა", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private async void BtnEditorCutAndCrop_Click(object s, RoutedEventArgs e)
        {
            if (_clips.Count == 0) return;
            if (_playing) PausePlayback();
            Directory.CreateDirectory(CacheDir);
            string tmp = Path.Combine(CacheDir, $"editor_{DateTime.Now:HHmmss}.mp4");
            BtnEditorCutOnly.IsEnabled = BtnEditorCutAndCrop.IsEnabled = false;
            var dlg = new RenderProgressDialog { Owner = Window.GetWindow(this) };
            dlg.SetProgress(0, "ვიდეო მზადდება…"); dlg.Show();
            var (ok, error) = await RenderTimelineAsync(tmp, dlg);
            if (!ok)
            {
                dlg.Close(); UpdateBtns();
                MessageBox.Show("ვერ შესრულდა.\n\n" + error, "შეცდომა", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            dlg.SetProgress(95, "გადახედვა…");
            double dur = ExportDuration();
            string thumb = await ExtractFrame(tmp, TimeSpan.FromSeconds(dur));
            dlg.SetProgress(100, "მზადაა"); await Task.Delay(500); dlg.Close();
            UpdateBtns();
            Notify?.Invoke("კლიპი მზადაა ვერტიკალურებისთვის", "success");
            ClipReadyForCrop?.Invoke(tmp, thumb, TimeSpan.FromSeconds(dur));
        }

        private async Task<string> ExtractFrame(string vp, TimeSpan dur)
        {
            Directory.CreateDirectory(CacheDir);
            string th = Path.Combine(CacheDir, $"prev_{DateTime.Now:HHmmss}.jpg");
            var si = new ProcessStartInfo { FileName = FF(), UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-y", "-ss", Ts(TimeSpan.FromSeconds(Math.Max(0.1, dur.TotalSeconds / 2))),
                                      "-i", vp, "-vframes", "1", "-s", "480x270", "-q:v", "2", th })
                si.ArgumentList.Add(a);
            await Task.Run(() => { try { using var p = Process.Start(si); p?.WaitForExit(); } catch { } });
            return File.Exists(th) ? th : "";
        }

        private static string FF() => AppPaths.Ffmpeg();
        private static string Ts(TimeSpan t) => t.ToString(@"hh\:mm\:ss\.fff");
        private static string FormatTime(double sec) { var t = TimeSpan.FromSeconds(Math.Max(0, sec)); return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"; }
    }
}

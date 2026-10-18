using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Shapes = System.Windows.Shapes;

namespace AiAssistant
{
    // Who / what stays: people found by the AI, things marked by hand (tracked through the video), the timeline lanes.
    public partial class AiBackgroundView
    {
        // ───────────────────────── model ─────────────────────────
        private static readonly Color[] Palette =
        {
            Color.FromRgb(0x4C, 0x9A, 0xFF), Color.FromRgb(0xFF, 0xB0, 0x20), Color.FromRgb(0x2F, 0xD2, 0x7F),
            Color.FromRgb(0xB3, 0x6B, 0xFF), Color.FromRgb(0xFF, 0x6B, 0x9A), Color.FromRgb(0x22, 0xD3, 0xEE),
            Color.FromRgb(0xFF, 0x8A, 0x3D), Color.FromRgb(0xA3, 0xE6, 0x35), Color.FromRgb(0xF4, 0x3F, 0x5E),
            Color.FromRgb(0x5E, 0xEA, 0xD4), Color.FromRgb(0xFA, 0xCC, 0x15), Color.FromRgb(0x94, 0xA3, 0xB8)
        };
        private static readonly Color CrowdColor = Color.FromRgb(0x8A, 0x91, 0x9C);

        private sealed class BgPerson
        {
            public int Id, No, First, Last;
            public bool Minor;
            public string Thumb = "";
            public Color Col;
            public Dictionary<int, double[]> Boxes = new();
            public int[] Sorted = Array.Empty<int>();
            public List<(int a, int b)> Segs = new();
            public CheckBox? Toggle;
        }

        private sealed class BgMark
        {
            public int Id;
            public string Type = "person";       // person | object | shape
            public string Name = "";
            public Color Col;
            public bool Enabled = true;
            public bool Tracking;
            public List<(int f, double[] box)> Keys = new();
            public Dictionary<int, double[]> Boxes = new();
            public Border? Card;
            public TextBlock? StateText;
        }

        private readonly List<BgPerson> _people = new();
        private CheckBox? _groupToggle;
        private List<(int a, int b)> _crowdSegs = new();
        private string _peopleFile = "";
        private int _pw = 1, _ph = 1;

        private readonly List<BgMark> _marks = new();
        private BgMark? _sel;
        private int _nextMark = 1;
        private readonly List<(double a, double b)> _off = new();

        private int CurFrame => (int)Math.Round(Math.Max(0, _t) * _fps);

        private string TracksFile
        {
            get
            {
                var fi = new FileInfo(_input);
                string key = _input.ToLowerInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
                return Path.Combine(WorkDir, "tracks_" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..12] + ".json");
            }
        }

        private string PeopleFileFor(string path)
        {
            var fi = new FileInfo(path);
            string key = path.ToLowerInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks + "|people3";
            return Path.Combine(WorkDir, "people_" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..12] + ".json");
        }

        private static Brush B(Color c, double op = 1) { var b = new SolidColorBrush(c) { Opacity = op }; b.Freeze(); return b; }

        private void ResetSubjects()
        {
            _people.Clear(); _groupToggle = null; _crowdSegs = new(); _peopleFile = "";
            _marks.Clear(); _sel = null; _nextMark = 1;
            PeoplePanel?.Children.Clear(); MarksPanel?.Children.Clear();
            if (BtnPeopleAll != null) BtnPeopleAll.Visibility = BtnPeopleNone.Visibility = Visibility.Collapsed;
            if (BtnFindPeople != null) BtnFindPeople.Visibility = Visibility.Visible;
            if (TxtPeopleHint != null) TxtPeopleHint.Text = "ჯერ მოძებნე ადამიანები — თითოეულს ფერი და ნომერი მიენიჭება.";
            ToolSelect.IsChecked = true;
            UpdateMarkTexts();
            RefreshVisuals();
        }

        private void RefreshVisuals()
        {
            RedrawLanes();
            DrawOverlay();
        }

        // ───────────────────────── subject mode ─────────────────────────
        private void Subject_Changed(object sender, RoutedEventArgs e)
        {
            SyncSubjectUi();
            SchedulePreview(150);
            DrawOverlay();
        }

        private void SyncSubjectUi()
        {
            if (PeopleBox == null) return;
            bool sel = ModeSel.IsChecked == true;
            PeopleBox.Visibility = sel ? Visibility.Visible : Visibility.Collapsed;
            TxtSubjectHint.Text = sel
                ? "რჩება მხოლოდ ის, რასაც ჩართავ ქვემოთ ან ხელით მონიშნავ. ყველაფერი დანარჩენი ახალ ფონზე გადადის."
                : "AI ყველა ადამიანს ტოვებს. თუ საჭიროა რომელიმე ობიექტიც (პროდუქტი, ეკრანი…), მონიშნე ის პრევიუზე — „მონიშვნა“.";
        }

        // ───────────────────────── people (face_blur.py analysis) ─────────────────────────
        private bool _findingPeople;
        private Process? _peopleProc;

        private async void BtnFindPeople_Click(object sender, RoutedEventArgs e)
        {
            if (_findingPeople) { try { _peopleProc?.Kill(true); } catch { } return; }
            if (string.IsNullOrEmpty(_input) || !File.Exists(_input)) { Toast("ჯერ აირჩიე ვიდეო", "error"); return; }
            string? script = Script("face_blur.py");
            if (script == null) { Toast("face_blur.py ვერ მოიძებნა", "error"); return; }
            string input = _input, outJson = PeopleFileFor(input);
            _findingPeople = true;
            BtnFindPeople.Content = "გაჩერება";
            PbPeople.Value = 0; PbPeople.Visibility = Visibility.Visible;
            TxtPeopleHint.Text = "სახეები იძებნება…";
            bool ok = false; string last = "";
            await Task.Run(() =>
            {
                try
                {
                    var si = MakePy(script, "analyze", input, outJson, "balanced");
                    using var p = new Process { StartInfo = si };
                    p.OutputDataReceived += (_, ev) =>
                    {
                        if (ev.Data == null) return;
                        if (ev.Data.StartsWith("[PROGRESS]") && int.TryParse(ev.Data[10..].Trim(), out int pct)) Dispatcher.InvokeAsync(() => PbPeople.Value = pct);
                        else if (ev.Data.StartsWith("[STATUS]")) last = ev.Data[8..].Trim();
                        else if (ev.Data.StartsWith("[SUCCESS]")) ok = true;
                    };
                    p.ErrorDataReceived += (_, _) => { };
                    _peopleProc = p;
                    p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine(); p.WaitForExit();
                    _peopleProc = null;
                }
                catch (Exception ex) { last = ex.Message; }
            });
            _findingPeople = false;
            BtnFindPeople.Content = "ადამიანების ძებნა";
            PbPeople.Visibility = Visibility.Collapsed;
            if (input != _input) return;
            if (!ok || !File.Exists(outJson) || !LoadPeople(outJson))
            { TxtPeopleHint.Text = string.IsNullOrEmpty(last) ? "ძებნა შეწყდა." : "ძებნა ვერ მოხერხდა: " + last; return; }
            SchedulePreview(150);
        }

        private bool LoadPeople(string path)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                var list = new List<BgPerson>();
                int nFrames = root.TryGetProperty("frames", out var jf) ? jf.GetInt32() : 1;
                var crowd = new List<int>();
                foreach (var t in root.GetProperty("tracks").EnumerateArray())
                {
                    var pr = new BgPerson
                    {
                        Id = t.GetProperty("id").GetInt32(), First = t.GetProperty("first").GetInt32(), Last = t.GetProperty("last").GetInt32(),
                        Minor = t.TryGetProperty("minor", out var mi) && mi.GetBoolean(),
                        Thumb = t.TryGetProperty("thumb", out var th) ? th.GetString() ?? "" : ""
                    };
                    var frames = new List<int>();
                    foreach (var bp in t.GetProperty("boxes").EnumerateObject())
                    {
                        if (!int.TryParse(bp.Name, out int fr)) continue;
                        frames.Add(fr);
                        if (!pr.Minor) { var a = bp.Value; pr.Boxes[fr] = new[] { a[0].GetDouble(), a[1].GetDouble(), a[2].GetDouble(), a[3].GetDouble() }; }
                    }
                    if (pr.Minor) crowd.AddRange(frames);
                    else { pr.Sorted = pr.Boxes.Keys.OrderBy(k => k).ToArray(); pr.Segs = ToSegments(frames); }
                    list.Add(pr);
                }
                int no = 0;
                foreach (var pr in list.Where(x => !x.Minor).OrderBy(x => x.Id)) { pr.No = ++no; pr.Col = Palette[(pr.No - 1) % Palette.Length]; }
                _pw = root.TryGetProperty("width", out var jw) ? Math.Max(1, jw.GetInt32()) : _vw;
                _ph = root.TryGetProperty("height", out var jh) ? Math.Max(1, jh.GetInt32()) : _vh;
                _crowdSegs = ToSegments(crowd);
                _peopleFile = path;
                _people.Clear(); _people.AddRange(list);
                BuildPeopleUi();
                return true;
            }
            catch { return false; }
        }

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

        private Border NewCard()
        {
            var card = new Border { Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
            card.SetResourceReference(Border.BackgroundProperty, "BgInput");
            card.SetResourceReference(Border.BorderBrushProperty, "Border");
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            card.Child = g;
            return card;
        }

        private static Border Badge(string text, Color col, double w, double fs, HorizontalAlignment ha = HorizontalAlignment.Center, VerticalAlignment va = VerticalAlignment.Center)
        {
            double lum = (0.299 * col.R + 0.587 * col.G + 0.114 * col.B) / 255.0;
            return new Border
            {
                Width = w, Height = w * 0.9, CornerRadius = new CornerRadius(w), HorizontalAlignment = ha, VerticalAlignment = va, Background = B(col),
                BorderBrush = B(Color.FromRgb(0x14, 0x17, 0x1C)), BorderThickness = new Thickness(1.5),
                Child = new TextBlock { Text = text, FontSize = fs, FontWeight = FontWeights.Bold, Foreground = lum > 0.55 ? Brushes.Black : Brushes.White,
                                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
        }

        private void BuildPeopleUi()
        {
            PeoplePanel.Children.Clear();
            _groupToggle = null;
            var main = _people.Where(x => !x.Minor).OrderBy(x => x.No).ToList();
            var minor = _people.Where(x => x.Minor).ToList();
            bool any = main.Count + minor.Count > 0;
            BtnPeopleAll.Visibility = BtnPeopleNone.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            BtnFindPeople.Visibility = Visibility.Collapsed;
            TxtPeopleHint.Text = !any ? "სახე ვერ ვიპოვე ამ ვიდეოში. ადამიანს ხელით მონიშვნითაც შეგიძლია აირჩიო."
                : $"ნაპოვნია {main.Count} ძირითადი ადამიანი" + (minor.Count > 0 ? $" + {minor.Count} სხვა სახე" : "") + ". ჩართული რჩება, გამორთული ფონში გადადის.";

            foreach (var p in main)
            {
                var card = NewCard(); var grid = (Grid)card.Child;
                var ring = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(23), ClipToBounds = true, BorderBrush = B(p.Col), BorderThickness = new Thickness(3),
                                        Cursor = Cursors.Hand, ToolTip = "დააჭირე — გადახვალ ამ ადამიანის კადრზე" };
                ring.SetResourceReference(Border.BackgroundProperty, "VideoBg");
                if (File.Exists(p.Thumb)) { var bmp = LoadImage(p.Thumb); if (bmp != null) ring.Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill }; }
                int seek = p.Sorted.Length > 0 ? p.Sorted[p.Sorted.Length / 2] : p.First;
                ring.MouseLeftButtonDown += (_, _) => SlTime.Value = Math.Clamp(seek / _fps, 0, _dur);
                var portrait = new Grid { Width = 50, Height = 50, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
                portrait.Children.Add(ring);
                portrait.Children.Add(Badge(p.No.ToString(), p.Col, 18, 10, HorizontalAlignment.Right, VerticalAlignment.Bottom));
                grid.Children.Add(portrait);
                var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var name = new TextBlock { Text = $"ადამიანი {p.No}", FontSize = 13, FontWeight = FontWeights.SemiBold };
                name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
                mid.Children.Add(name);
                var range = new TextBlock { Text = $"{FmtSec(p.First / _fps)} – {FmtSec(p.Last / _fps)}", FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0) };
                range.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
                mid.Children.Add(range);
                Grid.SetColumn(mid, 1); grid.Children.Add(mid);
                var tg = new CheckBox { IsChecked = true, Style = (Style)FindResource("ToggleSwitch"), VerticalAlignment = VerticalAlignment.Center, ToolTip = "ჩართული = ეს ადამიანი რჩება" };
                tg.Click += (_, _) => { SchedulePreview(150); RefreshVisuals(); };
                p.Toggle = tg; Grid.SetColumn(tg, 2); grid.Children.Add(tg);
                PeoplePanel.Children.Add(card);
            }

            if (minor.Count > 0)
            {
                var card = NewCard(); var grid = (Grid)card.Child;
                var tile = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(23), Margin = new Thickness(0, 0, 12, 0), BorderBrush = B(CrowdColor), BorderThickness = new Thickness(3),
                                        Background = B(CrowdColor, 0.2),
                                        Child = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 18, Foreground = B(CrowdColor),
                                                                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                grid.Children.Add(tile);
                var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var name = new TextBlock { Text = $"სხვა სახეები · {minor.Count}", FontSize = 13, FontWeight = FontWeights.SemiBold };
                name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
                mid.Children.Add(name);
                var sub = new TextBlock { Text = "ბრბო და ფონის ადამიანები", FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0) };
                sub.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
                mid.Children.Add(sub);
                Grid.SetColumn(mid, 1); grid.Children.Add(mid);
                _groupToggle = new CheckBox { IsChecked = true, Style = (Style)FindResource("ToggleSwitch"), VerticalAlignment = VerticalAlignment.Center, ToolTip = "ჩართული = ესენიც რჩება" };
                _groupToggle.Click += (_, _) => { SchedulePreview(150); RefreshVisuals(); };
                Grid.SetColumn(_groupToggle, 2); grid.Children.Add(_groupToggle);
                PeoplePanel.Children.Insert(0, card);
            }
            RefreshVisuals();
        }

        private bool Kept(BgPerson p) => p.Minor ? _groupToggle?.IsChecked == true : p.Toggle?.IsChecked == true;

        private void BtnPeopleAll_Click(object sender, RoutedEventArgs e) => SetAllPeople(true);
        private void BtnPeopleNone_Click(object sender, RoutedEventArgs e) => SetAllPeople(false);
        private void SetAllPeople(bool on)
        {
            foreach (var p in _people) if (p.Toggle != null) p.Toggle.IsChecked = on;
            if (_groupToggle != null) _groupToggle.IsChecked = on;
            SchedulePreview(150); RefreshVisuals();
        }

        private object? PeopleOption()
        {
            if (string.IsNullOrEmpty(_peopleFile) || _people.Count == 0) return null;
            return new Dictionary<string, object>
            {
                ["analysis"] = _peopleFile.Replace('\\', '/'),
                ["keep"] = _people.Where(Kept).Select(p => p.Id).ToArray()
            };
        }

        // ───────────────────────── manual marks ─────────────────────────
        private string MarkTypeKey => (CmbMarkType.SelectedItem as ComboBoxItem)?.Tag as string ?? "person";
        private static string TypeLabel(string t) => t switch { "object" => "ობიექტი", "shape" => "ფორმა", _ => "ადამიანი" };

        private void MarkType_Changed(object sender, SelectionChangedEventArgs e) => UpdateHint();

        private void Tool_Changed(object sender, RoutedEventArgs e)
        {
            Overlay.Cursor = ToolMark.IsChecked == true ? Cursors.Cross : Cursors.Arrow;
            UpdateHint();
        }

        private void UpdateHint()
        {
            if (HintChip == null) return;
            if (ToolMark.IsChecked == true)
            {
                TxtHint.Text = $"გადაათრიე მართკუთხედი — {TypeLabel(MarkTypeKey)}ზე · პროგრამა თვითონ მიჰყვება ვიდეოში";
                HintChip.Visibility = Visibility.Visible;
            }
            else if (_sel != null) { TxtHint.Text = "გადაათრიე ჩარჩო ან ზომა შეუცვალე — ამ კადრიდან ახლიდან მიჰყვება · Delete — წაშლა"; HintChip.Visibility = Visibility.Visible; }
            else HintChip.Visibility = Visibility.Collapsed;
        }

        private object[] MarksOption()
        {
            string tracks = string.IsNullOrEmpty(_input) ? "" : TracksFile.Replace('\\', '/');
            return _marks.Where(m => m.Enabled && m.Keys.Count > 0).Select(m => (object)new Dictionary<string, object>
            {
                ["id"] = m.Id, ["type"] = m.Type, ["name"] = m.Name,
                ["keys"] = m.Keys.Select(k => new Dictionary<string, object> { ["f"] = k.f, ["box"] = k.box }).ToArray(),
                ["track"] = tracks
            }).ToArray();
        }

        private BgMark CreateMark(double[] box)
        {
            string type = MarkTypeKey;
            int n = _marks.Count(m => m.Type == type) + 1;
            var m = new BgMark { Id = _nextMark++, Type = type, Name = $"{TypeLabel(type)} {n}", Col = Palette[(_marks.Count + 6) % Palette.Length] };
            m.Keys.Add((CurFrame, box)); m.Boxes[CurFrame] = box;
            _marks.Add(m);
            BuildMarkCard(m);
            _sel = m;
            if (type == "person" && ModeSel.IsChecked != true)
            {
                ModeSel.IsChecked = true; SyncSubjectUi();
                Toast("ჩაირთო „მხოლოდ არჩეული“ — რჩება მხოლოდ მონიშნული ადამიანი");
            }
            UpdateMarkTexts();
            ScheduleTrack();
            RefreshVisuals();
            return m;
        }

        private void UpdateMarkTexts()
        {
            if (TxtMarkCount == null) return;
            TxtMarkCount.Text = _marks.Count > 0 ? $"{_marks.Count}" : "";
            TxtMarksHint.Visibility = _marks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BuildMarkCard(BgMark m)
        {
            var card = NewCard(); var grid = (Grid)card.Child;
            card.Cursor = Cursors.Hand;
            var dot = Badge((_marks.IndexOf(m) + 1).ToString(), m.Col, 24, 11);
            dot.Margin = new Thickness(0, 0, 12, 0);
            grid.Children.Add(dot);
            var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { Text = m.Name, FontSize = 13, FontWeight = FontWeights.SemiBold };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            mid.Children.Add(name);
            m.StateText = new TextBlock { Text = "მიჰყვება…", FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0) };
            m.StateText.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            mid.Children.Add(m.StateText);
            Grid.SetColumn(mid, 1); grid.Children.Add(mid);
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var tg = new CheckBox { IsChecked = true, Style = (Style)FindResource("ToggleSwitch"), VerticalAlignment = VerticalAlignment.Center, ToolTip = "ჩართული = რჩება" };
            tg.Click += (_, _) => { m.Enabled = tg.IsChecked == true; SchedulePreview(150); RefreshVisuals(); };
            var del = new Button { Content = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12, Padding = new Thickness(8, 7, 8, 7), Margin = new Thickness(8, 0, 0, 0), ToolTip = "წაშლა" };
            del.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");
            del.Click += (_, _) => DeleteMark(m);
            right.Children.Add(tg); right.Children.Add(del);
            Grid.SetColumn(right, 2); grid.Children.Add(right);
            card.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is Button || e.OriginalSource is CheckBox) return; SelectMark(m); if (BoxAt(m) != null) SlTime.Value = Math.Clamp(m.Keys[0].f / _fps, 0, _dur); };
            m.Card = card;
            MarksPanel.Children.Add(card);
            HighlightCards();
        }

        private void SelectMark(BgMark? m) { _sel = m; HighlightCards(); UpdateHint(); DrawOverlay(); }

        private void HighlightCards()
        {
            foreach (var m in _marks)
                if (m.Card != null)
                {
                    if (m == _sel) { m.Card.BorderBrush = B(m.Col); m.Card.BorderThickness = new Thickness(1.5); }
                    else { m.Card.SetResourceReference(Border.BorderBrushProperty, "Border"); m.Card.BorderThickness = new Thickness(1); }
                }
        }

        private void DeleteMark(BgMark m)
        {
            _marks.Remove(m);
            if (m.Card != null) MarksPanel.Children.Remove(m.Card);
            if (_sel == m) _sel = null;
            UpdateMarkTexts(); UpdateHint();
            if (_marks.Count > 0) ScheduleTrack();
            SchedulePreview(150);
            RefreshVisuals();
        }

        private void OnKeys(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && _sel != null && !(Keyboard.FocusedElement is TextBox)) { DeleteMark(_sel); e.Handled = true; }
            else if (e.Key == Key.Escape && ToolMark.IsChecked == true) { ToolSelect.IsChecked = true; Tool_Changed(this, e); e.Handled = true; }
        }

        // ── tracking ──
        private readonly DispatcherTimer _trackTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
        private bool _trackWired, _trackRunning, _trackDirty;

        private void ScheduleTrack()
        {
            if (!_trackWired) { _trackWired = true; _trackTimer.Tick += async (_, _) => { _trackTimer.Stop(); await RunTrackAsync(); }; }
            foreach (var m in _marks) { m.Tracking = true; if (m.StateText != null) m.StateText.Text = "მიჰყვება…"; }
            _trackTimer.Stop(); _trackTimer.Start();
        }

        private async Task RunTrackAsync()
        {
            if (_marks.Count == 0 || string.IsNullOrEmpty(_input)) return;
            if (_trackRunning) { _trackDirty = true; return; }
            string? script = Script("bg_studio.py");
            if (script == null) return;
            _trackRunning = true; _trackDirty = false;
            string proj = WriteProject(BuildProject(_input, "", true, false), "project_track.json");
            string outJson = TracksFile;
            string input = _input;
            SetBusy(true, "ობიექტი მიჰყვება…");
            bool ok = false;
            await Task.Run(() =>
            {
                try
                {
                    using var p = Process.Start(MakePy(script, "track", proj, outJson))!;
                    p.StandardError.ReadToEndAsync();
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(300000);
                    ok = o.Contains("[SUCCESS]") && File.Exists(outJson);
                }
                catch { }
            });
            SetBusy(false);
            _trackRunning = false;
            if (input == _input && ok) LoadTracks(outJson);
            else foreach (var m in _marks) { m.Tracking = false; if (m.StateText != null) m.StateText.Text = "მიყოლა ვერ მოხერხდა"; }
            if (_trackDirty) ScheduleTrack(); else { SchedulePreview(100); RefreshVisuals(); }
        }

        private void LoadTracks(string path)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var marks = doc.RootElement.GetProperty("marks");
                foreach (var m in _marks)
                {
                    m.Tracking = false;
                    if (!marks.TryGetProperty(m.Id.ToString(), out var t)) continue;
                    var dict = new Dictionary<int, double[]>();
                    foreach (var bp in t.GetProperty("boxes").EnumerateObject())
                        if (int.TryParse(bp.Name, out int f)) { var a = bp.Value; dict[f] = new[] { a[0].GetDouble(), a[1].GetDouble(), a[2].GetDouble(), a[3].GetDouble() }; }
                    if (dict.Count > 0) m.Boxes = dict;
                    int lost = t.TryGetProperty("lost", out var l) ? l.GetArrayLength() : 0;
                    if (m.StateText != null)
                    {
                        var fs = m.Boxes.Keys.OrderBy(k => k).ToList();
                        m.StateText.Text = fs.Count == 0 ? "—" : $"{FmtSec(fs.First() / _fps)} – {FmtSec(fs.Last() / _fps)}" + (lost > 0 ? "  ·  ⚠ ზოგან დაიკარგა" : "");
                    }
                }
            }
            catch { }
        }

        private double[]? BoxAt(BgMark m)
        {
            int f = CurFrame;
            if (m.Boxes.TryGetValue(f, out var b)) return b;
            for (int d = 1; d <= 2; d++)
            {
                if (m.Boxes.TryGetValue(f - d, out b)) return b;
                if (m.Boxes.TryGetValue(f + d, out b)) return b;
            }
            return null;
        }

        // ───────────────────────── overlay (frames on the stage) ─────────────────────────
        private (double sc, double ox, double oy) Fit()
        {
            double aw = Overlay.ActualWidth, ah = Overlay.ActualHeight;
            double sc = Math.Min(aw / Math.Max(1, _vw), ah / Math.Max(1, _vh));
            return (sc, (aw - _vw * sc) / 2, (ah - _vh * sc) / 2);
        }

        private Point ToVideo(Point p) { var (sc, ox, oy) = Fit(); return new Point((p.X - ox) / sc, (p.Y - oy) / sc); }

        private void Stage_SizeChanged(object sender, SizeChangedEventArgs e) => DrawOverlay();

        private const int HandleCount = 8;
        private static Point HandlePos(double[] b, int i) => i switch
        {
            0 => new Point(b[0], b[1]), 1 => new Point(b[0] + b[2] / 2, b[1]), 2 => new Point(b[0] + b[2], b[1]),
            3 => new Point(b[0] + b[2], b[1] + b[3] / 2), 4 => new Point(b[0] + b[2], b[1] + b[3]), 5 => new Point(b[0] + b[2] / 2, b[1] + b[3]),
            6 => new Point(b[0], b[1] + b[3]), _ => new Point(b[0], b[1] + b[3] / 2)
        };

        private void DrawOverlay()
        {
            if (Overlay == null) return;
            Overlay.Children.Clear();
            if (string.IsNullOrEmpty(_input)) return;
            var (sc, ox, oy) = Fit();
            if (sc <= 0) return;
            int frame = CurFrame;
            bool selMode = ModeSel.IsChecked == true;
            bool original = CurrentView == "original";

            // people the AI found
            double pk = (double)_vw / Math.Max(1, _pw);                       // analysis coordinates → video coordinates
            foreach (var p in _people.Where(x => !x.Minor))
            {
                var b = NearestBox(p, frame);
                if (b == null) continue;
                bool kept = !selMode || Kept(p);
                double x = ox + b[0] * pk * sc, y = oy + b[1] * pk * sc, w = b[2] * pk * sc, h = b[3] * pk * sc;
                var r = new Shapes.Rectangle { Width = Math.Max(4, w), Height = Math.Max(4, h), RadiusX = 8, RadiusY = 8, StrokeThickness = 2.2,
                                               Stroke = B(p.Col, kept ? 1 : 0.5), StrokeDashArray = kept ? null : new DoubleCollection { 3, 2 } };
                Canvas.SetLeft(r, x); Canvas.SetTop(r, y); Overlay.Children.Add(r);
                var badge = Badge(p.No.ToString(), p.Col, 20, 11, HorizontalAlignment.Left, VerticalAlignment.Top);
                badge.Opacity = kept ? 1 : 0.6;
                Canvas.SetLeft(badge, Math.Max(0, x - 4)); Canvas.SetTop(badge, Math.Max(0, y - 22)); Overlay.Children.Add(badge);
            }

            // marks drawn by hand
            foreach (var m in _marks.Where(x => x.Enabled || x == _sel))
            {
                var b = BoxAt(m);
                if (b == null) continue;
                double x = ox + b[0] * sc, y = oy + b[1] * sc, w = b[2] * sc, h = b[3] * sc;
                bool selected = m == _sel;
                var r = new Shapes.Rectangle { Width = Math.Max(4, w), Height = Math.Max(4, h), RadiusX = m.Type == "shape" ? 2 : 10, RadiusY = m.Type == "shape" ? 2 : 10,
                                               StrokeThickness = selected ? 3 : 2.4, Stroke = B(m.Col, m.Enabled ? 1 : 0.4),
                                               Fill = B(m.Col, selected ? 0.10 : 0.04), StrokeDashArray = m.Type == "object" ? new DoubleCollection { 5, 2 } : null };
                Canvas.SetLeft(r, x); Canvas.SetTop(r, y); Overlay.Children.Add(r);
                var chip = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 2, 8, 3), Background = B(m.Col),
                                        Child = new TextBlock { Text = m.Name, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.Black } };
                Canvas.SetLeft(chip, Math.Max(0, x)); Canvas.SetTop(chip, Math.Max(0, y - 24)); Overlay.Children.Add(chip);
                if (selected && ToolMark.IsChecked != true)
                    for (int i = 0; i < HandleCount; i++)
                    {
                        var hp = HandlePos(new[] { x, y, w, h }, i);
                        var hd = new Shapes.Rectangle { Width = 11, Height = 11, RadiusX = 3, RadiusY = 3, Fill = Brushes.White, Stroke = B(m.Col), StrokeThickness = 2 };
                        Canvas.SetLeft(hd, hp.X - 5.5); Canvas.SetTop(hd, hp.Y - 5.5); Overlay.Children.Add(hd);
                    }
            }

            // the rectangle being drawn
            if (_rubber != null)
            {
                var r = new Shapes.Rectangle { Width = _rubber.Value.Width * sc, Height = _rubber.Value.Height * sc, RadiusX = 6, RadiusY = 6, StrokeThickness = 2.5,
                                               Stroke = Brushes.White, StrokeDashArray = new DoubleCollection { 4, 3 }, Fill = B(Colors.White, 0.12) };
                Canvas.SetLeft(r, ox + _rubber.Value.X * sc); Canvas.SetTop(r, oy + _rubber.Value.Y * sc);
                Overlay.Children.Add(r);
            }
            if (original && _off.Any(r => _t >= r.a && _t <= r.b) == false) { /* nothing */ }
        }

        private double[]? NearestBox(BgPerson p, int frame)
        {
            if (p.Sorted.Length == 0) return null;
            int i = Array.BinarySearch(p.Sorted, frame);
            if (i >= 0) return p.Boxes[p.Sorted[i]];
            i = ~i;
            int lo = Math.Max(0, i - 1), hi = Math.Min(p.Sorted.Length - 1, i);
            int k = Math.Abs(p.Sorted[lo] - frame) <= Math.Abs(p.Sorted[hi] - frame) ? lo : hi;
            return Math.Abs(p.Sorted[k] - frame) <= 24 ? p.Boxes[p.Sorted[k]] : null;
        }

        // ── mouse on the stage ──
        private enum Drag { None, Rubber, Move, Resize }
        private Drag _drag = Drag.None;
        private Point _dragStart;
        private double[] _dragBox = Array.Empty<double>();
        private int _dragHandle;
        private Rect? _rubber;

        private int HandleAt(BgMark m, Point screen)
        {
            var b = BoxAt(m); if (b == null) return -1;
            var (sc, ox, oy) = Fit();
            var sb = new[] { ox + b[0] * sc, oy + b[1] * sc, b[2] * sc, b[3] * sc };
            for (int i = 0; i < HandleCount; i++)
            {
                var hp = HandlePos(sb, i);
                if (Math.Abs(hp.X - screen.X) <= 8 && Math.Abs(hp.Y - screen.Y) <= 8) return i;
            }
            return -1;
        }

        private BgMark? MarkAt(Point v)
        {
            foreach (var m in _marks.AsEnumerable().Reverse())
            {
                var b = BoxAt(m);
                if (b != null && v.X >= b[0] && v.X <= b[0] + b[2] && v.Y >= b[1] && v.Y <= b[1] + b[3]) return m;
            }
            return null;
        }

        private void Overlay_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (string.IsNullOrEmpty(_input)) return;
            Focus();
            var screen = e.GetPosition(Overlay);
            var v = ToVideo(screen);
            if (ToolMark.IsChecked == true)
            {
                _drag = Drag.Rubber; _dragStart = v; _rubber = new Rect(v, new Size(0, 0));
                Overlay.CaptureMouse();
                return;
            }
            if (_sel != null)
            {
                int h = HandleAt(_sel, screen);
                if (h >= 0) { _drag = Drag.Resize; _dragHandle = h; _dragBox = (double[])BoxAt(_sel)!.Clone(); _dragStart = v; Overlay.CaptureMouse(); return; }
            }
            var hit = MarkAt(v);
            if (hit != null)
            {
                SelectMark(hit);
                _drag = Drag.Move; _dragBox = (double[])BoxAt(hit)!.Clone(); _dragStart = v;
                Overlay.CaptureMouse();
                return;
            }
            // a person's frame: switch that person on / off
            double pk = (double)_vw / Math.Max(1, _pw);
            foreach (var p in _people.Where(x => !x.Minor))
            {
                var b = NearestBox(p, CurFrame);
                if (b == null) continue;
                if (v.X >= b[0] * pk && v.X <= (b[0] + b[2]) * pk && v.Y >= b[1] * pk && v.Y <= (b[1] + b[3]) * pk * 3.2)
                {
                    if (ModeSel.IsChecked != true) { ModeSel.IsChecked = true; SyncSubjectUi(); }
                    if (p.Toggle != null) { p.Toggle.IsChecked = p.Toggle.IsChecked != true; SchedulePreview(150); RefreshVisuals(); }
                    return;
                }
            }
            SelectMark(null);
        }

        private void Overlay_MouseMove(object sender, MouseEventArgs e)
        {
            var screen = e.GetPosition(Overlay);
            var v = ToVideo(screen);
            if (_drag == Drag.Rubber)
            {
                double x0 = Math.Min(_dragStart.X, v.X), y0 = Math.Min(_dragStart.Y, v.Y);
                _rubber = new Rect(Math.Clamp(x0, 0, _vw), Math.Clamp(y0, 0, _vh), Math.Min(Math.Abs(v.X - _dragStart.X), _vw - x0), Math.Min(Math.Abs(v.Y - _dragStart.Y), _vh - y0));
                DrawOverlay();
                return;
            }
            if (_drag == Drag.Move && _sel != null)
            {
                double dx = v.X - _dragStart.X, dy = v.Y - _dragStart.Y;
                _sel.Boxes[CurFrame] = new[] { _dragBox[0] + dx, _dragBox[1] + dy, _dragBox[2], _dragBox[3] };
                DrawOverlay();
                return;
            }
            if (_drag == Drag.Resize && _sel != null)
            {
                double x0 = _dragBox[0], y0 = _dragBox[1], x1 = x0 + _dragBox[2], y1 = y0 + _dragBox[3];
                switch (_dragHandle)
                {
                    case 0: x0 = v.X; y0 = v.Y; break;
                    case 1: y0 = v.Y; break;
                    case 2: x1 = v.X; y0 = v.Y; break;
                    case 3: x1 = v.X; break;
                    case 4: x1 = v.X; y1 = v.Y; break;
                    case 5: y1 = v.Y; break;
                    case 6: x0 = v.X; y1 = v.Y; break;
                    case 7: x0 = v.X; break;
                }
                double nx = Math.Min(x0, x1), ny = Math.Min(y0, y1);
                _sel.Boxes[CurFrame] = new[] { nx, ny, Math.Max(8, Math.Abs(x1 - x0)), Math.Max(8, Math.Abs(y1 - y0)) };
                DrawOverlay();
                return;
            }
            // hover cursor
            if (ToolMark.IsChecked == true) { Overlay.Cursor = Cursors.Cross; return; }
            int h = _sel != null ? HandleAt(_sel, screen) : -1;
            Overlay.Cursor = h switch { 0 or 4 => Cursors.SizeNWSE, 2 or 6 => Cursors.SizeNESW, 1 or 5 => Cursors.SizeNS, 3 or 7 => Cursors.SizeWE, _ => MarkAt(v) != null ? Cursors.SizeAll : Cursors.Arrow };
        }

        private void Overlay_MouseUp(object sender, MouseButtonEventArgs e)
        {
            Overlay.ReleaseMouseCapture();
            var d = _drag; _drag = Drag.None;
            if (d == Drag.Rubber)
            {
                var r = _rubber; _rubber = null;
                if (r != null && r.Value.Width > _vw * 0.015 && r.Value.Height > _vh * 0.015)
                {
                    CreateMark(new[] { r.Value.X, r.Value.Y, r.Value.Width, r.Value.Height });
                    ToolSelect.IsChecked = true; Tool_Changed(this, e);
                }
                DrawOverlay();
            }
            else if ((d == Drag.Move || d == Drag.Resize) && _sel != null)
            {
                var nb = BoxAt(_sel);
                if (nb != null)
                {
                    int f = CurFrame;
                    _sel.Keys.RemoveAll(k => k.f == f);
                    _sel.Keys.Add((f, (double[])nb.Clone()));
                    _sel.Keys.Sort((a, b) => a.f.CompareTo(b.f));
                    _sel.Boxes[f] = nb;
                    ScheduleTrack();
                }
            }
        }

        // ───────────────────────── time + lanes ─────────────────────────
        private int _frameVersion;

        private async void SlTime_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressTime || string.IsNullOrEmpty(_input)) return;
            _t = e.NewValue;
            TxtTime.Text = FmtSec(_t);
            UpdatePlayhead();
            DrawOverlay();
            int v = ++_frameVersion;
            await ShowFrameAsync();
            if (v != _frameVersion) return;
            SchedulePreview();
        }

        private sealed class LaneRow { public string Kind = ""; public object? Owner; public List<(double a, double b)> Segs = new(); public Color Col; public bool On = true; }
        private readonly List<LaneRow> _rows = new();
        private const double LaneH = 16, LaneGap = 5;

        private void Lanes_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawLanes();

        private void RedrawLanes()
        {
            if (Lanes == null) return;
            Lanes.Children.Clear(); LaneBadges.Children.Clear(); _rows.Clear();
            if (string.IsNullOrEmpty(_input)) { Lanes.Height = 0; return; }
            bool selMode = ModeSel.IsChecked == true;

            foreach (var p in _people.Where(x => !x.Minor).OrderBy(x => x.No))
                _rows.Add(new LaneRow { Kind = "person", Owner = p, Col = p.Col, On = !selMode || Kept(p), Segs = p.Segs.Select(s => (s.a / _fps, (s.b + 1) / _fps)).ToList() });
            foreach (var m in _marks)
                _rows.Add(new LaneRow { Kind = "mark", Owner = m, Col = m.Col, On = m.Enabled, Segs = ToSegments(m.Boxes.Keys.ToList()).Select(s => (s.a / _fps, (s.b + 1) / _fps)).ToList() });
            if (_crowdSegs.Count > 0)
                _rows.Add(new LaneRow { Kind = "crowd", Col = CrowdColor, On = !selMode || _groupToggle?.IsChecked == true, Segs = _crowdSegs.Select(s => (s.a / _fps, (s.b + 1) / _fps)).ToList() });
            _rows.Add(new LaneRow { Kind = "off", Col = Color.FromRgb(0x8A, 0x91, 0x9C), Segs = _off.ToList() });

            double w = Lanes.ActualWidth;
            Lanes.Height = _rows.Count * (LaneH + LaneGap) - LaneGap;
            if (w <= 0 || _dur <= 0) return;
            double fx = w / _dur, y = 0;
            foreach (var row in _rows)
            {
                var track = new Shapes.Rectangle { Width = w, Height = LaneH, RadiusX = 3, RadiusY = 3 };
                track.SetResourceReference(Shapes.Shape.FillProperty, "BgInput");
                Canvas.SetTop(track, y); Lanes.Children.Add(track);
                var brush = B(row.Col, row.Kind == "off" ? 0.8 : row.On ? 0.95 : 0.25);
                foreach (var (a, b) in row.Segs)
                {
                    var r = new Shapes.Rectangle { Width = Math.Max(2.5, (b - a) * fx), Height = LaneH, RadiusX = 3, RadiusY = 3, Fill = brush };
                    Canvas.SetLeft(r, a * fx); Canvas.SetTop(r, y); Lanes.Children.Add(r);
                }
                Border badge = row.Kind switch
                {
                    "person" => Badge(((BgPerson)row.Owner!).No.ToString(), row.Col, 24, 10.5),
                    "mark" => Badge((_marks.IndexOf((BgMark)row.Owner!) + 1).ToString(), row.Col, 24, 10.5),
                    "crowd" => Badge("∗", row.Col, 24, 12),
                    _ => Badge("◐", row.Col, 24, 11)
                };
                badge.Height = LaneH; badge.CornerRadius = new CornerRadius(LaneH / 2); badge.HorizontalAlignment = HorizontalAlignment.Right;
                badge.Margin = new Thickness(0, 0, 0, LaneGap);
                if (row.Kind == "off") badge.ToolTip = "ორიგინალი რჩება — გადათრიე ამ ზოლზე მონაკვეთის მოსანიშნად";
                LaneBadges.Children.Add(badge);
                y += LaneH + LaneGap;
            }
            var head = new Shapes.Rectangle { Width = 2, Height = Lanes.Height, Name = "Playhead", IsHitTestVisible = false };
            head.SetResourceReference(Shapes.Shape.FillProperty, "TextPrimary");
            Lanes.Children.Add(head);
            UpdatePlayhead();
        }

        private void UpdatePlayhead()
        {
            if (Lanes == null) return;
            foreach (var ch in Lanes.Children)
                if (ch is Shapes.Rectangle r && r.Name == "Playhead")
                {
                    double w = Lanes.ActualWidth;
                    Canvas.SetLeft(r, Math.Clamp(_dur > 0 ? Math.Max(0, _t) / _dur * w : 0, 0, Math.Max(0, w - 2)));
                    return;
                }
        }

        private bool _laneDrag, _offDrag;
        private double _laneStart, _laneNow;
        private Shapes.Rectangle? _ghost;

        private double LaneSec(MouseEventArgs e) => Math.Clamp(e.GetPosition(Lanes).X / Math.Max(1, Lanes.ActualWidth) * _dur, 0, _dur);
        private bool OnOffLane(MouseEventArgs e)
        {
            double y = e.GetPosition(Lanes).Y;
            return _rows.Count > 0 && y >= (_rows.Count - 1) * (LaneH + LaneGap) - 2;
        }

        private void Lanes_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_dur <= 0) return;
            _laneStart = _laneNow = LaneSec(e);
            _offDrag = OnOffLane(e);
            _laneDrag = true;
            Lanes.CaptureMouse();
            if (!_offDrag) SlTime.Value = _laneStart;
        }

        private void Lanes_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_laneDrag) return;
            _laneNow = LaneSec(e);
            double fx = Lanes.ActualWidth / _dur;
            if (!_offDrag) { SlTime.Value = _laneNow; return; }
            if (Math.Abs(_laneNow - _laneStart) * fx < 5) return;
            if (_ghost == null)
            {
                _ghost = new Shapes.Rectangle { Height = LaneH, RadiusX = 3, RadiusY = 3, IsHitTestVisible = false, Fill = B(CrowdColor, 0.45), Stroke = B(CrowdColor), StrokeThickness = 1.5,
                                                StrokeDashArray = new DoubleCollection { 2, 2 } };
                Canvas.SetTop(_ghost, (_rows.Count - 1) * (LaneH + LaneGap));
                Lanes.Children.Add(_ghost);
            }
            double a = Math.Min(_laneStart, _laneNow), b = Math.Max(_laneStart, _laneNow);
            _ghost.Width = Math.Max(1, (b - a) * fx); Canvas.SetLeft(_ghost, a * fx);
        }

        private void Lanes_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_laneDrag) return;
            _laneDrag = false; Lanes.ReleaseMouseCapture(); _ghost = null;
            if (!_offDrag) return;
            double fx = Lanes.ActualWidth / Math.Max(0.001, _dur);
            double a = Math.Min(_laneStart, _laneNow), b = Math.Max(_laneStart, _laneNow);
            if ((b - a) * fx < 5) { SlTime.Value = _laneStart; RedrawLanes(); return; }
            var keep = new List<(double, double)>();
            foreach (var (x, y) in _off) { if (y < a || x > b) keep.Add((x, y)); else { a = Math.Min(a, x); b = Math.Max(b, y); } }
            keep.Add((a, b));
            _off.Clear(); _off.AddRange(keep.OrderBy(r => r.Item1));
            Toast($"ორიგინალი რჩება: {FmtSec(a)} – {FmtSec(b)}");
            RedrawLanes(); SchedulePreview(150);
        }

        private void Lanes_RightUp(object sender, MouseButtonEventArgs e)
        {
            if (!OnOffLane(e)) return;
            double t = LaneSec(e);
            if (_off.RemoveAll(r => t >= r.a - 0.05 && t <= r.b + 0.05) == 0) return;
            RedrawLanes(); SchedulePreview(150);
        }
    }
}

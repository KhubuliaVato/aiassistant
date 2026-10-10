using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AiAssistant
{
    public partial class DownloaderView : UserControl
    {
        public event Action<string>?        SendToEditor;
        public event Action<string>?        SendToCrop;
        public event Action<string,string>? Notify;

        // ── download folder (persisted next to the history) ───────────
        private string _folder = "";
        public string DownloadFolder
        {
            get => _folder;
            set { _folder = value ?? ""; SaveSettings(); UpdateFolderText(); }
        }
        public string CurrentDownloadFolder => EffectiveFolder;
        private string EffectiveFolder =>
            string.IsNullOrEmpty(_folder)
                ? AppPaths.VideosFolder("1Flow Downloads", "FlowSocial Downloads")
                : _folder;

        // ── models ────────────────────────────────────────────────────
        private record FormatOpt(string Selector, string Label);
        private record VideoInfo(string Title, string Thumbnail, double DurationSec, string Uploader,
                                 string Platform, string UploadDate, List<FormatOpt> Formats);
        private record DlOptions(bool AudioOnly, bool UseRange, string Start, string End,
                                 bool Subtitles, string Format, bool Thumbs, bool AutoVertical);

        private VideoInfo? _current;
        private string     _currentUrl = "";
        public  string     LastError   { get; private set; } = "";

        private class DlEntry
        {
            public string   Title    { get; set; } = "";
            public string   FilePath { get; set; } = "";
            public DateTime When     { get; set; }
        }
        private readonly List<DlEntry> _history = new();
        public List<(string Title, string FilePath, DateTime When)> GetDownloadHistory() =>
            _history.Select(h => (h.Title, h.FilePath, h.When)).ToList();

        private enum QueueStatus { Waiting, Downloading, Done, Error }
        private class QueueItem
        {
            public string      Url    { get; set; } = "";
            public string      Label  { get; set; } = "";
            public QueueStatus Status { get; set; } = QueueStatus.Waiting;
            public double      Pct    { get; set; }
            public DlOptions?  Opts   { get; set; }
            public ProgressBar? Bar   { get; set; }
        }
        private readonly List<QueueItem> _queue = new();

        private bool    _busy;
        private bool    _cancelRequested;
        private Process? _activeProc;

        public DownloaderView()
        {
            InitializeComponent();
            LoadSettings();
            LoadHistory();
            UpdateFolderText();
            UpdateOptionsSummary();
            RefreshQueue();
            Loaded += async (_, _) => await RefreshYtChipAsync();
        }

        // ════════════════════════════════════════════════════════════
        // yt-dlp LOCATION  (exe next to app → PATH → "python -m yt_dlp")
        // ════════════════════════════════════════════════════════════
        private static string[]? ResolveYtDlp(bool force = false) => YtDlpTool.Resolve(force);
        private static ProcessStartInfo? MakeYt(params string[] args) => YtDlpTool.Make(args);
        private static IEnumerable<string> CommonArgs() => YtDlpTool.CommonArgs();
        private async Task RefreshYtChipAsync()
        {
            string ver = await GetYtDlpVersionAsync();
            bool ok = ver.Length > 0;
            TxtYtChip.Text = ok ? $"yt-dlp {ver}" : "yt-dlp ვერ მოიძებნა";
            DotYt.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, ok ? "Success" : "Danger");
            if (!ok)
                ShowError("yt-dlp არ არის დაყენებული",
                    "გადმოსაწერად საჭიროა yt-dlp. ტერმინალში (ერთხელ) გაუშვი:\n\n    pip install yt-dlp\n\nან დადე yt-dlp.exe პროგრამის საქაღალდეში.");
        }

        // ════════════════════════════════════════════════════════════
        // SETTINGS / HISTORY FILES
        // ════════════════════════════════════════════════════════════
        private static string DataDir => AppPaths.DataDir;
        private string HistoryFile  => Path.Combine(DataDir, "dl_history.json");
        private string SettingsFile => Path.Combine(DataDir, "dl_settings.json");

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return;
                using var doc = JsonDocument.Parse(File.ReadAllText(SettingsFile));
                if (doc.RootElement.TryGetProperty("Folder", out var f)) _folder = f.GetString() ?? "";
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new { Folder = _folder }));
            }
            catch { }
        }

        private void UpdateFolderText() => TxtFolder.Text = EffectiveFolder;

        private void BtnChooseFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "საქაღალდე გადმოწერილი ვიდეოებისთვის", InitialDirectory = EffectiveFolder };
            if (dlg.ShowDialog() == true) DownloadFolder = dlg.FolderName;
        }

        private void BtnOpenDownloadsFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(EffectiveFolder);
                Process.Start("explorer.exe", EffectiveFolder);
            }
            catch { }
        }

        // ════════════════════════════════════════════════════════════
        // UI STATE HELPERS
        // ════════════════════════════════════════════════════════════
        private void ShowEmpty()
        {
            EmptyState.Visibility = Visibility.Visible;
            ErrorCard.Visibility = Visibility.Collapsed;
            PreviewCard.Visibility = Visibility.Collapsed;
        }

        private void ShowError(string title, string text)
        {
            EmptyState.Visibility = Visibility.Collapsed;
            PreviewCard.Visibility = Visibility.Collapsed;
            ErrorCard.Visibility = Visibility.Visible;
            TxtErrorTitle.Text = title;
            TxtError.Text = text;
        }

        private void ShowPreview()
        {
            EmptyState.Visibility = Visibility.Collapsed;
            ErrorCard.Visibility = Visibility.Collapsed;
            PreviewCard.Visibility = Visibility.Visible;
        }

        private void SetStatus(string text, double percent, bool show, bool indeterminate = false,
                               string speed = "", bool cancellable = false)
        {
            StatusBorder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            TxtStatus.Text = text;
            PbProgress.IsIndeterminate = indeterminate;
            PbProgress.Value = percent;
            TxtPercent.Text = !indeterminate && percent > 0 ? $"{percent:0}%" : "";
            TxtSpeed.Text = speed;
            BtnCancel.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            BtnDownload.IsEnabled = !busy;
            BtnFetchInfo.IsEnabled = !busy;
            BtnDownloadAll.IsEnabled = !busy;
        }

        private void RightTab_Changed(object sender, RoutedEventArgs e)
        {
            OptsPanel.Visibility = RbTabOpts.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            QueuePanelHost.Visibility = RbTabQueue.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            HistoryPanelHost.Visibility = RbTabHistory.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private TextBlock Txt(string text, double size, string brushKey, FontWeight? weight = null)
        {
            var t = new TextBlock { Text = text, FontSize = size };
            t.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            if (weight.HasValue) t.FontWeight = weight.Value;
            return t;
        }

        private Border Chip(string text, string glyph = "")
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            if (glyph.Length > 0)
            {
                var g = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11,
                                        Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Center };
                g.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
                sp.Children.Add(g);
            }
            sp.Children.Add(Txt(text, 12, "TextSecondary"));
            var b = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(11, 5, 11, 5),
                                 Margin = new Thickness(0, 0, 8, 8), BorderThickness = new Thickness(1), Child = sp };
            b.SetResourceReference(Border.BackgroundProperty, "BgCard");
            b.SetResourceReference(Border.BorderBrushProperty, "Border");
            return b;
        }

        // ════════════════════════════════════════════════════════════
        // OPTIONS
        // ════════════════════════════════════════════════════════════
        private void Options_Changed(object sender, RoutedEventArgs e)
        {
            TimestampPanel.Visibility = ChkTimestamp.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            CmbQuality.IsEnabled = ChkAudioOnly.IsChecked != true;
            UpdateOptionsSummary();
        }

        private void Options_Changed_Text(object sender, TextChangedEventArgs e)
        {
            if (TxtOptionsSummary != null) UpdateOptionsSummary();
        }

        private void UpdateOptionsSummary()
        {
            // TextChanged can fire while the XAML is still being built
            if (TxtOptionsSummary == null || TxtEndTime == null || TxtStartTime == null
                || ChkAudioOnly == null || ChkTimestamp == null || ChkSubtitles == null || ChkAutoVertical == null) return;
            var parts = new List<string>();
            if (ChkAudioOnly.IsChecked == true) parts.Add("მხოლოდ ხმა (MP3)");
            if (ChkTimestamp.IsChecked == true) parts.Add($"მონაკვეთი {TxtStartTime.Text.Trim()} – {TxtEndTime.Text.Trim()}");
            if (ChkSubtitles.IsChecked == true) parts.Add("სუბტიტრებით");
            if (ChkAutoVertical.IsChecked == true) parts.Add("მერე ვერტიკალში გადავა");
            TxtOptionsSummary.Text = parts.Count == 0 ? "" : "აქტიური პარამეტრები: " + string.Join(" · ", parts);
        }

        private static readonly Regex RxTime = new(@"^(\d{1,2}:)?\d{1,2}:\d{2}(\.\d+)?$|^\d+(\.\d+)?$", RegexOptions.Compiled);

        private DlOptions BuildOptions(string? formatOverride = null)
        {
            string fmt = formatOverride
                ?? (CmbQuality.SelectedItem is ComboBoxItem ci ? (string)(ci.Tag ?? "bv*+ba/b") : "bv*+ba/b");
            return new DlOptions(
                ChkAudioOnly.IsChecked == true,
                ChkTimestamp.IsChecked == true,
                TxtStartTime.Text.Trim(), TxtEndTime.Text.Trim(),
                ChkSubtitles.IsChecked == true, fmt,
                ChkThumbs.IsChecked == true, ChkAutoVertical.IsChecked == true);
        }

        private bool ValidateOptions(DlOptions o)
        {
            if (!o.UseRange) return true;
            if (!RxTime.IsMatch(o.Start) || !RxTime.IsMatch(o.End))
            {
                Notify?.Invoke("დრო ჩაწერე ფორმატით 00:01:30 (ან წუთი:წამი)", "error");
                return false;
            }
            return true;
        }

        // ════════════════════════════════════════════════════════════
        // URL INPUT
        // ════════════════════════════════════════════════════════════
        private void TxtUrl_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (BtnClearUrl != null)
                BtnClearUrl.Visibility = string.IsNullOrEmpty(TxtUrl.Text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void BtnClearUrl_Click(object sender, RoutedEventArgs e)
        {
            TxtUrl.Clear();
            TxtUrl.Focus();
        }

        private void TxtUrl_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) BtnFetchInfo_Click(sender, new RoutedEventArgs());
        }

        private void BtnPaste_Click(object sender, RoutedEventArgs e)
        {
            string clip = "";
            try { clip = Clipboard.GetText().Trim(); } catch { }
            if (string.IsNullOrWhiteSpace(clip))
            {
                Notify?.Invoke("ბუფერი ცარიელია.", "error");
                return;
            }
            // take the first line if several links were copied
            TxtUrl.Text = clip.Split('\n')[0].Trim();
            BtnFetchInfo_Click(sender, e);
        }

        // ════════════════════════════════════════════════════════════
        // FETCH INFO
        // ════════════════════════════════════════════════════════════
        private async void BtnFetchInfo_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            string url = TxtUrl.Text.Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                TxtUrl.Focus();
                return;
            }
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;

            SetBusy(true);
            SetStatus("ინფორმაცია იძებნება…", 0, true, indeterminate: true);
            var info = await Task.Run(() => FetchVideoInfo(url));
            SetBusy(false);

            if (info == null)
            {
                SetStatus("", 0, false);
                ShowError("ვერ მოიძებნა", FriendlyError(LastError));
                return;
            }

            _current = info;
            _currentUrl = url;
            SetStatus("", 0, false);
            ShowInfo(info);
            ShowPreview();
            if (!string.IsNullOrEmpty(info.Thumbnail)) _ = LoadThumbnail(info.Thumbnail);
        }

        private void ShowInfo(VideoInfo info)
        {
            TxtVideoTitle.Text = info.Title;
            TxtDurBadge.Text = FormatDuration(info.DurationSec);

            MetaChips.Children.Clear();
            if (!string.IsNullOrEmpty(info.Platform)) MetaChips.Children.Add(Chip(info.Platform, ""));
            if (!string.IsNullOrEmpty(info.Uploader)) MetaChips.Children.Add(Chip(info.Uploader, ""));
            if (info.DurationSec > 0) MetaChips.Children.Add(Chip(FormatDuration(info.DurationSec), ""));
            if (!string.IsNullOrEmpty(info.UploadDate)) MetaChips.Children.Add(Chip(info.UploadDate, ""));

            CmbQuality.Items.Clear();
            foreach (var f in info.Formats)
                CmbQuality.Items.Add(new ComboBoxItem { Content = f.Label, Tag = f.Selector });
            if (CmbQuality.Items.Count > 0) CmbQuality.SelectedIndex = 0;
            CmbQuality.IsEnabled = ChkAudioOnly.IsChecked != true;
            ImgThumb.Source = null;
        }

        private static string FriendlyError(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "უცნობი შეცდომა.";
            string last = raw.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR")) ?? raw.Trim();
            string lower = raw.ToLowerInvariant();
            string hint = "";
            if (lower.Contains("sign in to confirm") || lower.Contains("not a bot"))
                hint = "\n\nYouTube-მა ავტორიზაცია მოითხოვა. სცადე სხვა ვიდეო ან განაახლე yt-dlp (სეთინგები → yt-dlp განახლება).";
            else if (lower.Contains("unsupported url"))
                hint = "\n\nეს ლინკი არ არის მხარდაჭერილი — დარწმუნდი, რომ ლინკი კონკრეტულ ვიდეოზე მიდის.";
            else if (lower.Contains("private") || lower.Contains("login required") || lower.Contains("members-only"))
                hint = "\n\nვიდეო პირადია ან შესვლას მოითხოვს.";
            else if (lower.Contains("unable to download") || lower.Contains("getaddrinfo") || lower.Contains("timed out"))
                hint = "\n\nშეამოწმე ინტერნეტ-კავშირი და სცადე თავიდან.";
            return last + hint;
        }

        private VideoInfo? FetchVideoInfo(string url)
        {
            LastError = "";
            try
            {
                var si = MakeYt(CommonArgs().Concat(new[] { "--dump-json", url }).ToArray());
                if (si == null)
                {
                    LastError = "yt-dlp ვერ მოიძებნა. დააყენე: pip install yt-dlp";
                    return null;
                }

                using var p = Process.Start(si);
                if (p == null) { LastError = "პროცესის გაშვება ვერ მოხერხდა."; return null; }

                var errTask = p.StandardError.ReadToEndAsync();
                string json = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(60000)) { try { p.Kill(true); } catch { } LastError = "დრო ამოიწურა (timeout)."; return null; }
                string err = errTask.Result;

                if (p.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
                {
                    LastError = string.IsNullOrWhiteSpace(err) ? $"yt-dlp კოდი {p.ExitCode}" : err.Trim();
                    return null;
                }

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string Str(string name) =>
                    root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

                string title = Str("title"); if (title.Length == 0) title = "video";
                string thumb = Str("thumbnail");
                string uploader = Str("uploader"); if (uploader.Length == 0) uploader = Str("channel");
                string platform = Str("extractor_key"); if (platform == "Generic") platform = "";
                string ud = Str("upload_date");
                if (ud.Length == 8) ud = $"{ud[6..8]}.{ud[4..6]}.{ud[0..4]}"; else ud = "";
                double dur = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;

                // distinct heights, with approximate total size (video + best audio) when yt-dlp knows it
                var sizeByHeight = new SortedDictionary<int, long>();
                long bestAudio = 0;
                if (root.TryGetProperty("formats", out var fmts) && fmts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in fmts.EnumerateArray())
                    {
                        string vcodec = f.TryGetProperty("vcodec", out var vc) && vc.ValueKind == JsonValueKind.String ? vc.GetString() ?? "" : "";
                        string acodec = f.TryGetProperty("acodec", out var ac) && ac.ValueKind == JsonValueKind.String ? ac.GetString() ?? "" : "";
                        long size = 0;
                        foreach (var key in new[] { "filesize", "filesize_approx" })
                            if (f.TryGetProperty(key, out var sv) && sv.ValueKind == JsonValueKind.Number) { size = (long)sv.GetDouble(); break; }

                        if ((vcodec == "none" || vcodec.Length == 0) && acodec != "none") { bestAudio = Math.Max(bestAudio, size); continue; }
                        if (vcodec == "none" || vcodec.Length == 0) continue;
                        if (f.TryGetProperty("height", out var hv) && hv.ValueKind == JsonValueKind.Number)
                        {
                            int h = hv.GetInt32();
                            if (h < 144) continue;
                            sizeByHeight[h] = Math.Max(sizeByHeight.TryGetValue(h, out var old) ? old : 0, size);
                        }
                    }
                }

                var formats = new List<FormatOpt> { new("bv*+ba/b", "საუკეთესო ხარისხი (ავტომატურად)") };
                foreach (var kv in sizeByHeight.Reverse())
                {
                    string label = $"{kv.Key}p";
                    if (kv.Key >= 2160) label += " · 4K"; else if (kv.Key >= 1440) label += " · 2K";
                    else if (kv.Key >= 1080) label += " · Full HD"; else if (kv.Key >= 720) label += " · HD";
                    if (kv.Value > 0) label += $" · ≈ {(kv.Value + bestAudio) / 1048576.0:0} MB";
                    formats.Add(new FormatOpt($"bv*[height<={kv.Key}]+ba/b[height<={kv.Key}]", label));
                    if (formats.Count >= 9) break;
                }

                return new VideoInfo(title, thumb, dur, uploader, platform, ud, formats);
            }
            catch (Exception ex) { LastError = ex.Message; return null; }
        }

        private async Task LoadThumbnail(string url)
        {
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var bytes = await http.GetByteArrayAsync(url);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit(); bmp.Freeze();
                ImgThumb.Source = bmp;
            }
            catch { }
        }

        // ════════════════════════════════════════════════════════════
        // DOWNLOAD (core)
        // ════════════════════════════════════════════════════════════
        private static readonly Regex RxProgress = new(
            @"\[download\]\s+(?<p>\d+(?:\.\d+)?)%(?:\s+of\s+~?\s*(?<size>\S+))?(?:\s+at\s+(?<spd>\S+))?(?:\s+ETA\s+(?<eta>\S+))?",
            RegexOptions.Compiled);

        private async Task<string?> DownloadCoreAsync(string url, string title, DlOptions o,
            Action<double, string>? onProgress, string statusPrefix = "")
        {
            Directory.CreateDirectory(EffectiveFolder);

            string safe = SanitizeFileName(title);
            string template = Path.Combine(EffectiveFolder, $"{safe}_{DateTime.Now:HHmmss}.%(ext)s");

            var args = new List<string>(CommonArgs()) { "--newline", "--no-simulate", "--print", "after_move:filepath" };
            if (o.AudioOnly)
            {
                args.AddRange(new[] { "-f", "bestaudio[ext=m4a]/bestaudio/best", "-x", "--audio-format", "mp3", "--audio-quality", "0" });
            }
            else
            {
                // prefer H.264/AAC at a given resolution — plays everywhere, edits fast
                args.AddRange(new[] { "-f", o.Format, "-S", "res,vcodec:h264,acodec:aac", "--merge-output-format", "mp4" });
            }
            if (o.UseRange && o.Start.Length > 0 && o.End.Length > 0)
                args.AddRange(new[] { "--download-sections", $"*{o.Start}-{o.End}" });
            if (o.Subtitles)
                args.AddRange(new[] { "--write-subs", "--write-auto-subs", "--sub-langs", "ka,en" });
            args.AddRange(new[] { "-o", template, url });

            var si = MakeYt(args.ToArray());
            if (si == null) { LastError = "yt-dlp ვერ მოიძებნა."; return null; }

            string prefix = statusPrefix;
            Dispatcher.Invoke(() => SetStatus($"{prefix}{title}", 0, true, indeterminate: true, cancellable: true));
            _cancelRequested = false;

            string? finalPath = null;
            var errBuf = new StringBuilder();

            bool ok = await Task.Run(() =>
            {
                try
                {
                    using var p = new Process { StartInfo = si };
                    p.OutputDataReceived += (_, ev) =>
                    {
                        string? line = ev.Data;
                        if (string.IsNullOrWhiteSpace(line)) return;
                        var m = RxProgress.Match(line);
                        if (m.Success)
                        {
                            double pct = double.Parse(m.Groups["p"].Value, CultureInfo.InvariantCulture);
                            string spd = m.Groups["spd"].Success ? m.Groups["spd"].Value : "";
                            string eta = m.Groups["eta"].Success ? m.Groups["eta"].Value : "";
                            string size = m.Groups["size"].Success ? m.Groups["size"].Value : "";
                            string info = string.Join("  ·  ", new[] { size, spd, eta.Length > 0 ? "დარჩა " + eta : "" }
                                .Where(s => s.Length > 0));
                            Dispatcher.InvokeAsync(() =>
                            {
                                SetStatus($"{prefix}{title}", pct, true, false, info, true);
                                onProgress?.Invoke(pct, info);
                            });
                        }
                        else if (line.StartsWith("[Merger]") || line.StartsWith("[ExtractAudio]") || line.StartsWith("[VideoConvertor]")
                                 || line.StartsWith("[FixupM3u8]") || line.StartsWith("[ModifyChapters]"))
                        {
                            Dispatcher.InvokeAsync(() => SetStatus($"{prefix}ფაილი მზადდება…", 100, true, true, "", true));
                        }
                        else if (Path.IsPathRooted(line) && File.Exists(line.Trim()))
                        {
                            finalPath = line.Trim();
                        }
                    };
                    p.ErrorDataReceived += (_, ev) => { if (ev.Data != null) errBuf.AppendLine(ev.Data); };
                    _activeProc = p;
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    p.WaitForExit();
                    _activeProc = null;
                    return p.ExitCode == 0;
                }
                catch (Exception ex) { errBuf.AppendLine(ex.Message); _activeProc = null; return false; }
            });

            if (_cancelRequested)
            {
                LastError = "გაუქმდა";
                TryDeletePartial(template);
                return null;
            }
            if (!ok || finalPath == null || !File.Exists(finalPath))
            {
                LastError = errBuf.ToString().Trim();
                return null;
            }
            return finalPath;
        }

        private static void TryDeletePartial(string template)
        {
            try
            {
                string dir = Path.GetDirectoryName(template)!;
                string stem = Path.GetFileName(template).Replace(".%(ext)s", "");
                foreach (var f in Directory.GetFiles(dir, stem + "*"))
                    if (f.EndsWith(".part") || f.EndsWith(".ytdl") || f.Contains(".f") && f.EndsWith(".mp4.part")) File.Delete(f);
            }
            catch { }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            _cancelRequested = true;
            try { _activeProc?.Kill(true); } catch { }
            SetStatus("გაუქმება…", 0, true, true);
        }

        // ── single download ──────────────────────────────────────────
        private async void BtnDownload_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null || _busy) return;
            var o = BuildOptions();
            if (!ValidateOptions(o)) return;

            SetBusy(true);
            string? path = await DownloadCoreAsync(_currentUrl, _current.Title, o, null, "⬇  ");
            SetBusy(false);
            FinishSingle(path, _current.Title, o, false);
        }

        private void FinishSingle(string? path, string title, DlOptions o, bool forceVertical)
        {
            if (path == null)
            {
                if (_cancelRequested) { SetStatus("გადმოწერა გაუქმდა", 0, true); return; }
                SetStatus("შეცდომა — გადმოწერა ვერ მოხერხდა", 0, true);
                ShowError("გადმოწერა ვერ მოხერხდა", FriendlyError(LastError));
                return;
            }
            SetStatus("გადმოიწერა: " + Path.GetFileName(path), 100, true, false, FormatSize(new FileInfo(path).Length));
            OnDownloadComplete(path, title, o, forceVertical);
        }

        private void OnDownloadComplete(string path, string title, DlOptions o, bool forceVertical)
        {
            _history.Insert(0, new DlEntry { Title = title, FilePath = path, When = DateTime.Now });
            SaveHistory();
            RefreshHistory();
            Notify?.Invoke($"გადმოიწერა: {Path.GetFileName(path)}", "success");

            // system toast only when the app is in the background (otherwise the in-app toast is enough)
            if (Window.GetWindow(this) is Window w && !w.IsActive)
                ShowWindowsNotification("1Flow — გადმოიწერა", title);

            if (o.Thumbs && !o.AudioOnly) _ = ExtractThumbnailsAsync(path);

            if ((o.AutoVertical || forceVertical) && !o.AudioOnly)
                SendToCrop?.Invoke(path);
        }

        // ════════════════════════════════════════════════════════════
        // QUEUE
        // ════════════════════════════════════════════════════════════
        private void BtnAddToQueue_Click(object sender, RoutedEventArgs e)
        {
            string url = _currentUrl.Length > 0 ? _currentUrl : TxtUrl.Text.Trim();
            if (string.IsNullOrWhiteSpace(url)) return;
            if (_queue.Any(q => q.Url == url && q.Status != QueueStatus.Done))
            {
                Notify?.Invoke("ეს ლინკი უკვე სიაშია", "error");
                return;
            }
            var o = BuildOptions();
            if (!ValidateOptions(o)) return;

            string label = (_current != null && _currentUrl == url) ? _current.Title : url;
            _queue.Add(new QueueItem { Url = url, Label = label, Opts = o });
            RefreshQueue();
            Notify?.Invoke($"სიაში დაემატა: {label}", "success");
            RbTabQueue.IsChecked = true;
            RightTab_Changed(this, new RoutedEventArgs());
        }

        private async void BtnDownloadAll_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var pending = _queue.Where(q => q.Status != QueueStatus.Done).ToList();
            if (pending.Count == 0) return;

            SetBusy(true);
            int idx = 0, okCount = 0;
            foreach (var item in pending)
            {
                idx++;
                item.Status = QueueStatus.Downloading;
                item.Pct = 0;
                RefreshQueue();

                if (item.Label == item.Url)
                {
                    var info = await Task.Run(() => FetchVideoInfo(item.Url));
                    if (info != null) { item.Label = info.Title; RefreshQueue(); }
                }

                var opts = item.Opts ?? BuildOptions();
                string? path = await DownloadCoreAsync(item.Url, item.Label, opts,
                    (pct, _) => { item.Pct = pct; if (item.Bar != null) item.Bar.Value = pct; },
                    $"[{idx}/{pending.Count}]  ");

                if (path != null) { item.Status = QueueStatus.Done; item.Pct = 100; okCount++; OnDownloadComplete(path, item.Label, opts, false); }
                else item.Status = QueueStatus.Error;
                RefreshQueue();

                if (_cancelRequested) break;
            }
            SetBusy(false);
            SetStatus(_cancelRequested ? "სია გაჩერდა" : $"სია დასრულდა — {okCount}/{pending.Count} გადმოიწერა", 100, true);
        }

        private void BtnClearQueue_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _queue.Clear();
            RefreshQueue();
        }

        private async void BtnImportUrls_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "URL-ების იმპორტი:\n\n[დიახ] ფაილიდან (.txt, ერთი ლინკი ხაზზე)\n[არა] ბუფერიდან\n[გაუქმება] გაუქმება",
                "URL-ების იმპორტი", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            string raw;
            if (result == MessageBoxResult.Yes)
            {
                var ofd = new Microsoft.Win32.OpenFileDialog { Title = "URL-ების ფაილი", Filter = "Text|*.txt|All|*.*" };
                if (ofd.ShowDialog() != true) return;
                raw = await File.ReadAllTextAsync(ofd.FileName);
            }
            else if (result == MessageBoxResult.No) { try { raw = Clipboard.GetText(); } catch { raw = ""; } }
            else return;

            var o = BuildOptions();
            int added = 0;
            foreach (var line in raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string url = line.Trim();
                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                if (_queue.Any(q => q.Url == url)) continue;
                _queue.Add(new QueueItem { Url = url, Label = url, Opts = o });
                added++;
            }
            RefreshQueue();
            Notify?.Invoke($"{added} ლინკი სიაში დაემატა", added > 0 ? "success" : "error");
        }

        private void RefreshQueue()
        {
            QueuePanel.Children.Clear();
            TxtNoQueue.Visibility = _queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            BtnDownloadAll.IsEnabled = _queue.Count > 0 && !_busy;
            RbTabQueue.Content = _queue.Count > 0 ? $"სია ({_queue.Count})" : "სია";

            foreach (var item in _queue)
            {
                var card = new Border { Padding = new Thickness(13, 11, 10, 11), Margin = new Thickness(0, 0, 0, 8),
                                        CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
                card.SetResourceReference(Border.BackgroundProperty, "BgCard");
                card.SetResourceReference(Border.BorderBrushProperty, "Border");

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                (string glyph, string brush) = item.Status switch
                {
                    QueueStatus.Downloading => ("", "Accent"),
                    QueueStatus.Done        => ("", "Success"),
                    QueueStatus.Error       => ("", "Danger"),
                    _                       => ("", "TextMuted")
                };
                var gl = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 15,
                                         Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
                gl.SetResourceReference(TextBlock.ForegroundProperty, brush);
                grid.Children.Add(gl);

                var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var title = Txt(item.Label, 12.5, "TextPrimary", FontWeights.Medium);
                title.TextTrimming = TextTrimming.CharacterEllipsis;
                title.ToolTip = item.Url;
                mid.Children.Add(title);
                string sub = item.Status switch
                {
                    QueueStatus.Waiting     => "ელოდება",
                    QueueStatus.Downloading => "იწერება…",
                    QueueStatus.Done        => "მზადაა",
                    _                       => "შეცდომა — დააჭირე გადმოწერას თავიდან საცდელად"
                };
                var subTxt = Txt(sub, 11, "TextMuted"); subTxt.Margin = new Thickness(0, 2, 0, 0);
                mid.Children.Add(subTxt);
                if (item.Status == QueueStatus.Downloading)
                {
                    var bar = new ProgressBar { Height = 4, Minimum = 0, Maximum = 100, Value = item.Pct, Margin = new Thickness(0, 7, 0, 0) };
                    item.Bar = bar;
                    mid.Children.Add(bar);
                }
                Grid.SetColumn(mid, 1);
                grid.Children.Add(mid);

                if (item.Status != QueueStatus.Downloading)
                {
                    var rm = new Button { Content = "", Style = (Style)FindResource("GhostButton"),
                                          FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11,
                                          Width = 30, Height = 30, Padding = new Thickness(0), ToolTip = "სიიდან ამოღება",
                                          VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
                    var captured = item;
                    rm.Click += (_, _) => { if (_busy) return; _queue.Remove(captured); RefreshQueue(); };
                    Grid.SetColumn(rm, 2);
                    grid.Children.Add(rm);
                }

                card.Child = grid;
                QueuePanel.Children.Add(card);
            }
        }

        // ════════════════════════════════════════════════════════════
        // HISTORY
        // ════════════════════════════════════════════════════════════
        private void SaveHistory()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(HistoryFile, JsonSerializer.Serialize(_history.Take(40)));
            }
            catch { }
        }

        private void LoadHistory()
        {
            try
            {
                if (File.Exists(HistoryFile))
                {
                    var loaded = JsonSerializer.Deserialize<List<DlEntry>>(File.ReadAllText(HistoryFile));
                    if (loaded != null) { _history.Clear(); _history.AddRange(loaded); }
                }
            }
            catch { }
            RefreshHistory();
        }

        private Button HistBtn(string glyph, string tip, Action action, bool enabled = true)
        {
            var b = new Button { Content = glyph, Style = (Style)FindResource("ToolButton"), Width = 32, Height = 32,
                                 FontSize = 13, ToolTip = tip, Margin = new Thickness(5, 0, 0, 0), IsEnabled = enabled };
            b.Click += (_, _) => action();
            return b;
        }

        private void RefreshHistory()
        {
            HistoryPanel.Children.Clear();
            TxtNoHistory.Visibility = _history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var entry in _history.ToList())
            {
                bool exists = File.Exists(entry.FilePath);
                bool audio = entry.FilePath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);

                var card = new Border { Padding = new Thickness(13, 11, 10, 11), Margin = new Thickness(0, 0, 0, 8),
                                        CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
                card.SetResourceReference(Border.BackgroundProperty, "BgCard");
                card.SetResourceReference(Border.BorderBrushProperty, "Border");
                if (!exists) card.Opacity = 0.55;

                var grid = new Grid();
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var head = new StackPanel { Orientation = Orientation.Horizontal };
                var ic = new TextBlock { Text = audio ? "" : "", FontFamily = new FontFamily("Segoe MDL2 Assets"),
                                         FontSize = 14, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                ic.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
                head.Children.Add(ic);
                var titleBox = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Width = 270 };
                var t = Txt(entry.Title, 12.5, "TextPrimary", FontWeights.Medium);
                t.TextTrimming = TextTrimming.CharacterEllipsis;
                t.ToolTip = entry.FilePath;
                titleBox.Children.Add(t);
                string meta = entry.When.ToString("HH:mm · dd.MM.yyyy");
                meta += exists ? "  ·  " + FormatSize(new FileInfo(entry.FilePath).Length) : "  ·  ფაილი ვერ მოიძებნა";
                var m = Txt(meta, 11, "TextMuted"); m.Margin = new Thickness(0, 2, 0, 0);
                titleBox.Children.Add(m);
                head.Children.Add(titleBox);
                Grid.SetRow(head, 0);
                grid.Children.Add(head);

                var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0),
                                            HorizontalAlignment = HorizontalAlignment.Left };
                string path = entry.FilePath;
                btns.Children.Add(HistBtn("", "მინი მონტაჟში", () => SendToEditor?.Invoke(path), exists && !audio));
                btns.Children.Add(HistBtn("", "გავერტიკალურება", () => SendToCrop?.Invoke(path), exists && !audio));
                btns.Children.Add(HistBtn("", "დაკვრა სისტემურ პლეერში", () =>
                {
                    try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
                }, exists));
                btns.Children.Add(HistBtn("", "საქაღალდეში ჩვენება", () =>
                {
                    try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
                }, exists));
                btns.Children.Add(HistBtn("", "ისტორიიდან წაშლა (ფაილი რჩება)", () =>
                {
                    _history.Remove(entry); SaveHistory(); RefreshHistory();
                }));
                Grid.SetRow(btns, 1);
                grid.Children.Add(btns);

                card.Child = grid;
                HistoryPanel.Children.Add(card);
            }
        }

        // ════════════════════════════════════════════════════════════
        // SOCIAL PRESETS (best ≤1080p → straight to verticalize)
        // ════════════════════════════════════════════════════════════
        private async void BtnSocialPreset_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            string url = TxtUrl.Text.Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                Notify?.Invoke("ჯერ ჩასვი ლინკი", "error");
                TxtUrl.Focus();
                return;
            }
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;

            string label = ((sender as Button)?.Tag?.ToString()) switch { "tiktok" => "TikTok", "reels" => "Reels", _ => "Shorts" };

            SetBusy(true);
            if (_current == null || _currentUrl != url)
            {
                SetStatus($"{label} — ინფორმაცია იძებნება…", 0, true, indeterminate: true);
                var info = await Task.Run(() => FetchVideoInfo(url));
                if (info == null)
                {
                    SetBusy(false);
                    SetStatus("", 0, false);
                    ShowError("ვერ მოიძებნა", FriendlyError(LastError));
                    return;
                }
                _current = info; _currentUrl = url;
                ShowInfo(info); ShowPreview();
                if (!string.IsNullOrEmpty(info.Thumbnail)) _ = LoadThumbnail(info.Thumbnail);
            }

            var baseOpts = BuildOptions("bv*[height<=1080]+ba/b[height<=1080]");
            var o = baseOpts with { AudioOnly = false, UseRange = false, AutoVertical = true };
            string? path = await DownloadCoreAsync(url, _current!.Title, o, null, $"{label}  ·  ");
            SetBusy(false);
            FinishSingle(path, _current.Title, o, true);
            if (path != null) Notify?.Invoke($"{label} — ვერტიკალში გადადის…", "success");
        }

        // ════════════════════════════════════════════════════════════
        // EXTRAS
        // ════════════════════════════════════════════════════════════
        private async Task ExtractThumbnailsAsync(string videoPath)
        {
            try
            {
                string thumbDir = Path.Combine(Path.GetDirectoryName(videoPath)!,
                    Path.GetFileNameWithoutExtension(videoPath) + "_thumbs");
                Directory.CreateDirectory(thumbDir);

                var si = new ProcessStartInfo(AppPaths.Ffmpeg()) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var a in new[] { "-y", "-loglevel", "error", "-i", videoPath, "-vf", "select=eq(pict_type\\,I)",
                                          "-vsync", "vfr", "-frames:v", "5", Path.Combine(thumbDir, "thumb_%02d.jpg") })
                    si.ArgumentList.Add(a);

                await Task.Run(() => { using var p = Process.Start(si); p?.WaitForExit(40000); });

                if (Directory.GetFiles(thumbDir, "*.jpg").Length > 0)
                    Notify?.Invoke("კადრები შენახულია: " + Path.GetFileName(thumbDir), "success");
            }
            catch { }
        }

        private static void ShowWindowsNotification(string title, string message)
        {
            try
            {
                string t = System.Security.SecurityElement.Escape(title.Replace("'", " ").Replace("\"", " ")) ?? "";
                string m = System.Security.SecurityElement.Escape(message.Replace("'", " ").Replace("\"", " ")) ?? "";
                string ps = $@"
$xml = [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom, ContentType=WindowsRuntime]::new()
$xml.LoadXml('<toast><visual><binding template=""ToastText02""><text id=""1"">{t}</text><text id=""2"">{m}</text></binding></visual></toast>')
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType=WindowsRuntime]::CreateToastNotifier('1Flow').Show([Windows.UI.Notifications.ToastNotification, Windows.UI.Notifications, ContentType=WindowsRuntime]::new($xml))";
                var si = new ProcessStartInfo("powershell") { UseShellExecute = false, CreateNoWindow = true };
                si.ArgumentList.Add("-WindowStyle"); si.ArgumentList.Add("Hidden");
                si.ArgumentList.Add("-Command"); si.ArgumentList.Add(ps);
                Process.Start(si);
            }
            catch { }
        }

        // ── helpers ──────────────────────────────────────────────────
        private static string FormatDuration(double sec)
        {
            var t = TimeSpan.FromSeconds(sec);
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }

        private static string FormatSize(long bytes)
        {
            double mb = bytes / 1048576.0;
            return mb >= 1024 ? $"{mb / 1024:0.0} GB" : mb >= 10 ? $"{mb:0} MB" : $"{mb:0.0} MB";
        }

        private static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            name = name.Trim().TrimEnd('.');
            if (name.Length == 0) name = "video";
            return name.Length > 60 ? name[..60].TrimEnd() : name;
        }

        // ════════════════════════════════════════════════════════════
        // Settings tab: yt-dlp version + update
        // ════════════════════════════════════════════════════════════
        public async Task<string> GetYtDlpVersionAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    var si = MakeYt("--version");
                    if (si == null) return "";
                    using var p = Process.Start(si);
                    if (p == null) return "";
                    string o = p.StandardOutput.ReadToEnd().Trim();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(20000);
                    return p.ExitCode == 0 ? o : "";
                }
                catch { return ""; }
            });
        }

        public async Task<(bool Success, string Message)> UpdateYtDlpAsync(Action<string>? progress = null)
        {
            var cmd = await Task.Run(() => ResolveYtDlp());
            if (cmd == null) return (false, "yt-dlp ვერ მოიძებნა — დააყენე: pip install yt-dlp");

            ProcessStartInfo si;
            if (cmd[0] == "python")
            {
                si = new ProcessStartInfo("python") { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
                foreach (var a in new[] { "-m", "pip", "install", "-U", "yt-dlp" }) si.ArgumentList.Add(a);
            }
            else
            {
                si = MakeYt("-U")!;
            }

            return await Task.Run(() =>
            {
                try
                {
                    using var p = new Process { StartInfo = si };
                    var last = new StringBuilder();
                    p.OutputDataReceived += (_, ev) =>
                    {
                        if (ev.Data == null) return;
                        last.Clear(); last.Append(ev.Data);
                        progress?.Invoke(ev.Data);
                    };
                    p.Start(); p.BeginOutputReadLine();
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit(180000);
                    return (p.ExitCode == 0, p.ExitCode == 0 ? last.ToString() : err.Trim());
                }
                catch (Exception ex) { return (false, ex.Message); }
            });
        }
    }
}

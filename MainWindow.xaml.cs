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
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AiAssistant
{
    // ================================================================
    // MODELS
    // ================================================================
    public class VideoItem
    {
        public string FileName           { get; set; } = "";
        public string FullPath           { get; set; } = "";
        public string ThumbnailPath      { get; set; } = "";
        public string FileExtensionBadge { get; set; } = "";
        public string FileMeta           { get; set; } = "";   // "42.3 MB · 12 ოქტ"
    }

    public class FlowConfig
    {
        public string Theme                        { get; set; } = ThemeManager.Current;
        public string MainDefaultFolder            { get; set; } = "";
        public string CacheFolder                  { get; set; } = "";
        public Dictionary<string, string> Shortcuts { get; set; } = new();
    }

    public class LtPreset
    {
        public string Label       { get; set; } = "";
        public string Name        { get; set; } = "";
        public string Desc        { get; set; } = "";
        public string TemplateId  { get; set; } = "1tv";
        public int    Duration    { get; set; } = 0;
        public string OverlayPath { get; set; } = "";
    }

    public class WmPreset
    {
        public string Label    { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string Position { get; set; } = "topright";
        public int    Size     { get; set; } = 15;
        public int    Opacity  { get; set; } = 100;
    }

    public class IoPreset
    {
        public string Label           { get; set; } = "";
        public string TemplateContent { get; set; } = ""; // CmbStingerTemplate item text
        public bool   ApplyIntro      { get; set; } = true;
        public bool   ApplyOutro      { get; set; } = true;
        public double Duration        { get; set; } = 2.0;
        public string CustomIntroPath { get; set; } = "";
        public string CustomOutroPath { get; set; } = "";
    }

    public class MediaLibraryItem
    {
        public string   FilePath { get; set; } = "";
        public string   Title    { get; set; } = "";
        public string   Source   { get; set; } = ""; // "დამუშავებული" | "ჩამოტვირთული"
        public DateTime When     { get; set; }
    }

    public class JobEntry
    {
        public string   OutputPath  { get; set; } = "";
        public string   FileName    { get; set; } = "";
        public string   LtName      { get; set; } = "";
        public string   TemplateId  { get; set; } = "";
        public DateTime ProcessedAt { get; set; } = DateTime.Now;
    }

    // ================================================================
    // MAIN WINDOW
    // ================================================================
    public partial class MainWindow : Window
    {
        // ── File names ───────────────────────────────────────────────
        private const string CFG_FILE       = "flow_config.json";
        private const string PRESETS_FILE   = "lt_presets.json";
        private const string WM_FILE        = "wm_presets.json";
        private const string IO_FILE        = "io_presets.json";
        private const string HISTORY_FILE   = "job_history.json";

        // ── Paths ────────────────────────────────────────────────────
        private string BaseDir => AppDomain.CurrentDomain.BaseDirectory;
        private string CacheDir
        {
            get
            {
                if (!string.IsNullOrEmpty(_cacheFolder)) return _cacheFolder;
                string root = !string.IsNullOrEmpty(selectedFolderPath)
                    ? selectedFolderPath : Path.GetTempPath();
                return Path.Combine(root, ".flow_cache");
            }
        }

        // ── App state ────────────────────────────────────────────────
        private const string StoppedText = "პროცესი გაჩერდა";
        private const string ErrorText   = "დამუშავებისას მოხდა შეცდომა";
        private string selectedFolderPath = "";
        private string selectedVideoPath  = "";
        private string _cacheFolder       = "";
        private bool   _durFull           = true;
        private Process? currentRunningProcess = null;

        // ── Data ─────────────────────────────────────────────────────
        private List<LtPreset>            _presets   = new();
        private List<WmPreset>            _wmPresets = new();
        private List<IoPreset>            _ioPresets = new();
        private List<JobEntry>            _history   = new();
        private Dictionary<string,string> _shortcuts = DefaultShortcuts();

        // ── Editor ───────────────────────────────────────────────────


        // ── Shortcut capture ─────────────────────────────────────────
        private string? _capturingAction = null;
        private Button? _capturingButton = null;

        // ================================================================
        // CONSTRUCTOR
        // ================================================================
        public MainWindow()
        {
            InitializeComponent();
            SourceInitialized += (_, _) => WindowMaximizeFix.Attach(this);
            KeyDown += Window_KeyDown;
        }

        // ================================================================
        // DEFAULT SHORTCUTS
        // ================================================================
        private static Dictionary<string,string> DefaultShortcuts() => new()
        {
            ["Play/Pause"]  = "Space",
            ["Add Cut"]     = "R",
            ["Del Segment"] = "Delete",
            ["Undo"]        = "Ctrl+Z",
            ["Redo"]        = "Ctrl+Y",
            ["Toggle Snap"] = "S",
            ["Cut Only"]    = "Return",
            ["Cut + Crop"]  = "Ctrl+Return",
            ["Rewind"]      = "J",
            ["-5 seconds"]  = "Left",
            ["+5 seconds"]  = "Right",
            ["Ripple Delete"] = "Shift+Delete",
            ["Prev Frame"]  = "OemComma",
            ["Next Frame"]  = "OemPeriod",
        };

        // ================================================================
        // STARTUP
        // ================================================================
        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // ── App icon (title bar + taskbar) ──────────────────────────
            // Resolved at runtime with the same multi-candidate pattern used
            // by ffmpeg.exe and the Python scripts.
            string[] iconCandidates =
            {
                Path.Combine(BaseDir, "assets", "logos", "logo.ico"),
                Path.GetFullPath(Path.Combine(BaseDir, @"..\..\..\assets\logos\logo.ico")),
                Path.GetFullPath(Path.Combine(BaseDir, @"..\..\..\..\assets\logos\logo.ico")),
            };
            foreach (var iconPath in iconCandidates)
            {
                if (!File.Exists(iconPath)) continue;
                try
                {
                    var icon = new System.Windows.Media.Imaging.BitmapImage();
                    icon.BeginInit();
                    icon.UriSource   = new Uri(iconPath, UriKind.Absolute);
                    icon.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    icon.EndInit();
                    icon.Freeze();
                    Icon = icon;
                }
                catch { }
                break;
            }

            // ── Title bar logo (logo.png) ────────────────────────────────
            string[] logoCandidates =
            {
                Path.Combine(BaseDir, "assets", "logos", "logo.png"),
                Path.GetFullPath(Path.Combine(BaseDir, @"..\..\..\assets\logos\logo.png")),
            };
            foreach (var logoPath in logoCandidates)
            {
                if (!File.Exists(logoPath)) continue;
                try
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit(); bmp.UriSource = new Uri(logoPath, UriKind.Absolute);
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.EndInit(); bmp.Freeze();
                    // FindName is null-safe — works whether or not the custom title bar
                    // XAML has already been applied.
                    if (FindName("TitleBarLogo") is System.Windows.Controls.Image logoImg)
                        logoImg.Source = bmp;
                }
                catch { }
                break;
            }

            MainTabControl.SelectedIndex = 0;
            LoadConfig();
            LoadPresets();
            LoadWmPresets();
            LoadHistory();
            RefreshPresetsUI();
            RefreshWmPresetsUI();
            RefreshHistoryUI();
            RefreshShortcutsUI();

            // Brand Kit: Intro/Outro presets + quick-apply chips for Crop tab
            LoadIoPresets();
            RefreshIoPresetsUI();
            RefreshQuickLtChips();
            RefreshQuickWmChips();
            RefreshQuickIoChips();
            WireMediaLibrary();
            WireFaceBlur();
            WireAiBackground();
            WireVoiceOver();
            RefreshMediaLibraryUI();
            WireSettings();
            UpdateBrandPreview();

            // Wire up EditorView events
            TheEditorView.ClipReadyForCrop += OnEditorClipReadyForCrop;
            TheEditorView.CacheFolder = CacheDir;
            TheEditorView.Notify += (msg, kind) => ShowToast(msg, kind);

            // Wire up DownloaderView events — pushes a downloaded file
            // straight into the Editor or Crop tab.
            // NOTE: downloads use their own persistent folder (Videos/1Flow Downloads
            // by default), NOT the cache folder — cache gets cleared by "Clear Cache".
            TheDownloaderView.SendToEditor += OnDownloaderSendToEditor;
            TheDownloaderView.SendToCrop   += OnDownloaderSendToCrop;
            TheDownloaderView.Notify       += (msg, kind) => { ShowToast(msg, kind); RefreshMediaLibraryUI(); };

            // StreamClipper — reuse same hand-off handlers as Downloader
            TheStreamClipper.SendToEditor  += OnDownloaderSendToEditor;
            TheStreamClipper.SendToCrop    += OnDownloaderSendToCrop;
            TheStreamClipper.Notify        += (msg, kind) => { ShowToast(msg, kind); RefreshMediaLibraryUI(); };

            // Show currently installed yt-dlp version in Settings
            _ = RefreshYtDlpVersionLabel();

            if (!string.IsNullOrEmpty(selectedFolderPath) && Directory.Exists(selectedFolderPath))
            {
                TxtFolderPath.Text = selectedFolderPath;
                await RefreshVideoListWithThumbnails();
            }
        }

        // ================================================================
        // KEYBOARD SHORTCUTS
        // ================================================================
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            // Capturing a new shortcut?
            if (_capturingAction != null && _capturingButton != null)
            {
                if (e.Key != Key.Escape
                    && e.Key != Key.LeftCtrl  && e.Key != Key.RightCtrl
                    && e.Key != Key.LeftShift && e.Key != Key.RightShift
                    && e.Key != Key.LeftAlt   && e.Key != Key.RightAlt)
                {
                    string newKey = KeyToString(e.Key, Keyboard.Modifiers);
                    string? clash = _shortcuts.Where(kv => kv.Value == newKey && kv.Key != _capturingAction)
                                              .Select(kv => kv.Key).FirstOrDefault();
                    if (clash != null)
                    {
                        string nm = ShortcutLabels.TryGetValue(clash, out var cl) ? cl : clash;
                        ShowToast($"„{PrettyKey(newKey)}“ უკვე გამოიყენება: {nm}", "error");
                    }
                    else
                    {
                        _shortcuts[_capturingAction] = newKey;
                        SaveConfig();
                    }
                }
                _capturingAction = null;
                _capturingButton = null;
                RefreshShortcutsUI();
                e.Handled = true;
                return;
            }

            // Editor tab only, skip when typing in TextBox
            if (MainTabControl.SelectedIndex != 1) return;
            if (e.OriginalSource is TextBox) return;

            string pressed = KeyToString(e.Key, Keyboard.Modifiers);

            // Reverse-lookup: which action (if any) is currently bound to this key?
            // This is what makes "Change" in Settings actually take effect —
            // rebinding an action's key changes what triggers it, and the OLD
            // key stops doing anything (it no longer matches any dictionary value).
            foreach (var kv in _shortcuts)
            {
                if (kv.Value == pressed)
                {
                    TheEditorView.HandleAction(kv.Key);
                    e.Handled = true;
                    return;
                }
            }
        }

        private static string KeyToString(Key key, ModifierKeys mods)
        {
            string s = "";
            if ((mods & ModifierKeys.Control) != 0) s += "Ctrl+";
            if ((mods & ModifierKeys.Shift)   != 0) s += "Shift+";
            if ((mods & ModifierKeys.Alt)     != 0) s += "Alt+";
            return s + key.ToString();
        }

        private void StartCapturingShortcut(string action, Button btn)
        {
            _capturingAction = action;
            _capturingButton = btn;
            BeginCaptureVisual(action);
            btn.Focus();
        }

        // ================================================================
        // CONFIG
        // ================================================================
        private void LoadConfig()
        {
            try
            {
                string path = Path.Combine(AppPaths.DataDir, CFG_FILE);
                if (!File.Exists(path)) return;
                var cfg = JsonSerializer.Deserialize<FlowConfig>(File.ReadAllText(path));
                if (cfg == null) return;
                selectedFolderPath = cfg.MainDefaultFolder;
                _cacheFolder       = cfg.CacheFolder;
                if (cfg.Shortcuts != null)
                    foreach (var kv in cfg.Shortcuts)
                        if (_shortcuts.ContainsKey(kv.Key)) _shortcuts[kv.Key] = kv.Value;
            }
            catch { }
        }

        private void SaveConfig()
        {
            try
            {
                var cfg = new FlowConfig
                {
                    MainDefaultFolder = selectedFolderPath,
                    CacheFolder       = _cacheFolder,
                    Shortcuts         = _shortcuts
                };
                File.WriteAllText(Path.Combine(AppPaths.DataDir, CFG_FILE),
                    JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        // ================================================================
        // LT PRESETS
        // ================================================================
        private void LoadPresets()
        {
            try
            {
                string p = Path.Combine(AppPaths.DataDir, PRESETS_FILE);
                if (File.Exists(p))
                    _presets = JsonSerializer.Deserialize<List<LtPreset>>(File.ReadAllText(p)) ?? new();
            }
            catch { _presets = new(); }
        }

        private void SavePresets()
        {
            try
            {
                File.WriteAllText(Path.Combine(AppPaths.DataDir, PRESETS_FILE),
                    JsonSerializer.Serialize(_presets, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private void RefreshPresetsUI(string filter = "")
        {
            PresetsPanel.Children.Clear();
            bool hasAny = _presets.Count > 0;
            PresetSearchBorder.Visibility = hasAny ? Visibility.Visible : Visibility.Collapsed;

            if (!hasAny)
            {
                TxtNoPresets.Text = "პრეზეტი ჯერ არ გაქვს — ჩაწერე ტექსტი, აირჩიე სტილი და დააჭირე „შენახვა“.";
                TxtNoPresets.Visibility = Visibility.Visible;
                return;
            }

            bool searching = !string.IsNullOrWhiteSpace(filter);
            var filtered = searching
                ? _presets.Where(p =>
                    p.Label.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    p.Name.Contains(filter,  StringComparison.OrdinalIgnoreCase) ||
                    p.Desc.Contains(filter,  StringComparison.OrdinalIgnoreCase)).ToList()
                : _presets;

            SearchCountBadge.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
            if (searching) TxtSearchCount.Text = $"{filtered.Count}/{_presets.Count}";

            if (filtered.Count == 0)
            {
                TxtNoPresets.Text = $"„{filter}“ — ვერ მოიძებნა";
                TxtNoPresets.Visibility = Visibility.Visible;
                return;
            }
            TxtNoPresets.Visibility = Visibility.Collapsed;

            foreach (var preset in filtered)
                PresetsPanel.Children.Add(BuildPresetChip(preset));
        }

        private UIElement BuildPresetChip(LtPreset p)
            => BuildBrandChip(p.Label, () => ApplyPreset(p), () =>
            {
                _presets.Remove(p);
                SavePresets(); RefreshPresetsUI(TxtPresetSearch?.Text ?? ""); RefreshQuickLtChips();
            });

        private void ApplyPreset(LtPreset p)
        {
            ChkLowerThird.IsChecked = true;
            LowerThirdFields.Visibility = Visibility.Visible;
            TxtLTName.Text = p.Name;
            TxtLTDesc.Text = p.Desc;
            TmplClassic.IsChecked = false; TmplDark.IsChecked = false;
            TmplRed.IsChecked = false; TmplMinimal.IsChecked = false; TmplCustom.IsChecked = false;
            switch (p.TemplateId)
            {
                case "dark":    TmplDark.IsChecked    = true; break;
                case "red":     TmplRed.IsChecked     = true; break;
                case "minimal": TmplMinimal.IsChecked = true; break;
                case "custom":  TmplCustom.IsChecked  = true; break;
                default:        TmplClassic.IsChecked = true; break;
            }
            if (p.Duration == 0) SetDurationMode(true);
            else { SetDurationMode(false); SlDuration.Value = Math.Clamp(p.Duration, 2, 30); }
            if (!string.IsNullOrEmpty(p.OverlayPath)) TxtOverlayPath.Text = p.OverlayPath;
        }

        private void BtnSavePreset_Click(object sender, RoutedEventArgs e)
        {
            SavePresetPanel.Visibility = Visibility.Visible;
            TxtPresetLabel.Text = GetLTFieldValue(TxtLTName);
            TxtPresetLabel.Focus();
            TxtPresetLabel.SelectAll();
        }

        private void BtnConfirmSavePreset_Click(object sender, RoutedEventArgs e)
        {
            string label = TxtPresetLabel.Text.Trim();
            if (string.IsNullOrEmpty(label))
            {
                ShowToast("ჩაწერე პრეზეტის სახელი", "error");
                return;
            }
            var preset = new LtPreset
            {
                Label       = label,
                Name        = GetLTFieldValue(TxtLTName),
                Desc        = GetLTFieldValue(TxtLTDesc),
                TemplateId  = GetSelectedTemplate(),
                Duration    = _durFull ? 0 : (int)SlDuration.Value,
                OverlayPath = TmplCustom.IsChecked == true ? TxtOverlayPath.Text.Trim() : ""
            };
            _presets.RemoveAll(p => p.Label == label);
            _presets.Insert(0, preset);
            SavePresets(); RefreshPresetsUI(); RefreshQuickLtChips();
            SavePresetPanel.Visibility = Visibility.Collapsed;
            ShowToast($"პრეზეტი შენახულია: {label}", "success");
        }

        private void BtnCancelSavePreset_Click(object sender, RoutedEventArgs e)
            => SavePresetPanel.Visibility = Visibility.Collapsed;

        private void TxtPresetSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            string q = TxtPresetSearch.Text;
            BtnClearPresetSearch.Visibility = string.IsNullOrEmpty(q)
                ? Visibility.Collapsed : Visibility.Visible;
            RefreshPresetsUI(q);
        }

        private void BtnClearPresetSearch_Click(object sender, MouseButtonEventArgs e)
        {
            TxtPresetSearch.Text = "";
            BtnClearPresetSearch.Visibility = Visibility.Collapsed;
            SearchCountBadge.Visibility = Visibility.Collapsed;
            RefreshPresetsUI();
        }

        // ================================================================
        // WATERMARK PRESETS
        // ================================================================
        private void LoadWmPresets()
        {
            try
            {
                string p = Path.Combine(AppPaths.DataDir, WM_FILE);
                if (File.Exists(p))
                    _wmPresets = JsonSerializer.Deserialize<List<WmPreset>>(File.ReadAllText(p)) ?? new();
            }
            catch { _wmPresets = new(); }
        }

        private void SaveWmPresets()
        {
            try
            {
                File.WriteAllText(Path.Combine(AppPaths.DataDir, WM_FILE),
                    JsonSerializer.Serialize(_wmPresets, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private void RefreshWmPresetsUI()
        {
            WmPresetsPanel.Children.Clear();
            TxtNoWmPresets.Visibility = _wmPresets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var p in _wmPresets)
                WmPresetsPanel.Children.Add(BuildWmChip(p));
        }

        private UIElement BuildWmChip(WmPreset p)
            => BuildBrandChip(p.Label, () => ApplyWmPreset(p), () =>
            {
                _wmPresets.Remove(p);
                SaveWmPresets(); RefreshWmPresetsUI(); RefreshQuickWmChips();
            });

        private void ApplyWmPreset(WmPreset p)
        {
            ChkWatermark.IsChecked = true;
            WatermarkFields.Visibility = Visibility.Visible;
            TxtWmPath.Text = p.FilePath;
            WmTL.IsChecked = p.Position == "topleft";
            WmTR.IsChecked = p.Position == "topright";
            WmBL.IsChecked = p.Position == "bottomleft";
            WmBR.IsChecked = p.Position == "bottomright";
            SlWmSize.Value    = Math.Clamp(p.Size,    5, 40);
            SlWmOpacity.Value = Math.Clamp(p.Opacity, 10, 100);
        }

        private void ChkWatermark_Changed(object sender, RoutedEventArgs e)
        {
            if (WatermarkFields == null) return;
            WatermarkFields.Visibility =
                ChkWatermark.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        // ================================================================
        // INTRO / OUTRO STINGER — UI handlers
        // ================================================================
        private void ChkIntroOutro_Changed(object sender, RoutedEventArgs e)
        {
            if (IntroOutroFields == null) return;
            IntroOutroFields.Visibility =
                ChkIntroOutro.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void CmbStingerTemplate_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (CustomStingerPanel == null || StingerDurationPanel == null) return;
            bool isCustom = (CmbStingerTemplate.SelectedItem as ComboBoxItem)?.Content?.ToString()
                ?.Contains("საკუთარი") == true;
            CustomStingerPanel.Visibility   = isCustom ? Visibility.Visible   : Visibility.Collapsed;
            StingerDurationPanel.Visibility = isCustom ? Visibility.Collapsed : Visibility.Visible;
            UpdateBrandPreview();
        }

        private void SlStingerDuration_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        { if (TxtStingerDuration != null) TxtStingerDuration.Text = $"{SlStingerDuration.Value:F1}წმ"; UpdateBrandPreview(); }

        private void BtnBrowseCustomIntro_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog { Title = "Intro ფაილი", Filter = "Video|*.mp4;*.mov;*.mkv|All|*.*" };
            if (ofd.ShowDialog() == true) TxtCustomIntroPath.Text = ofd.FileName;
        }

        private void BtnBrowseCustomOutro_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog { Title = "Outro ფაილი", Filter = "Video|*.mp4;*.mov;*.mkv|All|*.*" };
            if (ofd.ShowDialog() == true) TxtCustomOutroPath.Text = ofd.FileName;
        }

        private void BtnBrowseWm_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog { Title = "Watermark PNG", Filter = "PNG|*.png|All|*.*" };
            if (ofd.ShowDialog() == true) TxtWmPath.Text = ofd.FileName;
        }

        private void SlWmSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        { if (TxtWmSize != null) TxtWmSize.Text = $"{(int)SlWmSize.Value}%"; UpdateBrandPreview(); }

        private void SlWmOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        { if (TxtWmOpacity != null) TxtWmOpacity.Text = $"{(int)SlWmOpacity.Value}%"; UpdateBrandPreview(); }

        private void BtnSaveWmPreset_Click(object sender, RoutedEventArgs e)
        {
            SaveWmPresetPanel.Visibility = Visibility.Visible;
            TxtWmPresetLabel.Focus();
        }

        private void BtnConfirmSaveWmPreset_Click(object sender, RoutedEventArgs e)
        {
            string label = TxtWmPresetLabel.Text.Trim();
            if (string.IsNullOrEmpty(label))
            {
                ShowToast("ჩაწერე პრეზეტის სახელი", "error");
                return;
            }
            var preset = new WmPreset
            {
                Label    = label,
                FilePath = TxtWmPath.Text.Trim(),
                Position = GetWmPosition(),
                Size     = (int)SlWmSize.Value,
                Opacity  = (int)SlWmOpacity.Value
            };
            _wmPresets.RemoveAll(p => p.Label == label);
            _wmPresets.Insert(0, preset);
            SaveWmPresets(); RefreshWmPresetsUI(); RefreshQuickWmChips();
            SaveWmPresetPanel.Visibility = Visibility.Collapsed;
            ShowToast($"ლოგოს პრეზეტი შენახულია: {label}", "success");
        }

        private void BtnCancelSaveWmPreset_Click(object sender, RoutedEventArgs e)
            => SaveWmPresetPanel.Visibility = Visibility.Collapsed;

        private string GetWmPosition()
        {
            if (WmTL.IsChecked == true) return "topleft";
            if (WmBL.IsChecked == true) return "bottomleft";
            if (WmBR.IsChecked == true) return "bottomright";
            return "topright";
        }

        // ================================================================
        // INTRO/OUTRO PRESETS  (Brand Kit)
        // ================================================================
        private void LoadIoPresets()
        {
            try
            {
                string p = Path.Combine(AppPaths.DataDir, IO_FILE);
                if (File.Exists(p))
                    _ioPresets = JsonSerializer.Deserialize<List<IoPreset>>(File.ReadAllText(p)) ?? new();
            }
            catch { _ioPresets = new(); }
        }

        private void SaveIoPresets()
        {
            try
            {
                File.WriteAllText(Path.Combine(AppPaths.DataDir, IO_FILE),
                    JsonSerializer.Serialize(_ioPresets, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private void RefreshIoPresetsUI()
        {
            IoPresetsPanel.Children.Clear();
            TxtNoIoPresets.Visibility = _ioPresets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var p in _ioPresets)
                IoPresetsPanel.Children.Add(BuildIoChip(p));
        }

        private UIElement BuildIoChip(IoPreset p)
            => BuildBrandChip(p.Label, () => ApplyIoPreset(p), () =>
            {
                _ioPresets.Remove(p);
                SaveIoPresets(); RefreshIoPresetsUI(); RefreshQuickIoChips();
            });

        private void ApplyIoPreset(IoPreset p)
        {
            ChkIntroOutro.IsChecked = true;
            IntroOutroFields.Visibility = Visibility.Visible;
            foreach (ComboBoxItem item in CmbStingerTemplate.Items)
                if (item.Content?.ToString() == p.TemplateContent) { CmbStingerTemplate.SelectedItem = item; break; }
            ChkApplyIntro.IsChecked = p.ApplyIntro;
            ChkApplyOutro.IsChecked = p.ApplyOutro;
            SlStingerDuration.Value = Math.Clamp(p.Duration, 1, 5);
            // (older presets saved the old placeholder text as a "path" — only accept real files)
            if (File.Exists(p.CustomIntroPath)) TxtCustomIntroPath.Text = p.CustomIntroPath;
            if (File.Exists(p.CustomOutroPath)) TxtCustomOutroPath.Text = p.CustomOutroPath;
            UpdateBrandPreview();
        }

        private void BtnSaveIoPreset_Click(object sender, RoutedEventArgs e)
        {
            SaveIoPresetPanel.Visibility = Visibility.Visible;
            TxtIoPresetLabel.Focus();
        }

        private void BtnConfirmSaveIoPreset_Click(object sender, RoutedEventArgs e)
        {
            string label = TxtIoPresetLabel.Text.Trim();
            if (string.IsNullOrEmpty(label))
            {
                ShowToast("ჩაწერე პრეზეტის სახელი", "error");
                return;
            }
            var preset = new IoPreset
            {
                Label           = label,
                TemplateContent = (CmbStingerTemplate.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "",
                ApplyIntro      = ChkApplyIntro.IsChecked == true,
                ApplyOutro      = ChkApplyOutro.IsChecked == true,
                Duration        = SlStingerDuration.Value,
                CustomIntroPath = TxtCustomIntroPath.Text.Trim(),
                CustomOutroPath = TxtCustomOutroPath.Text.Trim()
            };
            _ioPresets.RemoveAll(p => p.Label == label);
            _ioPresets.Insert(0, preset);
            SaveIoPresets(); RefreshIoPresetsUI(); RefreshQuickIoChips();
            SaveIoPresetPanel.Visibility = Visibility.Collapsed;
            ShowToast($"Intro/Outro პრეზეტი შენახულია: {label}", "success");
        }

        private void BtnCancelSaveIoPreset_Click(object sender, RoutedEventArgs e)
            => SaveIoPresetPanel.Visibility = Visibility.Collapsed;

        // ================================================================
        // BRAND KIT NAVIGATION
        // ================================================================
        private void BtnGoToBrandKit_Click(object sender, MouseButtonEventArgs e)
            => MainTabControl.SelectedIndex = 4;

        // ================================================================
        // QUICK-APPLY CHIPS — lightweight versions shown in the Crop tab.
        // No search, no delete — just "click to apply this saved preset".
        // Full creation/editing/deletion happens in the Brand Kit tab;
        // these read from the SAME underlying _presets/_wmPresets/_ioPresets
        // lists, so anything saved in Brand Kit shows up here immediately.
        // ================================================================
        private void RefreshQuickLtChips()
        {
            if (QuickLtChips == null) return;
            QuickLtChips.Children.Clear();
            if (TxtNoQuickLt != null) TxtNoQuickLt.Visibility = _presets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var p in _presets) QuickLtChips.Children.Add(BuildQuickChip(p.Label, () => ApplyPreset(p)));
        }

        private void RefreshQuickWmChips()
        {
            if (QuickWmChips == null) return;
            QuickWmChips.Children.Clear();
            if (TxtNoQuickWm != null) TxtNoQuickWm.Visibility = _wmPresets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var p in _wmPresets) QuickWmChips.Children.Add(BuildQuickChip(p.Label, () => ApplyWmPreset(p)));
        }

        private void RefreshQuickIoChips()
        {
            if (QuickIoChips == null) return;
            QuickIoChips.Children.Clear();
            if (TxtNoQuickIo != null) TxtNoQuickIo.Visibility = _ioPresets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var p in _ioPresets) QuickIoChips.Children.Add(BuildQuickChip(p.Label, () => ApplyIoPreset(p)));
        }

        private UIElement BuildQuickChip(string label, Action onApply)
        {
            var chip = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(9),
                Padding         = new Thickness(12, 6, 12, 6),
                Margin          = new Thickness(0, 0, 6, 6),
                Cursor          = Cursors.Hand
            };
            // theme-aware: resource references re-resolve when the theme is switched
            chip.SetResourceReference(Border.BackgroundProperty,  "BgInput");
            chip.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");
            var text = new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.Medium };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            chip.Child = text;
            chip.MouseEnter += (_, __) =>
            {
                chip.SetResourceReference(Border.BackgroundProperty,  "AccentSoft");
                chip.SetResourceReference(Border.BorderBrushProperty, "AccentBorder");
            };
            chip.MouseLeave += (_, __) =>
            {
                chip.SetResourceReference(Border.BackgroundProperty,  "BgInput");
                chip.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");
            };
            chip.MouseLeftButtonDown += (_, __) =>
            {
                onApply();
                ShowToast($"პრესეტი გააქტიურდა: {label}");
            };
            return chip;
        }

        // ================================================================
        // MEDIA LIBRARY — aggregates Job History (exported) + Download
        // History (downloaded) into one searchable, filterable archive.
        // ================================================================
        private List<MediaLibraryItem> BuildMediaLibrary()
        {
            var items = new List<MediaLibraryItem>();

            foreach (var j in _history.Where(j => File.Exists(j.OutputPath)))
                items.Add(new MediaLibraryItem
                {
                    FilePath = j.OutputPath, Title = j.FileName,
                    Source = j.TemplateId == "virtualbg" ? "AI ფონი"
                           : j.TemplateId == "faceblur" ? "სახის დაბუნდოვნება"
                           : j.TemplateId == "voiceover" ? "ხმის ჩაწერა" : "დამუშავებული",
                    When = j.ProcessedAt
                });

            foreach (var (title, path, when) in TheDownloaderView.GetDownloadHistory())
                if (File.Exists(path))
                    items.Add(new MediaLibraryItem
                    {
                        FilePath = path, Title = title,
                        Source = "ჩამოტვირთული", When = when
                    });

            return items.OrderByDescending(i => i.When).ToList();
        }

        public void RefreshMediaLibraryUI() => TheMediaLibrary?.Refresh();

        private void WireVoiceOver()
        {
            TheVoiceOver.CacheFolder = CacheDir;
            TheVoiceOver.Notify += (msg, kind) => ShowToast(msg, kind);
            TheVoiceOver.Processed += (input, output) =>
            {
                AddToHistory(output, "", "voiceover");
                RefreshMediaLibraryUI();
            };
            TheVoiceOver.OpenInAB += (orig, result) =>
            {
                TheABMonitor.LoadPair(orig, result);
                MainTabControl.SelectedIndex = 7;
            };
        }

        private void WireAiBackground()
        {
            TheAiBg.CacheFolder = CacheDir;
            TheAiBg.Notify += (msg, kind) => ShowToast(msg, kind);
            TheAiBg.Processed += (input, output) => AddToHistory(output, "", "virtualbg");
            TheAiBg.OpenInAB += (orig, result) =>
            {
                TheABMonitor.LoadPair(orig, result);
                MainTabControl.SelectedIndex = 7;     // A/B მონიტორი
            };
        }
        private void WireFaceBlur()
        {
            TheFaceBlur.CacheFolder = CacheDir;
            TheFaceBlur.Notify += (msg, kind) => ShowToast(msg, kind);
            TheFaceBlur.Processed += (input, output) =>
            {
                AddToHistory(output, "", "faceblur");
                RefreshMediaLibraryUI();
            };
            TheFaceBlur.OpenInAB += (orig, result) =>
            {
                TheABMonitor.LoadPair(orig, result);
                MainTabControl.SelectedIndex = 7;
            };
        }

        private void WireMediaLibrary()
        {
            TheMediaLibrary.Provider = BuildMediaLibrary;
            TheMediaLibrary.CacheFolder = CacheDir;
            TheMediaLibrary.FoldersProvider = () => new[]
            {
                (TheDownloaderView.CurrentDownloadFolder, "ჩამოტვირთული"),
                (TheStreamClipper.CurrentClipFolder,      "ლაივ კლიპი")
            };
            TheMediaLibrary.OpenInEditor += p =>
            {
                TheEditorView.CacheFolder = CacheDir;
                TheEditorView.LoadVideo(p);
                MainTabControl.SelectedIndex = 1;
            };
            TheMediaLibrary.OpenInCrop += OnDownloaderSendToCrop;
            TheMediaLibrary.OpenInVoiceOver += p =>
            {
                TheVoiceOver.LoadVideo(p);
                MainTabControl.SelectedIndex = 10;
            };
            TheMediaLibrary.OpenInFaceBlur += p =>
            {
                TheFaceBlur.LoadVideo(p);
                MainTabControl.SelectedIndex = 9;
            };
            TheMediaLibrary.OpenInAB += (p, asA) =>
            {
                if (asA) TheABMonitor.LoadA(p); else TheABMonitor.LoadB(p);
                MainTabControl.SelectedIndex = 7;     // A/B მონიტორი
            };
            TheMediaLibrary.FileDeleted += p =>
            {
                _history.RemoveAll(j => string.Equals(j.OutputPath, p, StringComparison.OrdinalIgnoreCase));
                SaveHistory(); RefreshHistoryUI();
            };
        }

        // ================================================================
        // JOB HISTORY
        // ================================================================
        private void LoadHistory()
        {
            try
            {
                string p = Path.Combine(AppPaths.DataDir, HISTORY_FILE);
                if (File.Exists(p))
                    _history = JsonSerializer.Deserialize<List<JobEntry>>(File.ReadAllText(p)) ?? new();
            }
            catch { _history = new(); }
        }

        private void SaveHistory()
        {
            try
            {
                File.WriteAllText(Path.Combine(AppPaths.DataDir, HISTORY_FILE),
                    JsonSerializer.Serialize(_history, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private void AddToHistory(string outputPath, string ltName, string tmplId)
        {
            _history.RemoveAll(j => j.OutputPath == outputPath);
            _history.Insert(0, new JobEntry
            {
                OutputPath  = outputPath,
                FileName    = Path.GetFileName(outputPath),
                LtName      = ltName,
                TemplateId  = tmplId,
                ProcessedAt = DateTime.Now
            });
            if (_history.Count > 300) _history = _history.Take(300).ToList();
            SaveHistory(); RefreshHistoryUI(); RefreshMediaLibraryUI();
        }

        private void RefreshHistoryUI()
        {
            JobHistoryPanel.Children.Clear();
            var recent = _history.Where(j => File.Exists(j.OutputPath)).Take(8).ToList();
            TxtNoJobs.Visibility = Visibility.Collapsed;
            if (JobsCard != null) JobsCard.Visibility = recent.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            foreach (var job in recent)
            {
                var row = new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Padding      = new Thickness(10, 8, 8, 8),
                    Margin       = new Thickness(0, 0, 0, 6)
                };
                row.SetResourceReference(Border.BackgroundProperty, "BgInput");

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // success badge
                var badge = new Border
                {
                    Width = 30, Height = 30, CornerRadius = new CornerRadius(15),
                    Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center
                };
                badge.SetResourceReference(Border.BackgroundProperty, "SuccessSoft");
                var check = new TextBlock
                {
                    Text = "\uE73E", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                };
                check.SetResourceReference(TextBlock.ForegroundProperty, "Success");
                badge.Child = check;
                Grid.SetColumn(badge, 0);

                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var name = new TextBlock
                {
                    Text = job.FileName, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
                string dateStr = job.ProcessedAt.Date == DateTime.Today
                    ? $"დღეს {job.ProcessedAt:HH:mm}"
                    : job.ProcessedAt.Date == DateTime.Today.AddDays(-1)
                        ? $"გუშინ {job.ProcessedAt:HH:mm}"
                        : job.ProcessedAt.ToString("dd/MM HH:mm");
                string extra = string.IsNullOrEmpty(job.LtName) ? "" : $" · {job.LtName}";
                var sub = new TextBlock
                {
                    Text = dateStr + extra, FontSize = 11, Margin = new Thickness(0, 2, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                sub.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
                info.Children.Add(name); info.Children.Add(sub);
                Grid.SetColumn(info, 1);

                var openBtn = new Button
                {
                    Content = "\uE838", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14,
                    Padding = new Thickness(10, 7, 10, 7), Tag = job.OutputPath, ToolTip = "საქაღალდის გახსნა",
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0)
                };
                openBtn.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");
                openBtn.Click += (s, _) =>
                {
                    string? dir = Path.GetDirectoryName((string)((Button)s).Tag);
                    if (dir != null && Directory.Exists(dir)) Process.Start("explorer.exe", dir);
                };
                Grid.SetColumn(openBtn, 2);

                grid.Children.Add(badge); grid.Children.Add(info); grid.Children.Add(openBtn);
                row.Child = grid;
                JobHistoryPanel.Children.Add(row);
            }
        }
        private void BtnClearHistory_Click(object sender, RoutedEventArgs e)
        {
            _history.Clear(); SaveHistory(); RefreshHistoryUI(); RefreshMediaLibraryUI();
        }

        // Clears the displayed video library list (visual declutter only —
        // does not delete any files, and does not forget the selected folder;
        // re-select the folder or it repopulates next time it's refreshed).
        private void BtnClearLibrary_Click(object sender, RoutedEventArgs e)
        {
            VideoListBox.Items.Clear();
        }

        // ================================================================
        // CUSTOM TITLE BAR — minimize / maximize-restore / close / drag
        // ================================================================
        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                // Double-click title bar toggles maximize/restore
                BtnWinMaximize_Click(sender, new RoutedEventArgs());
                return;
            }
            DragMove();
        }

        private void BtnWinMinimize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void BtnWinMaximize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal : WindowState.Maximized;
            if (FindName("BtnWinMaximize") is Button btn)
                btn.Content = WindowState == WindowState.Maximized ? "" : "";
        }

        private void BtnWinClose_Click(object sender, RoutedEventArgs e) => Close();

        // ================================================================
        // SETTINGS
        // ================================================================
        private async void BtnSaveGlobalFolder_Click(object sender, RoutedEventArgs e)
        {
            var fd = new OpenFolderDialog { Title = "Default Workspace" };
            if (fd.ShowDialog() != true) return;
            selectedFolderPath = fd.FolderName;
            TxtFolderPath.Text = selectedFolderPath;
            SaveConfig();
            RefreshSettingsUI();
            LibraryModePanel.Visibility   = Visibility.Visible;
            EditorPreviewPanel.Visibility = Visibility.Collapsed;
            await RefreshVideoListWithThumbnails();
            MainTabControl.SelectedIndex = 0;
        }

        private void BtnSetCacheFolder_Click(object sender, RoutedEventArgs e)
        {
            var fd = new OpenFolderDialog { Title = "Cache Folder" };
            if (fd.ShowDialog() != true) return;
            _cacheFolder = fd.FolderName;
            SaveConfig();
            TheEditorView.CacheFolder = CacheDir;
            RefreshSettingsUI();
        }

        private void BtnClearCache_Click(object sender, RoutedEventArgs e)
        {
            int deleted = 0;
            void TryDel(string dir)
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.GetFiles(dir))
                    try { File.Delete(f); deleted++; } catch { }
            }
            TryDel(CacheDir);
            if (!string.IsNullOrEmpty(selectedFolderPath))
                TryDel(Path.Combine(selectedFolderPath, ".flow_cache"));
            ShowToast($"ქეში გასუფთავდა — {deleted} ფაილი წაიშალა");
            RefreshSettingsUI();
        }

        // ================================================================
        // TOAST NOTIFICATIONS — lightweight, auto-dismissing, for
        // "task completed" type events (replaces blocking MessageBox popups)
        // ================================================================
        public void ShowToast(string message, string kind = "success")
        {
            // theme-aware: colours come from the active theme, so the toast matches dark and light
            (string soft, string strong, string glyph) = kind switch
            {
                "error" => ("DangerSoft",  "Danger",  ""),
                "info"  => ("InfoSoft",    "Info",    ""),
                _       => ("SuccessSoft", "Success", ""),
            };

            var card = new Border
            {
                CornerRadius    = new CornerRadius(14),
                BorderThickness = new Thickness(1),
                Margin          = new Thickness(0, 8, 0, 0),
                MinWidth        = 240,
                MaxWidth        = 400,
                ClipToBounds    = true,
                Opacity         = 0,
                RenderTransform = new TranslateTransform(40, 0),
                Effect          = new System.Windows.Media.Effects.DropShadowEffect
                                  { Color = Colors.Black, BlurRadius = 24, ShadowDepth = 6, Opacity = 0.35, Direction = 270 }
            };
            card.SetResourceReference(Border.BackgroundProperty, "BgPanel");
            card.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var row = new Grid { Margin = new Thickness(14, 12, 18, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var badge = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(15),
                Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center
            };
            badge.SetResourceReference(Border.BackgroundProperty, soft);
            var icon = new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            icon.SetResourceReference(TextBlock.ForegroundProperty, strong);
            badge.Child = icon;
            row.Children.Add(badge);

            var text = new TextBlock
            {
                Text = message, FontSize = 13, FontWeight = FontWeights.Medium,
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            root.Children.Add(row);

            // thin timer line along the bottom edge
            var timer = new Border { Height = 3, HorizontalAlignment = HorizontalAlignment.Left, Width = 400 };
            timer.SetResourceReference(Border.BackgroundProperty, strong);
            timer.Opacity = 0.75;
            Grid.SetRow(timer, 1);
            root.Children.Add(timer);

            card.Child = root;
            ToastHost.Children.Add(card);
            card.UpdateLayout();
            card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double fullWidth = Math.Max(card.DesiredSize.Width, 240);

            var slideIn = new DoubleAnimation(40, 0, TimeSpan.FromMilliseconds(240))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220));
            card.RenderTransform.BeginAnimation(TranslateTransform.XProperty, slideIn);
            card.BeginAnimation(UIElement.OpacityProperty, fadeIn);

            const double life = 3.4;
            timer.Width = fullWidth;
            timer.BeginAnimation(FrameworkElement.WidthProperty,
                new DoubleAnimation(fullWidth, 0, TimeSpan.FromSeconds(life)));

            var dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(life) };
            dismissTimer.Tick += (_, _) =>
            {
                dismissTimer.Stop();
                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260));
                fadeOut.Completed += (_, _) => ToastHost.Children.Remove(card);
                card.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                card.RenderTransform.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(0, 30, TimeSpan.FromMilliseconds(260)));
            };
            dismissTimer.Start();
        }

        // ================================================================
        // yt-dlp UPDATER
        // ================================================================
        private async Task RefreshYtDlpVersionLabel()
        {
            string ver = await TheDownloaderView.GetYtDlpVersionAsync();
            TxtYtDlpVersion.Text = string.IsNullOrEmpty(ver) ? "ვერ მოიძებნა" : ver;
            DotYtDlp?.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, string.IsNullOrEmpty(ver) ? "Danger" : "Success");
        }

        private async void BtnUpdateYtDlp_Click(object sender, RoutedEventArgs e)
        {
            BtnUpdateYtDlp.IsEnabled = false;
            TxtYtDlpStatus.Visibility = Visibility.Visible;

            // not installed yet → download it (same flow as first run)
            if (!await ToolManager.Get(ToolManager.YtDlpId).IsInstalledAsync())
            {
                TxtYtDlpStatus.Text = "yt-dlp მიიტვირთება…";
                var prog = new Progress<ToolProgress>(p => TxtYtDlpStatus.Text = p.Text);
                var (okI, msgI) = await ToolManager.Get(ToolManager.YtDlpId).InstallAsync(prog, System.Threading.CancellationToken.None);
                BtnUpdateYtDlp.IsEnabled = true;
                TxtYtDlpStatus.Text = msgI;
                ShowToast(msgI, okI ? "success" : "error");
                await RefreshYtDlpVersionLabel();
                return;
            }
            TxtYtDlpStatus.Text = "მოწმდება განახლებები…";

            var (ok, msg) = await TheDownloaderView.UpdateYtDlpAsync(
                line => Dispatcher.Invoke(() => TxtYtDlpStatus.Text = line));

            BtnUpdateYtDlp.IsEnabled = true;
            TxtYtDlpStatus.Text = msg;
            ShowToast(ok ? "yt-dlp განახლდა წარმატებით" : "yt-dlp განახლება ვერ შედგა",
                      ok ? "success" : "error");
            await RefreshYtDlpVersionLabel();
        }

        // ================================================================
        // CROP TAB
        // ================================================================
        private void OnEditorClipReadyForCrop(string clipPath, string thumbPath, TimeSpan duration)
        {
            selectedVideoPath = clipPath;
            BtnProcess.Visibility = BtnPreview.Visibility = Visibility.Visible;
            ShowEditorPreview(clipPath, thumbPath, duration);
            _ = RefreshFacePreviewAsync(clipPath);
            MainTabControl.SelectedIndex = 0;
            // Update EditorView's CacheFolder in case config changed
            TheEditorView.CacheFolder = CacheDir;
        }

        // ================================================================
        // DOWNLOADER TAB — send a downloaded file to Editor or Crop
        // ================================================================
        private void OnDownloaderSendToEditor(string filePath)
        {
            if (!File.Exists(filePath)) return;
            TheEditorView.CacheFolder = CacheDir;
            TheEditorView.LoadVideo(filePath);
            MainTabControl.SelectedIndex = 1;
        }

        private async void OnDownloaderSendToCrop(string filePath)
        {
            if (!File.Exists(filePath)) return;
            selectedVideoPath = filePath;
            BtnProcess.Visibility = BtnPreview.Visibility = Visibility.Visible;
            LibraryModePanel.Visibility   = Visibility.Collapsed;
            EditorPreviewPanel.Visibility = Visibility.Visible;
            // Reuse the same preview pathway as the editor hand-off; the real
            // thumbnail/duration are filled in a moment later by LoadRealPreviewAsync.
            ShowEditorPreview(filePath, "", TimeSpan.Zero);
            MainTabControl.SelectedIndex = 0;
            await LoadRealPreviewAsync(filePath);
        }

        // ════════════════════════════════════════════════════════════
        // DRAG & DROP — drop a video file onto the Crop tab to select it
        // ════════════════════════════════════════════════════════════
        private static readonly string[] CropVideoExts = { ".mp4", ".mov", ".mxf", ".avi", ".mkv" };

        private static bool IsDroppedVideoFile(DragEventArgs e, out string path)
        {
            path = "";
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return false;
            string p = files[0];
            if (!CropVideoExts.Contains(Path.GetExtension(p).ToLowerInvariant())) return false;
            path = p;
            return true;
        }

        private void CropArea_DragEnter(object sender, DragEventArgs e)
        {
            if (IsDroppedVideoFile(e, out _))
            {
                CropDragOverlay.Visibility = Visibility.Visible;
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void CropArea_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = IsDroppedVideoFile(e, out _) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void CropArea_DragLeave(object sender, DragEventArgs e)
        {
            CropDragOverlay.Visibility = Visibility.Collapsed;
        }

        private async void CropArea_Drop(object sender, DragEventArgs e)
        {
            CropDragOverlay.Visibility = Visibility.Collapsed;
            if (IsDroppedVideoFile(e, out string path))
            {
                // Same hand-off used when a downloaded clip is sent to Crop —
                // sets selectedVideoPath and switches to the editor-preview layout.
                selectedVideoPath = path;
                BtnProcess.Visibility = BtnPreview.Visibility = Visibility.Visible;
                LibraryModePanel.Visibility   = Visibility.Collapsed;
                EditorPreviewPanel.Visibility = Visibility.Visible;
                ShowEditorPreview(path, "", TimeSpan.Zero);
                await LoadRealPreviewAsync(path);
            }
            e.Handled = true;
        }

        private async void BtnChooseFolder_Click(object sender, RoutedEventArgs e)
        {
            var fd = new OpenFolderDialog { Title = "Select Workspace" };
            if (fd.ShowDialog() != true) return;
            selectedFolderPath = fd.FolderName;
            TxtFolderPath.Text = selectedFolderPath;
            LibraryModePanel.Visibility   = Visibility.Visible;
            EditorPreviewPanel.Visibility = Visibility.Collapsed;
            await RefreshVideoListWithThumbnails();
        }

        private async Task RefreshVideoListWithThumbnails()
        {
            VideoListBox.Items.Clear();
            if (!Directory.Exists(selectedFolderPath)) return;
            StatusLabel.Text = "ვიდეოები იძებნება…";
            BtnProcess.Visibility = BtnPreview.Visibility = Visibility.Collapsed;

            string cacheDir = CacheDir;
            Directory.CreateDirectory(cacheDir);

            string scriptPath = GetTrackerPath(BaseDir);
            var si = new ProcessStartInfo
            {
                FileName  = "python",
                Arguments = $"\"{scriptPath}\" \"--thumbnails\" \"{selectedFolderPath}\" \"{cacheDir}\"",
                UseShellExecute = false, CreateNoWindow = true
            };
            si.Environment["PYTHONUTF8"]       = "1";
            si.Environment["PYTHONIOENCODING"] = "utf-8";
            await Task.Run(() => { try { using var p = Process.Start(si); p?.WaitForExit(); } catch { } });

            var items = new List<VideoItem>();
            foreach (string file in Directory.GetFiles(selectedFolderPath, "*.*", SearchOption.TopDirectoryOnly))
            {
                string ext = Path.GetExtension(file).ToLower();
                if (ext is not (".mp4" or ".mov" or ".mxf")) continue;
                string fn    = Path.GetFileName(file);
                string thumb = Path.Combine(cacheDir, fn + ".jpg");
                items.Add(new VideoItem
                {
                    FileName           = fn,
                    FullPath           = file,
                    ThumbnailPath      = File.Exists(thumb) ? thumb : "",
                    FileExtensionBadge = ext.TrimStart('.').ToUpper(),
                    FileMeta           = DescribeFile(file)
                });
            }
            foreach (var item in items) VideoListBox.Items.Add(item);
            StatusLabel.Text = items.Count == 0 ? "ვიდეო ვერ მოიძებნა" : $"ნაპოვნია {items.Count} ვიდეო";
        }

        private void VideoListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (VideoListBox.SelectedItem is VideoItem item)
            {
                selectedVideoPath = item.FullPath;
                UpdateSelectedInfo(item.FullPath);
                StatusLabel.Text  = "მზად არის სამუშაოდ";
                BtnProcess.Visibility = BtnPreview.Visibility = Visibility.Visible;
            }
        }

        // "42.3 MB · 12 ოქტ"
        private static string DescribeFile(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                double mb = fi.Length / 1048576.0;
                string size = mb >= 1024 ? $"{mb / 1024:F1} GB" : $"{mb:F1} MB";
                string[] months = { "იან", "თებ", "მარ", "აპრ", "მაი", "ივნ", "ივლ", "აგვ", "სექ", "ოქტ", "ნოე", "დეკ" };
                var d = fi.LastWriteTime;
                return $"{size} · {d.Day} {months[d.Month - 1]}";
            }
            catch { return ""; }
        }

        // Bottom action bar: which file is about to be processed
        private void UpdateSelectedInfo(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                TxtSelName.Text = "ვიდეო არ არის არჩეული";
                TxtSelMeta.Text = "აირჩიე ვიდეო სიიდან ან გადმოიტანე აქ";
                return;
            }
            TxtSelName.Text = Path.GetFileName(path);
            TxtSelMeta.Text = DescribeFile(path);
        }

        private string BuildAllArgs(string outputPath, string speedMode, int previewSec = 0)
        {
            string ltArgs = BuildLtArgs();
            string wmArgs = BuildWmArgs();
            return $"\"{selectedVideoPath}\" \"{speedMode}\" \"{outputPath}\"{ltArgs}{wmArgs} \"{previewSec}\"";
        }

        private async void BtnProcess_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(selectedVideoPath) || !File.Exists(selectedVideoPath)) return;

            string baseName  = Path.GetFileNameWithoutExtension(selectedVideoPath);
            string sourceDir = Path.GetDirectoryName(selectedVideoPath) ?? "";
            var exportDlg = new ExportDialog(baseName + "_vertical", sourceDir) { Owner = this };
            if (exportDlg.ShowDialog() != true) return;

            string finalName = exportDlg.ExportFileName;
            if (!finalName.ToLower().EndsWith(".mp4")) finalName += ".mp4";
            string outputPath = Path.Combine(exportDlg.ExportFolder, finalName);

            string speed = RbAccurate.IsChecked == true ? "accurate"
                         : RbUltraFast.IsChecked == true ? "ultrafast" : "fast";

            bool useStinger = ChkIntroOutro.IsChecked == true
                && (ChkApplyIntro.IsChecked == true || ChkApplyOutro.IsChecked == true);

            string mainTarget = useStinger
                ? Path.Combine(CacheDir, $"main_{DateTime.Now:HHmmss}.mp4")
                : outputPath;
            if (useStinger) Directory.CreateDirectory(CacheDir);

            SetProcessingUI(true);
            await RunFFmpegProcess(GetTrackerPath(BaseDir), BuildAllArgs(mainTarget, speed), mainTarget, false);

            if (useStinger)
            {
                if (File.Exists(mainTarget))
                {
                    SetProcessingUI(true);
                    StatusLabel.Text = "Intro/Outro-ს დამატება... 🎬";
                    bool ok = await ApplyIntroOutroAsync(mainTarget, outputPath);
                    if (!ok)
                    {
                        ShowToast("Intro/Outro ვერ დაემატა — ძირითადი ვერსია შენახულია.", "error");
                        try { File.Copy(mainTarget, outputPath, true); } catch { }
                    }
                    try { File.Delete(mainTarget); } catch { }
                }
                SetProcessingUI(false);
            }

            if (File.Exists(outputPath))
            {
                AddToHistory(outputPath, GetLTFieldValue(TxtLTName), GetSelectedTemplate());
                RefreshMediaLibraryUI();
                ShowToast($"✅ {Path.GetFileName(outputPath)}");
                await Task.Delay(600);
                try { Process.Start(new ProcessStartInfo("cmd.exe",
                    $"/c start \"\" \"{outputPath}\"") { UseShellExecute = false, CreateNoWindow = true }); } catch { }
            }
            else if (!StatusLabel.Text.Contains(StoppedText) && !StatusLabel.Text.Contains(ErrorText))
                ShowToast("დამუშავება ვერ შედგა.", "error");
        }

        private async void BtnPreview_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(selectedVideoPath) || !File.Exists(selectedVideoPath)) return;
            Directory.CreateDirectory(CacheDir);
            string previewOut = Path.Combine(CacheDir, $"preview_{DateTime.Now:HHmmss}.mp4");
            StatusLabel.Text = "5 წმ გადახედვა მზადდება…";
            SetProcessingUI(true);
            await RunFFmpegProcess(GetTrackerPath(BaseDir), BuildAllArgs(previewOut, "fast", 5), previewOut, false);
            if (File.Exists(previewOut))
                try { Process.Start(new ProcessStartInfo("cmd.exe",
                    $"/c start \"\" \"{previewOut}\"") { UseShellExecute = false, CreateNoWindow = true }); } catch { }
        }

        // Speed choice (0 = accurate, 1 = fast, 2 = ultrafast): updates the hint and the preview summary.
        // BtnProcess_Click reads the radio buttons directly.
        private void SpeedRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (TxtSpeedHint == null || TxtSpeedValue == null) return;   // fires during InitializeComponent
            bool accurate = RbAccurate?.IsChecked == true;
            bool ultra    = RbUltraFast?.IsChecked == true;
            TxtSpeedValue.Text = accurate ? "ნელი" : ultra ? "ნიუსრუმი" : "ბალანსი";
            TxtSpeedHint.Text  = accurate ? "მაქსიმალური სიზუსტე — მეტ დროს მოითხოვს"
                               : ultra    ? "ყველაზე სწრაფი — სასწრაფო მასალისთვის"
                               :            "ოპტიმალური ბალანსი — რეკომენდებულია";
        }
        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            if (currentRunningProcess != null && !currentRunningProcess.HasExited)
                try { currentRunningProcess.Kill(true); StatusLabel.Text = StoppedText; } catch { }
            SetProcessingUI(false);
        }

        private void SetProcessingUI(bool processing)
        {
            BtnProcess.Visibility  = processing ? Visibility.Collapsed : Visibility.Visible;
            BtnPreview.Visibility  = processing ? Visibility.Collapsed : Visibility.Visible;
            BtnStop.Visibility     = processing ? Visibility.Visible   : Visibility.Collapsed;
            VideoListBox.IsEnabled = !processing;
            MainProgressBar.Visibility = processing ? Visibility.Visible : Visibility.Collapsed;
            if (!processing) { MainProgressBar.Value = 0; currentRunningProcess = null; }
        }

        private async Task RunFFmpegProcess(string scriptPath, string args, string outputPath, bool isEditor)
        {
            if (!File.Exists(scriptPath))
            {
                Dispatcher.Invoke(() => { StatusLabel.Text = $"tracker.py ვერ მოიძებნა: {scriptPath}"; SetProcessingUI(false); });
                return;
            }

            var si = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = $"\"{scriptPath}\" {args}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8
            };
            si.Environment["PYTHONUTF8"]       = "1";
            si.Environment["PYTHONIOENCODING"] = "utf-8";

            var errBuf = new System.Text.StringBuilder();

            await Task.Run(() =>
            {
                try
                {
                    // Local copy: BtnStop → SetProcessingUI(false) nulls the field
                    // while we're still waiting, which used to throw NRE on ExitCode.
                    var proc = new Process { StartInfo = si };
                    currentRunningProcess = proc;
                    using (proc)
                    {
                        // ── Drain BOTH streams async to prevent pipe-buffer deadlock ──
                        proc.OutputDataReceived += (_, ev) =>
                        {
                            if (ev.Data == null) return;
                            string line = ev.Data;
                            if (line.StartsWith("[PROGRESS]") &&
                                double.TryParse(line.Replace("[PROGRESS]", "").Trim(), out double pct))
                                Dispatcher.Invoke(() =>
                                {
                                    MainProgressBar.Value = pct;
                                    StatusLabel.Text = $"მუშავდება: {pct:F0}%";
                                });
                            else if (line.StartsWith("[STATUS]"))
                                Dispatcher.Invoke(() =>
                                    StatusLabel.Text = line.Replace("[STATUS]", "").Trim());
                        };
                        proc.ErrorDataReceived += (_, ev) =>
                        {
                            if (ev.Data != null) errBuf.AppendLine(ev.Data);
                        };

                        proc.Start();
                        proc.BeginOutputReadLine();
                        proc.BeginErrorReadLine();
                        proc.WaitForExit();

                        int exitCode = proc.ExitCode;
                        Dispatcher.Invoke(() =>
                        {
                            // Consider it a success if the output file was created,
                            // regardless of exit code — MediaPipe/TFLite internal
                            // messages (clearcut uploader, XNNPACK delegate) often
                            // cause non-zero exit codes even when processing succeeds.
                            bool outputExists = File.Exists(outputPath);
                            bool success = exitCode == 0 || outputExists;

                            if (success)
                            {
                                MainProgressBar.Value = 100;
                                StatusLabel.Text = "დასრულდა!";
                                if (!isEditor) _ = RefreshVideoListWithThumbnails();
                            }
                            else if (!StatusLabel.Text.Contains(StoppedText))
                            {
                                StatusLabel.Text = ErrorText;
                                // Filter out known MediaPipe/TFLite noise before showing
                                string cleanErr = FilterMediaPipeNoise(errBuf.ToString());
                                if (!string.IsNullOrEmpty(cleanErr))
                                    MessageBox.Show(cleanErr, "Processing Error",
                                        MessageBoxButton.OK, MessageBoxImage.Error);
                            }
                            SetProcessingUI(false);
                        });
                    }
                }
                catch (System.ComponentModel.Win32Exception winEx)
                {
                    Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show(
                            $"'python' ვერ გაეშვა: {winEx.Message}\n\nშეამოწმეთ Python PATH-შია.",
                            "Python Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        SetProcessingUI(false);
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => { MessageBox.Show(ex.Message, "Error"); SetProcessingUI(false); });
                }
            });
        }

        private string GetTrackerPath(string baseDir)
        {
            string p = Path.Combine(baseDir, "tracker.py");
            if (!File.Exists(p))
                p = Path.Combine(Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\")), "tracker.py");
            return p;
        }

        /// <summary>
        /// Strips known MediaPipe / TensorFlow Lite internal log lines from stderr
        /// so they don't pollute user-facing error dialogs.
        /// These messages (XNNPACK delegate, clearcut telemetry, feedback tensors)
        /// appear even when processing completes successfully.
        /// </summary>
        private static string FilterMediaPipeNoise(string stderr)
        {
            if (string.IsNullOrEmpty(stderr)) return "";
            var clean = stderr
                .Split('\n')
                .Where(l =>
                {
                    string t = l.TrimStart();
                    return !t.StartsWith("INFO:")          // TFLite info
                        && !t.StartsWith("W0000")          // MediaPipe warnings
                        && !t.StartsWith("E0000")          // MediaPipe "errors" (clearcut etc.)
                        && !t.StartsWith("===")            // MediaPipe stack trace separator
                        && !t.Contains("clearcut")         // Google telemetry
                        && !t.Contains("XNNPACK")          // TFLite delegate
                        && !t.Contains("TensorFlow Lite")
                        && !t.Contains("feedback_tensors")
                        && !t.Contains("wireless/android") // Android path in stack trace
                        && !t.Contains("FAILED_PRECONDITION: Not valid for uploading");
                })
                .ToArray();
            return string.Join('\n', clean).Trim();
        }

        // python <script> <args...> — ArgumentList handles quoting (Georgian paths, spaces)
        private static ProcessStartInfo MakePython(string script, params string[] args)
        {
            var si = new ProcessStartInfo
            {
                FileName = "python", UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            if (!string.IsNullOrEmpty(script)) si.ArgumentList.Add(script);
            foreach (var a in args) si.ArgumentList.Add(a);
            si.Environment["PYTHONUTF8"] = "1";
            si.Environment["PYTHONIOENCODING"] = "utf-8";
            return si;
        }
        // ================================================================
        // LOWER THIRD HELPERS
        // ================================================================
        private string BuildLtArgs()
        {
            if (ChkLowerThird.IsChecked != true) return " \"\" \"\" \"1tv\" \"0\" \"\"";
            string ltName  = GetLTFieldValue(TxtLTName);
            string ltDesc  = GetLTFieldValue(TxtLTDesc);
            string tmpl    = GetSelectedTemplate();
            bool isCustom  = TmplCustom.IsChecked == true;
            bool hasText   = !string.IsNullOrWhiteSpace(ltName);
            if (!hasText && !isCustom) return " \"\" \"\" \"1tv\" \"0\" \"\"";
            string dur = _durFull ? "0"
                : ((int)SlDuration.Value).ToString(CultureInfo.InvariantCulture);
            string ov = isCustom ? TxtOverlayPath.Text.Trim() : "";
            return $" \"{ltName}\" \"{ltDesc}\" \"{tmpl}\" \"{dur}\" \"{ov}\"";
        }

        private string BuildWmArgs()
        {
            if (ChkWatermark.IsChecked != true) return " \"\" \"topright\" \"0.15\" \"1.0\"";
            string wmPath = TxtWmPath.Text.Trim();
            if (string.IsNullOrEmpty(wmPath) || !File.Exists(wmPath))
                return " \"\" \"topright\" \"0.15\" \"1.0\"";
            string pos = GetWmPosition();
            string sz  = (SlWmSize.Value / 100.0).ToString("F2", CultureInfo.InvariantCulture);
            string op  = (SlWmOpacity.Value / 100.0).ToString("F2", CultureInfo.InvariantCulture);
            return $" \"{wmPath}\" \"{pos}\" \"{sz}\" \"{op}\"";
        }

        private string GetSelectedTemplate()
        {
            if (TmplDark.IsChecked    == true) return "dark";
            if (TmplRed.IsChecked     == true) return "red";
            if (TmplMinimal.IsChecked == true) return "minimal";
            if (TmplCustom.IsChecked  == true) return "custom";
            return "1tv";
        }

        private void ChkLowerThird_Changed(object sender, RoutedEventArgs e)
        {
            if (LowerThirdFields == null) return;
            LowerThirdFields.Visibility = ChkLowerThird.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Template_Changed(object sender, RoutedEventArgs e)
        {
            if (CustomOverlayPanel == null) return;
            CustomOverlayPanel.Visibility =
                TmplCustom.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            UpdateBrandPreview();
        }

        private void BtnBrowseOverlay_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog { Title = "Custom Overlay", Filter = "PNG|*.png|All|*.*" };
            if (ofd.ShowDialog() == true) TxtOverlayPath.Text = ofd.FileName;
        }

        private void DurMode_Changed(object sender, RoutedEventArgs e)
            => SetDurationMode(RbDurFull.IsChecked == true);

        private void SetDurationMode(bool full)
        {
            _durFull = full;
            if (RbDurFull != null)   RbDurFull.IsChecked   = full;
            if (RbDurCustom != null) RbDurCustom.IsChecked = !full;
            if (DurationSliderPanel != null)
                DurationSliderPanel.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
        }

        private void SlDuration_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        { if (TxtDurValue != null) TxtDurValue.Text = $"{(int)SlDuration.Value} წმ"; }

        // the hint text lives in the TextBox style (local:Ui.Hint), so the field value is just its text
        private string GetLTFieldValue(TextBox tb) => tb.Text.Trim();

        // ================================================================
        // EDITOR PREVIEW MODE
        // ================================================================
        private void ShowEditorPreview(string clipPath, string thumbPath, TimeSpan duration)
        {
            PreviewClipName.Text = Path.GetFileName(clipPath);
            PreviewDuration.Text = FormatTime(duration);
            UpdateSelectedInfo(clipPath);
            if (!string.IsNullOrEmpty(thumbPath) && File.Exists(thumbPath))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource   = new Uri(thumbPath, UriKind.Absolute);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit(); bmp.Freeze();
                    PreviewThumbnail.Source      = bmp;
                    PreviewPlaceholder.Visibility = Visibility.Collapsed;
                }
                catch
                {
                    PreviewThumbnail.Source      = null;
                    PreviewPlaceholder.Visibility = Visibility.Visible;
                }
            }
            else
            {
                PreviewThumbnail.Source      = null;
                PreviewPlaceholder.Visibility = Visibility.Visible;
            }
            LibraryModePanel.Visibility   = Visibility.Collapsed;
            EditorPreviewPanel.Visibility = Visibility.Visible;
            BtnProcess.Visibility = BtnPreview.Visibility = Visibility.Visible;
            BtnStop.Visibility        = Visibility.Collapsed;
            MainProgressBar.Visibility = Visibility.Collapsed;
            MainProgressBar.Value      = 0;
            StatusLabel.Text = "კლიპი მზადაა გასავერტიკალურებლად";
        }

        private void BtnBackToLibrary_Click(object sender, RoutedEventArgs e)
        {
            LibraryModePanel.Visibility   = Visibility.Visible;
            EditorPreviewPanel.Visibility = Visibility.Collapsed;
            selectedVideoPath = "";
            VideoListBox.SelectedItem = null;
            UpdateSelectedInfo("");
            BtnProcess.Visibility = BtnPreview.Visibility = Visibility.Collapsed;
            StatusLabel.Text = "მზად არის სამუშაოდ";
        }


        private string FindFFmpeg() => AppPaths.Ffmpeg();

        /// <summary>Grabs a single frame (roughly mid-clip) via ffmpeg and saves it as a jpg.</summary>
        private async Task<bool> ExtractSingleThumbnailAsync(string videoPath, double durationSec, string outThumbPath)
        {
            double seekSec = durationSec > 1 ? durationSec / 2.0 : 0.5;
            string seek = seekSec.ToString("F2", CultureInfo.InvariantCulture);
            var si = new ProcessStartInfo
            {
                FileName = FindFFmpeg(),
                Arguments = $"-y -ss {seek} -i \"{videoPath}\" -frames:v 1 -q:v 3 \"{outThumbPath}\"",
                UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true
            };
            return await Task.Run(() =>
            {
                try
                {
                    using var p = Process.Start(si);
                    p!.StandardError.ReadToEnd();
                    p.WaitForExit(8000);
                    return File.Exists(outThumbPath);
                }
                catch { return false; }
            });
        }

        /// <summary>
        /// Fills in the real duration + thumbnail for the crop-tab preview after a
        /// drag-drop or hand-off, which initially shows with empty/placeholder values.
        /// Guards against the user having switched to a different clip in the meantime.
        /// </summary>
        private async Task LoadRealPreviewAsync(string path)
        {
            double durSec = await ProbeDurationSecAsync(path);
            Directory.CreateDirectory(CacheDir);
            string thumbPath = Path.Combine(CacheDir, Path.GetFileName(path) + ".preview.jpg");
            bool ok = await ExtractSingleThumbnailAsync(path, durSec, thumbPath);

            if (selectedVideoPath != path) return; // user moved on to something else meanwhile

            PreviewDuration.Text = FormatTime(TimeSpan.FromSeconds(durSec));
            if (ok && File.Exists(thumbPath))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource   = new Uri(thumbPath, UriKind.Absolute);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit(); bmp.Freeze();
                    PreviewThumbnail.Source       = bmp;
                    PreviewPlaceholder.Visibility = Visibility.Collapsed;
                }
                catch { /* keep placeholder showing */ }
            }
            await RefreshFacePreviewAsync(path);
        }

        /// <summary>
        /// Replaces the preview picture with a 9:16 crop centred on the speaker's face — found with the
        /// same Haar detector the tracker uses (tracker.py --facepreview-video). Silently keeps the plain
        /// frame if Python/OpenCV is missing, the format can't be read, or no face is found.
        /// </summary>
        private async Task RefreshFacePreviewAsync(string videoPath)
        {
            string scriptPath = GetTrackerPath(BaseDir);
            if (!File.Exists(scriptPath)) return;
            Directory.CreateDirectory(CacheDir);
            string outPath = Path.Combine(CacheDir, Path.GetFileName(videoPath) + ".face.jpg");
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }

            if (TxtPreviewCaption != null) TxtPreviewCaption.Text = "სახის ძიება…";
            var si = new ProcessStartInfo
            {
                FileName  = "python",
                Arguments = $"\"{scriptPath}\" \"--facepreview-video\" \"{videoPath}\" \"{outPath}\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            si.Environment["PYTHONUTF8"] = "1";
            si.Environment["PYTHONIOENCODING"] = "utf-8";

            bool faceFound = false;
            await Task.Run(() =>
            {
                try
                {
                    using var p = Process.Start(si);
                    if (p == null) return;
                    var err = p.StandardError.ReadToEndAsync();      // drain stderr so the child never blocks
                    string stdout = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(30000)) { try { p.Kill(true); } catch { } return; }
                    faceFound = stdout.Contains("[FACE] found");
                }
                catch { /* python missing → keep the plain preview */ }
            });

            if (selectedVideoPath != videoPath) return;              // user moved on meanwhile
            if (!File.Exists(outPath))
            {
                if (TxtPreviewCaption != null) TxtPreviewCaption.Text = "კადრის გადახედვა";
                return;
            }
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource   = new Uri(outPath, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit(); bmp.Freeze();
                PreviewThumbnail.Source       = bmp;
                PreviewPlaceholder.Visibility = Visibility.Collapsed;
                if (TxtPreviewCaption != null)
                    TxtPreviewCaption.Text = faceFound ? "ცენტრირებულია სახეზე" : "კადრის გადახედვა";
            }
            catch { if (TxtPreviewCaption != null) TxtPreviewCaption.Text = "კადრის გადახედვა"; }
        }

        // ================================================================
        // INTRO / OUTRO STINGER — generation + concatenation
        // ================================================================

        // Generates a short branded stinger clip from scratch (solid colour +
        // fade, with the configured Watermark logo faded in/out on top if one
        // is set). No live preview/font dependency — robust on any Windows box.
        private async Task<bool> GenerateBuiltInStingerAsync(string colorHex, double duration, string? logoPath, string outputPath)
        {
            string ff = FindFFmpeg();
            string dur = duration.ToString("F2", CultureInfo.InvariantCulture);
            bool hasLogo = !string.IsNullOrEmpty(logoPath) && File.Exists(logoPath);

            string args;
            if (hasLogo)
            {
                double fadeIn = Math.Min(1.0, duration * 0.4), fadeOut = Math.Min(1.0, duration * 0.4);
                double holdEnd = Math.Max(fadeIn, duration - fadeOut);
                string fi = fadeIn.ToString("F2", CultureInfo.InvariantCulture);
                string fo = fadeOut.ToString("F2", CultureInfo.InvariantCulture);
                string he = holdEnd.ToString("F2", CultureInfo.InvariantCulture);
                args = $"-f lavfi -i color=c={colorHex}:s=1080x1920:d={dur}:r=30 -i \"{logoPath}\" " +
                       $"-filter_complex \"[1:v]scale=420:-1,format=rgba," +
                       $"fade=t=in:st=0:d={fi}:alpha=1,fade=t=out:st={he}:d={fo}:alpha=1[logo];" +
                       $"[0:v][logo]overlay=(W-w)/2:(H-h)/2:format=auto[outv]\" " +
                       $"-map \"[outv]\" -map \"[outa]\" -c:v libx264 -preset fast -crf 18 -pix_fmt yuv420p -c:a aac -ar 44100 -ac 2 -shortest \"{outputPath}\"";
            }
            else
            {
                double fadeIn = Math.Min(1.0, duration * 0.4), fadeOut = Math.Min(1.0, duration * 0.4);
                string fi = fadeIn.ToString("F2", CultureInfo.InvariantCulture);
                string fadeOutSt = Math.Max(fadeIn, duration - fadeOut).ToString("F2", CultureInfo.InvariantCulture);
                string fo = fadeOut.ToString("F2", CultureInfo.InvariantCulture);
                args = $"-f lavfi -i color=c={colorHex}:s=1080x1920:d={dur}:r=30 " +
                       $"-f lavfi -i anullsrc=r=44100:cl=stereo " +
                       $"-filter_complex \"[0:v]fade=t=in:st=0:d={fi},fade=t=out:st={fadeOutSt}:d={fo}[outv];[1:a]atrim=duration={dur},asetpts=PTS-STARTPTS[outa]\" " +
                       $"-map \"[outv]\" -map \"[outa]\" " +
                       $"-c:v libx264 -preset fast -crf 18 -pix_fmt yuv420p -c:a aac -ar 44100 -ac 2 -shortest \"{outputPath}\"";
            }

            var si = new ProcessStartInfo { FileName = ff, Arguments = args, UseShellExecute = false, CreateNoWindow = true };
            return await Task.Run(() =>
            {
                try { using var p = Process.Start(si); p?.WaitForExit(30000); return p?.ExitCode == 0 && File.Exists(outputPath); }
                catch { return false; }
            });
        }

        private static string StingerColorHex(string templateLabel)
        {
            if (templateLabel.Contains("წითელი"))    return "0xCD0F20";
            if (templateLabel.Contains("შავი"))       return "0x000000";
            if (templateLabel.Contains("ანთრაციტი"))  return "0x151A1F";
            return "0x151A1F";
        }

        /// <summary>Returns true if the file has at least one audio stream.</summary>
        private async Task<bool> ProbeHasAudioAsync(string path)
        {
            var si = new ProcessStartInfo { FileName = FindFFmpeg(), Arguments = $"-i \"{path}\"",
                UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
            string err = await Task.Run(() =>
            {
                try { using var p = Process.Start(si); string e = p!.StandardError.ReadToEnd(); p.WaitForExit(8000); return e; }
                catch { return ""; }
            });
            return err.Contains("Audio:") || err.Contains("audio");
        }

        private async Task<double> ProbeDurationSecAsync(string path)
        {
            var si = new ProcessStartInfo { FileName = FindFFmpeg(), Arguments = $"-i \"{path}\"",
                UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
            string err = await Task.Run(() =>
            {
                try { using var p = Process.Start(si); string e = p!.StandardError.ReadToEnd(); p.WaitForExit(8000); return e; }
                catch { return ""; }
            });
            var m = System.Text.RegularExpressions.Regex.Match(err, @"Duration:\s*(\d+):(\d+):(\d+\.\d+)");
            if (m.Success)
                return int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60
                     + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            return 2.0;
        }

        // Orchestrates: resolve intro clip (generated or custom file), resolve
        // outro clip, then concatenate [intro?][main][outro?] into finalOutputPath.
        // Main content's own audio is preserved; stinger segments play silent
        // (their own audio, if any, is intentionally not used — keeps the
        // concat filter graph simple and robust across arbitrary custom files).
        private async Task<bool> ApplyIntroOutroAsync(string mainContentPath, string finalOutputPath)
        {
            bool applyIntro = ChkApplyIntro.IsChecked == true;
            bool applyOutro = ChkApplyOutro.IsChecked == true;
            if (!applyIntro && !applyOutro)
            {
                try { File.Copy(mainContentPath, finalOutputPath, true); return true; }
                catch { return false; }
            }

            string template = (CmbStingerTemplate.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
            bool isCustom = template.Contains("საკუთარი");
            string? logoPath = (ChkWatermark.IsChecked == true && File.Exists(TxtWmPath.Text.Trim()))
                ? TxtWmPath.Text.Trim() : null;
            double duration = SlStingerDuration.Value;

            Directory.CreateDirectory(CacheDir);
            var tempFiles = new List<string>();
            string? introPath = null, outroPath = null;

            try
            {
                if (applyIntro)
                {
                    if (isCustom)
                    {
                        string p = TxtCustomIntroPath.Text.Trim();
                        if (!string.IsNullOrEmpty(p) && File.Exists(p)) introPath = p;
                    }
                    else
                    {
                        string gen = Path.Combine(CacheDir, $"intro_{DateTime.Now.Ticks}.mp4");
                        if (await GenerateBuiltInStingerAsync(StingerColorHex(template), duration, logoPath, gen))
                        { introPath = gen; tempFiles.Add(gen); }
                    }
                }
                if (applyOutro)
                {
                    if (isCustom)
                    {
                        string p = TxtCustomOutroPath.Text.Trim();
                        if (!string.IsNullOrEmpty(p) && File.Exists(p)) outroPath = p;
                    }
                    else
                    {
                        string gen = Path.Combine(CacheDir, $"outro_{DateTime.Now.Ticks}.mp4");
                        if (await GenerateBuiltInStingerAsync(StingerColorHex(template), duration, logoPath, gen))
                        { outroPath = gen; tempFiles.Add(gen); }
                    }
                }

                if (introPath == null && outroPath == null)
                {
                    try { File.Copy(mainContentPath, finalOutputPath, true); return true; }
                    catch { return false; }
                }

                return await ConcatWithStingersAsync(introPath, mainContentPath, outroPath, finalOutputPath);
            }
            finally
            {
                foreach (var f in tempFiles) try { if (File.Exists(f)) File.Delete(f); } catch { }
            }
        }

        // Scales/pads every segment to a consistent 1080x1920@30fps, then
        // CROSS-FADES at each intro→main and main→outro boundary (instead of
        // a hard cut) for a smooth, gentle reveal — same xfade technique used
        // by the mini-editor's own transition system.
        private async Task<bool> ConcatWithStingersAsync(string? introPath, string mainPath, string? outroPath, string outputPath)
        {
            string ff = FindFFmpeg();
            var inputs = new List<string>();
            if (introPath != null) inputs.Add(introPath);
            inputs.Add(mainPath);
            if (outroPath != null) inputs.Add(outroPath);

            // Need every segment's duration up front to compute xfade offsets.
            var durations = new List<double>();
            foreach (var p in inputs) durations.Add(await ProbeDurationSecAsync(p));

            // Caller only invokes this when at least one stinger resolved, so
            // inputs.Count is always >= 2 — this guard is just extra safety.
            if (inputs.Count < 2)
            {
                try { File.Copy(mainPath, outputPath, true); return true; } catch { return false; }
            }

            string insArg = string.Join(" ", inputs.Select(p => $"-i \"{p}\""));

            var vf = new StringBuilder();
            for (int i = 0; i < inputs.Count; i++)
                vf.Append($"[{i}:v]scale=1080:1920:force_original_aspect_ratio=decrease,pad=1080:1920:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30[v{i}];");

            // Probe audio availability — never reference [N:a] on a video-only file.
            var hasAudios = new bool[inputs.Count];
            for (int i = 0; i < inputs.Count; i++)
                hasAudios[i] = await ProbeHasAudioAsync(inputs[i]);

            var af = new StringBuilder();
            for (int i = 0; i < inputs.Count; i++)
            {
                if (hasAudios[i])
                    af.Append($"[{i}:a]aresample=44100,aformat=channel_layouts=stereo[a{i}];");
                else
                    af.Append($"aevalsrc=0:c=stereo:s=44100:d={durations[i].ToString("F2", CultureInfo.InvariantCulture)},asetpts=PTS-STARTPTS[a{i}];");
            }

            // Gentle cross-dissolve at every join, on BOTH video (xfade) and
            // audio (acrossfade) in lockstep — pairing them is essential,
            // otherwise the video shortens by the overlap but the audio
            // wouldn't, and everything would drift out of sync after the
            // first join.
            double cumOffset = 0;
            string lastV = "[v0]", lastA = "[a0]";
            for (int i = 0; i < inputs.Count - 1; i++)
            {
                double segDur  = durations[i];
                double nextDur = durations[i + 1];
                double xDur    = Math.Min(0.8, Math.Min(segDur, nextDur) * 0.4);
                cumOffset += segDur - xDur;

                string nextV = i == inputs.Count - 2 ? "[outv]" : $"[vx{i}]";
                string nextA = i == inputs.Count - 2 ? "[outa]" : $"[ax{i}]";
                string xd = xDur.ToString("F2", CultureInfo.InvariantCulture);

                vf.Append($"{lastV}[v{i+1}]xfade=transition=fade:duration={xd}:offset={cumOffset.ToString("F3", CultureInfo.InvariantCulture)}{nextV};");
                af.Append($"{lastA}[a{i+1}]acrossfade=d={xd}{nextA};");
                lastV = nextV; lastA = nextA;
            }

            string filter = (vf.ToString() + af.ToString()).TrimEnd(';');
            string args = $@"-y {insArg} -filter_complex ""{filter}"" -map ""[outv]"" -map ""[outa]"" -c:v libx264 -preset fast -crf 18 -pix_fmt yuv420p -c:a aac ""{outputPath}""";

            var si = new ProcessStartInfo { FileName = ff, Arguments = args, UseShellExecute = false, CreateNoWindow = true };
            return await Task.Run(() =>
            {
                try { using var p = Process.Start(si); p?.WaitForExit(120000); return p?.ExitCode == 0 && File.Exists(outputPath); }
                catch { return false; }
            });
        }



        private static string FormatTime(TimeSpan t) =>
            $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
    }
}
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

namespace AiAssistant
{
    // Settings tab: theme, folders, tool diagnostics, data, keyboard shortcuts
    public partial class MainWindow
    {
        private void WireSettings()
        {
            ThemeManager.ThemeChanged += _ => SyncThemeRadios();
            // while a shortcut is being recorded, take every key before any control can swallow it
            PreviewKeyDown += (s, e) =>
            {
                if (_capturingAction != null) Window_KeyDown(s, e);
            };
            MainTabControl.SelectionChanged += (s, e) =>
            {
                if (ReferenceEquals(e.OriginalSource, MainTabControl) && MainTabControl.SelectedIndex == 2)
                    RefreshSettingsUI();
            };
            RefreshSettingsUI();
            _ = CheckToolsAsync();
        }

        private void SyncThemeRadios()
        {
            if (RbSetDark == null) return;
            bool dark = ThemeManager.Current == ThemeManager.Dark;
            RbSetDark.IsChecked = dark;
            RbSetLight.IsChecked = !dark;
        }

        private void SetTheme_Click(object sender, RoutedEventArgs e)
        {
            bool wantDark = RbSetDark.IsChecked == true;
            if ((ThemeManager.Current == ThemeManager.Dark) != wantDark) ThemeManager.Toggle();
        }

        private void RefreshSettingsUI()
        {
            if (TxtRootFolder == null) return;
            SyncThemeRadios();

            TxtRootFolder.Text = string.IsNullOrEmpty(selectedFolderPath) ? "არ არის არჩეული" : selectedFolderPath;
            TxtCacheFolder.Text = string.IsNullOrEmpty(_cacheFolder) ? CacheDir + "   (ნაგულისხმევი)" : _cacheFolder;
            TxtSetDownloads.Text = TheDownloaderView.CurrentDownloadFolder;
            TxtSetClips.Text = TheStreamClipper.CurrentClipFolder;

            string cache = CacheDir;
            TxtCacheSize.Text = "…";
            _ = Task.Run(() =>
            {
                long bytes = 0;
                try
                {
                    if (Directory.Exists(cache))
                        foreach (var f in Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories))
                            try { bytes += new FileInfo(f).Length; } catch { }
                }
                catch { }
                return bytes;
            }).ContinueWith(t => Dispatcher.InvokeAsync(() =>
            {
                double mb = t.Result / 1048576.0;
                TxtCacheSize.Text = mb >= 1024 ? $"{mb / 1024:0.0} GB" : mb >= 10 ? $"{mb:0} MB" : $"{mb:0.0} MB";
            }));
        }

        // ── folders ──────────────────────────────────────────────────
        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            string tag = (sender as FrameworkElement)?.Tag?.ToString() ?? "";
            string path = tag switch
            {
                "root"      => selectedFolderPath,
                "downloads" => TheDownloaderView.CurrentDownloadFolder,
                "clips"     => TheStreamClipper.CurrentClipFolder,
                "app"       => BaseDir,
                _           => ""
            };
            if (string.IsNullOrEmpty(path)) { ShowToast("საქაღალდე ჯერ არ არის არჩეული", "error"); return; }
            try
            {
                Directory.CreateDirectory(path);
                Process.Start("explorer.exe", path);
            }
            catch { }
        }

        private void BtnSetDownloadFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "ჩამოტვირთვების საქაღალდე", InitialDirectory = TheDownloaderView.CurrentDownloadFolder };
            if (dlg.ShowDialog() != true) return;
            TheDownloaderView.DownloadFolder = dlg.FolderName;
            RefreshSettingsUI();
            RefreshMediaLibraryUI();
        }

        private void BtnSetClipsFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "ლაივ კლიპების საქაღალდე", InitialDirectory = TheStreamClipper.CurrentClipFolder };
            if (dlg.ShowDialog() != true) return;
            TheStreamClipper.SetOutputFolder(dlg.FolderName);
            RefreshSettingsUI();
            RefreshMediaLibraryUI();
        }

        private void BtnResetCacheFolder_Click(object sender, RoutedEventArgs e)
        {
            _cacheFolder = "";
            SaveConfig();
            TheEditorView.CacheFolder = CacheDir;
            RefreshSettingsUI();
        }

        // ── data ─────────────────────────────────────────────────────
        private void BtnClearPresets_Click(object sender, RoutedEventArgs e)
        {
            int n = _presets.Count + _wmPresets.Count + _ioPresets.Count;
            if (n == 0) { ShowToast("პრეზეტები არ გაქვს"); return; }
            var r = MessageBox.Show($"წავშალო ყველა პრეზეტი ({n})?\n\nLower Third, ლოგო და Intro/Outro პრეზეტები.",
                "პრეზეტების წაშლა", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
            _presets.Clear(); _wmPresets.Clear(); _ioPresets.Clear();
            SavePresets(); SaveWmPresets(); SaveIoPresets();
            RefreshPresetsUI(); RefreshWmPresetsUI(); RefreshIoPresetsUI();
            RefreshQuickLtChips(); RefreshQuickWmChips(); RefreshQuickIoChips();
            ShowToast("პრეზეტები წაიშალა");
        }

        private void BtnOpenLog_Click(object sender, RoutedEventArgs e)
        {
            string log = Path.Combine(AppPaths.RoamingDir, "error.log");
            if (!File.Exists(log)) { ShowToast("ლოგი ცარიელია — შეცდომები არ დაფიქსირებულა"); return; }
            try { Process.Start(new ProcessStartInfo(log) { UseShellExecute = true }); } catch { }
        }

        // ── tool diagnostics ─────────────────────────────────────────
        private static void SetToolState(System.Windows.Shapes.Ellipse dot, TextBlock text, bool ok, string message)
        {
            text.Text = message;
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, ok ? "Success" : "Danger");
        }

        private async void BtnRecheckTools_Click(object sender, RoutedEventArgs e) => await CheckToolsAsync();

        private async Task CheckToolsAsync()
        {
            if (DotFfmpeg == null) return;
            BtnRecheckTools.IsEnabled = false;
            TxtToolFfmpeg.Text = TxtToolPython.Text = TxtToolAi.Text = "მოწმდება…";

            // ffmpeg
            var ff = await Task.Run(() =>
            {
                try
                {
                    var si = new ProcessStartInfo(AppPaths.Ffmpeg(), "-version")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                    using var p = Process.Start(si);
                    string first = p?.StandardOutput.ReadLine() ?? "";
                    p?.WaitForExit(8000);
                    return first;
                }
                catch { return ""; }
            });
            string ffPath = AppPaths.Ffmpeg();
            if (ff.StartsWith("ffmpeg version"))
            {
                string ver = ff.Split(' ').Skip(2).FirstOrDefault() ?? "";
                TxtToolFfmpeg.Tag = ff;
                SetToolState(DotFfmpeg, TxtToolFfmpeg, true,
                    $"{(ver.Length > 22 ? ver[..22] : ver)}   ·   {(Path.IsPathRooted(ffPath) ? Path.GetFileName(ffPath) : "PATH")}");
            }
            else SetToolState(DotFfmpeg, TxtToolFfmpeg, false, "ვერ მოიძებნა — ffmpeg.exe დადე პროგრამის საქაღალდეში");

            // yt-dlp
            string yt = await TheDownloaderView.GetYtDlpVersionAsync();
            TxtYtDlpVersion.Text = string.IsNullOrEmpty(yt) ? "ვერ მოიძებნა (pip install yt-dlp)" : yt;
            DotYtDlp.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, string.IsNullOrEmpty(yt) ? "Danger" : "Success");

            // python + AI packages
            var py = await Task.Run(() =>
            {
                try
                {
                    var si = MakePython("", "--version");
                    using var p = Process.Start(si);
                    if (p == null) return "";
                    string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit(8000);
                    return p.ExitCode == 0 ? o.Trim() : "";
                }
                catch { return ""; }
            });
            if (py.StartsWith("Python"))
            {
                SetToolState(DotPython, TxtToolPython, true, py);

                var mods = await Task.Run(() =>
                {
                    try
                    {
                        var si = MakePython("", "-c",
                            "import importlib.util as u;print(','.join(m for m in ['cv2','numpy','onnxruntime','mediapipe'] if u.find_spec(m)))");
                        using var p = Process.Start(si);
                        if (p == null) return "";
                        string o = p.StandardOutput.ReadToEnd();
                        p.WaitForExit(15000);
                        return o.Trim();
                    }
                    catch { return ""; }
                });
                var have = mods.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                bool core = have.Contains("cv2") && have.Contains("numpy");
                bool hq = core && have.Contains("onnxruntime");
                bool std = core && have.Contains("mediapipe");
                string model = ToolManager.HasRvmModel ? "მოდელი ჩამოტვირთულია"
                             : "მოდელი ჩამოიტვირთება პირველ გამოყენებაზე (~15 MB)";
                if (hq) SetToolState(DotAi, TxtToolAi, true, $"მაღალი ხარისხი (onnxruntime) · {model}");
                else if (std) SetToolState(DotAi, TxtToolAi, true,
                    "სტანდარტული (mediapipe) — უკეთესისთვის: pip install onnxruntime");
                else
                {
                    var missing = new[] { "cv2", "numpy", "onnxruntime" }.Where(m => !have.Contains(m));
                    SetToolState(DotAi, TxtToolAi, false,
                        "აკლია: " + string.Join(", ", missing) + " — pip install opencv-python numpy onnxruntime");
                }
            }
            else
            {
                SetToolState(DotPython, TxtToolPython, false, "ვერ მოიძებნა — დააყენე Python 3.10+ (python.org)");
                SetToolState(DotAi, TxtToolAi, false, "საჭიროა Python");
            }

            BtnRecheckTools.IsEnabled = true;
        }

        // ── tool installs / updates (ffmpeg, AI models, Python packages) ─────────
        private async void BtnUpdateFfmpeg_Click(object sender, RoutedEventArgs e)
        {
            BtnUpdateFfmpeg.IsEnabled = false;
            try
            {
                bool installed = AppPaths.HasFfmpeg();
                if (installed)
                {
                    TxtToolFfmpeg.Text = "ვამოწმებ განახლებას…";
                    string latest = await ToolManager.LatestFfmpegVersionAsync();
                    string current = TxtToolFfmpeg.Tag as string ?? "";
                    if (latest.Length > 0 && current.Contains(latest, StringComparison.OrdinalIgnoreCase))
                    {
                        ShowToast($"ffmpeg უკვე უახლესია ({latest})");
                        return;
                    }
                }
                await InstallToolAsync(ToolManager.FfmpegId, TxtToolFfmpeg);
            }
            finally
            {
                BtnUpdateFfmpeg.IsEnabled = true;
                await CheckToolsAsync();
            }
        }

        private async void BtnInstallAi_Click(object sender, RoutedEventArgs e)
        {
            BtnInstallAi.IsEnabled = false;
            try
            {
                foreach (var id in new[] { ToolManager.PipId, ToolManager.RvmId, ToolManager.YunetId, ToolManager.SelfieId })
                {
                    var tool = ToolManager.Get(id);
                    if (await tool.IsInstalledAsync()) continue;
                    if (!await InstallToolAsync(id, TxtToolAi)) break;
                }
            }
            finally
            {
                BtnInstallAi.IsEnabled = true;
                await CheckToolsAsync();
            }
        }

        private async Task<bool> InstallToolAsync(string id, TextBlock statusText)
        {
            var tool = ToolManager.Get(id);
            var prog = new Progress<ToolProgress>(p =>
                statusText.Text = p.Percent >= 0 ? $"{p.Text}  ({p.Percent:0}%)" : p.Text);
            try
            {
                var (ok, msg) = await tool.InstallAsync(prog, System.Threading.CancellationToken.None);
                AppPaths.ExportEnvironment();
                ShowToast(msg, ok ? "success" : "error");
                return ok;
            }
            catch (Exception ex)
            {
                ShowToast(ex.Message, "error");
                return false;
            }
        }

        // ── keyboard shortcuts ───────────────────────────────────────
        private static readonly Dictionary<string, string> ShortcutLabels = new()
        {
            ["Play/Pause"]    = "დაკვრა / პაუზა",
            ["Add Cut"]       = "ჭრის დამატება",
            ["Del Segment"]   = "მონაკვეთის წაშლა",
            ["Ripple Delete"] = "წაშლა სივრცის ამოღებით",
            ["Undo"]          = "გაუქმება",
            ["Redo"]          = "გამეორება",
            ["Toggle Snap"]   = "მიმაგრება (Snap)",
            ["Cut Only"]      = "მხოლოდ ჭრა",
            ["Cut + Crop"]    = "ჭრა + ვერტიკალი",
            ["Rewind"]        = "უკან გადახვევა",
            ["-5 seconds"]    = "−5 წამი",
            ["+5 seconds"]    = "+5 წამი",
            ["Prev Frame"]    = "წინა კადრი",
            ["Next Frame"]    = "შემდეგი კადრი",
        };

        private static string PrettyKey(string s)
        {
            if (string.IsNullOrEmpty(s)) return "—";
            return s.Replace("Return", "Enter").Replace("OemComma", ",").Replace("OemPeriod", ".")
                    .Replace("Delete", "Del").Replace("Left", "←").Replace("Right", "→")
                    .Replace("Up", "↑").Replace("Down", "↓").Replace("Oem", "");
        }

        private readonly Dictionary<string, (Border Badge, TextBlock Key, Button Btn)> _scRows = new();

        /// <summary>Marks one row as "waiting for a key" without rebuilding the list (keeps keyboard focus on the button).</summary>
        private void BeginCaptureVisual(string action)
        {
            foreach (var kv in _scRows)
            {
                bool on = kv.Key == action;
                kv.Value.Badge.SetResourceReference(Border.BackgroundProperty, on ? "AccentSoft" : "BgInput");
                kv.Value.Badge.SetResourceReference(Border.BorderBrushProperty, on ? "Accent" : "Border");
                kv.Value.Key.Text = on ? "…" : PrettyKey(_shortcuts.TryGetValue(kv.Key, out var v) ? v : "");
                kv.Value.Btn.Content = on ? "დააჭირე კლავიშს" : "შეცვლა";
            }
        }

        private void RefreshShortcutsUI()
        {
            if (ShortcutsPanel == null) return;
            ShortcutsPanel.Children.Clear();
            _scRows.Clear();

            foreach (var kv in _shortcuts)
            {
                string action = kv.Key;
                var row = new Border { Padding = new Thickness(14, 9, 10, 9), Margin = new Thickness(0, 0, 0, 8),
                                       CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
                row.SetResourceReference(Border.BackgroundProperty, "BgCard");
                row.SetResourceReference(Border.BorderBrushProperty, "Border");

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var label = new TextBlock
                {
                    Text = ShortcutLabels.TryGetValue(action, out var l) ? l : action,
                    FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
                };
                label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
                grid.Children.Add(label);

                bool capturing = _capturingAction == action;
                var badge = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 5, 10, 5),
                                         Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                                         MinWidth = 54, BorderThickness = new Thickness(1) };
                badge.SetResourceReference(Border.BackgroundProperty, capturing ? "AccentSoft" : "BgInput");
                badge.SetResourceReference(Border.BorderBrushProperty, capturing ? "Accent" : "Border");
                var key = new TextBlock { Text = capturing ? "…" : PrettyKey(kv.Value), FontFamily = new FontFamily("Consolas"),
                                          FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center };
                key.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
                badge.Child = key;
                Grid.SetColumn(badge, 1);
                grid.Children.Add(badge);

                var change = new Button
                {
                    Content = capturing ? "დააჭირე კლავიშს" : "შეცვლა", Padding = new Thickness(12, 6, 12, 6), FontSize = 12,
                    Style = (Style)FindResource("SecondaryButton"), Tag = action
                };
                change.Click += (s, _) => StartCapturingShortcut(action, (Button)s);
                Grid.SetColumn(change, 2);
                grid.Children.Add(change);

                row.Child = grid;
                ShortcutsPanel.Children.Add(row);
                _scRows[action] = (badge, key, change);
            }
        }

        private void BtnResetShortcuts_Click(object sender, RoutedEventArgs e)
        {
            _capturingAction = null; _capturingButton = null;
            _shortcuts = DefaultShortcuts();
            SaveConfig();
            RefreshShortcutsUI();
            ShowToast("მალსახმობები დაბრუნდა ნაგულისხმევზე");
        }
    }
}

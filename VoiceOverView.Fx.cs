using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace AiAssistant
{
    // voice polish: presets + sliders → one ffmpeg filter chain (denoise, low-cut, EQ, de-ess, compress, loudness)
    public partial class VoiceOverView
    {
        private sealed class Fx
        {
            public int Nr { get; set; } = 10;          // noise reduction 0..100
            public int Hp { get; set; } = 80;          // low-cut Hz (0 = off)
            public double Warm { get; set; } = 1;      // dB @ 220 Hz
            public double Pres { get; set; } = 2;      // dB @ 3.6 kHz
            public int Deess { get; set; } = 30;       // 0..100
            public int Comp { get; set; } = 40;        // 0..100
            public int Gate { get; set; } = 0;         // 0..100
            public int Loud { get; set; } = -16;       // target LUFS (0 = leave)
        }

        private static readonly Dictionary<string, Fx> Presets = new()
        {
            ["natural"] = new Fx { Nr = 8, Hp = 70, Warm = 1, Pres = 1.5, Deess = 25, Comp = 30, Gate = 0, Loud = -16 },
            ["studio"] = new Fx { Nr = 14, Hp = 90, Warm = 2, Pres = 3, Deess = 45, Comp = 55, Gate = 20, Loud = -16 },
            ["tv"] = new Fx { Nr = 12, Hp = 100, Warm = 1.5, Pres = 2, Deess = 40, Comp = 60, Gate = 10, Loud = -23 },
            ["radio"] = new Fx { Nr = 10, Hp = 120, Warm = 3, Pres = 4, Deess = 50, Comp = 75, Gate = 15, Loud = -16 }
        };

        private bool _suppressFx;
        private static string FxFile => Path.Combine(AppPaths.DataDir, "voice_fx.json");

        private bool FxActive => FxOff != null && FxOff.IsChecked != true;

        private string PresetKey => new[] { FxOff, FxNatural, FxStudio, FxTv, FxRadio, FxCustom }.FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? "off";

        private Fx CurrentFx() => new Fx
        {
            Nr = (int)SlNr.Value, Hp = (int)SlHp.Value, Warm = Math.Round(SlWarm.Value), Pres = Math.Round(SlPres.Value),
            Deess = (int)SlDeess.Value, Comp = (int)SlComp.Value, Gate = (int)SlGate.Value,
            Loud = int.TryParse((CmbLoud.SelectedItem as ComboBoxItem)?.Tag as string, out int l) ? l : -16
        };

        private void ApplyFxToUi(Fx f)
        {
            _suppressFx = true;
            SlNr.Value = f.Nr; SlHp.Value = f.Hp; SlWarm.Value = f.Warm; SlPres.Value = f.Pres; SlDeess.Value = f.Deess; SlComp.Value = f.Comp; SlGate.Value = f.Gate;
            foreach (ComboBoxItem it in CmbLoud.Items) if ((it.Tag as string) == f.Loud.ToString()) CmbLoud.SelectedItem = it;
            _suppressFx = false;
            UpdateFxTexts();
        }

        private void UpdateFxTexts()
        {
            if (TxtNr == null || TxtGate == null) return;
            TxtNr.Text = $"{(int)SlNr.Value}";
            TxtHp.Text = SlHp.Value < 1 ? "გამორთ." : $"{(int)SlHp.Value} Hz";
            TxtWarm.Text = $"{Math.Round(SlWarm.Value):+0;−0;0} dB";
            TxtPres.Text = $"{Math.Round(SlPres.Value):+0;−0;0} dB";
            TxtDeess.Text = $"{(int)SlDeess.Value}";
            TxtComp.Text = $"{(int)SlComp.Value}";
            TxtGate.Text = $"{(int)SlGate.Value}";
        }

        private void FxPreset_Click(object sender, RoutedEventArgs e)
        {
            string key = PresetKey;
            FxPanel.Visibility = key == "off" ? Visibility.Collapsed : Visibility.Visible;
            if (Presets.TryGetValue(key, out var f)) ApplyFxToUi(f);
            FxChanged();
        }

        private void Fx_Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_built) return;
            UpdateFxTexts();
            if (_suppressFx) return;
            if (PresetKey != "custom") FxCustom.IsChecked = true;
            FxPanel.Visibility = Visibility.Visible;
            FxChanged();
        }

        private void Fx_Combo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_built || _suppressFx) return;
            if (PresetKey != "custom") FxCustom.IsChecked = true;
            FxPanel.Visibility = Visibility.Visible;
            FxChanged();
        }

        private void FxChanged()
        {
            if (ListenFx.IsChecked == true && !FxActive) { ListenRaw.IsChecked = true; if (_playing) { StopPlayback(); } }
            else if (ListenFx.IsChecked == true && _playing) StopPlayback();
            UpdateExportEnabled();
            InvalidateFxInfo();
            SaveFxSettings();
        }

        private void InvalidateFxInfo()
        {
            if (TxtFxInfo == null) return;
            TxtFxInfo.Text = !FxActive ? "დამუშავების გარეშე — ხმა ისე დარჩება, როგორც ჩაიწერა."
                : "ზემოთ „დამუშავებული“-ს ან ქვემოთ ღილაკს დააჭირე — გაიგონებ განსხვავებას. შენახვისას დამუშავება თავისით გამოიყენება.";
            if (BtnFxPreview != null)
            {
                BtnFxPreview.IsEnabled = _takes.Count > 0 && FxActive;
                BtnFxPreview.Content = ListenFx.IsChecked == true ? "დამუშავებული ისმის — მოუსმინე ჩემს ხმას" : "მოუსმინე დამუშავებულს";
            }
        }

        // ── persistence ──
        private void SaveFxSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FxFile)!);
                File.WriteAllText(FxFile, JsonSerializer.Serialize(new { preset = PresetKey, fx = CurrentFx() }));
            }
            catch { }
        }

        private void LoadFxSettings()
        {
            try
            {
                if (!File.Exists(FxFile)) return;
                using var doc = JsonDocument.Parse(File.ReadAllText(FxFile));
                string key = doc.RootElement.GetProperty("preset").GetString() ?? "off";
                var fx = JsonSerializer.Deserialize<Fx>(doc.RootElement.GetProperty("fx").GetRawText()) ?? new Fx();
                ApplyFxToUi(fx);
                var radio = new[] { FxOff, FxNatural, FxStudio, FxTv, FxRadio, FxCustom }.FirstOrDefault(r => (r.Tag as string) == key) ?? FxOff;
                radio.IsChecked = true;
                FxPanel.Visibility = key == "off" ? Visibility.Collapsed : Visibility.Visible;
            }
            catch { }
            InvalidateFxInfo();
        }

        // ── the ffmpeg chain ──
        private static string BuildFxChain(Fx f)
        {
            var c = new List<string>();
            string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
            if (f.Hp > 0) c.Add($"highpass=f={f.Hp}");
            if (f.Gate > 0)
            {
                double db = -62 + f.Gate * 0.27;                          // 100 → −35 dB
                c.Add($"agate=threshold={N(Math.Pow(10, db / 20))}:ratio=2:attack=8:release=260:range=0.18");
            }
            if (f.Nr > 0) c.Add($"afftdn=nr={N(Math.Max(1, f.Nr * 0.24))}:nf=-38:tn=1");
            if (Math.Abs(f.Warm) > 0.01) c.Add($"equalizer=f=220:t=q:w=0.9:g={N(f.Warm)}");
            if (Math.Abs(f.Pres) > 0.01) c.Add($"equalizer=f=3600:t=q:w=1.1:g={N(f.Pres)}");
            if (f.Deess > 0) c.Add($"deesser=i={N(0.15 + f.Deess / 100.0 * 0.6)}:m=0.5:f=0.5:s=o");
            if (f.Comp > 0)
                c.Add($"acompressor=threshold={N(-16 - f.Comp * 0.1)}dB:ratio={N(1.5 + f.Comp * 0.03)}:attack=12:release=180:makeup={N(1 + f.Comp * 0.03)}");
            if (f.Loud != 0) c.Add($"loudnorm=I={f.Loud}:TP=-1.5:LRA=9");
            c.Add("alimiter=limit=0.97");
            return string.Join(",", c);
        }

        private string FxKey() => PresetKey == "off" ? "off" : BuildFxChain(CurrentFx());

        private string ProcFile(Take t)
        {
            string h = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(t.Path + "|" + FxKey())))[..14];
            return Path.Combine(WorkDir, $"fx_{h}.wav");
        }

        private string PlayPath(Take t) => ListenFx.IsChecked == true && FxActive && File.Exists(ProcFile(t)) ? ProcFile(t) : t.Path;

        /// <summary>Makes sure every (active) take has its processed copy for the current settings.</summary>
        private async Task<bool> EnsureProcessedAsync(bool announce)
        {
            if (!FxActive) return false;
            var files = _takes.Where(t => !t.Muted && File.Exists(t.Path)).GroupBy(t => t.Path).Select(g => g.First()).ToList();
            if (files.Count == 0) return true;
            string chain = BuildFxChain(CurrentFx());
            int i = 0;
            foreach (var t in files)
            {
                i++;
                string outp = ProcFile(t);
                if (File.Exists(outp)) continue;
                TxtFxInfo.Text = $"ხმა მუშავდება… {i}/{files.Count}";
                BtnFxPreview.IsEnabled = false;
                bool ok = await RunFfmpegAsync(new[] { "-y", "-v", "error", "-i", t.Path, "-af", chain, "-ar", "48000", "-ac", "1", "-c:a", "pcm_s16le", outp });
                if (!ok) { try { if (File.Exists(outp)) File.Delete(outp); } catch { } TxtFxInfo.Text = "დამუშავება ვერ მოხერხდა."; InvalidateFxInfo(); return false; }
            }
            InvalidateFxInfo();
            if (announce)
            {
                var last = _selTake ?? _takes.LastOrDefault();
                if (last != null) _ = ShowLoudnessAsync(last, true);
            }
            return true;
        }

        private async void BtnFxPreview_Click(object sender, RoutedEventArgs e)
        {
            if (ListenFx.IsChecked == true) { ListenRaw.IsChecked = true; Listen_Changed(this, e); InvalidateFxInfo(); return; }
            if (!FxActive || _takes.Count == 0) return;
            if (!await EnsureProcessedAsync(true)) return;
            ListenFx.IsChecked = true;
            InvalidateFxInfo();
            // play from a second before the chosen / last take so the difference is heard right away
            var t = _selTake ?? _takes.OrderBy(x => x.Start).First();
            StopPlayback();
            SeekTo(Math.Max(0, t.Start - 1));
            await StartPlaybackAsync();
        }

        // ── loudness ──
        private static async Task<(double lufs, double tp)?> MeasureLufsAsync(string path)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var si = new ProcessStartInfo(AppPaths.Ffmpeg()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8 };
                    foreach (var a in new[] { "-hide_banner", "-nostats", "-i", path, "-af", "loudnorm=I=-23:print_format=json", "-f", "null", "-" }) si.ArgumentList.Add(a);
                    using var p = Process.Start(si)!;
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit(60000);
                    var mi = Regex.Match(err, "\"input_i\"\\s*:\\s*\"(-?[\\d.]+|-inf)\"");
                    var mt = Regex.Match(err, "\"input_tp\"\\s*:\\s*\"(-?[\\d.]+)\"");
                    if (!mi.Success || !double.TryParse(mi.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double i)) return ((double, double)?)null;
                    double.TryParse(mt.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double tp);
                    return (i, tp);
                }
                catch { return null; }
            });
        }

        private async Task ReportLoudnessAsync(Take t) => await ShowLoudnessAsync(t, false);

        private async Task ShowLoudnessAsync(Take t, bool withProcessed)
        {
            var raw = await MeasureLufsAsync(t.Path);
            if (raw == null) return;
            string txt = $"„{t.Name}“: {raw.Value.lufs:0.0} LUFS (პიკი {raw.Value.tp:0.0} dB)";
            if (withProcessed && FxActive && File.Exists(ProcFile(t)))
            {
                var pr = await MeasureLufsAsync(ProcFile(t));
                if (pr != null) txt += $"  →  დამუშავებული: {pr.Value.lufs:0.0} LUFS (პიკი {pr.Value.tp:0.0} dB)";
            }
            else if (raw.Value.lufs < -30) txt += " — ჩუმია; დამუშავება „სიმაღლეს“ გაათანაბრებს.";
            if (TxtFxInfo != null) TxtFxInfo.Text = txt;
        }
    }
}

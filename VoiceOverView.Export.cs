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

namespace AiAssistant
{
    // mixing the takes (+ voice polish) into the video, or into a separate WAV
    public partial class VoiceOverView
    {
        private static async Task<bool> RunFfmpegAsync(string[] args, int timeoutMs = 240000)
        {
            return await Task.Run(() =>
            {
                var si = new ProcessStartInfo(AppPaths.Ffmpeg()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                foreach (var a in args) si.ArgumentList.Add(a);
                try
                {
                    using var p = Process.Start(si);
                    if (p == null) return false;
                    p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return false; }
                    return p.ExitCode == 0;
                }
                catch { return false; }
            });
        }

        /// <summary>volume(t) = 1 − (1−lvl)·max(mask_i(t)); each mask ramps in over 0.15 s and out over 0.35 s around a take.</summary>
        private string BuildExpression(IEnumerable<Take> takes, double lvl)
        {
            var masks = takes.Select(t => $"clip(min((t-{F(t.Start - 0.2)})/0.15,({F(t.End + 0.4)}-t)/0.35),0,1)").ToList();
            if (masks.Count == 0) return F(lvl);
            string m = masks[0];
            for (int i = 1; i < masks.Count; i++) m = $"max({m},{masks[i]})";
            return $"1-(1-{F(lvl)})*{m}";
        }

        /// <summary>One chain per take: cut the audible part, polish, gain, tiny fades against clicks, place on the timeline.</summary>
        private void BuildVoiceChains(List<Take> takes, int firstInput, string fxChain, string norm, List<string> chains, out string voOut)
        {
            var labels = new List<string>();
            for (int i = 0; i < takes.Count; i++)
            {
                var t = takes[i];
                int ms = (int)Math.Round(t.Start * 1000);
                string lab = $"v{i}";
                string fx = string.IsNullOrEmpty(fxChain) ? "" : "," + fxChain;
                chains.Add($"[{firstInput + i}:a]atrim=start={F(t.Offset)}:duration={F(t.Dur)},asetpts=PTS-STARTPTS,aresample=48000,aformat=channel_layouts=mono{fx}," +
                           $"volume={F(t.Gain)},afade=t=in:st=0:d=0.02,afade=t=out:st={F(Math.Max(0, t.Dur - 0.04))}:d=0.04,adelay={ms}:all=1[{lab}]");
                labels.Add($"[{lab}]");
            }
            if (labels.Count == 1) voOut = labels[0];
            else
            {
                chains.Add(string.Concat(labels) + $"amix=inputs={labels.Count}:duration=longest{norm}[vo]");
                voOut = "[vo]";
            }
        }

        private async void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            if (_video == null || _recording) return;
            var active = _takes.Where(t => !t.Muted && File.Exists(t.Path)).OrderBy(t => t.Start).ToList();
            if (active.Count == 0) return;

            string dir = Path.GetDirectoryName(_video)!;
            string name = Path.GetFileNameWithoutExtension(_video);
            string output = Path.Combine(dir, $"{name}_voice_{DateTime.Now:HHmmss}.mp4");
            bool auto = RbDuckAuto.IsChecked == true;
            double lvl = SlOrig.Value / 100.0;
            string fx = FxActive ? BuildFxChain(CurrentFx()) : "";

            BtnExport.IsEnabled = BtnExportWav.IsEnabled = false;
            BtnRecord.IsEnabled = false;
            PbExport.Visibility = Visibility.Visible; PbExport.Value = 0;
            TxtExportStatus.Text = FxActive ? "ვიდეო ინახება — ხმა მუშავდება და ერთვის…" : "ვიდეო ინახება ახალი ხმით…";
            BtnCompare.Visibility = Visibility.Collapsed;

            bool ok = false; string err = "";
            foreach (bool reencode in new[] { false, true })
            {
                foreach (bool normOpt in new[] { true, false })        // older ffmpeg has no amix "normalize" option
                {
                    var r = await RunMixAsync(_video, active, output, auto, lvl, fx, normOpt, reencode);
                    ok = r.ok; err = r.err;
                    if (ok || !err.Contains("normalize", StringComparison.OrdinalIgnoreCase)) break;
                }
                if (ok) break;
            }

            PbExport.Visibility = Visibility.Collapsed;
            UpdateRecordEnabled(); UpdateExportEnabled();
            if (!ok || !File.Exists(output))
            {
                try { if (File.Exists(output)) File.Delete(output); } catch { }
                TxtExportStatus.Text = "შენახვა ვერ მოხერხდა: " + (err.Length > 160 ? err[^160..] : err);
                Toast("ხმის დამატება ვერ მოხერხდა", "error");
                return;
            }
            _lastOutput = output;
            TxtExportStatus.Text = $"მზადაა: {Path.GetFileName(output)} · {new FileInfo(output).Length / 1048576.0:0.#} MB";
            BtnCompare.Visibility = Visibility.Visible;
            Toast($"ვიდეო ახალი ხმით მზადაა: {Path.GetFileName(output)}");
            Processed?.Invoke(_video, output);
        }

        private async Task<(bool ok, string err)> RunMixAsync(string video, List<Take> takes, string output, bool auto, double lvl, string fx, bool useNormalizeOpt, bool reencode)
        {
            var si = new ProcessStartInfo(AppPaths.Ffmpeg()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8 };
            void A(params string[] a) { foreach (var s in a) si.ArgumentList.Add(s); }

            A("-y", "-hide_banner", "-loglevel", "error", "-stats", "-i", video);
            foreach (var t in takes) A("-i", t.Path);

            string norm = useNormalizeOpt ? ":normalize=0" : "";
            var chains = new List<string>();
            BuildVoiceChains(takes, 1, fx, norm, chains, out string voOut);

            if (_hasAudio)
            {
                string expr = auto ? BuildExpression(takes, lvl) : F(lvl);
                string volFilter = auto ? $"volume='{expr}':eval=frame" : $"volume={expr}";
                chains.Add($"[0:a]aresample=48000,aformat=channel_layouts=mono,{volFilter}[o]");
                chains.Add($"[o]{voOut}amix=inputs=2:duration=first{norm}[aout]");
            }
            else chains.Add($"{voOut}anull[aout]");

            A("-filter_complex", string.Join(";", chains));
            A("-map", "0:v:0", "-map", "[aout]");
            if (reencode) A("-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p"); else A("-c:v", "copy");
            A("-c:a", "aac", "-b:a", "192k", "-shortest", "-movflags", "+faststart", output);

            var errBuf = new StringBuilder();
            double total = Math.Max(1, _dur);
            return await Task.Run(() =>
            {
                try
                {
                    using var p = new Process { StartInfo = si };
                    p.ErrorDataReceived += (_, ev) =>
                    {
                        if (string.IsNullOrEmpty(ev.Data)) return;
                        var m = Regex.Match(ev.Data, @"time=(\d+):(\d+):(\d+(?:\.\d+)?)");
                        if (m.Success)
                        {
                            double sec = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                            Dispatcher.InvokeAsync(() => PbExport.Value = Math.Min(99, sec / total * 100));
                        }
                        else errBuf.AppendLine(ev.Data);
                    };
                    p.Start(); p.BeginErrorReadLine(); p.WaitForExit();
                    return (p.ExitCode == 0, errBuf.ToString().Trim());
                }
                catch (Exception ex) { return (false, ex.Message); }
            });
        }

        /// <summary>Only the voice, polished, as a WAV (for editors / archives).</summary>
        private async void BtnExportWav_Click(object sender, RoutedEventArgs e)
        {
            if (_video == null || _recording) return;
            var active = _takes.Where(t => !t.Muted && File.Exists(t.Path)).OrderBy(t => t.Start).ToList();
            if (active.Count == 0) return;
            string output = Path.Combine(Path.GetDirectoryName(_video)!, $"{Path.GetFileNameWithoutExtension(_video)}_voice_{DateTime.Now:HHmmss}.wav");
            string fx = FxActive ? BuildFxChain(CurrentFx()) : "";
            BtnExport.IsEnabled = BtnExportWav.IsEnabled = false;
            TxtExportStatus.Text = "ხმა ინახება…";
            bool ok = false;
            foreach (bool normOpt in new[] { true, false })
            {
                var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error" };
                foreach (var t in active) { args.Add("-i"); args.Add(t.Path); }
                var chains = new List<string>();
                BuildVoiceChains(active, 0, fx, normOpt ? ":normalize=0" : "", chains, out string voOut);
                chains.Add($"{voOut}apad=whole_dur={F(_dur)}[aout]");
                args.AddRange(new[] { "-filter_complex", string.Join(";", chains), "-map", "[aout]", "-ar", "48000", "-c:a", "pcm_s16le", output });
                ok = await RunFfmpegAsync(args.ToArray());
                if (ok) break;
            }
            UpdateExportEnabled();
            if (!ok || !File.Exists(output)) { TxtExportStatus.Text = "WAV ვერ შეიქმნა."; Toast("WAV ვერ შეიქმნა", "error"); return; }
            TxtExportStatus.Text = $"ხმა შენახულია: {Path.GetFileName(output)}";
            Toast($"ხმა შენახულია: {Path.GetFileName(output)}");
            try { Process.Start("explorer.exe", $"/select,\"{output}\""); } catch { }
        }
    }
}

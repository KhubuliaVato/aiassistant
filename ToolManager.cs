using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AiAssistant
{
    /// <summary>Progress report of one install step. Percent &lt; 0 = unknown (indeterminate).</summary>
    public readonly record struct ToolProgress(double Percent, string Text);

    /// <summary>
    /// The large files the app needs but does not ship (ffmpeg, yt-dlp, AI models, Python packages).
    /// They are downloaded on first run (SetupWindow) and can be refreshed from Settings.
    /// </summary>
    public sealed class ToolItem
    {
        public string Id { get; init; } = "";
        public string Title { get; init; } = "";
        public string Description { get; init; } = "";
        public string Size { get; init; } = "";
        public string Glyph { get; init; } = "";
        public bool Required { get; init; }
        public Func<Task<bool>> IsInstalledAsync { get; init; } = () => Task.FromResult(true);
        public Func<IProgress<ToolProgress>, CancellationToken, Task<(bool ok, string message)>> InstallAsync { get; init; }
            = (_, _) => Task.FromResult((true, ""));
    }

    public static class ToolManager
    {
        public const string FfmpegId = "ffmpeg", YtDlpId = "ytdlp", RvmId = "rvm", YunetId = "yunet",
                            SelfieId = "selfie", PipId = "pip";

        private const string RvmFile = "rvm_mobilenetv3_fp32.onnx";
        private const string YunetFile = "face_detection_yunet_2023mar.onnx";
        private const string SelfieFile = "selfie_segmenter.tflite";

        private static readonly string[] FfmpegUrls =
        {
            "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
            "https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip"
        };
        private const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
        private const string RvmUrl = "https://github.com/PeterL1n/RobustVideoMatting/releases/download/v1.0.0/" + RvmFile;
        private const string YunetUrl = "https://github.com/opencv/opencv_zoo/raw/main/models/face_detection_yunet/" + YunetFile;
        private const string SelfieUrl = "https://storage.googleapis.com/mediapipe-models/image_segmenter/selfie_segmenter/float16/1/" + SelfieFile;

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(40) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("1Flow-AiAssistant/1.0 (+https://github.com/KhubuliaVato/aiassistant)");
            return c;
        }

        // ── model locations ───────────────────────────────────────────
        private static bool ModelPresent(string file) =>
            File.Exists(Path.Combine(AppPaths.ModelsDir, file))
            || File.Exists(Path.Combine(AppPaths.BaseDir, file))
            || File.Exists(Path.GetFullPath(Path.Combine(AppPaths.BaseDir, @"..\..\..\" + file)));

        public static bool HasRvmModel => ModelPresent(RvmFile);

        // ── catalogue ─────────────────────────────────────────────────
        public static IReadOnlyList<ToolItem> All { get; } = Build();

        public static ToolItem Get(string id) => All.First(t => t.Id == id);

        private static List<ToolItem> Build() => new()
        {
            new ToolItem
            {
                Id = FfmpegId, Title = "ffmpeg", Required = true, Size = "~80 MB", Glyph = "",
                Description = "ვიდეოს ჭრა, შეკუმშვა და ხმა — პროგრამის საფუძველია",
                IsInstalledAsync = () => Task.FromResult(AppPaths.HasFfmpeg()),
                InstallAsync = InstallFfmpegAsync
            },
            new ToolItem
            {
                Id = YtDlpId, Title = "yt-dlp", Size = "~18 MB", Glyph = "",
                Description = "YouTube / Facebook / TikTok ვიდეოების გადმოწერა და ლაივ კლიპერი",
                IsInstalledAsync = () => Task.FromResult(File.Exists(AppPaths.YtDlpInstallPath) || YtDlpTool.Resolve() != null),
                InstallAsync = InstallYtDlpAsync
            },
            new ToolItem
            {
                Id = RvmId, Title = "AI ფონის მოდელი", Size = "~15 MB", Glyph = "",
                Description = "ადამიანის გამოჭრა ფონიდან (Robust Video Matting)",
                IsInstalledAsync = () => Task.FromResult(ModelPresent(RvmFile)),
                InstallAsync = (p, ct) => InstallModelAsync(RvmUrl, RvmFile, "AI ფონის მოდელი", p, ct)
            },
            new ToolItem
            {
                Id = YunetId, Title = "სახის ამოცნობის მოდელი", Size = "~0.2 MB", Glyph = "",
                Description = "სახეების პოვნა და თვალის დევნება დაბუნდოვნებისთვის",
                IsInstalledAsync = () => Task.FromResult(ModelPresent(YunetFile)),
                InstallAsync = (p, ct) => InstallModelAsync(YunetUrl, YunetFile, "სახის ამოცნობის მოდელი", p, ct)
            },
            new ToolItem
            {
                Id = SelfieId, Title = "სარეზერვო ფონის მოდელი", Size = "~0.2 MB", Glyph = "",
                Description = "მარტივი მოდელი, თუ მთავარი მიუწვდომელია",
                IsInstalledAsync = () => Task.FromResult(ModelPresent(SelfieFile)),
                InstallAsync = (p, ct) => InstallModelAsync(SelfieUrl, SelfieFile, "სარეზერვო მოდელი", p, ct)
            },
            new ToolItem
            {
                Id = PipId, Title = "Python პაკეტები", Size = "~90 MB", Glyph = "",
                Description = "opencv, numpy, onnxruntime — AI ფონისა და სახის დაბუნდოვნებისთვის",
                IsInstalledAsync = async () => !await HasPythonAsync() || await PipPackagesPresentAsync(),
                InstallAsync = InstallPipAsync
            }
        };

        /// <summary>Items that are not installed yet (Python packages only when Python itself exists).</summary>
        public static async Task<List<ToolItem>> GetMissingAsync()
        {
            var missing = new List<ToolItem>();
            foreach (var t in All)
            {
                try { if (!await t.IsInstalledAsync()) missing.Add(t); } catch { }
            }
            return missing;
        }

        // ── installers ────────────────────────────────────────────────
        private static async Task<(bool, string)> InstallFfmpegAsync(IProgress<ToolProgress> prog, CancellationToken ct)
        {
            string tmpZip = Path.Combine(Path.GetTempPath(), "1flow_ffmpeg_" + Guid.NewGuid().ToString("N") + ".zip");
            string lastErr = "";
            try
            {
                foreach (var url in FfmpegUrls)
                {
                    try
                    {
                        await DownloadFileAsync(url, tmpZip, prog, ct, "ffmpeg");
                        prog.Report(new ToolProgress(-1, "ამოარქივება…"));
                        string dest = AppPaths.FfmpegInstallPath;
                        string part = dest + ".new";
                        bool extracted = await Task.Run(() =>
                        {
                            using var zip = ZipFile.OpenRead(tmpZip);
                            var e = zip.Entries.FirstOrDefault(x =>
                                x.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase)
                                || x.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
                            if (e == null) return false;
                            e.ExtractToFile(part, true);
                            return true;
                        }, ct);
                        if (!extracted) { lastErr = "არქივში ffmpeg.exe ვერ ვიპოვე"; continue; }
                        File.Move(part, dest, true);

                        if (!await VerifyFfmpegAsync(dest)) { lastErr = "ჩამოტვირთული ffmpeg არ მუშაობს"; continue; }
                        AppPaths.ExportEnvironment();
                        return (true, "ffmpeg დაყენებულია");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { lastErr = ex.Message; }
                }
                return (false, "ffmpeg ვერ ჩამოვტვირთე: " + lastErr);
            }
            finally { try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { } }
        }

        private static async Task<bool> VerifyFfmpegAsync(string exe)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var si = new ProcessStartInfo(exe, "-version")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                    using var p = Process.Start(si);
                    if (p == null) return false;
                    p.StandardOutput.ReadToEnd();
                    return p.WaitForExit(10000) && p.ExitCode == 0;
                }
                catch { return false; }
            });
        }

        private static async Task<(bool, string)> InstallYtDlpAsync(IProgress<ToolProgress> prog, CancellationToken ct)
        {
            try
            {
                await DownloadFileAsync(YtDlpUrl, AppPaths.YtDlpInstallPath, prog, ct, "yt-dlp");
                YtDlpTool.Resolve(force: true);
                return (true, "yt-dlp დაყენებულია");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return (false, "yt-dlp ვერ ჩამოვტვირთე: " + ex.Message); }
        }

        private static async Task<(bool, string)> InstallModelAsync(string url, string file, string label,
            IProgress<ToolProgress> prog, CancellationToken ct)
        {
            try
            {
                await DownloadFileAsync(url, Path.Combine(AppPaths.ModelsDir, file), prog, ct, label);
                return (true, label + " ჩამოტვირთულია");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return (false, label + " ვერ ჩამოვტვირთე: " + ex.Message); }
        }

        public static async Task<bool> HasPythonAsync() => await Task.Run(() =>
        {
            try
            {
                var si = new ProcessStartInfo("python", "--version")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using var p = Process.Start(si);
                if (p == null) return false;
                p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
                return p.WaitForExit(10000) && p.ExitCode == 0;
            }
            catch { return false; }
        });

        public static async Task<bool> PipPackagesPresentAsync() => await Task.Run(() =>
        {
            try
            {
                var si = new ProcessStartInfo("python")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                si.ArgumentList.Add("-c");
                si.ArgumentList.Add("import importlib.util as u,sys;sys.exit(0 if all(u.find_spec(m) for m in ('cv2','numpy','onnxruntime')) else 1)");
                using var p = Process.Start(si);
                if (p == null) return false;
                p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
                return p.WaitForExit(20000) && p.ExitCode == 0;
            }
            catch { return false; }
        });

        private static async Task<(bool, string)> InstallPipAsync(IProgress<ToolProgress> prog, CancellationToken ct)
        {
            if (!await HasPythonAsync())
                return (false, "Python ვერ მოიძებნა — დააყენე Python 3.10+ (python.org) და გაუშვი ხელახლა");

            prog.Report(new ToolProgress(-1, "pip install… (შეიძლება რამდენიმე წუთი დასჭირდეს)"));
            return await Task.Run(() =>
            {
                try
                {
                    var si = new ProcessStartInfo("python")
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                    };
                    foreach (var a in new[] { "-m", "pip", "install", "--user", "--upgrade", "--disable-pip-version-check",
                                              "opencv-python", "numpy", "onnxruntime" })
                        si.ArgumentList.Add(a);
                    si.Environment["PYTHONUTF8"] = "1";
                    using var p = new Process { StartInfo = si };
                    string last = "";
                    p.OutputDataReceived += (_, e) =>
                    {
                        if (string.IsNullOrWhiteSpace(e.Data)) return;
                        last = e.Data.Trim();
                        prog.Report(new ToolProgress(-1, last.Length > 70 ? last[..70] + "…" : last));
                    };
                    var err = new StringBuilder();
                    p.ErrorDataReceived += (_, e) => { if (e.Data != null) err.AppendLine(e.Data); };
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    using (ct.Register(() => { try { p.Kill(true); } catch { } }))
                        p.WaitForExit();
                    ct.ThrowIfCancellationRequested();
                    return p.ExitCode == 0
                        ? (true, "Python პაკეტები დაყენებულია")
                        : (false, "pip შეცდომა: " + (err.Length > 0 ? err.ToString().Trim().Split('\n').Last() : last));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { return (false, ex.Message); }
            }, ct);
        }

        // ── ffmpeg update check (Settings) ────────────────────────────
        /// <summary>Latest release version text published by gyan.dev (e.g. "8.1"); empty if unreachable.</summary>
        public static async Task<string> LatestFfmpegVersionAsync()
        {
            try { return (await Http.GetStringAsync("https://www.gyan.dev/ffmpeg/builds/release-version")).Trim(); }
            catch { return ""; }
        }

        // ── download helper ───────────────────────────────────────────
        public static async Task DownloadFileAsync(string url, string dest, IProgress<ToolProgress> prog,
            CancellationToken ct, string label)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            string tmp = dest + ".part";
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;

            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tmp))
            {
                var buf = new byte[81920];
                long read = 0;
                var sw = Stopwatch.StartNew();
                long lastBytes = 0;
                int n;
                while ((n = await src.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    read += n;
                    if (sw.ElapsedMilliseconds >= 200)
                    {
                        double speed = (read - lastBytes) / 1048576.0 / (sw.ElapsedMilliseconds / 1000.0);
                        lastBytes = read;
                        sw.Restart();
                        string sz = total.HasValue
                            ? $"{read / 1048576.0:0.0} / {total.Value / 1048576.0:0.0} MB"
                            : $"{read / 1048576.0:0.0} MB";
                        prog.Report(new ToolProgress(total.HasValue ? read * 100.0 / total.Value : -1,
                            $"{label} · {sz} · {speed:0.0} MB/წმ"));
                    }
                }
            }
            File.Move(tmp, dest, true);
            prog.Report(new ToolProgress(100, label));
        }
    }
}

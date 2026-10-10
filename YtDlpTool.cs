using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace AiAssistant
{
    /// <summary>
    /// Finds yt-dlp (exe next to the app → PATH → "python -m yt_dlp") and builds process start infos.
    /// Shared by the downloader and the live clipper.
    /// </summary>
    internal static class YtDlpTool
    {
        private static string[]? _cmd;
        private static bool _resolved;

        public static string[]? Resolve(bool force = false)
        {
            if (_resolved && !force) return _cmd;
            _cmd = null;

            string b = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new List<string>
            {
                AppPaths.YtDlpInstallPath,                 // downloaded by the app (ToolManager)
                Path.Combine(b, "yt-dlp.exe"),
                Path.GetFullPath(Path.Combine(b, @"..\..\..\yt-dlp.exe"))
            };
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                if (!string.IsNullOrWhiteSpace(dir)) candidates.Add(Path.Combine(dir.Trim(), "yt-dlp.exe"));

            foreach (var c in candidates)
            {
                try { if (File.Exists(c)) { _cmd = new[] { c }; break; } } catch { }
            }

            if (_cmd == null)
            {
                // pip-installed yt-dlp has no exe on PATH but runs as a module
                try
                {
                    var si = new ProcessStartInfo("python")
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true
                    };
                    si.ArgumentList.Add("-m"); si.ArgumentList.Add("yt_dlp"); si.ArgumentList.Add("--version");
                    using var p = Process.Start(si);
                    if (p != null)
                    {
                        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
                        if (p.WaitForExit(20000) && p.ExitCode == 0) _cmd = new[] { "python", "-m", "yt_dlp" };
                    }
                }
                catch { }
            }

            _resolved = true;
            return _cmd;
        }

        public static ProcessStartInfo? Make(params string[] args)
        {
            var cmd = Resolve();
            if (cmd == null) return null;
            var si = new ProcessStartInfo(cmd[0])
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var a in cmd.Skip(1)) si.ArgumentList.Add(a);
            foreach (var a in args) si.ArgumentList.Add(a);
            si.Environment["PYTHONUTF8"] = "1";
            si.Environment["PYTHONIOENCODING"] = "utf-8";
            return si;
        }

        public static IEnumerable<string> CommonArgs()
        {
            yield return "--no-playlist";
            yield return "--no-warnings";
            yield return "--extractor-args";
            yield return "youtube:player_client=android,web";
            string ff = AppPaths.Ffmpeg();
            if (Path.IsPathRooted(ff) && File.Exists(ff)) { yield return "--ffmpeg-location"; yield return ff; }
        }
    }
}

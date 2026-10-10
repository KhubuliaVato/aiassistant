using System;
using System.IO;

namespace AiAssistant
{
    /// <summary>
    /// One place for every location the app uses:
    ///   DataDir  = %LOCALAPPDATA%\1Flow         settings, presets, history, caches
    ///   ToolsDir = DataDir\tools                ffmpeg.exe, yt-dlp.exe (downloaded on first run)
    ///   ModelsDir= DataDir\tools\models         AI model files (downloaded on first run)
    /// Environment variable FLOW_DATA_DIR overrides DataDir (used by tests / portable setups).
    /// </summary>
    public static class AppPaths
    {
        public const string AppFolder = "1Flow";
        private const string LegacyFolder = "FlowSocial";

        public static string BaseDir => AppDomain.CurrentDomain.BaseDirectory;

        private static string? _data;
        public static string DataDir
        {
            get
            {
                if (_data == null)
                {
                    string? ov = Environment.GetEnvironmentVariable("FLOW_DATA_DIR");
                    _data = !string.IsNullOrWhiteSpace(ov)
                        ? ov
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolder);
                    try { Directory.CreateDirectory(_data); } catch { }
                }
                return _data;
            }
        }

        /// <summary>Error log etc.</summary>
        public static string RoamingDir
        {
            get
            {
                string? ov = Environment.GetEnvironmentVariable("FLOW_DATA_DIR");
                return !string.IsNullOrWhiteSpace(ov)
                    ? ov
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolder);
            }
        }

        public static string ToolsDir
        {
            get { string d = Path.Combine(DataDir, "tools"); try { Directory.CreateDirectory(d); } catch { } return d; }
        }

        public static string ModelsDir
        {
            get { string d = Path.Combine(ToolsDir, "models"); try { Directory.CreateDirectory(d); } catch { } return d; }
        }

        public static string FfmpegInstallPath => Path.Combine(ToolsDir, "ffmpeg.exe");
        public static string YtDlpInstallPath => Path.Combine(ToolsDir, "yt-dlp.exe");

        /// <summary>
        /// ffmpeg.exe: downloaded copy → next to the app → project root (3 levels above bin\Debug\net*)
        /// → plain "ffmpeg" from PATH.
        /// </summary>
        public static string Ffmpeg()
        {
            if (File.Exists(FfmpegInstallPath)) return FfmpegInstallPath;
            string p1 = Path.Combine(BaseDir, "ffmpeg.exe");
            if (File.Exists(p1)) return p1;
            string p2 = Path.GetFullPath(Path.Combine(BaseDir, @"..\..\..\ffmpeg.exe"));
            return File.Exists(p2) ? p2 : "ffmpeg";
        }

        /// <summary>True when a usable ffmpeg is present (downloaded, bundled or on PATH).</summary>
        public static bool HasFfmpeg()
        {
            string f = Ffmpeg();
            if (Path.IsPathRooted(f)) return File.Exists(f);
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try { if (!string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir.Trim(), "ffmpeg.exe"))) return true; } catch { }
            }
            return false;
        }

        /// <summary>The user's Videos folder + a sub-folder, keeping an older folder name if it already exists.</summary>
        public static string VideosFolder(string name, string legacyName)
        {
            string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            string fresh = Path.Combine(videos, name);
            string old = Path.Combine(videos, legacyName);
            return !Directory.Exists(fresh) && Directory.Exists(old) ? old : fresh;
        }

        /// <summary>Make the Python scripts find the same tools the app uses.</summary>
        public static void ExportEnvironment()
        {
            Environment.SetEnvironmentVariable("FLOW_MODELS_DIR", ModelsDir);
            string ff = Ffmpeg();
            if (Path.IsPathRooted(ff)) Environment.SetEnvironmentVariable("FLOW_FFMPEG", ff);
        }

        /// <summary>Settings from older builds (app-data folder "FlowSocial", config files next to the exe).</summary>
        public static void MigrateLegacy()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLOW_DATA_DIR")))
                {
                    string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    string oldLocal = Path.Combine(local, LegacyFolder);
                    string newLocal = Path.Combine(local, AppFolder);
                    if (Directory.Exists(oldLocal) && !Directory.Exists(newLocal)) Directory.Move(oldLocal, newLocal);

                    string roam = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    string oldRoam = Path.Combine(roam, LegacyFolder);
                    string newRoam = Path.Combine(roam, AppFolder);
                    if (Directory.Exists(oldRoam) && !Directory.Exists(newRoam)) Directory.Move(oldRoam, newRoam);
                }

                foreach (var f in new[] { "flow_config.json", "lt_presets.json", "wm_presets.json", "io_presets.json", "job_history.json" })
                {
                    string src = Path.Combine(BaseDir, f);
                    string dst = Path.Combine(DataDir, f);
                    if (File.Exists(src) && !File.Exists(dst)) File.Copy(src, dst);
                }
            }
            catch { /* migration is best-effort */ }
        }
    }
}

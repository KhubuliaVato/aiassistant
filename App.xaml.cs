using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace AiAssistant;

public partial class App : Application
{
    private static string LogPath => Path.Combine(AppPaths.RoamingDir, "error.log");

    public App()
    {
        DispatcherUnhandledException += (sender, e) =>
        {
            Log("UI", e.Exception);
            MessageBox.Show(
                $"Error:\n\n{e.Exception.GetType().Name}\n{e.Exception.Message}\n\n" +
                $"{e.Exception.StackTrace?.Split('\n')[0]}\n\nLog: {LogPath}",
                "1Flow AiAssistant — Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            e.Handled = true;
        };

        // Exceptions on background threads / unobserved tasks used to vanish silently.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log("Domain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("Task", e.Exception);
            e.SetObserved();
        };
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;     // the setup window must not end the app when it closes

        AppPaths.MigrateLegacy();      // settings from older builds
        ThemeManager.ApplySaved();     // before the first window is created
        AppPaths.ExportEnvironment();  // lets the Python scripts find ffmpeg and the AI models

        try
        {
            // ffmpeg, yt-dlp, AI models, Python packages: download whatever is missing, with visible progress
            var missing = Environment.GetEnvironmentVariable("FLOW_SKIP_SETUP") == "1"
                ? new System.Collections.Generic.List<ToolItem>()      // automated tests only
                : await ToolManager.GetMissingAsync();
            if (missing.Count > 0)
            {
                new SetupWindow(missing).ShowDialog();
                AppPaths.ExportEnvironment();
                YtDlpTool.Resolve(force: true);
            }
        }
        catch (Exception ex) { Log("Setup", ex); }

        var main = new MainWindow();
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
    }

    private static void Log(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex}\n\n");
        }
        catch { /* logging must never throw */ }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace AiAssistant
{
    /// <summary>
    /// Swaps Themes/Dark.xaml ⇄ Themes/Light.xaml at runtime. The choice is stored in
    /// flow_config.json ("Theme") next to the exe, alongside the other settings.
    /// </summary>
    public static class ThemeManager
    {
        public const string Dark  = "dark";
        public const string Light = "light";

        private static string ConfigPath =>
            Path.Combine(AppPaths.DataDir, "flow_config.json");

        public static string Current { get; private set; } = Dark;

        public static event Action<string>? ThemeChanged;

        /// <summary>Call once on startup, before the first window is created.</summary>
        public static void ApplySaved()
        {
            string saved = Dark;
            try
            {
                if (File.Exists(ConfigPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
                    if (doc.RootElement.TryGetProperty("Theme", out var t) &&
                        string.Equals(t.GetString(), Light, StringComparison.OrdinalIgnoreCase))
                        saved = Light;
                }
            }
            catch { /* corrupt config → default theme */ }
            Apply(saved, save: false);
        }

        public static void Toggle() => Apply(Current == Dark ? Light : Dark, save: true);

        public static void Apply(string theme, bool save)
        {
            theme = string.Equals(theme, Light, StringComparison.OrdinalIgnoreCase) ? Light : Dark;
            var merged = Application.Current.Resources.MergedDictionaries;

            var old = merged.FirstOrDefault(d => d.Source != null &&
                (d.Source.OriginalString.EndsWith("Themes/Dark.xaml",  StringComparison.OrdinalIgnoreCase) ||
                 d.Source.OriginalString.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase)));

            var fresh = new ResourceDictionary
            {
                Source = new Uri($"Themes/{(theme == Light ? "Light" : "Dark")}.xaml", UriKind.Relative)
            };

            if (old != null) merged[merged.IndexOf(old)] = fresh;
            else merged.Insert(0, fresh);

            Current = theme;
            if (save) Save();
            ThemeChanged?.Invoke(theme);
        }

        /// <summary>Looks up a theme brush for code-behind (always the active theme's value).</summary>
        public static System.Windows.Media.Brush Brush(string key) =>
            (System.Windows.Media.Brush)Application.Current.FindResource(key);

        private static void Save()
        {
            try
            {
                JsonObject obj = new();
                if (File.Exists(ConfigPath))
                    obj = JsonNode.Parse(File.ReadAllText(ConfigPath)) as JsonObject ?? new JsonObject();
                obj["Theme"] = Current;
                File.WriteAllText(ConfigPath,
                    obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* settings are best-effort */ }
        }
    }
}

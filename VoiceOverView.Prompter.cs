using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace AiAssistant
{
    // teleprompter: the script rolls over the video at the reading speed, words dim once they are read
    public partial class VoiceOverView
    {
        private readonly List<Run> _pWords = new();
        private int _pIdx;
        private static readonly Brush PromptUpcoming = Brushes.White;
        private static readonly Brush PromptDone = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF));
        private static readonly Brush PromptNow = new SolidColorBrush(Color.FromRgb(0xFF, 0xD1, 0x66));

        private static string ScriptFile => Path.Combine(AppPaths.DataDir, "voice_script.txt");

        private void LoadScript()
        {
            try { if (File.Exists(ScriptFile)) TxtScript.Text = File.ReadAllText(ScriptFile); } catch { }
            BuildPrompter();
        }

        private void Script_Changed(object sender, TextChangedEventArgs e)
        {
            if (PrompterText == null) return;
            try { Directory.CreateDirectory(Path.GetDirectoryName(ScriptFile)!); File.WriteAllText(ScriptFile, TxtScript.Text); } catch { }
            bool first = _pWords.Count == 0;
            BuildPrompter();
            if (first && _pWords.Count > 0 && !_prompterOffByUser) SetPrompter(true);        // first text typed → show it on the video
            if (!HasScript) SetPrompter(false);
            UpdatePrompterVisibility();
        }

        private bool HasScript => !string.IsNullOrWhiteSpace(TxtScript?.Text);

        private bool _prompterOn;
        private bool _prompterOffByUser;

        private void TglPrompter_Click(object sender, RoutedEventArgs e)
        {
            if (!_prompterOn && !HasScript)
            {
                Toast("ჯერ ჩაწერე ან ჩასვი ტექსტი მარჯვენა პანელში (მე-2 ნაბიჯი)", "error");
                return;
            }
            _prompterOffByUser = _prompterOn;                 // the user hid it on purpose → do not pop it up again by itself
            SetPrompter(!_prompterOn);
        }

        private void SetPrompter(bool on)
        {
            _prompterOn = on;
            TglPrompter.SetResourceReference(Control.ForegroundProperty, on ? "Accent" : "TextPrimary");
            UpdatePrompterVisibility();
        }

        private void UpdatePrompterVisibility()
            => PrompterBox.Visibility = _prompterOn && HasScript ? Visibility.Visible : Visibility.Collapsed;

        private void BuildPrompter()
        {
            _pWords.Clear();
            PrompterText.Inlines.Clear();
            _pIdx = 0;
            string[] lines = (TxtScript.Text ?? "").Replace("\r", "").Split('\n');
            for (int li = 0; li < lines.Length; li++)
            {
                foreach (var w in lines[li].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var run = new Run(w + " ") { Foreground = PromptUpcoming };
                    _pWords.Add(run);
                    PrompterText.Inlines.Add(run);
                }
                if (li < lines.Length - 1) PrompterText.Inlines.Add(new LineBreak());
            }
        }

        private void PrompterReset()
        {
            foreach (var r in _pWords) r.Foreground = PromptUpcoming;
            _pIdx = 0;
            PrompterScroll.ScrollToVerticalOffset(0);
            if (_pWords.Count > 0) _pWords[0].Foreground = PromptNow;
        }

        /// <summary>elapsed = seconds since the recording really started (after the pre-roll).</summary>
        private void PrompterTick(double elapsed)
        {
            if (_pWords.Count == 0) return;
            int idx = Math.Clamp((int)(elapsed * SlWpm.Value / 60.0), 0, _pWords.Count);
            if (idx == _pIdx) return;
            for (int i = _pIdx; i < Math.Min(idx, _pWords.Count); i++) _pWords[i].Foreground = PromptDone;
            _pIdx = idx;
            if (idx >= _pWords.Count) return;
            _pWords[idx].Foreground = PromptNow;
            try
            {
                var rect = _pWords[idx].ElementStart.GetCharacterRect(LogicalDirection.Forward);
                PrompterScroll.ScrollToVerticalOffset(Math.Max(0, rect.Top - 40));
            }
            catch { }
        }
    }
}

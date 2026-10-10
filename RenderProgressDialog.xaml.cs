using System.Windows;

namespace AiAssistant
{
    public partial class RenderProgressDialog : Window
    {
        public RenderProgressDialog()
        {
            InitializeComponent();
        }

        /// <summary>Thread-safe progress update — can be called from any thread.</summary>
        public void SetProgress(int percent, string status)
        {
            Dispatcher.Invoke(() =>
            {
                PbMain.Value = percent;
                TxtStatus.Text = status;
                TxtPercent.Text = $"{percent}%";

                if (percent >= 100)
                {
                    TxtIcon.Text = "✅";
                    TxtTitle.Text = "დამუშავება დასრულდა!";
                    TxtTitle.Foreground = System.Windows.Media.Brushes.White;
                }
            });
        }
    }
}
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace AiAssistant
{
    public partial class ExportDialog : Window
    {
        public string ExportFileName { get; private set; } = "";
        public string ExportFolder  { get; private set; } = "";

        public ExportDialog(string defaultName, string defaultFolder)
        {
            InitializeComponent();

            TxtFileName.Text = defaultName;
            ExportFolder = defaultFolder;
            TxtFolderDisplay.Text = defaultFolder;

            TxtFileName.Focus();
            TxtFileName.SelectAll();
        }

        private void BtnBrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            var fd = new OpenFolderDialog
            {
                Title = "აირჩიეთ შენახვის საქაღალდე",
                InitialDirectory = ExportFolder
            };
            if (fd.ShowDialog() == true)
            {
                ExportFolder = fd.FolderName;
                TxtFolderDisplay.Text = fd.FolderName;
            }
        }

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtFileName.Text))
            {
                MessageBox.Show("გთხოვთ ჩაწეროთ ფაილის სახელი!", "ყურადღება",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(ExportFolder) || !Directory.Exists(ExportFolder))
            {
                MessageBox.Show("გთხოვთ მიუთითოთ არსებული შენახვის საქაღალდე!", "ყურადღება",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ExportFileName = TxtFileName.Text.Trim();
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
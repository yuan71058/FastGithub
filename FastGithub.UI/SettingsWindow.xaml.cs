using System.Windows;

namespace FastGithub.UI
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
            AutoStartCheckBox.IsChecked = Properties.Settings.Default.AutoStart;
            StartMinimizedCheckBox.IsChecked = Properties.Settings.Default.StartMinimized;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            var autoStart = AutoStartCheckBox.IsChecked ?? false;
            var startMinimized = StartMinimizedCheckBox.IsChecked ?? false;
            
            Properties.Settings.Default.AutoStart = autoStart;
            Properties.Settings.Default.StartMinimized = startMinimized;
            Properties.Settings.Default.Save();
            
            Program.SetAutoStart(autoStart);
            
            this.Close();
        }
    }
}
using System.Windows;

namespace FastGithub.UI
{
    /// <summary>
    /// 托盘右键打开的设置窗口
    /// 内容与主窗口的"设置"页是同一个控件，保证两处始终一致
    /// </summary>
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}

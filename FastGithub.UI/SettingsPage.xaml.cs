using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace FastGithub.UI
{
    /// <summary>
    /// 设置页
    /// 所有改动立即写入当前用户的配置并即时生效，不提供"取消"语义，
    /// 这样主窗口的设置页与托盘右键打开的设置窗口共用同一份状态，不会出现两边取值不一致
    /// </summary>
    public partial class SettingsPage : UserControl
    {
        private const string RELEASES_URI = "https://gitee.com/yuan71058/fast-github/releases";
        private const string FASTGITHUB_EXE = "fastgithub.exe";

        private bool loading = true;

        public SettingsPage()
        {
            InitializeComponent();
            this.LoadSettings();
            this.loading = false;

            this.Loaded += (s, e) => this.RefreshBackendState();
            this.Unloaded += (s, e) => IpRefreshService.Reported -= this.OnRefreshed;
            IpRefreshService.Reported += this.OnRefreshed;
            this.UpdateLastRefreshText();
        }

        /// <summary>
        /// 把配置里的值读到控件上
        /// </summary>
        private void LoadSettings()
        {
            var settings = Properties.Settings.Default;
            this.autoStartCheckBox.IsChecked = settings.AutoStart;
            this.startMinimizedCheckBox.IsChecked = settings.StartMinimized;
            this.autoRefreshOnStartCheckBox.IsChecked = settings.AutoRefreshOnStart;
            this.showBalloonCheckBox.IsChecked = settings.ShowBalloon;

            var hours = settings.AutoRefreshHours;
            var matched = false;
            foreach (var item in this.refreshIntervalComboBox.Items)
            {
                if (item is ComboBoxItem comboBoxItem && int.TryParse(comboBoxItem.Tag?.ToString(), out var value))
                {
                    if (value == hours)
                    {
                        this.refreshIntervalComboBox.SelectedItem = comboBoxItem;
                        matched = true;
                        break;
                    }
                }
            }

            if (matched == false)
            {
                this.refreshIntervalComboBox.SelectedIndex = 0;
            }
        }

        private void AutoStartCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (this.loading == true)
            {
                return;
            }

            var enabled = this.autoStartCheckBox.IsChecked ?? false;
            Properties.Settings.Default.AutoStart = enabled;
            Properties.Settings.Default.Save();
            Program.SetAutoStart(enabled);
        }

        private void StartMinimizedCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (this.loading == true)
            {
                return;
            }

            Properties.Settings.Default.StartMinimized = this.startMinimizedCheckBox.IsChecked ?? false;
            Properties.Settings.Default.Save();
        }

        private void AutoRefreshOnStartCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (this.loading == true)
            {
                return;
            }

            Properties.Settings.Default.AutoRefreshOnStart = this.autoRefreshOnStartCheckBox.IsChecked ?? false;
            Properties.Settings.Default.Save();
        }

        private void ShowBalloonCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (this.loading == true)
            {
                return;
            }

            Properties.Settings.Default.ShowBalloon = this.showBalloonCheckBox.IsChecked ?? false;
            Properties.Settings.Default.Save();
        }

        private void RefreshIntervalComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (this.loading == true)
            {
                return;
            }

            var hours = 0;
            if (this.refreshIntervalComboBox.SelectedItem is ComboBoxItem item)
            {
                int.TryParse(item.Tag?.ToString(), out hours);
            }

            Properties.Settings.Default.AutoRefreshHours = hours;
            Properties.Settings.Default.Save();
        }

        private async void RefreshNowButton_Click(object sender, RoutedEventArgs e)
        {
            this.refreshNowButton.IsEnabled = false;
            try
            {
                await IpRefreshService.RunAsync();
            }
            finally
            {
                this.refreshNowButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// 刷新过程中的反馈，用来更新"上次更新"这一行
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnRefreshed(object sender, IpRefreshEventArgs e)
        {
            if (e.IsFinal == false)
            {
                this.lastRefreshText.Text = e.Message;
                return;
            }

            this.UpdateLastRefreshText();
        }

        private void UpdateLastRefreshText()
        {
            var time = IpRefreshService.LastCompletedTime;
            if (time.HasValue == false)
            {
                this.lastRefreshText.Text = "上次更新：本次运行尚未更新";
                return;
            }

            var state = IpRefreshService.LastSucceeded ? "成功" : "未成功";
            this.lastRefreshText.Text = $"上次更新：{time.Value:HH:mm:ss}（{state}）";
        }

        /// <summary>
        /// 刷新后端状态显示
        /// </summary>
        private async void RefreshBackendState()
        {
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;

            var version = this.GetBackendVersion(baseDirectory);
            this.backendVersionText.Text = string.IsNullOrEmpty(version) ? "未找到 fastgithub.exe" : version;

            var running = false;
            try
            {
                running = Process.GetProcessesByName("fastgithub").Length > 0;
            }
            catch (Exception)
            {
            }

            if (running == false)
            {
                this.backendStateText.Text = "未运行";
                this.backendStateText.Foreground = System.Windows.Media.Brushes.Firebrick;
                this.backendPortText.Text = "—";
                return;
            }

            this.backendStateText.Text = "运行中";
            this.backendStateText.Foreground = System.Windows.Media.Brushes.SeaGreen;

            try
            {
                var baseUri = await UiApi.GetBaseUriAsync();
                this.backendPortText.Text = UiApi.Detected
                    ? $"{new Uri(baseUri).Port}（已连通）"
                    : $"{new Uri(baseUri).Port}（未响应）";
            }
            catch (Exception)
            {
                this.backendPortText.Text = "—";
            }
        }

        /// <summary>
        /// 读取后端 exe 的版本号
        /// </summary>
        /// <param name="baseDirectory"></param>
        /// <returns></returns>
        private string? GetBackendVersion(string baseDirectory)
        {
            try
            {
                var path = Path.Combine(baseDirectory, FASTGITHUB_EXE);
                if (File.Exists(path) == false)
                {
                    return null;
                }

                var version = FileVersionInfo.GetVersionInfo(path);
                return FormatVersion(version.ProductVersion ?? version.FileVersion);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 把 "2.4.6+4343d01e1721fa16..." 压成 "2.4.6（4343d01）"
        /// 完整提交号有40位，直接显示会把这一行撑得很长
        /// </summary>
        /// <param name="raw"></param>
        /// <returns></returns>
        private static string? FormatVersion(string? raw)
        {
            if (string.IsNullOrEmpty(raw) == true)
            {
                return raw;
            }

            var plus = raw!.IndexOf('+');
            if (plus <= 0)
            {
                return raw;
            }

            var head = raw.Substring(0, plus);
            var hash = raw.Substring(plus + 1);
            if (hash.Length > 7)
            {
                hash = hash.Substring(0, 7);
            }
            return $"{head}（{hash}）";
        }

        private void RefreshStateButton_Click(object sender, RoutedEventArgs e)
        {
            this.RefreshBackendState();
        }

        private void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(RELEASES_URI);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法打开浏览器：{ex.Message}", "检测更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
        {
            this.OpenDirectory(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs"));
        }

        private void OpenConfigButton_Click(object sender, RoutedEventArgs e)
        {
            this.OpenDirectory(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings"));
        }

        private void OpenRootButton_Click(object sender, RoutedEventArgs e)
        {
            this.OpenDirectory(AppDomain.CurrentDomain.BaseDirectory);
        }

        private void OpenCacheButton_Click(object sender, RoutedEventArgs e)
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ipaddresses.json");
            if (File.Exists(path) == false)
            {
                MessageBox.Show("还没有生成 IP 缓存文件，执行一次「立即更新 IP」后再看。", "查看 IP 缓存", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法打开文件：{ex.Message}", "查看 IP 缓存", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// 打开目录，不存在时给出提示而不是静默失败
        /// </summary>
        /// <param name="path"></param>
        private void OpenDirectory(string path)
        {
            if (Directory.Exists(path) == false)
            {
                MessageBox.Show($"目录不存在：{path}\n\n程序首次运行或使用自定义路径时可能出现，请确认程序是否已正常启动。", "打开目录", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                Process.Start("explorer.exe", $"\"{path}\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法打开目录：{ex.Message}", "打开目录", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}

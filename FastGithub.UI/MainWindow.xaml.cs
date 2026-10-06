using System;
using System.Configuration;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace FastGithub.UI
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly System.Windows.Forms.NotifyIcon notifyIcon;
        private const string FASTGITHUB_UI = "FastGithub.UI";
        private const string RELEASES_URI = "https://github.com/yuan71058/FastGithub/releases";

        public MainWindow()
        {
            InitializeComponent();

            var upgrade = new System.Windows.Forms.MenuItem("检测更新(&U)");
            upgrade.Click += (s, e) => Process.Start(RELEASES_URI);

            var refreshIp = new System.Windows.Forms.MenuItem("更新IP(&R)");
            refreshIp.Click += async (s, e) => await this.RefreshIpAsync();

            var settings = new System.Windows.Forms.MenuItem("设置(&S)");
            settings.Click += (s, e) =>
            {
                var settingsWindow = new SettingsWindow();
                settingsWindow.Owner = this.IsVisible ? this : null;
                settingsWindow.ShowDialog();
            };

            var exit = new System.Windows.Forms.MenuItem("关闭应用(&C)");
            exit.Click += (s, e) => this.Close();

            var version = this.GetType().Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            this.Title = $"{FASTGITHUB_UI} v{version}";
            this.notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                Visible = true,
                Text = FASTGITHUB_UI,
                ContextMenu = new System.Windows.Forms.ContextMenu(new[] { refreshIp, upgrade, settings, exit }),
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath)
            };

            this.notifyIcon.MouseClick += (s, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left)
                {
                    this.Show();
                    this.Activate();
                    this.WindowState = WindowState.Normal;
                }
            };

            if (Properties.Settings.Default.StartMinimized)
            {
                this.Hide();
            }
        }

        /// <summary>
        /// 通过UI内部通信端口请求fastgithub刷新IP
        /// </summary>
        /// <returns></returns>
        private async Task RefreshIpAsync()
        {
            this.ShowStatus("正在更新IP，请稍候…");
            try
            {
                var baseUri = await UiApi.GetBaseUriAsync();
                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60d) };
                var requestTask = httpClient.GetStringAsync($"{baseUri}/refresh-ip");

                // 后端刷新较慢时先给一次反馈，避免点击后长时间没有任何提示
                var completedTask = await Task.WhenAny(requestTask, Task.Delay(TimeSpan.FromSeconds(3d)));
                if (completedTask != requestTask)
                {
                    this.ShowStatus("IP更新进行中，后端正在清空缓存并重新解析测速…");
                    this.notifyIcon.ShowBalloonTip(3000, FASTGITHUB_UI, "IP更新进行中，正在重新解析与测速…", System.Windows.Forms.ToolTipIcon.Info);
                }

                var json = await requestTask;

                RefreshIpResult? result = null;
                try
                {
                    result = Newtonsoft.Json.JsonConvert.DeserializeObject<RefreshIpResult>(json);
                }
                catch (Newtonsoft.Json.JsonException)
                {
                }

                var message = "已触发IP更新，但后端未返回刷新结果（后端可能为旧版本）";
                if (result != null)
                {
                    message = $"IP更新完成：{result.DomainCount}个域名、{result.AddressCount}个可用IP";
                    message += result.HostsUpdated == true
                        ? $"（在线hosts源{result.HostsDomainCount}个域名）"
                        : $"（在线hosts源更新失败：{result.HostsError}）";
                }

                if (UiApi.Detected == false)
                {
                    message += "；未探测到新版后端，请确认 fastgithub.exe 已更新并正在运行";
                }

                this.ShowStatus(message);
                this.notifyIcon.ShowBalloonTip(5000, FASTGITHUB_UI, message, System.Windows.Forms.ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                var message = $"IP更新失败：{ex.Message}";
                this.ShowStatus(message);
                this.notifyIcon.ShowBalloonTip(5000, FASTGITHUB_UI, message, System.Windows.Forms.ToolTipIcon.Error);
            }
        }

        /// <summary>
        /// 在主窗口底部显示状态
        /// </summary>
        /// <param name="message"></param>
        private void ShowStatus(string message)
        {
            var text = $"{DateTime.Now:HH:mm:ss} {message}";
            this.Dispatcher.Invoke(new Action(() =>
            {
                this.textBlockStatus.Text = text;
                this.borderStatus.Visibility = Visibility.Visible;
            }));
        }

        /// <summary>
        /// 拦截最小化事件
        /// </summary>
        /// <param name="e"></param>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwndSource = (HwndSource)PresentationSource.FromVisual(this);
            hwndSource.AddHook(WndProc);

            IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
            {
                const int WM_SYSCOMMAND = 0x112;
                const int SC_MINIMIZE = 0xf020;
                const int SC_CLOSE = 0xf060;

                if (msg == WM_SYSCOMMAND)
                {
                    if (wParam.ToInt32() == SC_MINIMIZE || wParam.ToInt32() == SC_CLOSE)
                    {
                        this.Hide();
                        handled = true;
                    }
                }
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// 关闭时
        /// </summary>
        /// <param name="e"></param>
        protected override void OnClosed(EventArgs e)
        {
            this.notifyIcon.Icon = null;
            this.notifyIcon.Dispose();
            base.OnClosed(e);
        }
    }
}

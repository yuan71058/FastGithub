using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace FastGithub.UI
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly System.Windows.Forms.NotifyIcon notifyIcon;
        private readonly DispatcherTimer autoRefreshTimer;
        private const string FASTGITHUB_UI = "FastGithub.UI";
        private const string RELEASES_URI = "https://gitee.com/yuan71058/fast-github/releases";

        /// <summary>
        /// 启动后自动更新IP前的等待时间，留出后端加载驱动与首轮初始化的时间
        /// </summary>
        private static readonly TimeSpan startupRefreshDelay = TimeSpan.FromSeconds(15d);

        public MainWindow()
        {
            InitializeComponent();

            var upgrade = new System.Windows.Forms.MenuItem("检测更新(&U)");
            upgrade.Click += (s, e) => Process.Start(RELEASES_URI);

            var refreshIp = new System.Windows.Forms.MenuItem("更新IP(&R)");
            refreshIp.Click += async (s, e) => await IpRefreshService.RunAsync();

            var settings = new System.Windows.Forms.MenuItem("设置(&S)");
            settings.Click += (s, e) =>
            {
                var settingsWindow = new SettingsWindow();
                settingsWindow.Owner = this.IsVisible ? this : null;
                settingsWindow.ShowDialog();
            };

            var exit = new System.Windows.Forms.MenuItem("关闭应用(&C)");
            exit.Click += (s, e) => this.Close();

            var version = this.GetType().Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0];
            this.Title = $"{FASTGITHUB_UI} v{version}";
            this.textBlockVersion.Text = $"{FASTGITHUB_UI} v{version}";
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

            // 托盘气泡会先判断设置里的开关，所以先把订阅挂上再触发任何刷新
            IpRefreshService.Reported += this.OnRefreshReported;
            // ApplicationSettingsBase 的 PropertyChanged 是显式实现，必须转成接口再订阅
            ((INotifyPropertyChanged)Properties.Settings.Default).PropertyChanged += this.OnSettingChanged;

            this.autoRefreshTimer = new DispatcherTimer();
            this.autoRefreshTimer.Tick += async (s, e) => await this.OnAutoRefreshTickAsync();
            this.ResetAutoRefreshTimer();

            if (Properties.Settings.Default.StartMinimized)
            {
                this.Hide();
            }

            if (Properties.Settings.Default.AutoRefreshOnStart)
            {
                this.RunStartupRefreshAsync();
            }
        }

        /// <summary>
        /// 启动后延迟自动更新一次IP
        /// </summary>
        /// <returns></returns>
        private async void RunStartupRefreshAsync()
        {
            await Task.Delay(startupRefreshDelay);
            await IpRefreshService.RunAsync();
        }

        /// <summary>
        /// 自动更新间隔到点
        /// </summary>
        /// <returns></returns>
        private async Task OnAutoRefreshTickAsync()
        {
            // 手动刷新期间不叠加另一次刷新
            if (IpRefreshService.IsRunning == true)
            {
                return;
            }

            await IpRefreshService.RunAsync();
        }

        /// <summary>
        /// 按当前设置重建自动更新定时器
        /// </summary>
        private void ResetAutoRefreshTimer()
        {
            this.autoRefreshTimer.Stop();

            var hours = Properties.Settings.Default.AutoRefreshHours;
            if (hours <= 0)
            {
                return;
            }

            this.autoRefreshTimer.Interval = TimeSpan.FromHours(hours);
            this.autoRefreshTimer.Start();
        }

        /// <summary>
        /// 设置项变化时同步定时器
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnSettingChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Properties.Settings.Default.AutoRefreshHours))
            {
                this.Dispatcher.Invoke(new Action(this.ResetAutoRefreshTimer));
            }
        }

        /// <summary>
        /// IP刷新的反馈统一显示在状态栏，必要时弹托盘气泡
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnRefreshReported(object sender, IpRefreshEventArgs e)
        {
            this.ShowStatus(e.Message);

            if (e.IsFinal == true && Properties.Settings.Default.ShowBalloon == true)
            {
                this.notifyIcon.ShowBalloonTip(5000, FASTGITHUB_UI, e.Message, e.Icon);
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
            this.autoRefreshTimer.Stop();
            IpRefreshService.Reported -= this.OnRefreshReported;
            ((INotifyPropertyChanged)Properties.Settings.Default).PropertyChanged -= this.OnSettingChanged;
            this.notifyIcon.Icon = null;
            this.notifyIcon.Dispose();
            base.OnClosed(e);
        }
    }
}

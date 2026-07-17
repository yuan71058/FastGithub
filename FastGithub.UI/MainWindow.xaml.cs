using System;
using System.Configuration;
using System.Diagnostics;
using System.Reflection;
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
                ContextMenu = new System.Windows.Forms.ContextMenu(new[] { upgrade, settings, exit }),
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

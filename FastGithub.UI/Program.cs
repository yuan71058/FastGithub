using Microsoft.Win32;
using System;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;

namespace FastGithub.UI
{
    class Program
    {
        private const string MUTEX_NAME = "Global\\FastGithub.UI";
        private const string MAIN_WINDOWS = "MainWindow.xaml";
        private const string FASTGITHUB_PATH = "fastgithub.exe";

        [STAThread]
        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
            using var mutex = new Mutex(true, MUTEX_NAME, out var isFirstInstance);
            if (isFirstInstance == false)
            {
                return;
            }

            var settings = Properties.Settings.Default;
            if (settings.AutoStart)
            {
                SetAutoStart(true);
            }
            else
            {
                SetAutoStart(false);
            }

            StartFastGithub();
            SetWebBrowserDPI();
            SetWebBrowserVersion();

            var app = new Application();
            app.StartupUri = new Uri(MAIN_WINDOWS, UriKind.Relative);
            app.Run();
        }

        /// <summary>
        /// 程序集加载失败时
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        /// <returns></returns>
        private static Assembly? OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            var name = new AssemblyName(args.Name).Name;
            if (name.EndsWith(".resources"))
            {
                return default;
            }

            var stream = Application.GetResourceStream(new Uri($"Resource/{name}.dll", UriKind.Relative)).Stream;
            var buffer = new byte[stream.Length];
            stream.Read(buffer, 0, buffer.Length);
            return Assembly.Load(buffer);
        }

        /// <summary>
        /// 设置浏览器版本
        /// </summary>
        private static void SetWebBrowserVersion()
        {
            const string subKey = @"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION";
            var registryKey = Registry.CurrentUser.OpenSubKey(subKey, true);
            if (registryKey == null)
            {
                registryKey = Registry.CurrentUser.CreateSubKey(subKey);
            }
            var name = $"{Process.GetCurrentProcess().ProcessName}.exe";
            using var webBrowser = new System.Windows.Forms.WebBrowser();
            var value = int.Parse($"{webBrowser.Version.Major}000");
            registryKey.SetValue(name, value, RegistryValueKind.DWord);
        }

        /// <summary>
        /// 设置浏览器DPI
        /// </summary>
        private static void SetWebBrowserDPI()
        {
            const string subKey = @"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_96DPI_PIXEL";
            var registryKey = Registry.CurrentUser.OpenSubKey(subKey, true);
            if (registryKey == null)
            {
                registryKey = Registry.CurrentUser.CreateSubKey(subKey);
            }
            var name = $"{Process.GetCurrentProcess().ProcessName}.exe";
            registryKey.SetValue(name, 1, RegistryValueKind.DWord);
        }

        /// <summary>
        /// 启动fastgithub
        /// </summary>
        /// <returns></returns>
        private static void StartFastGithub()
        {
            var fastgithubPath = FindFastGithubPath();
            if (fastgithubPath == null)
            {
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = fastgithubPath,
                Arguments = $"--ParentProcessId={Process.GetCurrentProcess().Id} --UdpLoggerPort={UdpLogger.Port}",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(startInfo);
        }

        /// <summary>
        /// 查找fastgithub.exe路径
        /// </summary>
        /// <returns></returns>
        private static string? FindFastGithubPath()
        {
            // 生产环境：与UI同目录
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var localPath = Path.Combine(baseDir, FASTGITHUB_PATH);
            if (File.Exists(localPath))
            {
                return localPath;
            }

            // 开发环境：向上查找FastGithub项目输出目录
            var searchDir = baseDir;
            for (var i = 0; i < 10; i++)
            {
                searchDir = Path.GetDirectoryName(searchDir);
                if (searchDir == null)
                {
                    break;
                }

                var devPath = Path.Combine(searchDir, "FastGithub", "bin", "Debug", "net7.0", "win-x64", FASTGITHUB_PATH);
                if (File.Exists(devPath))
                {
                    return devPath;
                }

                devPath = Path.Combine(searchDir, "FastGithub", "bin", "Release", "net7.0", "win-x64", FASTGITHUB_PATH);
                if (File.Exists(devPath))
                {
                    return devPath;
                }
            }

            return null;
        }

        /// <summary>
        /// 设置开机自启
        /// </summary>
        /// <param name="enable"></param>
        public static void SetAutoStart(bool enable)
        {
            const string appName = "FastGithub.UI";
            const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
            using var key = Registry.CurrentUser.OpenSubKey(runKey, true);
            if (key == null)
            {
                return;
            }

            if (enable)
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (exePath != null)
                {
                    key.SetValue(appName, exePath);
                }
            }
            else
            {
                key.DeleteValue(appName, false);
            }
        }
    }
}

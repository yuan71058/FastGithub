using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FastGithub.UI
{
    /// <summary>
    /// IP刷新的一次反馈
    /// </summary>
    sealed class IpRefreshEventArgs : EventArgs
    {
        public IpRefreshEventArgs(string message, ToolTipIcon icon, bool isFinal)
        {
            this.Message = message;
            this.Icon = icon;
            this.IsFinal = isFinal;
        }

        /// <summary>
        /// 提示文本
        /// </summary>
        public string Message { get; }

        /// <summary>
        /// 提示图标
        /// </summary>
        public ToolTipIcon Icon { get; }

        /// <summary>
        /// 是否为最终结果，false表示只是过程中的进度反馈
        /// </summary>
        public bool IsFinal { get; }
    }

    /// <summary>
    /// IP刷新的统一入口
    /// 托盘菜单、设置页按钮、自动定时器都走这里，避免三处各写一套请求与提示逻辑
    /// </summary>
    static class IpRefreshService
    {
        /// <summary>
        /// 刷新过程与结果的反馈
        /// </summary>
        public static event EventHandler<IpRefreshEventArgs>? Reported;

        /// <summary>
        /// 是否有刷新正在进行
        /// </summary>
        public static bool IsRunning { get; private set; }

        /// <summary>
        /// 上一次刷新完成的时间，从未刷新为null
        /// </summary>
        public static DateTime? LastCompletedTime { get; private set; }

        /// <summary>
        /// 上一次刷新是否成功
        /// </summary>
        public static bool LastSucceeded { get; private set; }

        /// <summary>
        /// 触发一次IP刷新
        /// </summary>
        /// <returns></returns>
        public static async Task RunAsync()
        {
            if (IsRunning == true)
            {
                Report("IP更新正在进行中，请稍候…", ToolTipIcon.Info, false);
                return;
            }

            IsRunning = true;
            try
            {
                Report("正在更新IP，请稍候…", ToolTipIcon.Info, false);

                var baseUri = await UiApi.GetBaseUriAsync();
                // 后端刷新有45秒预算，这里的超时必须大于它，否则后端还没返回就被UI取消
                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120d) };
                var requestTask = httpClient.GetStringAsync($"{baseUri}/refresh-ip");

                // 后端刷新较慢时先给一次反馈，避免点击后长时间没有任何提示
                var completedTask = await Task.WhenAny(requestTask, Task.Delay(TimeSpan.FromSeconds(3d)));
                if (completedTask != requestTask)
                {
                    Report("IP更新进行中，后端正在清空缓存并重新解析测速…", ToolTipIcon.Info, false);
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
                var succeeded = false;

                if (result != null)
                {
                    if (string.IsNullOrEmpty(result.Error) == false)
                    {
                        message = $"IP更新失败：{result.Error}";
                    }
                    else
                    {
                        succeeded = true;
                        message = result.TimedOut
                            ? $"IP更新部分完成（超时结束）：{result.DomainCount}个域名、{result.AddressCount}个可用IP"
                            : $"IP更新完成：{result.DomainCount}个域名、{result.AddressCount}个可用IP";

                        message += result.HostsUpdated == true
                            ? $"（在线hosts源{result.HostsDomainCount}个域名）"
                            : $"（在线hosts源更新失败：{result.HostsError}）";

                        if (result.TimedOut == true)
                        {
                            message += "；未完成的域名保留原IP，可稍后再次更新";
                        }
                    }
                }

                if (UiApi.Detected == false)
                {
                    message += "；未探测到新版后端，请确认 fastgithub.exe 已更新并正在运行";
                }

                Finish(message, succeeded ? ToolTipIcon.Info : ToolTipIcon.Warning, succeeded);
            }
            catch (TaskCanceledException)
            {
                Finish("IP更新超时：后端120秒内未返回，刷新可能仍在后台继续，稍后可直接使用", ToolTipIcon.Warning, false);
            }
            catch (Exception ex)
            {
                Finish($"IP更新失败：{ex.Message}", ToolTipIcon.Error, false);
            }
            finally
            {
                IsRunning = false;
            }
        }

        /// <summary>
        /// 上报一次最终结果
        /// </summary>
        /// <param name="message"></param>
        /// <param name="icon"></param>
        /// <param name="succeeded"></param>
        private static void Finish(string message, ToolTipIcon icon, bool succeeded)
        {
            LastCompletedTime = DateTime.Now;
            LastSucceeded = succeeded;
            Report(message, icon, true);
        }

        /// <summary>
        /// 上报一次反馈
        /// </summary>
        /// <param name="message"></param>
        /// <param name="icon"></param>
        /// <param name="isFinal"></param>
        private static void Report(string message, ToolTipIcon icon, bool isFinal)
        {
            Reported?.Invoke(null, new IpRefreshEventArgs(message, icon, isFinal));
        }
    }
}

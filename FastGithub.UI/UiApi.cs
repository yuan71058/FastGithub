using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace FastGithub.UI
{
    /// <summary>
    /// 后端内部通信接口
    /// </summary>
    static class UiApi
    {
        private const int DEFAULT_PORT = 45678;
        private const int PROBE_COUNT = 10;

        private static readonly HttpClient probeClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2d) };
        private static int? port;

        /// <summary>
        /// 是否探测到支持/ping的后端，false表示后端未启动或为旧版本
        /// </summary>
        public static bool Detected { get; private set; }

        /// <summary>
        /// 获取后端内部通信基址
        /// 后端端口被占用时会顺延，这里从默认端口向上探测实际监听的端口
        /// </summary>
        /// <returns></returns>
        public static async Task<string> GetBaseUriAsync()
        {
            var value = await GetPortAsync().ConfigureAwait(false);
            return $"http://localhost:{value}";
        }

        /// <summary>
        /// 获取后端内部通信端口
        /// </summary>
        /// <returns></returns>
        private static async Task<int> GetPortAsync()
        {
            if (port.HasValue == true)
            {
                return port.Value;
            }

            for (var value = DEFAULT_PORT; value < DEFAULT_PORT + PROBE_COUNT; value++)
            {
                try
                {
                    using var response = await probeClient.GetAsync($"http://localhost:{value}/ping").ConfigureAwait(false);
                    var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (response.IsSuccessStatusCode == true && content.Contains("FastGithub") == true)
                    {
                        Detected = true;
                        port = value;
                        return value;
                    }
                }
                catch (Exception)
                {
                }
            }

            port = DEFAULT_PORT;
            return DEFAULT_PORT;
        }
    }
}

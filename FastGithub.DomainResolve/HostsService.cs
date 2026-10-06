using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// 在线hosts源刷新结果
    /// </summary>
    public sealed class HostsRefreshResult
    {
        /// <summary>
        /// 是否更新成功
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// hosts源包含的域名数量
        /// </summary>
        public int DomainCount { get; set; }

        /// <summary>
        /// 更新失败的原因
        /// </summary>
        public string? Error { get; set; }
    }

    /// <summary>
    /// 在线hosts源解析服务
    /// 定时从远程hosts地址拉取“域名->IP”映射，作为DNS解析之外的额外候选IP来源
    /// </summary>
    sealed class HostsService
    {
        private const string DEFAULT_HOSTS_URL = "https://raw.hellogithub.com/hosts.json";

        private readonly HttpClient httpClient;
        private readonly ILogger<HostsService> logger;
        private readonly ConcurrentDictionary<string, IReadOnlyList<IPAddress>> mapping = new();

        /// <summary>
        /// 在线hosts源解析服务
        /// </summary>
        /// <param name="logger"></param>
        public HostsService(ILogger<HostsService> logger)
        {
            this.logger = logger;
            this.httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10d) };
        }

        /// <summary>
        /// 刷新hosts映射，拉取远程hosts源并解析为“域名->IP”缓存
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<HostsRefreshResult> RefreshAsync(CancellationToken cancellationToken)
        {
            try
            {
                var content = await this.httpClient.GetStringAsync(DEFAULT_HOSTS_URL, cancellationToken);
                var map = Parse(content);

                this.mapping.Clear();
                foreach (var item in map)
                {
                    this.mapping[item.Key] = item.Value;
                }
                this.logger.LogInformation($"已更新在线hosts解析，共{this.mapping.Count}个域名");
                return new HostsRefreshResult { Success = true, DomainCount = this.mapping.Count };
            }
            catch (Exception ex)
            {
                this.logger.LogWarning($"在线hosts源更新失败：{ex.Message}");
                return new HostsRefreshResult { Success = false, DomainCount = this.mapping.Count, Error = ex.Message };
            }
        }

        /// <summary>
        /// 获取指定域名在hosts源中的IP地址
        /// </summary>
        /// <param name="host">域名</param>
        /// <param name="addresses">命中的IP列表</param>
        /// <returns></returns>
        public bool TryGetAddresses(string host, out IReadOnlyList<IPAddress> addresses)
        {
            return this.mapping.TryGetValue(host, out addresses!);
        }

        /// <summary>
        /// 指定域名是否被hosts源覆盖
        /// </summary>
        /// <param name="host">域名</param>
        /// <returns></returns>
        public bool IsCovered(string host)
        {
            return this.mapping.TryGetValue(host, out var addresses) && addresses.Count > 0;
        }

        /// <summary>
        /// 解析hosts源内容，支持json数组与hosts文本两种格式
        /// </summary>
        /// <param name="content">hosts源内容</param>
        /// <returns>域名->IP映射</returns>
        private static Dictionary<string, IReadOnlyList<IPAddress>> Parse(string content)
        {
            var trimmed = content.TrimStart();
            return trimmed.StartsWith('[') || trimmed.StartsWith('{')
                ? ParseJson(content)
                : ParseText(content);
        }

        /// <summary>
        /// 解析json格式：[["ip","domain"],...]
        /// </summary>
        /// <param name="content"></param>
        /// <returns></returns>
        private static Dictionary<string, IReadOnlyList<IPAddress>> ParseJson(string content)
        {
            var map = new Dictionary<string, List<IPAddress>>();
            try
            {
                var items = JsonSerializer.Deserialize<string[][]>(content);
                if (items == null)
                {
                    return ToReadOnly(map);
                }

                foreach (var item in items)
                {
                    if (item == null || item.Length < 2)
                    {
                        continue;
                    }

                    if (IPAddress.TryParse(item[0], out var ip) == false)
                    {
                        continue;
                    }

                    var host = item[1].Trim().ToLowerInvariant();
                    if (host.Length == 0)
                    {
                        continue;
                    }

                    if (map.TryGetValue(host, out var list) == false)
                    {
                        list = new List<IPAddress>();
                        map[host] = list;
                    }

                    if (list.Contains(ip) == false)
                    {
                        list.Add(ip);
                    }
                }
            }
            catch (JsonException)
            {
            }
            return ToReadOnly(map);

            static Dictionary<string, IReadOnlyList<IPAddress>> ToReadOnly(Dictionary<string, List<IPAddress>> map)
            {
                return map.ToDictionary(item => item.Key, item => (IReadOnlyList<IPAddress>)item.Value);
            }
        }

        /// <summary>
        /// 解析hosts文本内容
        /// </summary>
        /// <param name="content"></param>
        /// <returns></returns>
        private static Dictionary<string, IReadOnlyList<IPAddress>> ParseText(string content)
        {
            var map = new Dictionary<string, List<IPAddress>>();
            foreach (var line in content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    continue;
                }

                var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || IPAddress.TryParse(parts[0], out var ip) == false)
                {
                    continue;
                }

                var host = parts[1].Trim().ToLowerInvariant();
                if (map.TryGetValue(host, out var list) == false)
                {
                    list = new List<IPAddress>();
                    map[host] = list;
                }
                list.Add(ip);
            }
            return map.ToDictionary(item => item.Key, item => (IReadOnlyList<IPAddress>)item.Value);
        }
    }
}
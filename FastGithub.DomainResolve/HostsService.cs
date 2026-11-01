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
        /// <summary>
        /// 在线hosts源列表，按顺序尝试后合并
        /// 单一源一旦下线，候选池就只剩被污染的DNS结果，所以这里保留多个来源互为备份。
        /// 两个源都是非GitHub域名，拉取本项配置本身不受GitHub连通性影响。
        /// </summary>
        private static readonly string[] HOSTS_URLS =
        {
            "https://raw.hellogithub.com/hosts.json",
            "https://hosts.gitcdn.top/hosts.txt",
        };

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
            this.httpClient = new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                Timeout = TimeSpan.FromSeconds(20d)
            };
        }

        /// <summary>
        /// 刷新hosts映射，拉取全部远程hosts源并合并为“域名->IP”缓存
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<HostsRefreshResult> RefreshAsync(CancellationToken cancellationToken)
        {
            var merged = new Dictionary<string, IReadOnlyList<IPAddress>>(StringComparer.OrdinalIgnoreCase);
            var errors = new List<string>();
            var successCount = 0;

            // 并行拉取：串行时每个慢源都要额外等一轮超时，总耗时会叠加到几十秒
            var fetches = await Task.WhenAll(HOSTS_URLS.Select(url => this.FetchAsync(url, cancellationToken)));

            foreach (var fetch in fetches)
            {
                if (fetch.Content == null)
                {
                    errors.Add($"{fetch.Url}: {fetch.Error}");
                    this.logger.LogWarning($"在线hosts源{fetch.Url}更新失败：{fetch.Error}");
                    continue;
                }

                var map = Parse(fetch.Content);
                if (map.Count == 0)
                {
                    errors.Add($"{fetch.Url}: 未解析出域名");
                    continue;
                }

                successCount++;
                foreach (var item in map)
                {
                    if (merged.TryGetValue(item.Key, out var exists) == false)
                    {
                        merged[item.Key] = item.Value;
                    }
                    else
                    {
                        merged[item.Key] = exists.Concat(item.Value).Distinct().ToArray();
                    }
                }
            }

            // 全部源都失败时保留上一次的映射，不能因为源故障就把候选池清空
            if (successCount == 0)
            {
                this.logger.LogWarning($"在线hosts源全部更新失败：{string.Join("; ", errors)}");
                return new HostsRefreshResult
                {
                    Success = false,
                    DomainCount = this.mapping.Count,
                    Error = string.Join("; ", errors)
                };
            }

            this.mapping.Clear();
            foreach (var item in merged)
            {
                this.mapping[item.Key] = item.Value;
            }

            this.logger.LogInformation($"已更新在线hosts解析，{successCount}/{HOSTS_URLS.Length}个源、共{this.mapping.Count}个域名");
            return new HostsRefreshResult
            {
                Success = true,
                DomainCount = this.mapping.Count,
                Error = errors.Count == 0 ? null : string.Join("; ", errors)
            };
        }

        /// <summary>
        /// 拉取单个hosts源
        /// </summary>
        /// <param name="url"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<(string Url, string? Content, string? Error)> FetchAsync(string url, CancellationToken cancellationToken)
        {
            try
            {
                var content = await this.httpClient.GetStringAsync(url, cancellationToken);
                return (url, content, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (url, null, ex.Message);
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
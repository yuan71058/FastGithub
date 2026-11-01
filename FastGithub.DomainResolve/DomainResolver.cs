using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// 域名解析器
    /// </summary> 
    sealed class DomainResolver : IDomainResolver
    {
        private const int MAX_IP_COUNT = 3;
        private const int MAX_CONCURRENCY = 12;
        private const int MAX_MERGED_ADDRESS_COUNT = 8;
        private readonly DnsClient dnsClient;
        private readonly PersistenceService persistence;
        private readonly IPAddressService addressService;
        private readonly HostsService hostsService;
        private readonly ILogger<DomainResolver> logger;
        private readonly ConcurrentDictionary<DnsEndPoint, IPAddress[]> dnsEndPointAddress = new();

        /// <summary>
        /// 域名解析器
        /// </summary>
        /// <param name="dnsClient"></param>
        /// <param name="persistence"></param>
        /// <param name="addressService"></param>
        /// <param name="hostsService"></param>
        /// <param name="logger"></param>
        public DomainResolver(
            DnsClient dnsClient,
            PersistenceService persistence,
            IPAddressService addressService,
            HostsService hostsService,
            ILogger<DomainResolver> logger)
        {
            this.dnsClient = dnsClient;
            this.persistence = persistence;
            this.addressService = addressService;
            this.hostsService = hostsService;
            this.logger = logger;

            foreach (var endPoint in persistence.ReadDnsEndPoints())
            {
                this.dnsEndPointAddress.TryAdd(endPoint, Array.Empty<IPAddress>());
            }
        }

        /// <summary>
        /// 解析域名
        /// </summary>
        /// <param name="endPoint">节点</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async IAsyncEnumerable<IPAddress> ResolveAsync(DnsEndPoint endPoint, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (this.dnsEndPointAddress.TryGetValue(endPoint, out var addresses) && addresses.Length > 0)
            {
                foreach (var address in addresses)
                {
                    yield return address;
                }
            }
            else
            {
                if (this.dnsEndPointAddress.TryAdd(endPoint, Array.Empty<IPAddress>()))
                {
                    await this.persistence.WriteDnsEndPointsAsync(this.dnsEndPointAddress.Keys, cancellationToken);
                }

                await foreach (var adddress in this.dnsClient.ResolveAsync(endPoint, fastSort: true, cancellationToken))
                {
                    yield return adddress;
                }
            }
        }

        /// <summary>
        /// 对所有节点进行测速
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task TestSpeedAsync(CancellationToken cancellationToken = default)
        {
            await this.TestSpeedCoreAsync(cancellationToken);
        }

        /// <summary>
        /// 对所有节点进行并发测速
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns>参与测速的域名数量、可用IP数量、是否因超时提前结束</returns>
        private async Task<(int DomainCount, int AddressCount, bool TimedOut)> TestSpeedCoreAsync(CancellationToken cancellationToken)
        {
            var domainCount = 0;
            var addressCount = 0;
            var timedOut = false;

            var keyValues = this.dnsEndPointAddress.OrderBy(item => item.Value.Length).ToArray();
            // 不Dispose：软超时返回后后台任务还要Release，提前Dispose会抛ObjectDisposedException
            var concurrency = new SemaphoreSlim(MAX_CONCURRENCY);

            var tasks = keyValues.Select(async keyValue =>
            {
                await concurrency.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    var dnsEndPoint = keyValue.Key;
                    var oldAddresses = keyValue.Value;

                    var newAddresses = await this.addressService.GetAddressesAsync(dnsEndPoint, oldAddresses, cancellationToken).ConfigureAwait(false);

                    // 网络抖动时探测可能把所有IP都判为不可用，此时必须保留原IP：
                    // 用空列表覆盖会让该域名彻底没有可用地址，刷新后反而更糟
                    if (newAddresses.Length > 0)
                    {
                        this.dnsEndPointAddress[dnsEndPoint] = MergeAddresses(newAddresses, oldAddresses);
                    }
                    else
                    {
                        newAddresses = oldAddresses.ToArray();
                    }

                    Interlocked.Add(ref domainCount, 1);
                    Interlocked.Add(ref addressCount, newAddresses.Length);

                    var oldSegmentums = oldAddresses.Take(MAX_IP_COUNT);
                    var newSegmentums = newAddresses.Take(MAX_IP_COUNT);
                    if (oldSegmentums.SequenceEqual(newSegmentums) == false)
                    {
                        var addressArray = string.Join(", ", newSegmentums.Select(item => item.ToString()));
                        this.logger.LogInformation($"{dnsEndPoint.Host}:{dnsEndPoint.Port}->[{addressArray}]");
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    this.logger.LogWarning($"{keyValue.Key.Host}测速异常：{ex.Message}");
                }
                finally
                {
                    concurrency.Release();
                }
            }).ToArray();

            var allTask = Task.WhenAll(tasks);

            // 用软超时：预算用完立即返回已完成的部分，剩余域名在后台继续跑完。
            // 直接取消会让正在进行的DNS查询拖到自然超时才结束，请求被拖成70秒以上。
            var cancelSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => cancelSource.TrySetResult(true)))
            {
                var completedTask = await Task.WhenAny(allTask, cancelSource.Task).ConfigureAwait(false);
                if (completedTask != allTask)
                {
                    timedOut = true;
                    this.logger.LogWarning($"测速未在预算内完成：{domainCount}/{keyValues.Length}个域名，已完成的IP直接生效，其余继续在后台完成");
                    // 观察后台任务的异常，避免未观察异常
                    _ = allTask.ContinueWith(task => { _ = task.Exception; }, TaskScheduler.Default);
                }
            }

            return (domainCount, addressCount, timedOut);
        }

        /// <summary>
        /// 刷新所有域名的IP（清空所有缓存并重新解析测速）
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<RefreshIpResult> RefreshAsync(CancellationToken cancellationToken = default)
        {
            this.logger.LogInformation("触发IP刷新：清空缓存并重新解析测速");
            this.ClearCache();

            var (domainCount, addressCount, timedOut) = await this.TestSpeedCoreAsync(cancellationToken);
            var hosts = await this.hostsService.RefreshAsync(CancellationToken.None);
            return this.CreateResult(domainCount, addressCount, hosts, timedOut);
        }

        /// <summary>
        /// 刷新所有域名的IP（优先使用在线hosts源，未覆盖的域名回退DNS查询）
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<RefreshIpResult> RefreshHostsAsync(CancellationToken cancellationToken = default)
        {
            this.logger.LogInformation("手动触发IP刷新：优先使用在线hosts源");
            this.ClearCache();

            var hosts = await this.hostsService.RefreshAsync(CancellationToken.None);
            var (domainCount, addressCount, timedOut) = await this.TestSpeedCoreAsync(cancellationToken);
            return this.CreateResult(domainCount, addressCount, hosts, timedOut);
        }

        /// <summary>
        /// 合并新旧候选IP：新结果优先，旧结果中未重复的追加在后面。
        /// 刷新时若探测网络抖动导致只选出1个IP，直接用新结果覆盖会让候选池塌缩，
        /// 一旦这个IP随后不可用，该域名就没有任何备选，表现为"更新IP后反而打不开"。
        /// </summary>
        /// <param name="newAddresses">本次探测出的可用IP</param>
        /// <param name="oldAddresses">刷新前的候选IP</param>
        /// <returns></returns>
        private static IPAddress[] MergeAddresses(IPAddress[] newAddresses, IReadOnlyCollection<IPAddress> oldAddresses)
        {
            if (oldAddresses.Count == 0)
            {
                return newAddresses;
            }

            var merged = new List<IPAddress>(newAddresses.Length + oldAddresses.Count);
            var exists = new HashSet<IPAddress>();

            foreach (var address in newAddresses)
            {
                if (exists.Add(address) == true)
                {
                    merged.Add(address);
                }
            }

            foreach (var address in oldAddresses)
            {
                if (merged.Count >= MAX_MERGED_ADDRESS_COUNT)
                {
                    break;
                }

                if (exists.Add(address) == true)
                {
                    merged.Add(address);
                }
            }

            return merged.ToArray();
        }

        /// <summary>
        /// 清空IP缓存与DNS解析缓存，强制后续重新解析与测速
        /// </summary>
        private void ClearCache()
        {
            this.addressService.ClearCache();
            this.dnsClient.ClearCache();
        }

        /// <summary>
        /// 上报请求阶段连接失败的IP，使其短期不再被优先选中
        /// </summary>
        /// <param name="address">失败的IP</param>
        public void ReportFailure(IPAddress address)
        {
            this.addressService.ReportFailure(address);

            foreach (var keyValue in this.dnsEndPointAddress)
            {
                if (keyValue.Value.Contains(address))
                {
                    this.dnsEndPointAddress[keyValue.Key] = keyValue.Value
                        .Where(item => item.Equals(address) == false)
                        .ToArray();
                }
            }
        }

        /// <summary>
        /// 判断IP是否已被上报失败（短期拉黑）
        /// </summary>
        /// <param name="address">IP</param>
        /// <returns></returns>
        public bool IsFailed(IPAddress address)
        {
            return this.addressService.IsFailed(address);
        }

        /// <summary>
        /// 上报请求阶段连接成功的IP，把它提到该域名候选列表首位。
        /// 浏览器打开一个页面会并发几十个请求，逐个试错时只要落到坏IP上就要白等一次超时，
        /// 整体表现为"一直转圈最后超时"。记住最近成功的IP后，后续请求直接命中它，不再重复试错。
        /// </summary>
        /// <param name="endPoint">域名节点</param>
        /// <param name="address">成功的IP</param>
        public void ReportSuccess(DnsEndPoint endPoint, IPAddress address)
        {
            if (this.dnsEndPointAddress.TryGetValue(endPoint, out var addresses) == false)
            {
                return;
            }

            if (addresses.Length <= 1 || addresses[0].Equals(address) == true)
            {
                return;
            }

            if (addresses.Contains(address) == false)
            {
                return;
            }

            var reordered = new IPAddress[addresses.Length];
            reordered[0] = address;

            var index = 1;
            foreach (var item in addresses)
            {
                if (item.Equals(address) == false)
                {
                    reordered[index++] = item;
                }
            }

            this.dnsEndPointAddress[endPoint] = reordered;
        }

        /// <summary>
        /// 生成刷新结果
        /// </summary>
        /// <param name="domainCount"></param>
        /// <param name="addressCount"></param>
        /// <param name="hosts"></param>
        /// <param name="timedOut">是否因超时提前结束</param>
        /// <returns></returns>
        private RefreshIpResult CreateResult(int domainCount, int addressCount, HostsRefreshResult hosts, bool timedOut = false)        {
            if (timedOut == true)
            {
                this.logger.LogWarning($"IP刷新超时结束：{domainCount}个域名、{addressCount}个可用IP，其余域名保留原IP");
            }
            else
            {
                this.logger.LogInformation($"IP刷新完成：{domainCount}个域名、{addressCount}个可用IP");
            }

            return new RefreshIpResult
            {
                DomainCount = domainCount,
                AddressCount = addressCount,
                TimedOut = timedOut,
                HostsUpdated = hosts.Success,
                HostsDomainCount = hosts.DomainCount,
                HostsError = hosts.Error
            };
        }
    }
}

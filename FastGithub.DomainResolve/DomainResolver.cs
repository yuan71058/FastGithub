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
        private const int MAX_CONCURRENCY = 8;
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
        /// <returns>参与测速的域名数量与可用IP数量</returns>
        private async Task<(int DomainCount, int AddressCount)> TestSpeedCoreAsync(CancellationToken cancellationToken)
        {
            var domainCount = 0;
            var addressCount = 0;

            var keyValues = this.dnsEndPointAddress.OrderBy(item => item.Value.Length).ToArray();
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = MAX_CONCURRENCY,
                CancellationToken = cancellationToken
            };

            await Parallel.ForEachAsync(keyValues, parallelOptions, async (keyValue, token) =>
            {
                var dnsEndPoint = keyValue.Key;
                var oldAddresses = keyValue.Value;

                var newAddresses = await this.addressService.GetAddressesAsync(dnsEndPoint, oldAddresses, token);
                this.dnsEndPointAddress[dnsEndPoint] = newAddresses;

                Interlocked.Add(ref domainCount, 1);
                Interlocked.Add(ref addressCount, newAddresses.Length);

                var oldSegmentums = oldAddresses.Take(MAX_IP_COUNT);
                var newSegmentums = newAddresses.Take(MAX_IP_COUNT);
                if (oldSegmentums.SequenceEqual(newSegmentums) == false)
                {
                    var addressArray = string.Join(", ", newSegmentums.Select(item => item.ToString()));
                    this.logger.LogInformation($"{dnsEndPoint.Host}:{dnsEndPoint.Port}->[{addressArray}]");
                }
            });

            return (domainCount, addressCount);
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

            var (domainCount, addressCount) = await this.TestSpeedCoreAsync(cancellationToken);
            var hosts = await this.hostsService.RefreshAsync(cancellationToken);
            return this.CreateResult(domainCount, addressCount, hosts);
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

            var hosts = await this.hostsService.RefreshAsync(cancellationToken);
            var (domainCount, addressCount) = await this.TestSpeedCoreAsync(cancellationToken);
            return this.CreateResult(domainCount, addressCount, hosts);
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
        /// 生成刷新结果
        /// </summary>
        /// <param name="domainCount"></param>
        /// <param name="addressCount"></param>
        /// <param name="hosts"></param>
        /// <returns></returns>
        private RefreshIpResult CreateResult(int domainCount, int addressCount, HostsRefreshResult hosts)
        {
            this.logger.LogInformation($"IP刷新完成：{domainCount}个域名、{addressCount}个可用IP");
            return new RefreshIpResult
            {
                DomainCount = domainCount,
                AddressCount = addressCount,
                HostsUpdated = hosts.Success,
                HostsDomainCount = hosts.DomainCount,
                HostsError = hosts.Error
            };
        }
    }
}

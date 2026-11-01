using DNS.Client;
using DNS.Client.RequestResolver;
using DNS.Protocol;
using DNS.Protocol.ResourceRecords;
using FastGithub.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// DNS客户端
    /// </summary>
    sealed class DnsClient
    {
        private const int DNS_PORT = 53;
        private const string LOCALHOST = "localhost";

        private readonly DnscryptProxy dnscryptProxy;
        private readonly FastGithubConfig fastGithubConfig;
        private readonly HostsService hostsService;
        private readonly ILogger<DnsClient> logger;

        private readonly ConcurrentDictionary<string, SemaphoreSlim> semaphoreSlims = new();
        private readonly ConcurrentDictionary<IPEndPoint, int> dnsFailureCounts = new();
        private readonly IMemoryCache dnsStateCache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        private readonly IMemoryCache dnsLookupCache = new MemoryCache(Options.Create(new MemoryCacheOptions()));

        private readonly TimeSpan stateExpiration = TimeSpan.FromMinutes(5d);
        private readonly TimeSpan minTimeToLive = TimeSpan.FromSeconds(30d);
        private readonly TimeSpan maxTimeToLive = TimeSpan.FromMinutes(10d);

        /// <summary>
        /// 同一个DNS连续查询失败达到该次数就不再使用
        /// TCP能连上但查询被重置的DNS必须靠这个熔断排除，否则每个域名都要在它身上白等一次超时
        /// </summary>
        private const int DNS_FAILURE_THRESHOLD = 3;

        private readonly int resolveTimeout = (int)TimeSpan.FromSeconds(4d).TotalMilliseconds;
        private static readonly TimeSpan tcpConnectTimeout = TimeSpan.FromSeconds(2d);

        private record LookupResult(IList<IPAddress> Addresses, TimeSpan TimeToLive);

        /// <summary>
        /// GitHub关键域名的兜底候选IP
        /// 在线hosts源与DNS结果都可能只剩被阻断的地址，这里补充历史上可用的入口段作为备选。
        /// 是否真正可用仍由IPAddressService的TCP+TLS探测决定，这里只扩大候选集合。
        ///
        /// 顺序按本机实测延迟排（2026-11-01），同一域名的不同Azure段差异可达20倍：
        /// github.com   20.27.177.113=0.55s / 20.205.243.166=0.91s / 20.29.134.23=1.27s / 140.82.113.3=6.82s
        /// api.github   20.27.177.113=0.24s / 20.205.243.166=0.35s / 20.29.134.23=0.51s / 140.82.121.6=12s超时
        /// 静态资源走Fastly，不同后缀服务不同内容：.133=文件内容 / .153=Pages / .154=站点静态资源
        /// </summary>
        private static readonly Dictionary<string, IPAddress[]> seedAddresses = new(StringComparer.OrdinalIgnoreCase)
        {
            ["github.com"] = new[]
            {
                IPAddress.Parse("20.27.177.113"),
                IPAddress.Parse("20.205.243.166"),
                IPAddress.Parse("20.29.134.23"),
                IPAddress.Parse("20.26.156.215"),
                IPAddress.Parse("20.233.83.145"),
                IPAddress.Parse("140.82.113.3"),
            },
            ["api.github.com"] = new[]
            {
                IPAddress.Parse("20.27.177.113"),
                IPAddress.Parse("20.205.243.166"),
                IPAddress.Parse("20.205.243.168"),
                IPAddress.Parse("20.29.134.23"),
            },
            ["gist.github.com"] = new[]
            {
                IPAddress.Parse("20.205.243.166"),
                IPAddress.Parse("20.27.177.113"),
                IPAddress.Parse("20.26.156.215"),
            },
            ["codeload.github.com"] = new[]
            {
                IPAddress.Parse("20.205.243.165"),
                IPAddress.Parse("20.27.177.113"),
                IPAddress.Parse("20.26.156.215"),
            },
            ["raw.githubusercontent.com"] = new[]
            {
                IPAddress.Parse("185.199.110.133"),
                IPAddress.Parse("185.199.109.133"),
                IPAddress.Parse("185.199.111.133"),
                IPAddress.Parse("185.199.108.133"),
            },
            ["avatars.githubusercontent.com"] = new[]
            {
                IPAddress.Parse("185.199.110.133"),
                IPAddress.Parse("185.199.109.133"),
                IPAddress.Parse("185.199.111.133"),
                IPAddress.Parse("185.199.108.133"),
            },
            ["camo.githubusercontent.com"] = new[]
            {
                IPAddress.Parse("185.199.110.133"),
                IPAddress.Parse("185.199.109.133"),
                IPAddress.Parse("185.199.111.133"),
                IPAddress.Parse("185.199.108.133"),
            },
            ["github.githubassets.com"] = new[]
            {
                IPAddress.Parse("185.199.108.154"),
                IPAddress.Parse("185.199.110.154"),
                IPAddress.Parse("185.199.109.154"),
                IPAddress.Parse("185.199.111.154"),
            },
            ["github.io"] = new[]
            {
                IPAddress.Parse("185.199.110.153"),
                IPAddress.Parse("185.199.111.153"),
                IPAddress.Parse("185.199.109.153"),
                IPAddress.Parse("185.199.108.153"),
            },
        };

        /// <summary>
        /// DNS客户端
        /// </summary>
        /// <param name="dnscryptProxy"></param>
        /// <param name="fastGithubConfig"></param>
        /// <param name="logger"></param>
        public DnsClient(
            DnscryptProxy dnscryptProxy,
            FastGithubConfig fastGithubConfig,
            HostsService hostsService,
            ILogger<DnsClient> logger)
        {
            this.dnscryptProxy = dnscryptProxy;
            this.fastGithubConfig = fastGithubConfig;
            this.hostsService = hostsService;
            this.logger = logger;
        }

        /// <summary>
        /// 清空DNS解析缓存，强制后续重新发起DNS查询
        /// </summary>
        public void ClearCache()
        {
            (this.dnsLookupCache as MemoryCache)?.Compact(1.0);
            (this.dnsStateCache as MemoryCache)?.Compact(1.0);
        }

        /// <summary>
        /// 解析域名
        /// </summary>
        /// <param name="endPoint">远程结节</param>
        /// <param name="fastSort">是否使用快速排序</param>
        /// <param name="cancellationToken"></param>
        /// <param name="hostsOnly">是否只使用在线hosts源提供的IP</param>
        /// <returns></returns>
        public async IAsyncEnumerable<IPAddress> ResolveAsync(DnsEndPoint endPoint, bool fastSort, [EnumeratorCancellation] CancellationToken cancellationToken, bool hostsOnly = false)
        {
            var hashSet = new HashSet<IPAddress>();

            // 在线hosts源覆盖的域名优先使用hosts源提供的IP
            if (this.hostsService.TryGetAddresses(endPoint.Host, out var hostsAddresses) && hostsAddresses.Count > 0)
            {
                foreach (var address in hostsAddresses)
                {
                    if (hashSet.Add(address) == true)
                    {
                        yield return address;
                    }
                }

                // hostsOnly表示本次只信任hosts源，不补充DNS结果
                if (hostsOnly == true)
                {
                    // 但必须补充内置候选池：hosts源经常只给1个IP，一旦这个入口被阻断，
                    // 该域名就只剩一个不可用候选，表现为"更新IP后反而打不开"。
                    foreach (var address in this.GetSeedAddresses(endPoint.Host, hashSet))
                    {
                        yield return address;
                    }

                    yield break;
                }
            }
            else if (hostsOnly == true)
            {
                // hosts源未覆盖时，用内置候选池兜底；没有候选则交由调用方决定是否回退DNS
                foreach (var address in this.GetSeedAddresses(endPoint.Host, hashSet))
                {
                    yield return address;
                }

                yield break;
            }

            // hosts源未覆盖、或hosts源提供的IP不可用时，补充DNS查询结果
            await foreach (var dns in this.GetDnsServersAsync(cancellationToken))
            {
                var addresses = await this.LookupAsync(dns, endPoint, fastSort, cancellationToken);
                foreach (var address in addresses)
                {
                    if (hashSet.Add(address) == true)
                    {
                        yield return address;
                    }
                }
            }

            // 兜底候选：仅补充GitHub关键域名，扩大候选集合以应对单个入口被阻断
            foreach (var address in this.GetSeedAddresses(endPoint.Host, hashSet))
            {
                yield return address;
            }
        }

        /// <summary>
        /// 获取域名的内置候选IP（已存在的地址会被跳过）
        /// </summary>
        /// <param name="host">域名</param>
        /// <param name="exists">已产出的地址集合</param>
        /// <returns></returns>
        private IEnumerable<IPAddress> GetSeedAddresses(string host, HashSet<IPAddress> exists)
        {
            if (seedAddresses.TryGetValue(host, out var seeds) == false)
            {
                yield break;
            }

            foreach (var address in seeds)
            {
                if (exists.Add(address) == true)
                {
                    yield return address;
                }
            }
        }

        /// <summary>
        /// 获取dns服务
        /// </summary>
        /// <returns></returns>
        private async IAsyncEnumerable<IPEndPoint> GetDnsServersAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var cryptDns = this.dnscryptProxy.LocalEndPoint;
            if (cryptDns != null)
            {
                yield return cryptDns;
                yield return cryptDns;
            }

            foreach (var dns in this.fastGithubConfig.FallbackDns)
            {
                if (await this.IsDnsAvailableAsync(dns, cancellationToken))
                {
                    yield return dns;
                }
            }
        }

        /// <summary>
        /// 获取dns是否可用
        /// </summary>
        /// <param name="dns"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async ValueTask<bool> IsDnsAvailableAsync(IPEndPoint dns, CancellationToken cancellationToken)
        {
            if (dns.Port != DNS_PORT)
            {
                return true;
            }

            if (this.dnsStateCache.TryGetValue<bool>(dns, out var available))
            {
                return available;
            }

            var key = dns.ToString();
            var semaphore = this.semaphoreSlims.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync(CancellationToken.None);

            try
            {
                using var timeoutTokenSource = new CancellationTokenSource(tcpConnectTimeout);
                using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(timeoutTokenSource.Token, cancellationToken);
                using var socket = new Socket(dns.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(dns, linkedTokenSource.Token);
                return this.dnsStateCache.Set(dns, true, this.stateExpiration);
            }
            catch (Exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return this.dnsStateCache.Set(dns, false, this.stateExpiration);
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// 记录一次DNS查询成功
        /// 这里只递减而不是清零：偶发成功就清零会让持续被干扰的DNS永远熔断不掉
        /// </summary>
        /// <param name="dns">DNS服务器</param>
        private void ReportDnsSuccess(IPEndPoint dns)
        {
            if (dns.Port != DNS_PORT)
            {
                return;
            }

            this.dnsFailureCounts.AddOrUpdate(dns, 0, (_, value) => value > 0 ? value - 1 : 0);
        }

        /// <summary>
        /// 记录一次DNS查询失败
        /// 部分网络的53端口能建立TCP连接，但查询报文会被重置，仅靠TCP探测无法发现，必须按查询结果熔断
        /// </summary>
        /// <param name="dns">DNS服务器</param>
        private void ReportDnsFailure(IPEndPoint dns)
        {
            // 仅熔断公共DNS（53端口）；dnscrypt-proxy使用随机本地端口，是主力解析通道，不参与熔断
            if (dns.Port != DNS_PORT)
            {
                return;
            }

            var count = this.dnsFailureCounts.AddOrUpdate(dns, 1, (_, value) => value + 1);
            if (count >= DNS_FAILURE_THRESHOLD)
            {
                this.dnsStateCache.Set(dns, false, this.stateExpiration);
                this.logger.LogWarning($"{dns}连续{count}次查询失败，{this.stateExpiration.TotalMinutes:0}分钟内不再使用");
            }
        }

        /// <summary>
        /// 解析域名
        /// </summary>
        /// <param name="dns"></param>
        /// <param name="endPoint"></param>
        /// <param name="fastSort"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<IList<IPAddress>> LookupAsync(IPEndPoint dns, DnsEndPoint endPoint, bool fastSort, CancellationToken cancellationToken = default)
        {
            var key = $"{dns}/{endPoint}";
            var semaphore = this.semaphoreSlims.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync(CancellationToken.None);

            try
            {
                if (this.dnsLookupCache.TryGetValue<IList<IPAddress>>(key, out var value))
                {
                    return value;
                }
                var result = await this.LookupCoreAsync(dns, endPoint, fastSort, cancellationToken);
                this.ReportDnsSuccess(dns);
                return this.dnsLookupCache.Set(key, result.Addresses, result.TimeToLive);
            }
            catch (OperationCanceledException)
            {
                // 调用方主动取消（如刷新超时）不算DNS本身的问题
                if (cancellationToken.IsCancellationRequested == false)
                {
                    this.ReportDnsFailure(dns);
                }
                return Array.Empty<IPAddress>();
            }
            catch (Exception ex)
            {
                this.logger.LogWarning($"{endPoint.Host}@{dns}->{ex.Message}");
                this.ReportDnsFailure(dns);
                var expiration = IsSocketException(ex) ? this.maxTimeToLive : this.minTimeToLive;
                return this.dnsLookupCache.Set(key, Array.Empty<IPAddress>(), expiration);
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// 是否为Socket异常
        /// </summary>
        /// <param name="ex"></param>
        /// <returns></returns>
        private static bool IsSocketException(Exception ex)
        {
            if (ex is SocketException)
            {
                return true;
            }

            var inner = ex.InnerException;
            return inner != null && IsSocketException(inner);
        }


        /// <summary>
        /// 解析域名
        /// </summary>
        /// <param name="dns"></param>
        /// <param name="endPoint"></param>
        /// <param name="fastSort"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<LookupResult> LookupCoreAsync(IPEndPoint dns, DnsEndPoint endPoint, bool fastSort, CancellationToken cancellationToken = default)
        {
            if (endPoint.Host == LOCALHOST)
            {
                var loopbacks = new List<IPAddress>();
                if (Socket.OSSupportsIPv4 == true)
                {
                    loopbacks.Add(IPAddress.Loopback);
                }
                if (Socket.OSSupportsIPv6 == true)
                {
                    loopbacks.Add(IPAddress.IPv6Loopback);
                }
                return new LookupResult(loopbacks, TimeSpan.MaxValue);
            }

            var resolver = dns.Port == DNS_PORT
                ? (IRequestResolver)new TcpRequestResolver(dns)
                : new UdpRequestResolver(dns, new TcpRequestResolver(dns), this.resolveTimeout);

            var addressRecords = await GetAddressRecordsAsync(resolver, endPoint.Host, cancellationToken);
            var addresses = (IList<IPAddress>)addressRecords
                .Where(item => IPAddress.IsLoopback(item.IPAddress) == false)
                .Select(item => item.IPAddress)
                .ToArray();

            if (addresses.Count == 0)
            {
                return new LookupResult(addresses, this.minTimeToLive);
            }

            if (fastSort == true)
            {
                addresses = await OrderByConnectAnyAsync(addresses, endPoint.Port, cancellationToken);
            }

            var timeToLive = addressRecords.Min(item => item.TimeToLive);
            if (timeToLive <= TimeSpan.Zero)
            {
                timeToLive = this.minTimeToLive;
            }
            else if (timeToLive > this.maxTimeToLive)
            {
                timeToLive = this.maxTimeToLive;
            }

            return new LookupResult(addresses, timeToLive);
        }

        /// <summary>
        /// 获取IP记录
        /// </summary>
        /// <param name="resolver"></param>
        /// <param name="domain"></param> 
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private static async Task<IList<IPAddressResourceRecord>> GetAddressRecordsAsync(IRequestResolver resolver, string domain, CancellationToken cancellationToken)
        {
            var addressRecords = new List<IPAddressResourceRecord>();
            if (Socket.OSSupportsIPv4 == true)
            {
                var records = await GetRecordsAsync(RecordType.A);
                addressRecords.AddRange(records);
            }

            if (Socket.OSSupportsIPv6 == true)
            {
                var records = await GetRecordsAsync(RecordType.AAAA);
                addressRecords.AddRange(records);
            }
            return addressRecords;


            async Task<IEnumerable<IPAddressResourceRecord>> GetRecordsAsync(RecordType recordType)
            {
                var request = new Request
                {
                    RecursionDesired = true,
                    OperationCode = OperationCode.Query
                };

                request.Questions.Add(new Question(new Domain(domain), recordType));
                var clientRequest = new ClientRequest(resolver, request);
                var response = await clientRequest.Resolve(cancellationToken);
                return response.AnswerRecords.OfType<IPAddressResourceRecord>();
            }
        }


        /// <summary>
        /// 连接速度排序
        /// </summary>
        /// <param name="addresses"></param>
        /// <param name="port"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private static async Task<IList<IPAddress>> OrderByConnectAnyAsync(IList<IPAddress> addresses, int port, CancellationToken cancellationToken)
        {
            if (addresses.Count <= 1)
            {
                return addresses;
            }

            using var controlTokenSource = new CancellationTokenSource(tcpConnectTimeout);
            using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, controlTokenSource.Token);

            var connectTasks = addresses.Select(address => ConnectAsync(address, port, linkedTokenSource.Token));
            var fastestAddress = await await Task.WhenAny(connectTasks);
            controlTokenSource.Cancel();

            if (fastestAddress == null || addresses.First().Equals(fastestAddress))
            {
                return addresses;
            }

            var list = new List<IPAddress> { fastestAddress };
            foreach (var address in addresses)
            {
                if (address.Equals(fastestAddress) == false)
                {
                    list.Add(address);
                }
            }
            return list;
        }

        /// <summary>
        /// 连接指定ip和端口
        /// </summary>
        /// <param name="address"></param>
        /// <param name="port"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private static async Task<IPAddress?> ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken)
        {
            try
            {
                using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(address, port, cancellationToken);
                return address;
            }
            catch (Exception)
            {
                return default;
            }
        }
    }
}

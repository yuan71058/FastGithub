using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// IP服务
    /// 域名IP关系缓存10分钟
    /// IPEndPoint时延缓存5分钟
    /// IPEndPoint连接超时5秒
    /// </summary>
    sealed class IPAddressService
    {
        private record DomainAddress(string Domain, IPAddress Address);
        private readonly TimeSpan domainAddressExpiration = TimeSpan.FromMinutes(10d);
        private readonly IMemoryCache domainAddressCache = new MemoryCache(Options.Create(new MemoryCacheOptions()));

        private record AddressElapsed(IPAddress Address, TimeSpan Elapsed);
        private readonly TimeSpan problemElapsedExpiration = TimeSpan.FromMinutes(1d);
        private readonly TimeSpan normalElapsedExpiration = TimeSpan.FromMinutes(5d);
        private readonly TimeSpan connectTimeout = TimeSpan.FromSeconds(5d);

        /// <summary>
        /// 请求阶段失败的IP，短期不再参与排序
        /// </summary>
        private readonly TimeSpan failedExpiration = TimeSpan.FromSeconds(90d);

        private readonly IMemoryCache addressElapsedCache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        private readonly IMemoryCache failedAddressCache = new MemoryCache(Options.Create(new MemoryCacheOptions()));

        private readonly DnsClient dnsClient;

        /// <summary>
        /// IP服务
        /// </summary>
        /// <param name="dnsClient"></param>
        public IPAddressService(DnsClient dnsClient)
        {
            this.dnsClient = dnsClient;
        }

        /// <summary>
        /// 清空IP缓存，强制后续重新解析与测速
        /// </summary>
        public void ClearCache()
        {
            (this.domainAddressCache as MemoryCache)?.Compact(1.0);
            (this.addressElapsedCache as MemoryCache)?.Compact(1.0);
        }

        /// <summary>
        /// 上报请求阶段连接失败的IP
        /// 测速通过不代表后续请求一定可用，失败后短期降权，避免持续选中同一个坏IP
        /// </summary>
        /// <param name="address"></param>
        public void ReportFailure(IPAddress address)
        {
            this.failedAddressCache.Set(address, default(object), this.failedExpiration);
        }

        /// <summary>
        /// 并行获取可连接的IP
        /// </summary>
        /// <param name="dnsEndPoint"></param>
        /// <param name="oldAddresses"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<IPAddress[]> GetAddressesAsync(DnsEndPoint dnsEndPoint, IEnumerable<IPAddress> oldAddresses, CancellationToken cancellationToken)
        {
            // 优先使用在线hosts源提供的IP，避免DNS结果被污染
            var addresses = await this.GetAddressesCoreAsync(dnsEndPoint, oldAddresses, hostsOnly: true, cancellationToken);
            if (addresses.Length > 0)
            {
                return addresses;
            }

            // hosts源未提供可用IP时，才回退到DNS查询
            return await this.GetAddressesCoreAsync(dnsEndPoint, oldAddresses, hostsOnly: false, cancellationToken);
        }

        /// <summary>
        /// 并行获取可连接的IP
        /// </summary>
        /// <param name="dnsEndPoint"></param>
        /// <param name="oldAddresses"></param>
        /// <param name="hostsOnly">是否只使用在线hosts源提供的IP</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<IPAddress[]> GetAddressesCoreAsync(DnsEndPoint dnsEndPoint, IEnumerable<IPAddress> oldAddresses, bool hostsOnly, CancellationToken cancellationToken)
        {
            var ipEndPoints = new HashSet<IPEndPoint>();

            // 历史未过期的IP节点
            foreach (var address in oldAddresses)
            {
                var domainAddress = new DomainAddress(dnsEndPoint.Host, address);
                if (this.domainAddressCache.TryGetValue(domainAddress, out _))
                {
                    ipEndPoints.Add(new IPEndPoint(address, dnsEndPoint.Port));
                }
            }

            // 新解析出的IP节点（在线hosts源优先，需要时补充DNS结果）
            await foreach (var address in this.dnsClient.ResolveAsync(dnsEndPoint, fastSort: false, cancellationToken, hostsOnly))
            {
                ipEndPoints.Add(new IPEndPoint(address, dnsEndPoint.Port));
                var domainAddress = new DomainAddress(dnsEndPoint.Host, address);
                this.domainAddressCache.Set(domainAddress, default(object), this.domainAddressExpiration);
            }

            if (ipEndPoints.Count == 0)
            {
                return Array.Empty<IPAddress>();
            }

            var addressElapsedTasks = ipEndPoints.Select(item => this.GetAddressElapsedAsync(item, dnsEndPoint, cancellationToken));
            var addressElapseds = await Task.WhenAll(addressElapsedTasks);

            return addressElapseds
                .Where(item => item.Elapsed < TimeSpan.MaxValue)
                .OrderBy(item => item.Elapsed)
                .Select(item => item.Address)
                .ToArray();
        }


        /// <summary>
        /// 获取IP节点的时延
        /// </summary> 
        /// <param name="endPoint">IP节点</param>
        /// <param name="dnsEndPoint">域名节点，用于TLS握手的SNI</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<AddressElapsed> GetAddressElapsedAsync(IPEndPoint endPoint, DnsEndPoint dnsEndPoint, CancellationToken cancellationToken)
        {
            // 请求阶段已上报失败的IP，短期内直接视为不可用
            if (this.failedAddressCache.TryGetValue(endPoint.Address, out _))
            {
                return new AddressElapsed(endPoint.Address, TimeSpan.MaxValue);
            }

            if (this.addressElapsedCache.TryGetValue<AddressElapsed>(endPoint, out var addressElapsed))
            {
                return addressElapsed;
            }

            var stopWatch = Stopwatch.StartNew();
            try
            {
                using var timeoutTokenSource = new CancellationTokenSource(this.connectTimeout);
                using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutTokenSource.Token);
                using var socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(endPoint, linkedTokenSource.Token);

                // TCP可连接不代表HTTPS可用：被干扰的IP常常能完成TCP握手，随后在TLS阶段被重置
                if (await CanHandshakeAsync(socket, dnsEndPoint.Host, linkedTokenSource.Token) == false)
                {
                    addressElapsed = new AddressElapsed(endPoint.Address, TimeSpan.MaxValue);
                    return this.addressElapsedCache.Set(endPoint, addressElapsed, this.problemElapsedExpiration);
                }

                addressElapsed = new AddressElapsed(endPoint.Address, stopWatch.Elapsed);
                return this.addressElapsedCache.Set(endPoint, addressElapsed, this.normalElapsedExpiration);
            }
            catch (Exception ex)
            {
                cancellationToken.ThrowIfCancellationRequested();

                addressElapsed = new AddressElapsed(endPoint.Address, TimeSpan.MaxValue);
                var expiration = IsLocalNetworkProblem(ex) ? this.problemElapsedExpiration : this.normalElapsedExpiration;
                return this.addressElapsedCache.Set(endPoint, addressElapsed, expiration);
            }
            finally
            {
                stopWatch.Stop();
            }
        }

        /// <summary>
        /// 探测TLS握手是否能完成
        /// </summary>
        /// <param name="socket">已建立连接的socket</param>
        /// <param name="host">SNI域名</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private static async Task<bool> CanHandshakeAsync(Socket socket, string host, CancellationToken cancellationToken)
        {
            using var stream = new NetworkStream(socket, ownsSocket: false);
            using var sslStream = new SslStream(stream, leaveInnerStreamOpen: true, static (sender, certificate, chain, errors) => true);
            try
            {
                await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    RemoteCertificateValidationCallback = static (sender, certificate, chain, errors) => true
                }, cancellationToken);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 是否为本机网络问题
        /// </summary>
        /// <param name="ex"></param>
        /// <returns></returns>
        private static bool IsLocalNetworkProblem(Exception ex)
        {
            if (ex is not SocketException socketException)
            {
                return false;
            }

            var code = socketException.SocketErrorCode;
            return code == SocketError.NetworkDown || code == SocketError.NetworkUnreachable;
        }
    }
}

using FastGithub.Configuration;
using FastGithub.DomainResolve;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.Http
{
    /// <summary>
    /// HttpClientHandler
    /// </summary> 
    class HttpClientHandler : DelegatingHandler
    {
        private readonly DomainConfig domainConfig;
        private readonly IDomainResolver domainResolver;

        /// <summary>
        /// 单个IP的连接超时。可用入口建连通常在1秒内，1.5秒足以判定不可用，
        /// 过长会让页面里的并发请求逐个白等，表现为整体转圈超时。
        /// </summary>
        private readonly TimeSpan connectTimeout = TimeSpan.FromSeconds(1.5d);

        /// <summary>
        /// 一个请求尝试所有候选IP的总预算。
        /// 候选池可能有多个入口，若逐个等待超时，全不可用时会拖到十几秒；
        /// 有了总预算，候选再多也能在有限时间内失败并触发刷新。
        /// </summary>
        private readonly TimeSpan connectTotalTimeout = TimeSpan.FromSeconds(4d);

        /// <summary>
        /// 同时保持的连接数。串行逐个试错时，落在一批坏IP上的请求要依次白等一次超时；
        /// 并行连接把最坏等待从"超时×候选数"降到"一次超时"。
        /// </summary>
        private const int MaxParallelConnects = 3;

        /// <summary>
        /// 首连的等待窗口。多数情况下第一个候选就能建成，先只发一个可以省下多余的握手；
        /// 超过这个窗口还没结果，说明它在踩坏IP，立刻把其余候选一起发出去。
        /// </summary>
        private static readonly TimeSpan StaggerDelay = TimeSpan.FromMilliseconds(300d);

        private static int refreshingToken = 0;

        /// <summary>
        /// HttpClientHandler
        /// </summary>
        /// <param name="domainConfig"></param>
        /// <param name="domainResolver"></param> 
        public HttpClientHandler(DomainConfig domainConfig, IDomainResolver domainResolver)
        {
            this.domainConfig = domainConfig;
            this.domainResolver = domainResolver;
            this.InnerHandler = this.CreateSocketsHttpHandler();
        }

        /// <summary>
        /// 发送请求
        /// </summary>
        /// <param name="request"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri;
            if (uri == null)
            {
                throw new FastGithubException("必须指定请求的URI");
            }

            // 请求上下文信息
            var isHttps = uri.Scheme == Uri.UriSchemeHttps;
            var tlsSniValue = this.domainConfig.GetTlsSniPattern().WithDomain(uri.Host).WithRandom();
            request.SetRequestContext(new RequestContext(isHttps, tlsSniValue));

            // 设置请求头host，修改协议为http
            request.Headers.Host = uri.Host;
            request.RequestUri = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttp }.Uri;

            if (this.domainConfig.Timeout != null)
            {
                using var timeoutTokenSource = new CancellationTokenSource(this.domainConfig.Timeout.Value);
                using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutTokenSource.Token);
                return await base.SendAsync(request, linkedTokenSource.Token);
            }
            return await base.SendAsync(request, cancellationToken);
        }

        /// <summary>
        /// 创建转发代理的httpHandler
        /// </summary>
        /// <returns></returns>
        private SocketsHttpHandler CreateSocketsHttpHandler()
        {
            return new SocketsHttpHandler
            {
                Proxy = null,
                UseProxy = false,
                UseCookies = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                ConnectCallback = this.ConnectCallback
            };
        }

        /// <summary>
        /// 连接回调
        /// </summary>
        /// <param name="context"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async ValueTask<Stream> ConnectCallback(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        {
            var innerExceptions = new List<Exception>();
            var ipEndPoints = this.GetIPEndPointsAsync(context.DnsEndPoint, cancellationToken);

            using var totalTokenSource = new CancellationTokenSource(this.connectTotalTimeout);
            using var totalToken = CancellationTokenSource.CreateLinkedTokenSource(totalTokenSource.Token, cancellationToken);

            var batch = new List<IPEndPoint>(MaxParallelConnects);

            try
            {
                await foreach (var ipEndPoint in ipEndPoints.WithCancellation(totalToken.Token))
                {
                    batch.Add(ipEndPoint);
                    if (batch.Count < MaxParallelConnects)
                    {
                        continue;
                    }

                    var stream = await this.ConnectBatchAsync(context, batch, innerExceptions, totalToken.Token);
                    if (stream != null)
                    {
                        return stream;
                    }
                    batch.Clear();
                }

                if (batch.Count > 0)
                {
                    var stream = await this.ConnectBatchAsync(context, batch, innerExceptions, totalToken.Token);
                    if (stream != null)
                    {
                        return stream;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested == false)
            {
                innerExceptions.Add(new TimeoutException($"连接候选IP的总预算{this.connectTotalTimeout.TotalSeconds}秒已用完"));
            }

            // 找不到任何可成功连接的IP时，自动触发IP更新，便于下次请求恢复
            _ = this.RefreshIpAsync();
            throw new AggregateException("找不到任何可成功连接的IP，已自动触发IP更新，请稍后重试", innerExceptions);
        }

        /// <summary>
        /// 并行连接一批候选IP，返回第一个连通的流
        /// </summary>
        /// <param name="context"></param>
        /// <param name="ipEndPoints">本批候选</param>
        /// <param name="innerExceptions">失败的异常收集</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<Stream?> ConnectBatchAsync(
            SocketsHttpConnectionContext context,
            List<IPEndPoint> ipEndPoints,
            List<Exception> innerExceptions,
            CancellationToken cancellationToken)
        {
            using var batchTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = batchTokenSource.Token;

            var queue = new Queue<IPEndPoint>(ipEndPoints);
            var pending = new Dictionary<Task<Stream>, IPEndPoint>(ipEndPoints.Count);

            Task<Stream> Start(IPEndPoint ipEndPoint)
            {
                var task = this.ConnectWithTimeoutAsync(context, ipEndPoint, token);
                pending.Add(task, ipEndPoint);
                return task;
            }

            // 先只发第一个，它在窗口内连通就不用为这次请求多付两次握手
            var first = Start(queue.Dequeue());
            var settled = await Task.WhenAny(first, Task.Delay(StaggerDelay, token)).ConfigureAwait(false);
            var fill = ReferenceEquals(settled, first) == false;

            while (true)
            {
                // 首连踩了坏IP或已经失败，就把在飞数量补齐到上限，让这一批并行收敛
                if (fill)
                {
                    while (pending.Count < MaxParallelConnects && queue.Count > 0 && token.IsCancellationRequested == false)
                    {
                        Start(queue.Dequeue());
                    }
                    fill = false;
                }

                if (pending.Count == 0)
                {
                    return null;
                }

                var completed = await Task.WhenAny(pending.Keys).ConfigureAwait(false);
                var ipEndPoint = pending[completed];
                pending.Remove(completed);

                if (completed.IsCompletedSuccessfully)
                {
                    batchTokenSource.Cancel();
                    this.ObserveAbandoned(pending);

                    // 记住这个可用的IP，后续同名域名的请求直接命中它，不再逐个试错
                    this.domainResolver.ReportSuccess(context.DnsEndPoint, ipEndPoint.Address);
                    return await completed.ConfigureAwait(false);
                }

                var exception = completed.Exception?.GetBaseException()
                    ?? new HttpConnectTimeoutException(ipEndPoint.Address);

                if (cancellationToken.IsCancellationRequested)
                {
                    this.ObserveAbandoned(pending);
                    throw exception;
                }

                this.domainResolver.ReportFailure(ipEndPoint.Address);
                innerExceptions.Add(exception);
                fill = true;
            }
        }

        /// <summary>
        /// 带单个IP超时的连接
        /// </summary>
        /// <param name="context"></param>
        /// <param name="ipEndPoint"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<Stream> ConnectWithTimeoutAsync(
            SocketsHttpConnectionContext context,
            IPEndPoint ipEndPoint,
            CancellationToken cancellationToken)
        {
            using var timeoutTokenSource = new CancellationTokenSource(this.connectTimeout);
            using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(timeoutTokenSource.Token, cancellationToken);
            return await this.ConnectAsync(context, ipEndPoint, linkedTokenSource.Token);
        }

        /// <summary>
        /// 吃掉被放弃连接的后台异常，避免未观察任务异常
        /// </summary>
        /// <param name="pending"></param>
        private void ObserveAbandoned(Dictionary<Task<Stream>, IPEndPoint> pending)
        {
            foreach (var task in pending.Keys)
            {
                _ = task.ContinueWith(
                    static t => _ = t.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            pending.Clear();
        }

        /// <summary>
        /// 找不到任何可成功连接的IP时，自动触发IP更新
        /// 后台执行，不阻塞当前请求；使用静态令牌避免并发重复刷新
        /// </summary>
        /// <returns></returns>
        private async Task RefreshIpAsync()
        {
            if (Interlocked.Exchange(ref refreshingToken, 1) == 1)
            {
                return;
            }

            try
            {
                await this.domainResolver.RefreshAsync(CancellationToken.None);
            }
            catch (Exception)
            {
            }
            finally
            {
                Interlocked.Exchange(ref refreshingToken, 0);
            }
        }

        /// <summary>
        /// 建立连接
        /// </summary>
        /// <param name="context"></param>
        /// <param name="ipEndPoint"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, IPEndPoint ipEndPoint, CancellationToken cancellationToken)
        {
            var socket = new Socket(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(ipEndPoint, cancellationToken);
                var stream = new NetworkStream(socket, ownsSocket: true);

                var requestContext = context.InitialRequestMessage.GetRequestContext();
                if (requestContext.IsHttps == false)
                {
                    return stream;
                }

                var tlsSniValue = requestContext.TlsSniValue.WithIPAddress(ipEndPoint.Address);
                var sslStream = new SslStream(stream, leaveInnerStreamOpen: false);
                await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = tlsSniValue.Value,
                    RemoteCertificateValidationCallback = ValidateServerCertificate
                }, cancellationToken);

                return sslStream;
            }
            catch
            {
                // 并行连接里被放弃的候选会走到这里，必须把socket释放掉
                socket.Dispose();
                throw;
            }

            // 验证证书有效性
            bool ValidateServerCertificate(object sender, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
            {
                // 证书链错误永远不接受：污染IP后面是中间人，它会返回一张任意证书，
                // 只要放行一次就能"握手成功"，随后HTTP阶段收不到任何数据，表现为一直转圈直到超时。
                // TlsIgnoreNameMismatch只用于放过"证书名与域名不符"，不放过"证书不受信任"。
                if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
                {
                    return false;
                }

                if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
                {
                    if (this.domainConfig.TlsIgnoreNameMismatch == true)
                    {
                        return true;
                    }

                    var domain = context.DnsEndPoint.Host;
                    var dnsNames = ReadDnsNames(cert);
                    return dnsNames.Any(dns => IsMatch(dns, domain));
                }

                return errors == SslPolicyErrors.None;
            }
        }

        /// <summary>
        /// 解析为IPEndPoint
        /// </summary>
        /// <param name="dnsEndPoint"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async IAsyncEnumerable<IPEndPoint> GetIPEndPointsAsync(DnsEndPoint dnsEndPoint, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (IPAddress.TryParse(dnsEndPoint.Host, out var address))
            {
                yield return new IPEndPoint(address, dnsEndPoint.Port);
            }
            else
            {
                if (this.domainConfig.IPAddress != null)
                {
                    yield return new IPEndPoint(this.domainConfig.IPAddress, dnsEndPoint.Port);
                }

                await foreach (var item in this.domainResolver.ResolveAsync(dnsEndPoint, cancellationToken))
                {
                    yield return new IPEndPoint(item, dnsEndPoint.Port);
                }
            }
        }

        /// <summary>
        /// 读取使用的DNS名称
        /// </summary>
        /// <param name="cert"></param>
        /// <returns></returns>
        private static IEnumerable<string> ReadDnsNames(X509Certificate? cert)
        {
            if (cert is X509Certificate2 x509)
            {
                var extension = x509.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
                if (extension != null)
                {
                    return extension.EnumerateDnsNames();
                }
            }
            return Array.Empty<string>();
        }

        /// <summary>
        /// 比较域名
        /// </summary>
        /// <param name="dnsName"></param>
        /// <param name="domain"></param>
        /// <returns></returns>
        private static bool IsMatch(string dnsName, string? domain)
        {
            if (domain == null)
            {
                return false;
            }
            if (dnsName == domain)
            {
                return true;
            }
            if (dnsName[0] == '*')
            {
                return domain.EndsWith(dnsName[1..]);
            }
            return false;
        }
    }
}

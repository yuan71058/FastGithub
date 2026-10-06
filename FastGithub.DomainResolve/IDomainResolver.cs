using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// 域名解析器
    /// </summary>
    public interface IDomainResolver
    { 
        /// <summary>
        /// 解析所有ip
        /// </summary>
        /// <param name="endPoint">节点</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        IAsyncEnumerable<IPAddress> ResolveAsync(DnsEndPoint endPoint, CancellationToken cancellationToken = default);

        /// <summary>
        /// 对所有节点进行测速
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        Task TestSpeedAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 刷新所有域名的IP（清空所有缓存并重新解析测速）
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        Task<RefreshIpResult> RefreshAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 刷新所有域名的IP（优先使用在线hosts源，未覆盖的域名回退DNS查询）
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        Task<RefreshIpResult> RefreshHostsAsync(CancellationToken cancellationToken = default);
    }
}
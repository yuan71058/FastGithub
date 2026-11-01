using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// 域名解析后台服务
    /// </summary>
    sealed class DomainResolveHostedService : BackgroundService
    {
        private readonly DnscryptProxy dnscryptProxy;
        private readonly IDomainResolver domainResolver;
        private readonly HostsService hostsService;
        private readonly ILogger<DomainResolveHostedService> logger;
        private readonly TimeSpan dnscryptProxyInitDelay = TimeSpan.FromSeconds(5d);

        /// <summary>
        /// 测速周期。原值为1秒，等于每秒对全部域名×全部候选IP做一次TCP+TLS握手；
        /// 域名有几十个、候选IP上百个时，每秒上百次握手会把出口带宽和DNS配额打满，
        /// 反过来挤掉真实请求，表现为连接超时、DNS查询连续失败、页面转圈。
        /// </summary>
        private readonly TimeSpan testPeriodTimeSpan = TimeSpan.FromSeconds(60d);
        private readonly TimeSpan hostsRefreshPeriodTimeSpan = TimeSpan.FromHours(1d);

        /// <summary>
        /// 域名解析后台服务
        /// </summary>
        /// <param name="dnscryptProxy"></param>
        /// <param name="domainResolver"></param>
        /// <param name="hostsService"></param>
        public DomainResolveHostedService(
            DnscryptProxy dnscryptProxy,
            IDomainResolver domainResolver,
            HostsService hostsService,
            ILogger<DomainResolveHostedService> logger)
        {
            this.dnscryptProxy = dnscryptProxy;
            this.domainResolver = domainResolver;
            this.hostsService = hostsService;
            this.logger = logger;
        }

        /// <summary>
        /// 后台任务
        /// </summary>
        /// <param name="stoppingToken"></param>
        /// <returns></returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await this.dnscryptProxy.StartAsync(stoppingToken);
                await Task.Delay(dnscryptProxyInitDelay, stoppingToken);

                var lastHostsRefresh = default(DateTime);
                while (stoppingToken.IsCancellationRequested == false)
                {
                    await this.domainResolver.TestSpeedAsync(stoppingToken);

                    // 周期性刷新在线hosts源
                    if (DateTime.Now - lastHostsRefresh >= this.hostsRefreshPeriodTimeSpan)
                    {
                        lastHostsRefresh = DateTime.Now;
                        await this.hostsService.RefreshAsync(stoppingToken);
                    }

                    await Task.Delay(this.testPeriodTimeSpan, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "域名解析异常");
            }
        }

        /// <summary>
        /// 停止服务
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public override Task StopAsync(CancellationToken cancellationToken)
        {
            this.dnscryptProxy.Stop();
            return base.StopAsync(cancellationToken);
        }
    }
}

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// IP刷新结果
    /// </summary>
    public sealed class RefreshIpResult
    {
        /// <summary>
        /// 参与测速的域名数量
        /// </summary>
        public int DomainCount { get; set; }

        /// <summary>
        /// 测速后可用的IP数量
        /// </summary>
        public int AddressCount { get; set; }

        /// <summary>
        /// 在线hosts源是否更新成功
        /// </summary>
        public bool HostsUpdated { get; set; }

        /// <summary>
        /// 在线hosts源包含的域名数量
        /// </summary>
        public int HostsDomainCount { get; set; }

        /// <summary>
        /// 在线hosts源更新失败的原因
        /// </summary>
        public string? HostsError { get; set; }
    }
}

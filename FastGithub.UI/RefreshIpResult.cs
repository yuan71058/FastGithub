namespace FastGithub.UI
{
    /// <summary>
    /// IP刷新结果
    /// </summary>
    sealed class RefreshIpResult
    {
        public int DomainCount { get; set; }

        public int AddressCount { get; set; }

        /// <summary>
        /// 是否因超时提前结束
        /// </summary>
        public bool TimedOut { get; set; }

        /// <summary>
        /// 刷新过程中的异常信息
        /// </summary>
        public string Error { get; set; } = string.Empty;

        public bool HostsUpdated { get; set; }

        public int HostsDomainCount { get; set; }

        public string HostsError { get; set; } = string.Empty;
    }
}

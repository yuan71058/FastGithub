namespace FastGithub.UI
{
    /// <summary>
    /// IP刷新结果
    /// </summary>
    sealed class RefreshIpResult
    {
        public int DomainCount { get; set; }

        public int AddressCount { get; set; }

        public bool HostsUpdated { get; set; }

        public int HostsDomainCount { get; set; }

        public string HostsError { get; set; } = string.Empty;
    }
}

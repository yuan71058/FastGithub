using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;

namespace FastGithub.Configuration
{
    /// <summary>
    /// 监听器
    /// </summary>
    public static class GlobalListener
    {
        private static readonly IPGlobalProperties global = IPGlobalProperties.GetIPGlobalProperties();
        private static readonly HashSet<int> tcpListenPorts = GetListenPorts(global.GetActiveTcpListeners);
        private static readonly HashSet<int> udpListenPorts = GetListenPorts(global.GetActiveUdpListeners);
        private static readonly HashSet<int> reservedPorts = new();
        private static readonly Dictionary<string, List<int>> occupiedPorts = new();
        private static readonly object portLock = new();

        /// <summary>
        /// Windows下http反向代理的首选端口
        /// </summary>
        public const int HttpPreferredPort = 2000;

        /// <summary>
        /// Windows下https反向代理的首选端口
        /// </summary>
        public const int HttpsPreferredPort = 2000;

        /// <summary>
        /// ssh端口
        /// </summary>
        public static int SshPort { get; } = GetAvailablePort(CanListenTcp, 3000, "ssh");

        /// <summary>
        /// git端口
        /// </summary>
        public static int GitPort { get; } = GetAvailablePort(CanListenTcp, 9418, "git");

        /// <summary>
        /// http端口
        /// </summary>
        public static int HttpPort { get; } = OperatingSystem.IsWindows()
            ? GetAvailablePort(CanListenTcp, HttpPreferredPort, "http")
            : GetAvailableTcpPort(3880);

        /// <summary>
        /// https端口
        /// </summary>
        public static int HttpsPort { get; } = OperatingSystem.IsWindows()
            ? GetAvailablePort(CanListenTcp, HttpsPreferredPort, "https")
            : GetAvailableTcpPort(38443);

        /// <summary>
        /// UI内部通信端口
        /// </summary>
        public static int UiHttpPort { get; } = GetAvailablePort(CanListenTcp, 45678, "ui");

        /// <summary>
        /// 获取已监听的端口
        /// </summary>
        /// <param name="func"></param>
        /// <returns></returns>
        private static HashSet<int> GetListenPorts(Func<IPEndPoint[]> func)
        {
            var hashSet = new HashSet<int>();
            try
            {
                foreach (var endpoint in func())
                {
                    hashSet.Add(endpoint.Port);
                }
            }
            catch (Exception)
            {
            }
            return hashSet;
        }

        /// <summary>
        /// 是可以监听TCP
        /// </summary>
        /// <param name="port"></param>
        /// <returns></returns>
        public static bool CanListenTcp(int port)
        {
            return tcpListenPorts.Contains(port) == false;
        }

        /// <summary>
        /// 是可以监听UDP
        /// </summary>
        /// <param name="port"></param>
        /// <returns></returns>
        public static bool CanListenUdp(int port)
        {
            return udpListenPorts.Contains(port) == false;
        }

        /// <summary>
        /// 是可以监听TCP和Udp
        /// </summary>
        /// <param name="port"></param>
        /// <returns></returns>
        public static bool CanListen(int port)
        {
            return CanListenTcp(port) && CanListenUdp(port);
        }

        /// <summary>
        /// 获取可用的随机Tcp端口
        /// </summary>
        /// <param name="minPort"></param> 
        /// <returns></returns>
        public static int GetAvailableTcpPort(int minPort)
        {
            return GetAvailablePort(CanListenTcp, minPort);
        }

        /// <summary>
        /// 获取可用的随机Udp端口
        /// </summary>
        /// <param name="minPort"></param> 
        /// <returns></returns>
        public static int GetAvailableUdpPort(int minPort)
        {
            return GetAvailablePort(CanListenUdp, minPort);
        }

        /// <summary>
        /// 获取可用的随机端口
        /// </summary>
        /// <param name="minPort"></param> 
        /// <returns></returns>
        public static int GetAvailablePort(int minPort)
        {
            return GetAvailablePort(CanListen, minPort);
        }

        /// <summary>
        /// 获取探测端口时遇到的被占用端口
        /// </summary>
        /// <param name="name">服务名</param>
        /// <returns></returns>
        public static IReadOnlyList<int> GetOccupiedPorts(string name)
        {
            lock (portLock)
            {
                if (occupiedPorts.TryGetValue(name, out var occupied) == true)
                {
                    return occupied.ToArray();
                }
                return Array.Empty<int>();
            }
        }

        /// <summary>
        /// 获取可用端口
        /// </summary>
        /// <param name="canFunc"></param>
        /// <param name="minPort"></param>
        /// <param name="name">服务名，用于记录探测时被占用的端口</param>
        /// <returns></returns>
        /// <exception cref="FastGithubException"></exception>
        private static int GetAvailablePort(Func<int, bool> canFunc, int minPort, string? name = null)
        {
            lock (portLock)
            {
                for (var port = minPort; port < IPEndPoint.MaxPort; port++)
                {
                    if (reservedPorts.Contains(port) == true)
                    {
                        continue;
                    }

                    if (canFunc(port) == true)
                    {
                        if (name != null)
                        {
                            reservedPorts.Add(port);
                        }
                        return port;
                    }

                    if (name != null)
                    {
                        if (occupiedPorts.TryGetValue(name, out var occupied) == false)
                        {
                            occupied = new List<int>();
                            occupiedPorts[name] = occupied;
                        }
                        occupied.Add(port);
                    }
                }
            }
            throw new FastGithubException("当前无可用的端口");
        }
    }
}

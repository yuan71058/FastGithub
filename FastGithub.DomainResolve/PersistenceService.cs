using FastGithub.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// 域名持久化
    /// </summary>
    sealed partial class PersistenceService
    {
        private static readonly string dataFile = "dnsendpoints.json";
        private static readonly string addressFile = "ipaddresses.json";
        private static readonly SemaphoreSlim dataLocker = new(1, 1); 

        private readonly FastGithubConfig fastGithubConfig;
        private readonly ILogger<PersistenceService> logger;


        private record EndPointItem(string Host, int Port);

        [JsonSerializable(typeof(EndPointItem[]))]
        [JsonSourceGenerationOptions(
            WriteIndented = true,
            PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
        private partial class EndPointItemsContext : JsonSerializerContext
        {
        }


        /// <summary>
        /// 域名与已验证IP的持久化记录
        /// </summary>
        private record AddressItem(string Host, int Port, string[] Addresses);

        [JsonSerializable(typeof(AddressItem[]))]
        [JsonSourceGenerationOptions(
            WriteIndented = true,
            PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
        private partial class AddressItemsContext : JsonSerializerContext
        {
        }


        /// <summary>
        /// 域名持久化
        /// </summary> 
        /// <param name="fastGithubConfig"></param>
        /// <param name="logger"></param>
        public PersistenceService(
            FastGithubConfig fastGithubConfig,
            ILogger<PersistenceService> logger)
        {
            this.fastGithubConfig = fastGithubConfig;
            this.logger = logger;
        }


        /// <summary>
        /// 读取保存的节点
        /// </summary>
        /// <returns></returns>
        public IList<DnsEndPoint> ReadDnsEndPoints()
        {
            if (File.Exists(dataFile) == false)
            {
                return Array.Empty<DnsEndPoint>();
            }

            try
            {
                dataLocker.Wait();

                var utf8Json = File.ReadAllBytes(dataFile);
                var endPointItems = JsonSerializer.Deserialize(utf8Json, EndPointItemsContext.Default.EndPointItemArray);
                if (endPointItems == null)
                {
                    return Array.Empty<DnsEndPoint>();
                }

                var dnsEndPoints = new List<DnsEndPoint>();
                foreach (var item in endPointItems)
                {
                    if (this.fastGithubConfig.IsMatch(item.Host) == true)
                    {
                        dnsEndPoints.Add(new DnsEndPoint(item.Host, item.Port));
                    }
                }
                return dnsEndPoints;
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex.Message, "读取dns记录异常");
                return Array.Empty<DnsEndPoint>();
            }
            finally
            {
                dataLocker.Release();
            }
        }

        /// <summary>
        /// 保存节点到文件
        /// </summary>
        /// <param name="dnsEndPoints"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task WriteDnsEndPointsAsync(IEnumerable<DnsEndPoint> dnsEndPoints, CancellationToken cancellationToken)
        {
            try
            {
                await dataLocker.WaitAsync(CancellationToken.None);

                var endPointItems = dnsEndPoints.Select(item => new EndPointItem(item.Host, item.Port)).ToArray();
                var utf8Json = JsonSerializer.SerializeToUtf8Bytes(endPointItems, EndPointItemsContext.Default.EndPointItemArray);
                await File.WriteAllBytesAsync(dataFile, utf8Json, cancellationToken);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex.Message, "保存dns记录异常");
            }
            finally
            {
                dataLocker.Release();
            }
        }

        /// <summary>
        /// 读取上次运行已验证过的IP
        /// IP是否仍然可用由后续测速复核，这里只负责让重启后不必从零开始探测
        /// </summary>
        /// <returns></returns>
        public IList<(DnsEndPoint EndPoint, IPAddress[] Addresses)> ReadAddressEntries()
        {
            if (File.Exists(addressFile) == false)
            {
                return Array.Empty<(DnsEndPoint, IPAddress[])>();
            }

            try
            {
                dataLocker.Wait();

                var utf8Json = File.ReadAllBytes(addressFile);
                var addressItems = JsonSerializer.Deserialize(utf8Json, AddressItemsContext.Default.AddressItemArray);
                if (addressItems == null)
                {
                    return Array.Empty<(DnsEndPoint, IPAddress[])>();
                }

                var result = new List<(DnsEndPoint, IPAddress[])>();
                foreach (var item in addressItems)
                {
                    if (this.fastGithubConfig.IsMatch(item.Host) == false)
                    {
                        continue;
                    }

                    var addresses = new List<IPAddress>();
                    foreach (var text in item.Addresses)
                    {
                        if (IPAddress.TryParse(text, out var address) == true && IPAddress.IsLoopback(address) == false)
                        {
                            addresses.Add(address);
                        }
                    }

                    if (addresses.Count > 0)
                    {
                        result.Add((new DnsEndPoint(item.Host, item.Port), addresses.ToArray()));
                    }
                }
                return result;
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex.Message, "读取ip记录异常");
                return Array.Empty<(DnsEndPoint, IPAddress[])>();
            }
            finally
            {
                dataLocker.Release();
            }
        }

        /// <summary>
        /// 保存已验证的IP到文件
        /// </summary>
        /// <param name="entries">域名节点与其候选IP</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task WriteAddressEntriesAsync(IEnumerable<KeyValuePair<DnsEndPoint, IPAddress[]>> entries, CancellationToken cancellationToken)
        {
            try
            {
                await dataLocker.WaitAsync(CancellationToken.None);

                var addressItems = entries
                    .Where(item => item.Value.Length > 0)
                    .OrderBy(item => item.Key.Host, StringComparer.OrdinalIgnoreCase)
                    .Select(item => new AddressItem(
                        item.Key.Host,
                        item.Key.Port,
                        item.Value.Select(address => address.ToString()).ToArray()))
                    .ToArray();

                var utf8Json = JsonSerializer.SerializeToUtf8Bytes(addressItems, AddressItemsContext.Default.AddressItemArray);
                await File.WriteAllBytesAsync(addressFile, utf8Json, cancellationToken);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex.Message, "保存ip记录异常");
            }
            finally
            {
                dataLocker.Release();
            }
        }
    }
}

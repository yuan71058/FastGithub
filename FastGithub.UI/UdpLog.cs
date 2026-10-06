using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;

namespace FastGithub.UI
{
    public class UdpLog
    {
        public DateTime Timestamp { get; set; }

        public LogLevel Level { get; set; }

        public string Message { get; set; } = string.Empty;

        public string SourceContext { get; set; } = string.Empty;

        public string Color => this.Level <= LogLevel.Information ? "#333" : "IndianRed";

        /// <summary>
        /// 复制到剪贴板
        /// 剪贴板可能被其它进程占用，此时SetText会抛出COMException，这里重试并忽略最终失败
        /// </summary>
        public void SetToClipboard()
        {
            var text = $"{this.Timestamp:yyyy-MM-dd HH:mm:ss.fff}\r\n{this.Message}";
            for (var i = 0; i < 3; i++)
            {
                try
                {
                    Clipboard.SetText(text);
                    return;
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    System.Threading.Thread.Sleep(100);
                }
            }
        }

    }

}

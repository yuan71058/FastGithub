using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FastGithub.UI
{
    /// <summary>
    /// IssuesWebbrowser.xaml 的交互逻辑
    /// </summary>
    public partial class IssuesWebbrowser : UserControl
    {
        private bool navigated;

        public IssuesWebbrowser()
        {
            InitializeComponent();

            // 不能在构造函数里导航：此时控件高度还是0，IE 会按极小的视口排版，
            // 之后再布局就会把滚动条留在底部，表现成打开问答页直接看到最后一段
            this.Loaded += this.OnLoaded;
            this.webBrowser.LoadCompleted += this.OnDocumentCompleted;
            this.webBrowser.AddHandler(KeyDownEvent, new RoutedEventHandler(WebBrowser_KeyDown), true);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (this.navigated == false)
            {
                this.navigated = true;
                this.NavigateIssueHtml();
            }
        }

        /// <summary>
        /// 每次加载完成都回到顶部，避免刷新后停在上一处
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnDocumentCompleted(object sender, System.Windows.Navigation.NavigationEventArgs e)
        {
            try
            {
                // 用脚本复位滚动条，避免为了拿 Document 而引入 mshtml 互操作程序集
                this.webBrowser.InvokeScript("eval", "document.documentElement.scrollTop=0;document.body.scrollTop=0;");
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 拦截F5
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void WebBrowser_KeyDown(object sender, RoutedEventArgs e)
        {
            var @event = (KeyEventArgs)e;
            if (@event.Key == Key.F5)
            {
                this.NavigateIssueHtml();
            }
        }

        private void NavigateIssueHtml()
        {
            try
            {
                var resource = Application.GetResourceStream(new Uri("Resource/issue.html", UriKind.Relative));
                this.webBrowser.NavigateToStream(resource.Stream);
            }
            catch (Exception)
            {
            }
        }
    }
}

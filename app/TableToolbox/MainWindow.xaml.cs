using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

namespace TableToolbox
{
    /// <summary>
    /// 原生 WinUI 3 窗口，内容区直接承载用户原来的「表格工具箱.html」。
    /// 网页逻辑一字未改：识别 / 对比 / 派生 / 导入导出 全部沿用原页面实现。
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private string webFolder;
        private string indexPath;
        private bool started;

        public MainWindow()
        {
            this.InitializeComponent();
            this.Title = "表格工具箱";
            // 等控件真正进入可视树再初始化 WebView2，避免过早调用失败
            Web.Loaded += (s, e) =>
            {
                if (started) return;
                started = true;
                _ = StartupAsync();
            };
        }

        private async Task StartupAsync()
        {
            try
            {
                App.Log("base dir = " + AppContext.BaseDirectory);
                string source = LocateHtml();
                if (source == null)
                {
                    ShowFallback(new FileNotFoundException("没找到网页文件 index.html（web 文件夹需与 exe 放在一起）"));
                    return;
                }
                App.Log("html source = " + source);

                // 统一复制到 %LOCALAPPDATA%\TableToolbox\web\index.html，用纯英文路径承载
                webFolder = CacheFolder();
                indexPath = Path.Combine(webFolder, "index.html");
                if (!(File.Exists(indexPath) && new FileInfo(indexPath).Length == new FileInfo(source).Length))
                {
                    File.Copy(source, indexPath, true);
                }
                App.Log("cached html = " + indexPath);

                string dataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TableToolbox", "WebView2");
                Directory.CreateDirectory(dataDir);

                var options = new CoreWebView2EnvironmentOptions();
                var env = await CoreWebView2Environment.CreateAsync(null, dataDir, options);
                await Web.EnsureCoreWebView2Async(env);

                var core = Web.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = true;
                core.Settings.IsZoomControlEnabled = true;
                core.Settings.AreBrowserAcceleratorKeysEnabled = true;

                // 网页里用 Blob 导出 Excel/CSV，交给 WebView2 默认下载流程（存到「下载」）
                core.NewWindowRequested += Core_NewWindowRequested;
                core.PermissionRequested += (s, e) => { e.State = CoreWebView2PermissionState.Allow; };

                // 用虚拟域名承载，避免 file:// 的跨域/下载限制
                core.SetVirtualHostNameToFolderMapping(
                    "app.local", webFolder, CoreWebView2HostResourceAccessKind.Allow);

                core.Navigate("https://app.local/index.html");
                App.Log("navigate app.local/index.html");
            }
            catch (Exception ex)
            {
                App.Log("startup FAIL: " + ex);
                ShowFallback(ex);
            }
        }

        private void Core_NewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
        {
            try
            {
                args.Handled = true;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(args.Uri)
                {
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private void ShowFallback(Exception ex)
        {
            try
            {
                Web.Visibility = Visibility.Collapsed;
                Fallback.Visibility = Visibility.Visible;
                FallbackText.Text = "内嵌页面启动失败。\n" +
                                    "请确认 exe 同目录下有 web\\index.html（解压后别只拷 exe 一个文件）。\n\n" +
                                    "详细信息：" + ex.Message;
            }
            catch { }
        }

        private void OpenInBrowser_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string target = indexPath;
                if (string.IsNullOrEmpty(target) || !File.Exists(target)) target = LocateHtml();
                if (target == null)
                {
                    FallbackText.Text = "找不到网页文件，请重新解压完整压缩包。";
                    return;
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                FallbackText.Text = "打开失败：" + ex.Message;
            }
        }

        /// <summary>按优先级找网页：exe 同目录 web\index.html → 同目录 表格工具箱.html → 本地缓存</summary>
        private static string LocateHtml()
        {
            string baseDir = AppContext.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDir, "web", "index.html"),
                Path.Combine(baseDir, "表格工具箱.html"),
                Path.Combine(baseDir, "index.html"),
                Path.Combine(CacheFolder(), "index.html")
            };
            foreach (var c in candidates)
            {
                try
                {
                    if (File.Exists(c) && new FileInfo(c).Length > 1024) return c;
                }
                catch { }
            }
            return null;
        }

        private static string CacheFolder()
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TableToolbox", "web");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }
}

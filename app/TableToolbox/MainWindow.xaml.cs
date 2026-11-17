using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace TableToolbox
{
    /// <summary>
    /// 原生 WinUI 3 窗口，内容区直接承载用户原来的「表格工具箱」网页。
    /// 网页逻辑一字未改：识别 / 对比 / 派生 / 导入导出 全部沿用原页面实现。
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private const string ResourceName = "TableToolbox.web.index.html";
        private const long SingleFileLimit = 24L * 1024 * 1024; // 单文件上限，超过就提示不导入

        private string webFolder;
        private string indexPath;
        private bool started;

        public MainWindow()
        {
            this.InitializeComponent();
            this.Title = "表格工具箱";
            SetupChrome();
            // 等控件真正进入可视树再初始化 WebView2，避免过早调用失败
            Web.Loaded += (s, e) =>
            {
                if (started) return;
                started = true;
                _ = StartupAsync();
            };
        }

        /// <summary>纯外观：隐藏系统标题栏，改用自绘 40px 标题栏；最小化/最大化/关闭仍是系统原生按钮。</summary>
        private void SetupChrome()
        {
            try
            {
                IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var apw = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(
                    Win32Interop.GetWindowIdFromWindow(hWnd));
                apw.TitleBar.ExtendsContentIntoTitleBar = true;
                apw.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
                apw.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
                apw.TitleBar.ButtonForegroundColor = Microsoft.UI.Colors.Black;
                apw.TitleBar.ButtonInactiveForegroundColor = Microsoft.UI.Colors.Gray;
                SetTitleBar(AppTitleBar);
            }
            catch (Exception ex)
            {
                App.Log("SetupChrome FAIL: " + ex.Message);
            }
        }

        private async Task StartupAsync()
        {
            try
            {
                App.Log("base dir = " + AppContext.BaseDirectory);

                // 页面唯一来源 = 编译进 exe 的嵌入资源，避免出现第二份过期副本
                webFolder = CacheFolder();
                indexPath = Path.Combine(webFolder, "index.html");
                long pageLen = ExtractPage(indexPath);
                App.Log("page bytes = " + pageLen + " -> " + indexPath);

                // 顺手清掉旧版本留在 exe 旁边的 WebView2 缓存目录，保持便携版「只有一个 exe」
                CleanLegacyDataFolder();

                // 关键：必须自己指定用户数据目录。用无参 EnsureCoreWebView2Async 时，
                // WebView2 默认会在 exe 同级生成「<exe 名>.WebView2」文件夹 —— 就是「双击后多出来一个文件夹」。
                string userData = WebDataFolder();
                var env = await CoreWebView2Environment.CreateAsync(null, userData, null);
                await Web.EnsureCoreWebView2Async(env);
                App.Log("webview2 data = " + userData);

                var core = Web.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = true;
                core.Settings.IsZoomControlEnabled = true;
                core.Settings.AreBrowserAcceleratorKeysEnabled = true;

                // 关掉浏览器味最重的几项：后退手势、自动填充、密码保存（都是纯 WebView 行为，与页面功能无关）
                try
                {
                    core.Settings.IsSwipeNavigationEnabled = false;
                    core.Settings.IsGeneralAutofillEnabled = false;
                    core.Settings.IsPasswordAutosaveEnabled = false;
                }
                catch (Exception ex) { App.Log("settings (optional) skip: " + ex.Message); }

                // 允许从资源管理器把文件拖进页面（默认即为 true，这里显式置位）
                try
                {
                    var prop = Web.GetType().GetProperty("AllowExternalDrop");
                    if (prop != null && prop.CanWrite) prop.SetValue(Web, true);
                    else App.Log("AllowExternalDrop: property not found (skip)");
                }
                catch (Exception ex)
                {
                    App.Log("AllowExternalDrop FAIL: " + ex.Message);
                }

                // 网页里用 Blob 导出 Excel/CSV，交给 WebView2 默认下载流程（存到「下载」）
                core.NewWindowRequested += Core_NewWindowRequested;
                core.PermissionRequested += (s, e) => { e.State = CoreWebView2PermissionState.Allow; };

                // 用虚拟域名承载，避免 file:// 的跨域 / 下载限制
                core.SetVirtualHostNameToFolderMapping(
                    "app.local", webFolder, CoreWebView2HostResourceAccessKind.Allow);

                core.NavigationCompleted += (s, e) =>
                {
                    App.Log("navigation: " + e.IsSuccess + " " + (e.WebErrorStatus.ToString()));
                    if (!e.IsSuccess)
                    {
                        ShowFallback(new Exception("页面加载失败：" + e.WebErrorStatus));
                    }
                };

                core.Navigate("https://app.local/index.html");
                App.Log("navigate app.local/index.html");
            }
            catch (Exception ex)
            {
                App.Log("startup FAIL: " + ex);
                ShowFallback(ex);
            }
        }

        /* ==================== 原生拖放（不依赖 WebView2 的 HTML5 拖放） ==================== */

        private async void Drop_DragOver(object sender, DragEventArgs e)
        {
            try
            {
                if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
                e.AcceptedOperation = DataPackageOperation.Copy;
                e.DragUIOverride.Caption = "松开导入到表格";
                e.DragUIOverride.IsCaptionVisible = true;
                e.Handled = true;
                await RunScriptAsync("window.__nwDragState && window.__nwDragState(true,-1)");
            }
            catch { }
        }

        private void Drop_DragLeave(object sender, DragEventArgs e)
        {
            HideDropMask();
        }

        /// <summary>收起页面里的拖放遮罩。原生拖放被这里接管时，页面收不到 dragleave/drop，
        /// 必须由外壳主动收，否则松手后那个「松开即可导入」的框会一直挂在那儿。</summary>
        private void HideDropMask()
        {
            _ = RunScriptAsync("window.__nwDragState && window.__nwDragState(false,-1)");
        }

        private async void Drop_Drop(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                HideDropMask();      // 拖的不是文件（比如一段文字），也要把遮罩收掉
                return;
            }
            var deferral = e.GetDeferral();
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var payload = new List<Dictionary<string, string>>();

                foreach (var item in items)
                {
                    var file = item as StorageFile;
                    if (file == null) continue;

                    var props = await file.GetBasicPropertiesAsync();
                    if ((long)props.Size > SingleFileLimit)
                    {
                        App.Log("drop skipped (too big): " + file.Name + " " + props.Size);
                        continue;
                    }

                    var buffer = await FileIO.ReadBufferAsync(file);
                    byte[] bytes = new byte[buffer.Length];
                    using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer))
                    {
                        reader.ReadBytes(bytes);
                    }

                    payload.Add(new Dictionary<string, string>
                    {
                        { "name", file.Name },
                        { "b64", Convert.ToBase64String(bytes) }
                    });
                    App.Log("drop file: " + file.Name + " " + bytes.Length + " bytes");
                }

                if (payload.Count == 0)
                {
                    App.Log("drop: no importable file");
                    return;
                }

                string json = JsonSerializer.Serialize(payload);
                string result = await RunScriptAsync(
                    "window.__nwImportFiles && window.__nwImportFiles(" + json + ",-1)");
                App.Log("drop -> " + result);
            }
            catch (Exception ex)
            {
                App.Log("drop FAIL: " + ex);
            }
            finally
            {
                // 先收遮罩再收尾：无论导入成功与否，松手都必须让遮罩消失
                HideDropMask();
                deferral.Complete();
            }
        }

        private async Task<string> RunScriptAsync(string js)
        {
            try
            {
                if (Web.CoreWebView2 == null) return "no-core";
                string r = await Web.CoreWebView2.ExecuteScriptAsync(js);
                return r;
            }
            catch (Exception ex)
            {
                App.Log("script FAIL: " + ex.Message);
                return "script-error";
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
                FallbackText.Text = "内嵌页面启动失败。\n\n详细信息：" + ex.Message +
                                    "\n\n可点下面的按钮，用系统浏览器打开同一份页面（功能完全一致）。";
            }
            catch { }
        }

        private void OpenInBrowser_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string target = indexPath;
                if (string.IsNullOrEmpty(target) || !File.Exists(target))
                {
                    target = Path.Combine(CacheFolder(), "index.html");
                    ExtractPage(target);
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

        /// <summary>把编译进 exe 的网页释放到本地纯英文路径；大小一致就不重写。返回释放后的字节数。</summary>
        private static long ExtractPage(string target)
        {
            var asm = Assembly.GetExecutingAssembly();
            using (Stream s = asm.GetManifestResourceStream(ResourceName))
            {
                if (s == null)
                {
                    var names = string.Join(", ", asm.GetManifestResourceNames());
                    throw new InvalidOperationException("嵌入的网页资源缺失（现有资源：" + names + "）");
                }

                long len = s.Length;
                var fi = new FileInfo(target);
                if (fi.Exists && fi.Length == len) return len;

                using (var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    s.CopyTo(fs);
                }
                return len;
            }
        }

        private static string CacheFolder()
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TableToolbox", "web");
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>WebView2 用户数据目录固定放 %LOCALAPPDATA%，绝不在 exe 旁边留东西。</summary>
        private static string WebDataFolder()
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TableToolbox", "WebView2");
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>清掉旧版本遗留在 exe 旁边的「xxx.exe.WebView2」目录；被占用删不掉就跳过。</summary>
        private static void CleanLegacyDataFolder()
        {
            try
            {
                string dir = AppContext.BaseDirectory;
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                foreach (var path in Directory.GetDirectories(dir, "*.WebView2"))
                {
                    try
                    {
                        Directory.Delete(path, true);
                        App.Log("legacy data folder removed: " + path);
                    }
                    catch (Exception ex)
                    {
                        App.Log("legacy data folder busy (skip): " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                App.Log("CleanLegacyDataFolder FAIL: " + ex.Message);
            }
        }
    }
}

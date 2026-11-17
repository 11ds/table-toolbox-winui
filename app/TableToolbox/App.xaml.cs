using Microsoft.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TableToolbox
{
    public partial class App : Application
    {
        public static MainWindow RootWindow { get; private set; }
        static string LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TableToolbox", "startup.log");
        static string StatePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TableToolbox", "window.json");

        // 窗口最小尺寸（DIP）：再小左栏和内容区就挤成一团了
        private const int MinWidthDip = 980;
        private const int MinHeightDip = 620;

        public static void Log(string msg)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss") + " " + msg + Environment.NewLine);
            }
            catch { }
        }

        public App()
        {
            Log("=== App ctor start ===");
            try
            {
                this.InitializeComponent();
                Log("InitializeComponent OK");
            }
            catch (Exception ex)
            {
                Log("InitializeComponent FAIL: " + ex.ToString());
                throw;
            }
            this.UnhandledException += App_UnhandledException;
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            Log("UNHANDLED: " + (e.Exception != null ? e.Exception.ToString() : "null"));
            e.Handled = true;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            Log("OnLaunched start");
            try
            {
                RootWindow = new MainWindow();
                Log("MainWindow created");
                RootWindow.Activate();
                Log("MainWindow activated");
            }
            catch (Exception ex)
            {
                Log("MainWindow FAIL: " + ex.ToString());
                throw;
            }

            try
            {
                IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(RootWindow);
                WindowId id = Win32Interop.GetWindowIdFromWindow(hWnd);
                AppWindow apw = AppWindow.GetFromWindowId(id);
                apw.Title = "表格工具箱";

                RestoreBounds(apw, hWnd);
                HookMinSize(hWnd);
                apw.Closing += (s, e) => SaveBounds(apw);
                Log("Window size set");
            }
            catch (Exception ex)
            {
                Log("Window size FAIL: " + ex.Message);
            }
            Log("OnLaunched done");
        }

        /* ==================== 窗口位置记忆（原生应用的常规行为） ==================== */

        private sealed class WindowState
        {
            public int x { get; set; }
            public int y { get; set; }
            public int w { get; set; }
            public int h { get; set; }
            public bool max { get; set; }
        }

        private static void RestoreBounds(AppWindow apw, IntPtr hWnd)
        {
            var area = DisplayArea.Primary.WorkArea;
            WindowState st = null;
            try
            {
                if (File.Exists(StatePath))
                    st = JsonSerializer.Deserialize<WindowState>(File.ReadAllText(StatePath));
            }
            catch (Exception ex) { Log("read state FAIL: " + ex.Message); }

            int w, h, x, y;
            bool useSaved = false;

            if (st != null && st.w >= MinWidthDip && st.h >= MinHeightDip)
            {
                // 只在窗口还落在当前显示器可视范围内时才沿用（换了显示器/分辨率不会跑到屏幕外）
                int ox = Math.Max(st.x, area.X);
                int oy = Math.Max(st.y, area.Y);
                int oright = Math.Min(st.x + st.w, area.X + area.Width);
                int obottom = Math.Min(st.y + st.h, area.Y + area.Height);
                if (oright - ox >= 200 && obottom - oy >= 200)
                {
                    w = Math.Min(st.w, area.Width);
                    h = Math.Min(st.h, area.Height);
                    // 整窗都留在工作区内，别只露出边角
                    x = Math.Max(area.X, Math.Min(st.x, area.X + area.Width - w));
                    y = Math.Max(area.Y, Math.Min(st.y, area.Y + area.Height - h));
                    useSaved = true;
                    goto apply;
                }
            }

            // 首次运行：按当前 DPI 换算成 DIP 再定尺寸，4K/150% 上不会变成一个小窗口
            double scale = 1.0;
            try
            {
                uint dpi = GetDpiForWindow(hWnd);
                if (dpi > 0) scale = dpi / 96.0;
            }
            catch { }

            w = Math.Min((int)(1440 * scale), area.Width - (int)(60 * scale));
            h = Math.Min((int)(900 * scale), area.Height - (int)(50 * scale));
            if (w < MinWidthDip) w = Math.Min(MinWidthDip, area.Width);
            if (h < MinHeightDip) h = Math.Min(MinHeightDip, area.Height);
            x = area.X + Math.Max(0, (area.Width - w) / 2);
            y = area.Y + Math.Max(0, (area.Height - h) / 2);

        apply:
            apw.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));
            if (useSaved && st.max)
            {
                try
                {
                    if (apw.Presenter is OverlappedPresenter op)
                        op.Maximize();
                }
                catch { }
            }
        }

        private static void SaveBounds(AppWindow apw)
        {
            try
            {
                var op = apw.Presenter as OverlappedPresenter;
                bool max = op != null && op.State == OverlappedPresenterState.Maximized;

                WindowState st = null;
                try
                {
                    if (File.Exists(StatePath))
                        st = JsonSerializer.Deserialize<WindowState>(File.ReadAllText(StatePath));
                }
                catch { }
                st = st ?? new WindowState();

                // 最大化时 apw.Size 是最大化后的尺寸，存下来会把下次的正常尺寸顶满，只在非最大化时记尺寸
                if (!max)
                {
                    var pos = apw.Position;
                    var size = apw.Size;
                    st.x = pos.X;
                    st.y = pos.Y;
                    st.w = size.Width;
                    st.h = size.Height;
                }
                st.max = max;

                Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
                File.WriteAllText(StatePath, JsonSerializer.Serialize(st));
            }
            catch (Exception ex) { Log("save state FAIL: " + ex.Message); }
        }

        /* ==================== 最小窗口尺寸（WM_GETMINMAXINFO） ==================== */

        private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, IntPtr dwRefData);

        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        private const uint WM_GETMINMAXINFO = 0x0024;
        private static SubclassProc _minSizeProc;   // 必须持有引用，否则回调会被回收

        private static void HookMinSize(IntPtr hWnd)
        {
            try
            {
                _minSizeProc = MinSizeProc;
                SetWindowSubclass(hWnd, _minSizeProc, (UIntPtr)1, IntPtr.Zero);
            }
            catch (Exception ex) { Log("HookMinSize FAIL: " + ex.Message); }
        }

        private static IntPtr MinSizeProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr data)
        {
            if (msg == WM_GETMINMAXINFO)
            {
                try
                {
                    uint dpi = GetDpiForWindow(hWnd);
                    double scale = dpi == 0 ? 1.0 : dpi / 96.0;
                    var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                    int minW = (int)(MinWidthDip * scale);
                    int minH = (int)(MinHeightDip * scale);
                    if (mmi.ptMinTrackSize.X < minW) mmi.ptMinTrackSize.X = minW;
                    if (mmi.ptMinTrackSize.Y < minH) mmi.ptMinTrackSize.Y = minH;
                    Marshal.StructureToPtr(mmi, lParam, false);
                }
                catch { }
            }
            return DefSubclassProc(hWnd, msg, wParam, lParam);
        }
    }
}

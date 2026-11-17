using Microsoft.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using System;
using System.IO;

namespace TableToolbox
{
    public partial class App : Application
    {
        public static MainWindow RootWindow { get; private set; }
        static string LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TableToolbox", "startup.log");

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

                var display = Microsoft.UI.Windowing.DisplayArea.Primary;
                if (display != null)
                {
                    var area = display.WorkArea;
                    // 尽量占满工作区，网页版原本就是宽屏布局
                    int w = Math.Min(1500, Math.Max(1100, area.Width - 80));
                    int h = Math.Min(980, Math.Max(760, area.Height - 60));
                    int x = area.X + Math.Max(0, (area.Width - w) / 2);
                    int y = area.Y + Math.Max(0, (area.Height - h) / 2);
                    apw.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));
                }
                Log("Window size set");
            }
            catch (Exception ex)
            {
                Log("Window size FAIL: " + ex.Message);
            }
            Log("OnLaunched done");
        }
    }
}

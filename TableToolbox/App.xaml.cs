using Microsoft.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using System;

namespace TableToolbox
{
    public partial class App : Application
    {
        public static MainWindow RootWindow { get; private set; }

        // Shared state passed from compare page to derive page (scheme C: derive is based on compare result)
        public static CompareCore.CompareResult SharedCompare;
        public static System.Collections.Generic.List<CompareCore.Table> SharedTables;
        public static CompareCore.CompareConfig SharedConfig;
        public static string SharedSig;

        public App()
        {
            this.InitializeComponent();
            this.UnhandledException += App_UnhandledException;
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            try { System.Diagnostics.Debug.WriteLine("Unhandled: " + e.Exception.Message); } catch { }
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            RootWindow = new MainWindow();
            RootWindow.Activate();

            // Center on screen with a sensible default size
            try
            {
                IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(RootWindow);
                WindowId id = Win32Interop.GetWindowIdFromWindow(hWnd);
                AppWindow apw = AppWindow.GetFromWindowId(id);
                var display = Microsoft.UI.Windowing.DisplayArea.Primary;
                if (display != null)
                {
                    int w = 1360, h = 860;
                    var area = display.WorkArea;
                    int x = area.X + Math.Max(0, (area.Width - w) / 2);
                    int y = area.Y + Math.Max(0, (area.Height - h) / 2);
                    apw.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));
                }
            }
            catch { }
        }
    }
}

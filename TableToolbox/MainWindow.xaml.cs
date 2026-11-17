using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI;
using System;

namespace TableToolbox
{
    public sealed partial class MainWindow : Window
    {
        private RecognizePage recognizePage;
        private ComparePage comparePage;
        private DerivePage derivePage;

        public MainWindow()
        {
            this.InitializeComponent();
            ExtendContentIntoTitleBar();
            recognizePage = new RecognizePage();
            ContentFrame.Navigate(typeof(RecognizePage), null, new Microsoft.UI.Xaml.Media.Animation.SuppressNavigationTransitionInfo());
        }

        private void ExtendContentIntoTitleBar()
        {
            try
            {
                IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
                var appWindow = AppWindow.GetFromWindowId(windowId);
                appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
                appWindow.TitleBar.ButtonBackgroundColor = Windows.UI.Colors.Transparent;
                appWindow.TitleBar.ButtonInactiveBackgroundColor = Windows.UI.Colors.Transparent;
                SetTitleBar(TitleBar);
            }
            catch { }
        }

        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is NavigationViewItem item)
            {
                string tag = item.Tag as string ?? "";
                Type pageType = null;
                if (tag == "recognize") pageType = typeof(RecognizePage);
                else if (tag == "compare") pageType = typeof(ComparePage);
                else if (tag == "derive") pageType = typeof(DerivePage);

                if (pageType != null && ContentFrame.CurrentSourcePageType != pageType)
                {
                    ContentFrame.Navigate(pageType, null, new Microsoft.UI.Xaml.Media.Animation.SuppressNavigationTransitionInfo());
                }
            }
        }

        public static MainWindow Current => App.RootWindow;

        public ComparePage GetComparePage() { return comparePage; }
        public DerivePage GetDerivePage() { return derivePage; }
    }
}

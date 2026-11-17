using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace TableToolbox
{
    public sealed partial class RecognizePage : Page
    {
        private string _path;
        private EngineBridge.LoadedTable _loaded;

        public RecognizePage()
        {
            this.InitializeComponent();
        }

        private async void BtnPick_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker();
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.RootWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".bmp");
                picker.FileTypeFilter.Add(".tif");
                picker.FileTypeFilter.Add(".tiff");
                picker.FileTypeFilter.Add(".pdf");
                picker.FileTypeFilter.Add(".xlsx");
                picker.FileTypeFilter.Add(".xls");
                picker.FileTypeFilter.Add(".csv");
                picker.FileTypeFilter.Add(".et");
                picker.FileTypeFilter.Add(".wps");

                var file = await picker.PickSingleFileAsync();
                if (file == null) return;
                _path = file.Path;
                TxtFile.Text = file.Name;
                BtnRecognize.IsEnabled = true;
                TxtStats.Text = "";
            }
            catch (Exception ex)
            {
                await ShowError(ex.Message);
            }
        }

        private async void BtnRecognize_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_path)) return;
            Busy.IsActive = true; Busy.Visibility = Visibility.Visible;
            ResultScroll.Visibility = Visibility.Collapsed; EmptyHint.Visibility = Visibility.Collapsed;
            BtnRecognize.IsEnabled = false;
            try
            {
                string runtime = Runtime.EnsureRuntime();
                int rotate = CboRotate.SelectedIndex * 90;
                bool enhance = ChkEnhance.IsChecked == true;
                _loaded = await Task.Run(() =>
                {
                    string ext = System.IO.Path.GetExtension(_path).ToLowerInvariant();
                    string warns;
                    if (ext == ".xlsx" || ext == ".xls" || ext == ".csv" || ext == ".et" || ext == ".wps")
                    {
                        return EngineBridge.ReadTable(_path, runtime);
                    }
                    return EngineBridge.Recognize(_path, runtime, rotate, enhance, "1", out warns);
                });

                RenderResult();
            }
            catch (Exception ex)
            {
                TxtResult.Text = "识别失败：" + ex.Message;
                ResultScroll.Visibility = Visibility.Visible;
                EmptyHint.Visibility = Visibility.Collapsed;
            }
            finally
            {
                Busy.IsActive = false; Busy.Visibility = Visibility.Collapsed;
                BtnRecognize.IsEnabled = true;
            }
        }

        private void RenderResult()
        {
            if (_loaded == null || _loaded.Sheets.Count == 0) return;
            var sb = new StringBuilder();
            int totalRows = 0;
            foreach (var sheet in _loaded.Sheets)
            {
                if (_loaded.Sheets.Count > 1) sb.AppendLine("【" + sheet.Name + "】");
                foreach (var row in sheet.Rows)
                {
                    var cells = new List<string>();
                    foreach (var c in row) cells.Add((c ?? "").Replace("\t", " ").Replace("\r", "").Replace("\n", " "));
                    sb.AppendLine(string.Join("\t", cells.ToArray()));
                }
                sb.AppendLine();
                totalRows += sheet.Rows.Count;
            }
            TxtResult.Text = sb.ToString();
            ResultScroll.Visibility = Visibility.Visible;
            EmptyHint.Visibility = Visibility.Collapsed;
            TxtStats.Text = _loaded.Sheets.Count + " 个工作表 / 共 " + totalRows + " 行";
            BtnToPool.IsEnabled = true;
            BtnExportXlsx.IsEnabled = true;
            BtnExportCsv.IsEnabled = true;
        }

        private void BtnToPool_Click(object sender, RoutedEventArgs e)
        {
            if (_loaded == null) return;
            foreach (var sheet in _loaded.Sheets)
            {
                if (sheet.Rows.Count == 0) continue;
                var headers = new List<string>(sheet.Rows[0]);
                var rows = new List<List<string>>();
                for (int i = 1; i < sheet.Rows.Count; i++) rows.Add(new List<string>(sheet.Rows[i]));
                TablePool.Add(string.IsNullOrEmpty(sheet.Name) ? _loaded.FileName : sheet.Name, headers, rows);
            }
            BtnToPool.Content = "已加入 ✓（" + TablePool.All.Count + "）";
        }

        private async void BtnExportXlsx_Click(object sender, RoutedEventArgs e) { await DoExport(false); }
        private async void BtnExportCsv_Click(object sender, RoutedEventArgs e) { await DoExport(true); }

        private async Task DoExport(bool csv)
        {
            try
            {
                if (_loaded == null || _loaded.Sheets.Count == 0) return;
                var rows = _loaded.Sheets[0].Rows;
                byte[] data = await Task.Run(() => EngineBridge.Export(rows, csv, _loaded.FileName));
                string ext = csv ? ".csv" : ".xlsx";
                string name = System.IO.Path.GetFileNameWithoutExtension(_loaded.FileName) + "-识别结果" + ext;
                string saved = EngineBridge.SaveExport(data, name);
                var dlg = new Microsoft.UI.Xaml.Controls.ContentDialog
                {
                    Title = "导出成功",
                    Content = "已保存到下载文件夹：\n" + saved,
                    CloseButtonText = "确定"
                };
                dlg.XamlRoot = this.XamlRoot;
                await dlg.ShowAsync();
            }
            catch (Exception ex)
            {
                await ShowError(ex.Message);
            }
        }

        private async Task ShowError(string msg)
        {
            var dlg = new Microsoft.UI.Xaml.Controls.ContentDialog
            {
                Title = "提示",
                Content = msg,
                CloseButtonText = "确定"
            };
            dlg.XamlRoot = this.XamlRoot;
            await dlg.ShowAsync();
        }
    }
}

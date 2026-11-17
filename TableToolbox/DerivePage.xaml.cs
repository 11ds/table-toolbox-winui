using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace TableToolbox
{
    public sealed partial class DerivePage : Page
    {
        private class OutSpec
        {
            public string SourceId;
            public int Col;
            public string Name;
        }

        private CompareCore.DeriveResult _result;
        private List<OutSpec> _outputs = new List<OutSpec>();
        private List<string> _srcNames = new List<string>();

        public DerivePage()
        {
            this.InitializeComponent();
            Loaded += DerivePage_Loaded;
            CboUnmatched.SelectionChanged += CboUnmatched_SelectionChanged;
        }

        private void CboUnmatched_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CboUnmatched != null)
                TxtUnmatched.Visibility = CboUnmatched.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void DerivePage_Loaded(object sender, RoutedEventArgs e)
        {
            CboBase.Items.Clear();
            CboSource.Items.Clear();
            _srcNames.Clear();
            foreach (var t in TablePool.All)
            {
                CboBase.Items.Add(t.Name);
                CboSource.Items.Add(t.Name);
                _srcNames.Add(t.Id);
            }
            bool hasCmp = App.SharedCompare != null && App.SharedTables != null;
            TxtHint.Text = hasCmp
                ? "对比已完成（" + App.SharedCompare.Rows.Count + " 行），选择基准表与来源列"
                : "请先到「表格对比」页执行对比";
            RefreshBuildButton();
        }

        private void CboBase_SelectionChanged(object sender, SelectionChangedEventArgs e) { RefreshBuildButton(); }

        private void CboSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            CboCol.Items.Clear();
            int i = CboSource.SelectedIndex;
            if (i < 0 || i >= TablePool.All.Count) return;
            var t = TablePool.All[i];
            for (int c = 0; c < t.Headers.Count; c++)
                CboCol.Items.Add(string.IsNullOrEmpty(t.Headers[c]) ? ("第 " + (c + 1) + " 列") : t.Headers[c]);
            if (CboCol.Items.Count > 0) CboCol.SelectedIndex = 0;
        }

        private void BtnAddOut_Click(object sender, RoutedEventArgs e)
        {
            int si = CboSource.SelectedIndex;
            int ci = CboCol.SelectedIndex;
            if (si < 0 || ci < 0) return;
            string name = TxtNewName.Text.Trim();
            if (name.Length == 0) name = CboCol.SelectedItem?.ToString() ?? ("新列 " + (_outputs.Count + 1));

            _outputs.Add(new OutSpec { SourceId = TablePool.All[si].Id, Col = ci, Name = name });
            var chip = new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(40, 0, 120, 212)),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 0, 0)
            };
            chip.Child = new TextBlock { Text = name, FontSize = 12 };
            OutChips.Children.Add(chip);
            TxtNewName.Text = "";
            RefreshBuildButton();
        }

        private void RefreshBuildButton()
        {
            BtnBuild.IsEnabled = App.SharedCompare != null && CboBase.SelectedIndex >= 0 && _outputs.Count > 0;
        }

        private void BtnBuild_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (App.SharedCompare == null || App.SharedTables == null)
                {
                    TxtStats.Text = "请先执行对比";
                    return;
                }
                int baseIdx = CboBase.SelectedIndex;
                if (baseIdx < 0 || baseIdx >= TablePool.All.Count) return;
                string baseId = TablePool.All[baseIdx].Id;

                var baseT = App.SharedTables.Find(t => t.Id == baseId);
                if (baseT == null)
                {
                    TxtStats.Text = "基准表不在对比结果中，请重新对比";
                    return;
                }

                var outs = new List<CompareCore.DeriveOutput>();
                foreach (var o in _outputs)
                    outs.Add(new CompareCore.DeriveOutput { SourceId = o.SourceId, Col = o.Col, Name = o.Name });

                string mode = "blank";
                string custom = "";
                if (CboUnmatched.SelectedIndex == 1) mode = "mark";
                else if (CboUnmatched.SelectedIndex == 2) { mode = "text"; custom = TxtUnmatched.Text; }

                var keyMaps = App.SharedConfig != null ? App.SharedConfig.KeyMaps : new List<CompareCore.KeyMap>();
                _result = CompareCore.Derive(App.SharedTables, baseT, App.SharedCompare, keyMaps, outs, mode, custom);
                Render();
            }
            catch (Exception ex)
            {
                TxtStats.Text = "生成失败：" + ex.Message;
            }
        }

        private void Render()
        {
            if (_result == null) return;
            var sb = new StringBuilder();
            sb.AppendLine(string.Join("\t", _result.Headers.ToArray()));
            sb.AppendLine(string.Join("\t", _result.AddedHeaders.ToArray()));
            foreach (var row in _result.Rows)
                sb.AppendLine(string.Join("\t", row.ToArray()));

            TxtResult.Text = sb.ToString();
            ResultScroll.Visibility = Visibility.Visible;
            EmptyHint.Visibility = Visibility.Collapsed;
            TxtStats.Text = _result.BaseName + " · " + _result.Rows.Count + " 行 · 已匹配 " + _result.Matched +
                            " · 未匹配 " + _result.Unmatched + " · 对比基准 " + _result.CompareRows + " 行" +
                            (_result.Issues.Count > 0 ? " · " + string.Join("；", _result.Issues.ToArray()) : "");
            BtnExportXlsx.IsEnabled = true;
            BtnExportCsv.IsEnabled = true;
        }

        private async void BtnExportXlsx_Click(object sender, RoutedEventArgs e) { await DoExport(false); }
        private async void BtnExportCsv_Click(object sender, RoutedEventArgs e) { await DoExport(true); }

        private async Task DoExport(bool csv)
        {
            try
            {
                if (_result == null) return;
                var rows = new List<List<string>>();
                var head = new List<string>(_result.Headers);
                head.AddRange(_result.AddedHeaders);
                rows.Add(head);
                rows.AddRange(_result.Rows);

                byte[] data = await Task.Run(() => EngineBridge.Export(rows, csv, _result.BaseName));
                string ext = csv ? ".csv" : ".xlsx";
                string saved = EngineBridge.SaveExport(data, _result.BaseName + "-派生" + ext);
                var dlg = new ContentDialog
                {
                    Title = "导出成功",
                    Content = "已保存到下载文件夹：\n" + saved,
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                };
                await dlg.ShowAsync();
            }
            catch (Exception ex)
            {
                var dlg = new ContentDialog { Title = "提示", Content = ex.Message, CloseButtonText = "确定", XamlRoot = this.XamlRoot };
                await dlg.ShowAsync();
            }
        }
    }
}

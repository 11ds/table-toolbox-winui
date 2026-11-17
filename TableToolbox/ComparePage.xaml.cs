using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace TableToolbox
{
    public sealed partial class ComparePage : Page
    {
        private CompareCore.CompareResult _result;
        private List<CompareCore.Table> _tables;
        private string _sig;

        public ComparePage()
        {
            this.InitializeComponent();
            Loaded += ComparePage_Loaded;
        }

        private void ComparePage_Loaded(object sender, RoutedEventArgs e) { RefreshTables(); }

        public void RefreshTables()
        {
            LstTables.Items.Clear();
            foreach (var t in TablePool.All)
            {
                var item = new ListViewItem { Content = t.Name, Tag = t.Id };
                LstTables.Items.Add(item);
            }
            bool ok = TablePool.All.Count >= 2;
            TxtHint.Text = ok ? "已就绪，可开始对比" : "请先在「表格识别」页加入至少两张表";
            UpdateCompareButton();
        }

        private List<CompareCore.Table> BuildTables()
        {
            var list = new List<CompareCore.Table>();
            foreach (ListViewItem item in LstTables.SelectedItems)
            {
                string id = item.Tag as string;
                var src = TablePool.Find(id);
                if (src == null) continue;
                list.Add(new CompareCore.Table
                {
                    Id = src.Id,
                    Name = src.Name,
                    Headers = new List<string>(src.Headers),
                    DataRows = src.Rows.Select(r => new List<string>(r)).ToList(),
                    Rows = new List<List<string>>(src.Rows)
                });
            }
            return list;
        }

        private void LstTables_SelectionChanged(object sender, SelectionChangedEventArgs e) { UpdateCompareButton(); }
        private void Cfg_Changed(object sender, RoutedEventArgs e) { UpdateCompareButton(); }

        private void UpdateCompareButton()
        {
            BtnCompare.IsEnabled = LstTables.SelectedItems.Count >= 2;
        }

        private static List<int> ParseCols(string text, List<string> headers)
        {
            var set = new List<int>();
            if (string.IsNullOrWhiteSpace(text))
            {
                for (int i = 0; i < headers.Count; i++) set.Add(i);
                return set;
            }
            var wanted = text.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            foreach (var w in wanted)
            {
                int idx = -1;
                for (int i = 0; i < headers.Count; i++)
                    if (headers[i].Equals(w, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
                if (idx < 0 && int.TryParse(w, out int n) && n >= 1 && n <= headers.Count) idx = n - 1;
                if (idx >= 0 && !set.Contains(idx)) set.Add(idx);
            }
            return set;
        }

        private void BtnCompare_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _tables = BuildTables();
                if (_tables.Count < 2) return;

                var keys = ParseCols(TxtKeys.Text, _tables[0].Headers);
                if (keys.Count == 0) { TxtStats.Text = "关键列找不到对应的列名"; return; }

                var cfg = new CompareCore.CompareConfig();
                foreach (var kmKey in keys)
                {
                    var cols = new int[_tables.Count];
                    for (int ti = 0; ti < _tables.Count; ti++)
                    {
                        cols[ti] = -1;
                        for (int c = 0; c < _tables[ti].Headers.Count; c++)
                            if (_tables[ti].Headers[c].Equals(_tables[0].Headers[kmKey], StringComparison.OrdinalIgnoreCase)) { cols[ti] = c; break; }
                    }
                    cfg.KeyMaps.Add(new CompareCore.KeyMap { Name = _tables[0].Headers[kmKey], Cols = cols });
                }

                var valCols = ParseCols(TxtValues.Text, _tables[0].Headers);
                for (int ti = 0; ti < _tables.Count; ti++)
                {
                    var hs = new HashSet<int>();
                    foreach (var vc in valCols)
                    {
                        for (int c = 0; c < _tables[ti].Headers.Count; c++)
                            if (_tables[ti].Headers[c].Equals(_tables[0].Headers[vc], StringComparison.OrdinalIgnoreCase)) { hs.Add(c); break; }
                    }
                    cfg.ValCols.Add(hs);
                    cfg.ExtCols.Add(new List<int>());
                }

                _result = CompareCore.Compare(_tables, cfg);
                _sig = CompareCore.Signature(_tables, cfg.KeyMaps);
                Render();
            }
            catch (Exception ex)
            {
                TxtStats.Text = "对比失败：" + ex.Message;
            }
        }

        private void Render()
        {
            ResultRows.Children.Clear();
            EmptyHint.Visibility = Visibility.Collapsed;
            ResultScroll.Visibility = Visibility.Visible;

            int same = 0, diff = 0, missing = 0;
            foreach (var r in _result.Rows)
            {
                if (r.Status == "same") same++;
                else if (r.Status == "diff") diff++;
                else missing++;

                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                string key = string.Join(" / ", r.KeyParts);
                row.Children.Add(new TextBlock { Text = key, Width = 180, FontSize = 12 });

                string statusText = r.Status == "same" ? "相同" : (r.Status == "diff" ? "有差异" : "缺失");
                var tag = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 2, 8, 2),
                    Background = r.Status == "same"
                        ? new SolidColorBrush(Windows.UI.Color.FromArgb(30, 16, 185, 129))
                        : (r.Status == "diff"
                            ? new SolidColorBrush(Windows.UI.Color.FromArgb(30, 245, 158, 11))
                            : new SolidColorBrush(Windows.UI.Color.FromArgb(30, 239, 68, 68)))
                };
                tag.Child = new TextBlock
                {
                    Text = statusText,
                    FontSize = 11,
                    Foreground = r.Status == "same"
                        ? new SolidColorBrush(Colors.Green)
                        : (r.Status == "diff" ? new SolidColorBrush(Colors.OrangeRed) : new SolidColorBrush(Colors.Red))
                };
                row.Children.Add(tag);

                var detail = new List<string>();
                for (int ti = 0; ti < _tables.Count; ti++)
                {
                    var vals = new List<string>();
                    if (r.ValMatrix != null)
                        foreach (var col in r.ValMatrix)
                            if (ti < col.Length && col[ti] != null) vals.Add(col[ti]);
                    detail.Add(_tables[ti].Name + ": " + string.Join(" | ", vals.ToArray()));
                }
                row.Children.Add(new TextBlock { Text = string.Join("    ", detail.ToArray()), FontSize = 12,
                    Foreground = new SolidColorBrush(Colors.Gray) });

                ResultRows.Children.Add(row);
            }

            TxtStats.Text = "共 " + _result.Rows.Count + " 行 · 相同 " + same + " · 差异 " + diff + " · 缺失 " + missing +
                            (_result.Issues.Count > 0 ? " · 提示：" + string.Join("；", _result.Issues.ToArray()) : "");
            BtnToDerive.IsEnabled = _result.Rows.Count > 0;
        }

        private void BtnToDerive_Click(object sender, RoutedEventArgs e)
        {
            App.SharedCompare = _result;
            App.SharedTables = _tables;
            App.SharedConfig = GetConfig();
            App.SharedSig = _sig;
            Frame.Navigate(typeof(DerivePage));
        }

        public CompareCore.CompareResult GetResult() { return _result; }
        public List<CompareCore.Table> GetTables() { return _tables; }
        public string GetSig() { return _sig; }
        public CompareCore.CompareConfig GetConfig()
        {
            var cfg = new CompareCore.CompareConfig();
            // Rebuild config from current inputs for derive column mapping
            if (_tables == null || _tables.Count == 0) return cfg;
            var keys = ParseCols(TxtKeys.Text, _tables[0].Headers);
            foreach (var kmKey in keys)
            {
                var cols = new int[_tables.Count];
                for (int ti = 0; ti < _tables.Count; ti++)
                {
                    cols[ti] = -1;
                    for (int c = 0; c < _tables[ti].Headers.Count; c++)
                        if (_tables[ti].Headers[c].Equals(_tables[0].Headers[kmKey], StringComparison.OrdinalIgnoreCase)) { cols[ti] = c; break; }
                }
                cfg.KeyMaps.Add(new CompareCore.KeyMap { Name = _tables[0].Headers[kmKey], Cols = cols });
            }
            return cfg;
        }
    }
}

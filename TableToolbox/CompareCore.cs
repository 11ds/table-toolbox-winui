using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TableToolbox
{
    // Pure C# port of the toolbox compare (computeAndRender) and derive (scheme C, runDerive).
    // Scheme C: compare decides the row relationships; derive takes values from the matched source rows.
    public static class CompareCore
    {
        public class Table
        {
            public string Id;
            public string Name;
            public List<List<string>> Rows = new List<List<string>>();
            public bool Selected;
            public List<string> Headers = new List<string>();
            public List<List<string>> DataRows = new List<List<string>>();
            public int NCols { get { int m = 0; foreach (var r in DataRows) if (r.Count > m) m = r.Count; return Math.Max(m, Headers.Count); } }
        }

        public class KeyMap
        {
            public string Name;
            public int[] Cols; // one column index per table (-1 = not present)
        }

        public class ResultRow
        {
            public string[] KeyParts;
            public string Status; // same / diff / missing
            public string[][] ValMatrix;
            public List<string>[] hitRows;
        }

        public class CompareResult
        {
            public List<ResultRow> Rows = new List<ResultRow>();
            public List<string> Issues = new List<string>();
        }

        public class CompareConfig
        {
            public List<KeyMap> KeyMaps = new List<KeyMap>();
            public List<HashSet<int>> ValCols = new List<HashSet<int>>();
            public List<List<int>> ExtCols = new List<List<int>>();
        }

        public static CompareResult Compare(List<Table> tables, CompareConfig cfg)
        {
            CompareResult outR = new CompareResult();
            if (tables.Count == 0) return outR;

            var rowMaps = new List<Dictionary<string, List<string>>>();
            var fuzzyMaps = new List<Dictionary<string, List<string>>>();
            for (int ti = 0; ti < tables.Count; ti++)
            {
                var exact = new Dictionary<string, List<string>>();
                var fuzzy = new Dictionary<string, List<string>>();
                int emptyKeys = 0, dupKeys = 0;
                foreach (var row in tables[ti].DataRows)
                {
                    var ep = new List<string>(); var fp = new List<string>();
                    foreach (var km in cfg.KeyMaps)
                    {
                        int ci = ti < km.Cols.Length ? km.Cols[ti] : -1;
                        string v = (ci >= 0 && ci < row.Count) ? (row[ci] ?? "").Trim() : "";
                        ep.Add(v);
                        fp.Add(Strip(v));
                    }
                    string ekey = string.Join("\u0001", ep.ToArray());
                    string fkey = string.Join("", fp.ToArray());
                    if (ekey.Replace("\u0001", "") == "") { emptyKeys++; continue; }
                    if (exact.ContainsKey(ekey)) dupKeys++;
                    else
                    {
                        exact[ekey] = row;
                        if (!fuzzy.ContainsKey(fkey)) fuzzy[fkey] = row;
                    }
                }
                rowMaps.Add(exact); fuzzyMaps.Add(fuzzy);
                if (emptyKeys > 0) outR.Issues.Add("「" + tables[ti].Name + "」有 " + emptyKeys + " 行键为空");
                if (dupKeys > 0) outR.Issues.Add("「" + tables[ti].Name + "」有 " + dupKeys + " 行键重复");
            }

            var order = new List<string>(); var seen = new HashSet<string>();
            foreach (var k in rowMaps[0].Keys) if (seen.Add(k)) order.Add(k);
            for (int ti = 1; ti < rowMaps.Count; ti++)
                foreach (var k in rowMaps[ti].Keys) if (seen.Add(k)) order.Add(k);

            foreach (var k in order)
            {
                var parts = k.Split('\u0001');
                string fk = Strip(string.Join("", parts));
                var hits = new List<List<string>>();
                for (int ti = 0; ti < tables.Count; ti++)
                {
                    List<string> row;
                    if (rowMaps[ti].TryGetValue(k, out row)) hits.Add(row);
                    else if (fuzzyMaps[ti].TryGetValue(fk, out row)) hits.Add(row);
                    else hits.Add(null);
                }
                bool missing = hits.Any(h => h == null);

                var colSet = new SortedSet<int>();
                for (int ti = 0; ti < tables.Count; ti++)
                {
                    if (ti < cfg.ValCols.Count) foreach (int c in cfg.ValCols[ti]) colSet.Add(c);
                    foreach (var km in cfg.KeyMaps) if (ti < km.Cols.Length && km.Cols[ti] >= 0) colSet.Add(km.Cols[ti]);
                }
                var matrix = new Dictionary<int, string[]>();
                foreach (int c in colSet)
                {
                    var arr = new string[tables.Count];
                    for (int ti = 0; ti < tables.Count; ti++)
                    {
                        var h = hits[ti];
                        arr[ti] = (h == null || c >= h.Count) ? null : (h[c] ?? "").Trim();
                    }
                    matrix[c] = arr;
                }
                bool same = true;
                foreach (var kv in matrix)
                {
                    var vals = kv.Value.Where(v => v != null && v != "").Distinct().ToList();
                    if (vals.Count >= 2) { same = false; break; }
                }
                outR.Rows.Add(new ResultRow
                {
                    KeyParts = parts,
                    Status = missing ? "missing" : (same ? "same" : "diff"),
                    ValMatrix = matrix.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToArray(),
                    hitRows = hits.ToArray()
                });
            }
            return outR;
        }

        public class DeriveOutput
        {
            public string SourceId;
            public int Col;
            public string Name;
        }

        public class DeriveResult
        {
            public List<string> Headers = new List<string>();
            public List<string> AddedHeaders = new List<string>();
            public List<List<string>> Rows = new List<List<string>>();
            public List<List<bool>> RowFlags = new List<List<bool>>();
            public int Matched, Unmatched;
            public List<string> Issues = new List<string>();
            public string BaseName;
            public int CompareRows;
        }

        public static DeriveResult Derive(List<Table> tables, Table baseTable, CompareResult cmp,
                                          List<KeyMap> keyMaps, List<DeriveOutput> outputs,
                                          string unmatchedMode, string unmatchedText)
        {
            var d = new DeriveResult { BaseName = baseTable.Name, CompareRows = cmp.Rows.Count };
            d.Headers.AddRange(baseTable.Headers);

            var exact = new Dictionary<string, ResultRow>();
            var fuzzy = new Dictionary<string, ResultRow>();
            foreach (var r in cmp.Rows)
            {
                string ek = string.Join("\u0001", r.KeyParts);
                if (!exact.ContainsKey(ek)) exact[ek] = r;
                string fk = Strip(string.Join("", r.KeyParts));
                if (!fuzzy.ContainsKey(fk)) fuzzy[fk] = r;
            }
            int baseTi = tables.FindIndex(t => t.Id == baseTable.Id);
            var keyColsA = new List<int>();
            foreach (var km in keyMaps)
                if (baseTi >= 0 && baseTi < km.Cols.Length && km.Cols[baseTi] >= 0) keyColsA.Add(km.Cols[baseTi]);

            Func<string> fill = () => unmatchedMode == "text" ? (unmatchedText ?? "") : (unmatchedMode == "mark" ? "未匹配" : "");

            foreach (var brow in baseTable.DataRows)
            {
                var parts = new List<string>();
                foreach (int ci in keyColsA) parts.Add(ci < brow.Count ? (brow[ci] ?? "").Trim() : "");
                ResultRow r = null;
                if (!exact.TryGetValue(string.Join("\u0001", parts.ToArray()), out r))
                    fuzzy.TryGetValue(Strip(string.Join("", parts.ToArray())), out r);

                var newRow = new List<string>();
                newRow.AddRange(brow);
                var flags = new List<bool>();
                foreach (var o in outputs)
                {
                    int ti = tables.FindIndex(t => t.Id == o.SourceId);
                    List<string> hit = (r != null && ti >= 0 && ti < r.hitRows.Length) ? r.hitRows[ti] : null;
                    string v = "";
                    bool ok = false;
                    if (hit != null && o.Col >= 0 && o.Col < hit.Count)
                    {
                        v = (hit[o.Col] ?? "").Trim();
                        ok = v.Length > 0;
                    }
                    if (!ok) v = fill();
                    newRow.Add(v);
                    flags.Add(ok);
                    if (ok) d.Matched++; else d.Unmatched++;
                }
                d.Rows.Add(newRow);
                d.RowFlags.Add(flags);
            }
            foreach (var o in outputs)
            {
                d.AddedHeaders.Add(string.IsNullOrEmpty(o.Name) ? "新列" : o.Name);
                var st = tables.Find(t => t.Id == o.SourceId);
                if (st != null && !(o.Col >= 0 && o.Col < st.NCols)) d.Issues.Add("「" + st.Name + "」的取值列已失效");
            }
            return d;
        }

        private static string Strip(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c)) continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        public static string Signature(List<Table> tables, List<KeyMap> keyMaps)
        {
            var sb = new StringBuilder();
            foreach (var t in tables)
            {
                sb.Append(t.Id).Append('|');
                foreach (var row in t.DataRows) { sb.Append(string.Join("\u0002", row.ToArray())); sb.Append('\u0003'); }
                sb.Append('\u0004');
            }
            foreach (var km in keyMaps) sb.Append(string.Join(",", km.Cols.Select(x => x.ToString()).ToArray())).Append(';');
            using (var md5 = System.Security.Cryptography.MD5.Create())
                return BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).Replace("-", "");
        }
    }
}

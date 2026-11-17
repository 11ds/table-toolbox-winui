using System;
using System.Collections.Generic;

namespace TableToolbox
{
    // Shared in-memory table store: recognize page loads tables here,
    // compare and derive pages consume them. No disk writes of user data.
    public static class TablePool
    {
        public class Tbl
        {
            public string Id;
            public string Name;
            public List<string> Headers = new List<string>();
            public List<List<string>> Rows = new List<List<string>>();
            public int NCols
            {
                get
                {
                    int m = 0;
                    foreach (var r in Rows) if (r.Count > m) m = r.Count;
                    return Math.Max(m, Headers.Count);
                }
            }
        }

        private static readonly List<Tbl> _all = new List<Tbl>();
        private static int _seq = 0;

        public static IReadOnlyList<Tbl> All { get { return _all; } }

        public static Tbl Add(string name, List<string> headers, List<List<string>> rows)
        {
            _seq++;
            Tbl t = new Tbl
            {
                Id = "T" + _seq.ToString(),
                Name = string.IsNullOrEmpty(name) ? ("表格 " + _seq) : name,
                Headers = headers ?? new List<string>(),
                Rows = rows ?? new List<List<string>>()
            };
            _all.Add(t);
            return t;
        }

        public static Tbl Find(string id)
        {
            foreach (var t in _all) if (t.Id == id) return t;
            return null;
        }

        public static void Remove(string id)
        {
            for (int i = 0; i < _all.Count; i++) if (_all[i].Id == id) { _all.RemoveAt(i); break; }
        }

        public static void Clear() { _all.Clear(); _seq = 0; }
    }
}

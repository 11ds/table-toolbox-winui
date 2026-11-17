// TableLens core (part 1/2) - zero third-party dependencies, .NET Framework 4.8 / C# 5.
// Compiled at runtime by engine.ps1. No network, no native binaries, no models.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Drawing;
using System.Drawing.Imaging;
using System.Xml;
using System.Web.Script.Serialization;

namespace TableLens
{
    // ---------------------------------------------------------------- JSON helpers
    public static class J
    {
        private static JavaScriptSerializer _s;
        static J() { _s = new JavaScriptSerializer(); _s.MaxJsonLength = int.MaxValue; _s.RecursionLimit = 40; }
        public static string S(object o) { return _s.Serialize(o); }
        public static Dictionary<string, object> D(string s) { return _s.Deserialize<Dictionary<string, object>>(s); }
        public static List<object> L(object o)
        {
            List<object> r = new List<object>();
            if (o == null) return r;
            object[] a = o as object[];
            if (a != null) { r.AddRange(a); return r; }
            ArrayList al = o as ArrayList;
            if (al != null) { foreach (object x in al) r.Add(x); return r; }
            IEnumerable en = o as IEnumerable;
            if (en != null && !(o is string)) { foreach (object x in en) r.Add(x); return r; }
            r.Add(o); return r;
        }
        public static string Str(object o)
        {
            if (o == null) return "";
            if (o is string) return (string)o;
            try { return Convert.ToString(o, CultureInfo.InvariantCulture); }
            catch { return ""; }
        }
        public static int Int(object o, int d)
        {
            if (o == null) return d;
            try { return Convert.ToInt32(o, CultureInfo.InvariantCulture); }
            catch { int v; if (int.TryParse(Str(o), out v)) return v; return d; }
        }
        public static double Dbl(object o, double d)
        {
            if (o == null) return d;
            try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); }
            catch { double v; if (double.TryParse(Str(o), NumberStyles.Any, CultureInfo.InvariantCulture, out v)) return v; return d; }
        }
        public static bool Bool(object o, bool d)
        {
            if (o == null) return d;
            if (o is bool) return (bool)o;
            string s = Str(o).ToLowerInvariant();
            if (s == "true" || s == "1" || s == "yes" || s == "on") return true;
            if (s == "false" || s == "0" || s == "no" || s == "off") return false;
            return d;
        }
        public static List<List<string>> Matrix(object o)
        {
            List<List<string>> m = new List<List<string>>();
            foreach (object row in L(o))
            {
                List<string> rr = new List<string>();
                foreach (object c in L(row)) rr.Add(Str(c));
                m.Add(rr);
            }
            return m;
        }
        public static List<object> JsonMatrix(List<List<string>> m)
        {
            List<object> o = new List<object>();
            foreach (List<string> row in m) o.Add(new List<object>(row.Cast<object>()));
            return o;
        }
        public static string Err(string msg)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["ok"] = false; d["error"] = msg; return S(d);
        }
        public static string At(List<List<string>> m, int r, int c)
        {
            if (m == null || r < 0 || r >= m.Count || c < 0) return "";
            List<string> row = m[r];
            if (c >= row.Count) return "";
            return row[c] ?? "";
        }
        public static void Set(List<List<string>> m, int r, int c, string v)
        {
            while (m.Count <= r) m.Add(new List<string>());
            while (m[r].Count <= c) m[r].Add("");
            m[r][c] = v ?? "";
        }
        public static int Cols(List<List<string>> m)
        {
            int n = 0; foreach (List<string> r in m) if (r.Count > n) n = r.Count; return n;
        }
        public static void Pad(List<List<string>> m, int cols)
        {
            foreach (List<string> r in m) while (r.Count < cols) r.Add("");
        }
        public static string ColName(int idx)
        {
            int n = idx + 1; string s = "";
            while (n > 0) { int rem = (n - 1) % 26; s = ((char)('A' + rem)) + s; n = (n - 1) / 26; }
            return s;
        }
        public static string Ref(int r, int c) { return ColName(c) + (r + 1).ToString(CultureInfo.InvariantCulture); }
        public static int ColIndex(string s)
        {
            s = (s ?? "").Trim().ToUpperInvariant();
            if (s.Length == 0) return -1;
            int n = 0;
            foreach (char ch in s) { if (ch < 'A' || ch > 'Z') return -1; n = n * 26 + (ch - 'A' + 1); }
            return n - 1;
        }
        public static string XmlEsc(string s)
        {
            if (s == null) return "";
            StringBuilder b = new StringBuilder();
            foreach (char ch in s)
            {
                if (ch == '&') b.Append("&amp;");
                else if (ch == '<') b.Append("&lt;");
                else if (ch == '>') b.Append("&gt;");
                else if (ch == '"') b.Append("&quot;");
                else if (ch == '\'') b.Append("&apos;");
                else if (ch < 0x20 && ch != '\t' && ch != '\n' && ch != '\r') b.Append(' ');
                else b.Append(ch);
            }
            return b.ToString();
        }
    }

    // ---------------------------------------------------------------- image pre-processing
    public static class Img
    {
        public static Bitmap LoadPrep(byte[] bytes, int rotate, int minWidth, bool enhance)
        {
            Bitmap work = null;
            using (MemoryStream ms = new MemoryStream(bytes))
            using (Image raw = Image.FromStream(ms, false, true))
            {
                work = new Bitmap(raw.Width, raw.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(work))
                {
                    g.Clear(Color.White);
                    g.DrawImage(raw, 0, 0, raw.Width, raw.Height);
                }
            }
            if (rotate == 90) work.RotateFlip(RotateFlipType.Rotate90FlipNone);
            else if (rotate == 180) work.RotateFlip(RotateFlipType.Rotate180FlipNone);
            else if (rotate == 270) work.RotateFlip(RotateFlipType.Rotate270FlipNone);
            double scale = 1.0;
            if (minWidth > 0 && work.Width < minWidth) scale = (double)minWidth / work.Width;
            if (work.Width * scale > 3200) scale = 3200.0 / work.Width;
            if (scale > 1.02 || scale < 0.98)
            {
                int nw = Math.Max(1, (int)(work.Width * scale));
                int nh = Math.Max(1, (int)(work.Height * scale));
                Bitmap nb = new Bitmap(nw, nh, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(nb))
                {
                    g.Clear(Color.White);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.DrawImage(work, 0, 0, nw, nh);
                }
                work.Dispose(); work = nb;
            }
            if (enhance) Enhance(work);
            return work;
        }
        // Grayscale + percentile contrast stretch: helps Windows OCR on phone photos of paper.
        public static void Enhance(Bitmap b)
        {
            BitmapData d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                byte[] px = new byte[d.Stride * d.Height];
                System.Runtime.InteropServices.Marshal.Copy(d.Scan0, px, 0, px.Length);
                int[] hist = new int[256];
                int n = 0;
                for (int y = 0; y < d.Height; y++)
                {
                    int off = y * d.Stride;
                    for (int x = 0; x < d.Width; x++)
                    {
                        int i = off + x * 4;
                        int g = (px[i] * 30 + px[i + 1] * 59 + px[i + 2] * 11) / 100;
                        hist[g]++; n++;
                    }
                }
                int lo = 0, hi = 255, acc = 0;
                for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc >= n * 0.02) { lo = i; break; } }
                acc = 0;
                for (int i = 255; i >= 0; i--) { acc += hist[i]; if (acc >= n * 0.02) { hi = i; break; } }
                if (hi - lo < 40) { lo = 0; hi = 255; }
                int[] map = new int[256];
                for (int i = 0; i < 256; i++) map[i] = Math.Max(0, Math.Min(255, (i - lo) * 255 / Math.Max(1, hi - lo)));
                for (int y = 0; y < d.Height; y++)
                {
                    int off = y * d.Stride;
                    for (int x = 0; x < d.Width; x++)
                    {
                        int i = off + x * 4;
                        int g = map[(px[i] * 30 + px[i + 1] * 59 + px[i + 2] * 11) / 100];
                        px[i] = (byte)g; px[i + 1] = (byte)g; px[i + 2] = (byte)g; px[i + 3] = 255;
                    }
                }
                System.Runtime.InteropServices.Marshal.Copy(px, 0, d.Scan0, px.Length);
            }
            finally { b.UnlockBits(d); }
        }
        public static byte[] ToBgra(Bitmap b)
        {
            BitmapData d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] raw = new byte[d.Stride * d.Height];
                System.Runtime.InteropServices.Marshal.Copy(d.Scan0, raw, 0, raw.Length);
                if (d.Stride == b.Width * 4) return raw;
                byte[] outp = new byte[b.Width * b.Height * 4];
                for (int y = 0; y < b.Height; y++) Array.Copy(raw, y * d.Stride, outp, y * b.Width * 4, b.Width * 4);
                return outp;
            }
            finally { b.UnlockBits(d); }
        }
        public static byte[] ToPng(Bitmap b)
        {
            using (MemoryStream ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); return ms.ToArray(); }
        }
    }

    // ---------------------------------------------------------------- table reconstruction
    public class WordBox
    {
        public string T = "";
        public double X, Y, W, H;
        public double CX { get { return X + W / 2.0; } }
        public double CY { get { return Y + H / 2.0; } }
    }
    public class CellRect { public int R, C, X, Y, W, H; }

    public static class Table
    {
        private static bool IsCJK(char c)
        {
            return (c >= 0x2E80 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0xF900 && c <= 0xFAFF);
        }
        private static bool IsTightPunct(char c)
        {
            return ".,:;!?)]}$%@_-/\\".IndexOf(c) >= 0;
        }
        private static char HalfWidth(char c)
        {
            if (c >= 0xFF01 && c <= 0xFF5E) return (char)(c - 0xFEE0);
            if (c == 0x3000) return ' ';
            return c;
        }
        // OCR cleanup: half/full width, CJK "一" misread as a hyphen, fullwidth punctuation.
        public static string NormalizeOcr(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder b = new StringBuilder();
            foreach (char ch in s) b.Append(HalfWidth(ch));
            string t = b.ToString();
            t = t.Replace("，", ",").Replace("：", ":").Replace("；", ";").Replace("（", "(").Replace("）", ")")
                 .Replace("｜", "|").Replace("－", "-").Replace("—", "-").Replace("–", "-").Replace("、", ",");
            t = Regex.Replace(t, "(?<=[0-9])\\s*[．·•。｡]\\s*(?=[0-9])", ".");
            t = Regex.Replace(t, "(?<=[0-9])\\s*一\\s*(?=[0-9])", "-");
            t = Regex.Replace(t, "(?<=[0-9])\\s*-\\s*(?=[0-9])", "-");
            t = Regex.Replace(t, "(?<=[\\u2E80-\\u9FFF])\\s+(?=[\\u2E80-\\u9FFF])", "");
            t = Regex.Replace(t, "\\s{2,}", " ");
            return t.Trim();
        }
        private static string JoinWords(List<WordBox> ws)
        {
            if (ws.Count == 0) return "";
            List<WordBox> byY = ws.OrderBy(w => w.CY).ToList();
            List<double> hs = byY.Where(w => w.H > 5).Select(w => w.H).OrderBy(v => v).ToList();
            double medH = hs.Count > 0 ? hs[hs.Count / 2] : 14.0;
            if (medH < 5) medH = 14;
            // Windows OCR gives same-line CJK glyphs slightly different Y (1-2 px).
            // Group into lines by vertical centre first, then order strictly by X.
            List<List<WordBox>> lines = new List<List<WordBox>>();
            foreach (WordBox w in byY)
            {
                bool placed = false;
                for (int j = lines.Count - 1; j >= Math.Max(0, lines.Count - 2); j--)
                {
                    double mean = lines[j].Average(t => t.CY);
                    if (Math.Abs(w.CY - mean) <= medH * 0.6) { lines[j].Add(w); placed = true; break; }
                }
                if (!placed) lines.Add(new List<WordBox> { w });
            }
            lines = lines.OrderBy(g => g.Min(t => t.Y)).ToList();
            StringBuilder sb = new StringBuilder();
            bool firstLine = true;
            foreach (List<WordBox> line in lines)
            {
                if (!firstLine) sb.Append(' ');
                firstLine = false;
                WordBox prev = null;
                foreach (WordBox w in line.OrderBy(o => o.X))
                {
                    string t = NormalizeOcr(w.T);
                    if (t.Length == 0) continue;
                    if (prev != null)
                    {
                        double gap = w.X - (prev.X + prev.W);
                        char last = prev.T[prev.T.Length - 1];
                        bool cjk = IsCJK(last) || IsCJK(t[0]);
                        bool tight = IsTightPunct(t[0]) || IsTightPunct(last);
                        bool cjkToDigit = IsCJK(last) && char.IsDigit(t[0]) && gap > medH * 0.40;
                        if (cjkToDigit || (!cjk && !tight && gap > Math.Max(3.0, medH * 0.30))) sb.Append(' ');
                    }
                    sb.Append(t);
                    prev = w;
                }
            }
            return NormalizeOcr(sb.ToString());
        }
        private static List<int> FindLines(bool[] ink, int w, int h, bool horizontal, double minInk)
        {
            int n = horizontal ? h : w;
            int m = horizontal ? w : h;
            bool[] mark = new bool[n];
            for (int a = 0; a < n; a++)
            {
                int cnt = 0, run = 0, best = 0;
                for (int b = 0; b < m; b++)
                {
                    bool v = horizontal ? ink[a * w + b] : ink[b * w + a];
                    if (v) { cnt++; run++; if (run > best) best = run; } else run = 0;
                }
                mark[a] = (cnt > minInk * m && best > 0.12 * m) || (cnt > 0.78 * m);
            }
            List<int> marks = new List<int>();
            for (int a = 0; a < n; a++) if (mark[a]) marks.Add(a);
            List<int> centers = new List<int>();
            int k = 0;
            while (k < marks.Count)
            {
                int st = marks[k], en = st; k++;
                while (k < marks.Count && marks[k] - en <= 3) { en = marks[k]; k++; }
                centers.Add((st + en) / 2);
            }
            List<int> merged = new List<int>();
            foreach (int c in centers)
            {
                if (merged.Count > 0 && c - merged[merged.Count - 1] < 10) merged[merged.Count - 1] = (merged[merged.Count - 1] + c) / 2;
                else merged.Add(c);
            }
            return merged;
        }
        private static List<double> KMeans(List<double> vals, int k)
        {
            if (vals == null || vals.Count == 0 || k < 1) return new List<double> { 0.0 };
            k = Math.Min(k, vals.Count);
            List<double> sorted = vals.OrderBy(v => v).ToList();
            List<double> centers = new List<double>();
            for (int i = 0; i < k; i++) centers.Add(sorted[(int)((i + 0.5) * sorted.Count / k)]);
            for (int iter = 0; iter < 16; iter++)
            {
                double[] sum = new double[k]; int[] cnt = new int[k];
                foreach (double v in vals)
                {
                    int best = 0; double bd = double.MaxValue;
                    for (int i = 0; i < k; i++) { double dd = Math.Abs(v - centers[i]); if (dd < bd) { bd = dd; best = i; } }
                    sum[best] += v; cnt[best]++;
                }
                for (int i = 0; i < k; i++) if (cnt[i] > 0) centers[i] = sum[i] / cnt[i];
            }
            return centers.OrderBy(c => c).ToList();
        }
        // Vote-based global column bands: x is a separator when almost no row has text there.
        // Fixes the classic bug where an empty cell shifts the rest of a row to the left.
        private static List<double[]> ColumnBands(List<WordBox> words, int w, double medH, int rowCount, int forcedCols)
        {
            if (forcedCols > 0)
            {
                List<double> centers = KMeans(words.Select(o => o.CX).ToList(), forcedCols);
                List<double[]> bb = new List<double[]>();
                for (int i = 0; i < centers.Count; i++)
                {
                    double lo = i == 0 ? 0 : (centers[i - 1] + centers[i]) / 2.0;
                    double hi = i == centers.Count - 1 ? w : (centers[i] + centers[i + 1]) / 2.0;
                    bb.Add(new double[] { lo, hi });
                }
                return bb;
            }
            int[] vote = new int[w];
            foreach (WordBox wb in words)
            {
                int x1 = Math.Max(0, (int)Math.Floor(wb.X));
                int x2 = Math.Min(w - 1, (int)Math.Ceiling(wb.X + wb.W));
                for (int x = x1; x <= x2; x++) vote[x]++;
            }
            int threshold = Math.Max(1, (int)Math.Ceiling(rowCount * 0.16));
            int minGap = Math.Max(6, (int)(medH * 0.45));
            List<double[]> bands = new List<double[]>();
            int start = 0;
            for (int x = 0; x < w; x++)
            {
                if (vote[x] > threshold) continue;
                int st = x;
                while (x < w && vote[x] <= threshold) x++;
                if (x - st < minGap) continue;
                if (st - start > 4) bands.Add(new double[] { start, st });
                start = x;
            }
            if (w - start > 4) bands.Add(new double[] { start, w });
            if (bands.Count < 2) bands = new List<double[]> { new double[] { 0, w / 2.0 }, new double[] { w / 2.0, w } };
            while (bands.Count > 40) bands.RemoveAt(bands.Count - 1);
            return bands;
        }
        private static List<List<WordBox>> RowGroups(List<WordBox> words, double medH, int forcedRows)
        {
            List<WordBox> sorted = words.OrderBy(w => w.CY).ToList();
            if (forcedRows > 0)
            {
                List<double> centers = KMeans(sorted.Select(w => w.CY).ToList(), forcedRows);
                List<double> bounds = new List<double>();
                for (int i = 0; i < centers.Count - 1; i++) bounds.Add((centers[i] + centers[i + 1]) / 2.0);
                List<List<WordBox>> gg = new List<List<WordBox>>();
                for (int i = 0; i <= bounds.Count; i++) gg.Add(new List<WordBox>());
                foreach (WordBox w in sorted)
                {
                    int bi = 0; while (bi < bounds.Count && w.CY > bounds[bi]) bi++;
                    gg[bi].Add(w);
                }
                return gg.Where(g => g.Count > 0).ToList();
            }
            List<List<WordBox>> groups = new List<List<WordBox>>();
            foreach (WordBox w in sorted)
            {
                double tol = Math.Max(9.0, medH * 0.68);
                bool placed = false;
                for (int j = groups.Count - 1; j >= Math.Max(0, groups.Count - 3); j--)
                {
                    double mean = groups[j].Average(t => t.CY);
                    if (Math.Abs(w.CY - mean) <= tol) { groups[j].Add(w); placed = true; break; }
                }
                if (!placed) groups.Add(new List<WordBox> { w });
            }
            return groups.OrderBy(g => g.Min(t => t.Y)).ToList();
        }
        private static int BandOf(double x, List<double[]> bands)
        {
            for (int i = 0; i < bands.Count; i++) if (x >= bands[i][0] && x < bands[i][1]) return i;
            double best = double.MaxValue; int bi = 0;
            for (int i = 0; i < bands.Count; i++)
            {
                double d = x < bands[i][0] ? bands[i][0] - x : (x > bands[i][1] ? x - bands[i][1] : 0);
                if (d < best) { best = d; bi = i; }
            }
            return bi;
        }
        public static string Analyze(byte[] px, int w, int h, string wordsJson, string optJson)
        {
            Dictionary<string, object> opt = J.D(string.IsNullOrEmpty(optJson) ? "{}" : optJson);
            string forceMode = J.Str(opt.ContainsKey("mode") ? opt["mode"] : "auto");
            int forcedRows = J.Int(opt.ContainsKey("rows") ? opt["rows"] : null, 0);
            int forcedCols = J.Int(opt.ContainsKey("cols") ? opt["cols"] : null, 0);
            double minInk = J.Dbl(opt.ContainsKey("ink") ? opt["ink"] : null, 0.30);
            List<WordBox> words = new List<WordBox>();
            Dictionary<string, object> wj = J.D(wordsJson);
            foreach (object o in J.L(wj.ContainsKey("words") ? wj["words"] : null))
            {
                Dictionary<string, object> d = o as Dictionary<string, object>;
                if (d == null) continue;
                WordBox wb = new WordBox();
                wb.T = J.Str(d.ContainsKey("t") ? d["t"] : "");
                wb.X = J.Dbl(d.ContainsKey("x") ? d["x"] : null, 0);
                wb.Y = J.Dbl(d.ContainsKey("y") ? d["y"] : null, 0);
                wb.W = J.Dbl(d.ContainsKey("w") ? d["w"] : null, 0);
                wb.H = J.Dbl(d.ContainsKey("h") ? d["h"] : null, 0);
                if (wb.W > 0 && wb.H > 0 && wb.T.Trim().Length > 0) words.Add(wb);
            }
            if (words.Count == 0) return J.Err("图片中没有识别到文字，请换更清晰的图片、放大后重试或调整旋转角度。");
            List<double> hs = words.Where(o => o.H > 5).Select(o => o.H).OrderBy(v => v).ToList();
            double medH = hs.Count > 0 ? hs[hs.Count / 2] : 14.0;
            if (medH < 5) medH = 14;

            bool[] ink = new bool[w * h];
            long sum = 0;
            for (int i = 0; i < px.Length; i += 4) sum += (px[i] * 30 + px[i + 1] * 59 + px[i + 2] * 11) / 100;
            int bg = (int)(sum / Math.Max(1, w * h));
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    int g = (px[i] * 30 + px[i + 1] * 59 + px[i + 2] * 11) / 100;
                    ink[y * w + x] = Math.Abs(g - bg) > 48;
                }
            List<int> hl = new List<int>(), vl = new List<int>();
            if (forceMode != "bands") { hl = FindLines(ink, w, h, true, minInk); vl = FindLines(ink, w, h, false, minInk); }
            bool grid = hl.Count >= 2 && vl.Count >= 2 && forceMode != "bands";
            bool hybrid = !grid && hl.Count >= 2 && forceMode != "bands";
            string mode;
            List<double[]> rowBands = new List<double[]>(), colBands = new List<double[]>();
            List<List<WordBox>> wrows = RowGroups(words, medH, forcedRows);
            if (grid)
            {
                mode = "grid";
                for (int i = 0; i < hl.Count - 1; i++) rowBands.Add(new double[] { hl[i], hl[i + 1] });
                for (int i = 0; i < vl.Count - 1; i++) colBands.Add(new double[] { vl[i], vl[i + 1] });
            }
            else if (hybrid)
            {
                mode = "hybrid";
                // A three-line table has only top/header/bottom rules. If line-derived
                // rows are fewer than the OCR text rows, use word clustering for rows,
                // otherwise several data rows would collapse into one giant cell.
                if ((hl.Count - 1) >= wrows.Count && forcedRows == 0)
                {
                    for (int i = 0; i < hl.Count - 1; i++) rowBands.Add(new double[] { hl[i], hl[i + 1] });
                }
                else foreach (List<WordBox> g in wrows) rowBands.Add(new double[] { g.Min(o => o.Y), g.Max(o => o.Y + o.H) });
                colBands = ColumnBands(words, w, medH, Math.Max(1, wrows.Count), forcedCols);
            }
            else
            {
                mode = "bands";
                if (forcedRows > 0)
                {
                    List<double> cs = KMeans(words.Select(o => o.CY).ToList(), forcedRows);
                    for (int i = 0; i < cs.Count; i++)
                    {
                        double lo = i == 0 ? 0 : (cs[i - 1] + cs[i]) / 2.0;
                        double hi = i == cs.Count - 1 ? h : (cs[i] + cs[i + 1]) / 2.0;
                        rowBands.Add(new double[] { lo, hi });
                    }
                }
                else foreach (List<WordBox> g in wrows) rowBands.Add(new double[] { g.Min(o => o.Y), g.Max(o => o.Y + o.H) });
                colBands = ColumnBands(words, w, medH, Math.Max(1, wrows.Count), forcedCols);
            }
            if (rowBands.Count == 0 || colBands.Count == 0) return J.Err("无法还原表格结构，请尝试「有边框 / 无边框」模式或指定行列数。");
            if (rowBands.Count > 400) rowBands = rowBands.Take(400).ToList();
            if (colBands.Count > 60) colBands = colBands.Take(60).ToList();
            int R = rowBands.Count, C = colBands.Count;
            List<List<string>> rows = new List<List<string>>();
            for (int r = 0; r < R; r++) { List<string> rr = new List<string>(); for (int c = 0; c < C; c++) rr.Add(""); rows.Add(rr); }
            List<CellRect> rects = new List<CellRect>();
            Dictionary<string, List<WordBox>> buckets = new Dictionary<string, List<WordBox>>();
            int mapped = 0, unmapped = 0;
            foreach (WordBox wb in words)
            {
                int r = BandOf(wb.CY, rowBands), c = BandOf(wb.CX, colBands);
                string key = r + ":" + c;
                if (!buckets.ContainsKey(key)) buckets[key] = new List<WordBox>();
                buckets[key].Add(wb);
                if (grid && (wb.CY < rowBands[0][0] - 2 || wb.CY > rowBands[R - 1][1] + 2)) unmapped++; else mapped++;
            }
            foreach (KeyValuePair<string, List<WordBox>> kv in buckets)
            {
                string[] parts = kv.Key.Split(':');
                int r = int.Parse(parts[0]), c = int.Parse(parts[1]);
                if (r >= 0 && r < R && c >= 0 && c < C) rows[r][c] = JoinWords(kv.Value);
            }
            for (int r = 0; r < R; r++)
                for (int c = 0; c < C; c++)
                    rects.Add(new CellRect
                    {
                        R = r, C = c,
                        X = (int)Math.Round(colBands[c][0]), Y = (int)Math.Round(rowBands[r][0]),
                        W = (int)Math.Round(colBands[c][1] - colBands[c][0]),
                        H = (int)Math.Round(rowBands[r][1] - rowBands[r][0])
                    });
            // trim only all-empty leading/trailing rows and trailing columns; never interior
            while (rows.Count > 1 && rows[0].All(s => s.Length == 0))
            {
                rows.RemoveAt(0); rects.RemoveAll(q => q.R == 0);
                foreach (CellRect q in rects) q.R -= 1; R--;
            }
            while (rows.Count > 1 && rows[rows.Count - 1].All(s => s.Length == 0))
            {
                rows.RemoveAt(rows.Count - 1); rects.RemoveAll(q => q.R == R - 1); R--;
            }
            int cc = J.Cols(rows);
            while (cc > 1 && rows.All(rr => rr.Count < cc || rr[cc - 1].Length == 0))
            {
                foreach (List<string> rr in rows) while (rr.Count > cc - 1) rr.RemoveAt(rr.Count - 1);
                rects.RemoveAll(q => q.C == cc - 1); cc--;
            }
            J.Pad(rows, cc);
            StringBuilder plain = new StringBuilder();
            foreach (WordBox wb in words.OrderBy(o => o.Y).ThenBy(o => o.X)) plain.Append(NormalizeOcr(wb.T)).Append(" ");
            Dictionary<string, object> res = new Dictionary<string, object>();
            res["ok"] = true;
            res["rows"] = J.JsonMatrix(rows);
            res["mode"] = mode;
            res["rowCount"] = rows.Count; res["colCount"] = cc;
            res["width"] = w; res["height"] = h;
            res["hLines"] = hl; res["vLines"] = vl;
            res["mapped"] = mapped; res["unmapped"] = unmapped; res["words"] = words.Count;
            res["text"] = plain.ToString().Trim();
            List<object> cellJson = new List<object>();
            foreach (CellRect q in rects)
            {
                Dictionary<string, object> dd = new Dictionary<string, object>();
                dd["r"] = q.R; dd["c"] = q.C; dd["x"] = q.X; dd["y"] = q.Y; dd["w"] = q.W; dd["h"] = q.H;
                // Ink count tells the UI which empty cells actually have content in the image.
                // Windows OCR silently drops isolated glyphs (a lone "1" in a serial column is
                // the classic case), so an empty cell with ink is a suspected miss, not a blank.
                // An absolute pixel count beats a ratio here: JPEG noise scatters a few pixels,
                // whereas one glyph stroke leaves hundreds.
                long inkPx = InkPixels(ink, w, h, q.X, q.Y, q.W, q.H);
                dd["inkPx"] = inkPx;
                bool blank = q.R < rows.Count && q.C < rows[q.R].Count && rows[q.R][q.C].Length == 0;
                if (blank && inkPx >= 120) { dd["miss"] = true; }
                cellJson.Add(dd);
            }
            res["cells"] = cellJson;
            return J.S(res);
        }

        // Dark pixels inside a cell, inset far enough to skip the ruling lines.
        private static long InkPixels(bool[] ink, int w, int h, int x, int y, int cw, int ch)
        {
            const int pad = 6;
            int x0 = Math.Max(0, x + pad), y0 = Math.Max(0, y + pad);
            int x1 = Math.Min(w - 1, x + cw - pad), y1 = Math.Min(h - 1, y + ch - pad);
            if (x1 <= x0 || y1 <= y0) return 0;
            long hit = 0;
            for (int yy = y0; yy <= y1; yy++)
            {
                int row = yy * w;
                for (int xx = x0; xx <= x1; xx++) if (ink[row + xx]) hit++;
            }
            return hit;
        }
    }

    // ---------------------------------------------------------------- range expressions
    public static class RangeX
    {
        private static List<string> Tokens(string expr)
        {
            string s = (expr ?? "").Trim();
            if (s.Length == 0 || s == "*" || s.ToLowerInvariant() == "all") return new List<string>();
            return s.Replace('，', ',').Replace('；', ',').Replace(';', ',').Replace("：", ":")
                    .Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        }
        public static int[] Parse(string expr, int max, bool isCol)
        {
            List<string> toks = Tokens(expr);
            List<int> outp = new List<int>();
            if (toks.Count == 0) { for (int i = 0; i < max; i++) outp.Add(i); return outp.ToArray(); }
            foreach (string tok in toks)
            {
                string t = tok.Replace('–', '-').Replace('—', '-').Replace('~', '-').Trim().ToUpperInvariant();
                if (t.Contains(":")) t = t.Replace(':', '-');
                string[] ab = t.Split(new char[] { '-' }, 2);
                int a = ParseOne(ab[0], isCol), b;
                if (ab.Length == 1) b = a;
                else if (ab[1].Trim().Length == 0) b = max;
                else b = ParseOne(ab[1].Trim(), isCol);
                if (a < 0 || b < 0 || a > b) throw new Exception("范围写法有误：" + tok);
                for (int i = a; i <= b && i < max; i++) outp.Add(i);
            }
            outp = outp.Distinct().OrderBy(i => i).ToList();
            if (outp.Count == 0) throw new Exception("范围没有命中任何" + (isCol ? "列" : "行") + "：" + expr);
            return outp.ToArray();
        }
        private static int ParseOne(string t, bool isCol)
        {
            t = t.Trim(); int n;
            if (int.TryParse(t, out n)) return n - 1;
            if (isCol) return J.ColIndex(t);
            return -1;
        }
        public static string Describe(int[] idx, bool isCol)
        {
            if (idx == null || idx.Length == 0) return "全部";
            List<string> parts = new List<string>();
            int st = idx[0], prev = idx[0];
            for (int i = 1; i <= idx.Length; i++)
            {
                if (i == idx.Length || idx[i] != prev + 1)
                {
                    string a = isCol ? J.ColName(st) : (st + 1).ToString();
                    string b = isCol ? J.ColName(prev) : (prev + 1).ToString();
                    parts.Add(a == b ? a : a + "-" + b);
                    if (i < idx.Length) { st = idx[i]; prev = idx[i]; }
                }
                else prev = idx[i];
            }
            return string.Join(",", parts.ToArray());
        }
    }

    // ---------------------------------------------------------------- diff engine
    public static class DiffX
    {
        private static string Norm(string s, Dictionary<string, object> o)
        {
            if (s == null) s = "";
            string t = s.Trim();
            if (J.Bool(o.ContainsKey("normalizeWidth") ? o["normalizeWidth"] : null, true)) t = Table.NormalizeOcr(t);
            if (J.Bool(o.ContainsKey("ignoreCase") ? o["ignoreCase"] : null, false)) t = t.ToUpperInvariant();
            if (J.Bool(o.ContainsKey("ignoreSpace") ? o["ignoreSpace"] : null, true)) t = Regex.Replace(t, "\\s+", "");
            if (J.Bool(o.ContainsKey("numericEqual") ? o["numericEqual"] : null, true))
            {
                double d;
                if (double.TryParse(t.Replace(",", "").Replace("¥", "").Replace("￥", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out d))
                    t = "N" + d.ToString("0.########", CultureInfo.InvariantCulture);
            }
            return t;
        }
        private static List<object> CharOps(string a, string b)
        {
            List<object> ops = new List<object>();
            if (a.Length > 400 || b.Length > 400)
            {
                Dictionary<string, object> d1 = new Dictionary<string, object>(); d1["op"] = "del"; d1["t"] = a; ops.Add(d1);
                Dictionary<string, object> d2 = new Dictionary<string, object>(); d2["op"] = "ins"; d2["t"] = b; ops.Add(d2);
                return ops;
            }
            int n = a.Length, m = b.Length;
            int[,] dp = new int[n + 1, m + 1];
            for (int i = 1; i <= n; i++) for (int j = 1; j <= m; j++)
                dp[i, j] = a[i - 1] == b[j - 1] ? dp[i - 1, j - 1] + 1 : Math.Max(dp[i - 1, j], dp[i, j - 1]);
            List<object[]> rev = new List<object[]>();
            int x = n, y = m;
            while (x > 0 || y > 0)
            {
                if (x > 0 && y > 0 && a[x - 1] == b[y - 1]) { rev.Add(new object[] { "eq", a[x - 1].ToString() }); x--; y--; }
                else if (y > 0 && (x == 0 || dp[x, y - 1] >= dp[x - 1, y])) { rev.Add(new object[] { "ins", b[y - 1].ToString() }); y--; }
                else { rev.Add(new object[] { "del", a[x - 1].ToString() }); x--; }
            }
            rev.Reverse();
            foreach (object[] p in rev)
            {
                string op = (string)p[0], txt = (string)p[1];
                if (ops.Count > 0)
                {
                    Dictionary<string, object> last = (Dictionary<string, object>)ops[ops.Count - 1];
                    if ((string)last["op"] == op) { last["t"] = (string)last["t"] + txt; continue; }
                }
                Dictionary<string, object> d = new Dictionary<string, object>(); d["op"] = op; d["t"] = txt; ops.Add(d);
            }
            return ops;
        }
        private static string Detail(List<object> ops)
        {
            StringBuilder b = new StringBuilder();
            foreach (object o in ops)
            {
                Dictionary<string, object> d = (Dictionary<string, object>)o;
                string op = J.Str(d["op"]), t = J.Str(d["t"]);
                if (op == "del") b.Append("[-").Append(t).Append("]");
                else if (op == "ins") b.Append("{+").Append(t).Append("}");
                else b.Append(t);
            }
            return b.ToString();
        }
        private static string KindName(string k)
        {
            if (k == "changed") return "内容不同";
            if (k == "onlyA") return "仅识别表有";
            if (k == "onlyB") return "仅表格有";
            if (k == "rowOnlyA") return "整行仅识别表有";
            if (k == "rowOnlyB") return "整行仅表格有";
            return "一致";
        }
        private static Dictionary<string, object> EmptyCell(int r, int c, string kind)
        {
            Dictionary<string, object> cell = new Dictionary<string, object>();
            cell["a"] = ""; cell["b"] = ""; cell["kind"] = kind;
            cell["ops"] = new List<object>(); cell["aRef"] = ""; cell["bRef"] = "";
            cell["r"] = r; cell["c"] = c;
            return cell;
        }
        public static string Compare(string json)
        {
            Dictionary<string, object> q = J.D(json);
            List<List<string>> A = J.Matrix(q.ContainsKey("a") ? q["a"] : null);
            List<List<string>> B = J.Matrix(q.ContainsKey("b") ? q["b"] : null);
            Dictionary<string, object> o = q.ContainsKey("options") ? (q["options"] as Dictionary<string, object> ?? new Dictionary<string, object>()) : new Dictionary<string, object>();
            int ar = J.Int(q.ContainsKey("ar") ? q["ar"] : null, A.Count);
            int ac = J.Int(q.ContainsKey("ac") ? q["ac"] : null, J.Cols(A));
            int br = J.Int(q.ContainsKey("br") ? q["br"] : null, B.Count);
            int bc = J.Int(q.ContainsKey("bc") ? q["bc"] : null, J.Cols(B));
            if (ar < 1) ar = A.Count; if (br < 1) br = B.Count;
            if (ac < 1) ac = J.Cols(A); if (bc < 1) bc = J.Cols(B);
            int[] ra, ca, rb, cb;
            try
            {
                ra = RangeX.Parse(J.Str(q.ContainsKey("aRows") ? q["aRows"] : ""), ar, false);
                ca = RangeX.Parse(J.Str(q.ContainsKey("aCols") ? q["aCols"] : ""), ac, true);
                rb = RangeX.Parse(J.Str(q.ContainsKey("bRows") ? q["bRows"] : ""), br, false);
                cb = RangeX.Parse(J.Str(q.ContainsKey("bCols") ? q["bCols"] : ""), bc, true);
            }
            catch (Exception e) { return J.Err("范围设置错误：" + e.Message); }
            string align = J.Str(o.ContainsKey("align") ? o["align"] : "position");
            int keyA = J.Int(o.ContainsKey("keyA") ? o["keyA"] : null, -1);
            int keyB = J.Int(o.ContainsKey("keyB") ? o["keyB"] : null, -1);
            bool keyMode = align == "key" && keyA >= 0 && keyB >= 0 && keyA < ca.Length && keyB < cb.Length;
            if (keyMode && ra.Length != rb.Length) { /* key mode allows differing row counts */ }
            else if (ra.Length != rb.Length || ca.Length != cb.Length)
                return J.Err("两侧选中的范围大小不一致：识别表 " + ra.Length + " 行 × " + ca.Length + " 列，电子表格 " + rb.Length + " 行 × " + cb.Length + " 列。请调整范围或改用「按关键列对齐」。");
            bool skipBothEmpty = J.Bool(o.ContainsKey("skipBothEmpty") ? o["skipBothEmpty"] : null, true);
            bool skipOneSided = J.Bool(o.ContainsKey("skipOneSided") ? o["skipOneSided"] : null, false);

            List<List<string>> SA = new List<List<string>>(), SB = new List<List<string>>();
            List<int> rowLabelsA = new List<int>(), rowLabelsB = new List<int>();
            foreach (int r in ra) { List<string> rr = new List<string>(); foreach (int c in ca) rr.Add(J.At(A, r, c)); SA.Add(rr); rowLabelsA.Add(r); }
            foreach (int r in rb) { List<string> rr = new List<string>(); foreach (int c in cb) rr.Add(J.At(B, r, c)); SB.Add(rr); rowLabelsB.Add(r); }

            List<int> orderA = new List<int>(), orderB = new List<int>();
            List<string> rowOnlyA = new List<string>(), rowOnlyB = new List<string>();
            if (keyMode)
            {
                Dictionary<string, int> mapB = new Dictionary<string, int>();
                for (int i = 0; i < SB.Count; i++)
                {
                    string k = Norm(SB[i][keyB], o);
                    if (k.Length == 0) continue;
                    if (!mapB.ContainsKey(k)) mapB[k] = i;
                }
                HashSet<int> usedB = new HashSet<int>();
                for (int i = 0; i < SA.Count; i++)
                {
                    string k = Norm(SA[i][keyA], o);
                    if (k.Length > 0 && mapB.ContainsKey(k))
                    {
                        orderA.Add(i); orderB.Add(mapB[k]); usedB.Add(mapB[k]);
                    }
                    else { orderA.Add(i); orderB.Add(-1); rowOnlyA.Add(SA[i][keyA]); }
                }
                for (int i = 0; i < SB.Count; i++)
                    if (!usedB.Contains(i)) { orderA.Add(-1); orderB.Add(i); rowOnlyB.Add(SB[i][keyB]); }
            }
            else
            {
                for (int i = 0; i < Math.Max(SA.Count, SB.Count); i++)
                {
                    orderA.Add(i < SA.Count ? i : -1);
                    orderB.Add(i < SB.Count ? i : -1);
                }
            }
            int R = orderA.Count, C = ca.Length;
            List<object> cells = new List<object>(), diffs = new List<object>();
            int equal = 0, changed = 0, onlyA = 0, onlyB = 0, compared = 0;
            List<object> colLabelsA = ca.Select(c => (object)J.ColName(c)).ToList();
            List<object> colLabelsB = cb.Select(c => (object)J.ColName(c)).ToList();
            List<object> rowLabels = new List<object>();
            for (int r = 0; r < R; r++)
            {
                int ia = orderA[r], ib = orderB[r];
                int rA = ia >= 0 ? rowLabelsA[ia] : -1;
                int rB = ib >= 0 ? rowLabelsB[ib] : -1;
                string rowKind = ia < 0 ? "rowOnlyB" : (ib < 0 ? "rowOnlyA" : "");
                rowLabels.Add((object)(J.Ref(Math.Max(0, rA), ca[0]) + (rA != rB ? " ↔ " + J.Ref(Math.Max(0, rB), cb[0]) : "")));
                List<object> rowCells = new List<object>();
                for (int c = 0; c < C; c++)
                {
                    string va = ia >= 0 ? SA[ia][c] : "";
                    string vb = ib >= 0 ? SB[ib][c] : "";
                    if (skipBothEmpty && va.Trim().Length == 0 && vb.Trim().Length == 0)
                    {
                        Dictionary<string, object> skip = EmptyCell(r, c, "skipped");
                        skip["aRef"] = rA >= 0 ? J.Ref(rA, ca[c]) : "";
                        skip["bRef"] = rB >= 0 ? J.Ref(rB, cb[c]) : "";
                        rowCells.Add(skip); continue;
                    }
                    compared++;
                    string na = Norm(va, o), nb = Norm(vb, o);
                    string kind; List<object> ops = new List<object>();
                    if (rowKind == "rowOnlyA") kind = "rowOnlyA";
                    else if (rowKind == "rowOnlyB") kind = "rowOnlyB";
                    else if (na == nb) { kind = "eq"; equal++; }
                    else if (va.Trim().Length == 0) { kind = "onlyB"; onlyB++; }
                    else if (vb.Trim().Length == 0) { kind = "onlyA"; onlyA++; }
                    else { kind = "changed"; changed++; ops = CharOps(va, vb); }
                    if (skipOneSided && (kind == "onlyA" || kind == "onlyB")) { compared--; kind = "skipped"; ops = new List<object>(); }
                    Dictionary<string, object> cell = EmptyCell(r, c, kind);
                    cell["a"] = va; cell["b"] = vb; cell["ops"] = ops;
                    cell["aRef"] = rA >= 0 ? J.Ref(rA, ca[c]) : "";
                    cell["bRef"] = rB >= 0 ? J.Ref(rB, cb[c]) : "";
                    rowCells.Add(cell);
                    if (kind != "eq" && kind != "skipped")
                    {
                        Dictionary<string, object> dd = new Dictionary<string, object>();
                        dd["r"] = r; dd["c"] = c; dd["aRef"] = cell["aRef"]; dd["bRef"] = cell["bRef"];
                        dd["a"] = va; dd["b"] = vb; dd["kind"] = kind; dd["kindName"] = KindName(kind);
                        dd["ops"] = ops; dd["detail"] = Detail(ops);
                        diffs.Add(dd);
                    }
                }
                cells.Add(rowCells);
            }
            Dictionary<string, object> stats = new Dictionary<string, object>();
            stats["compared"] = compared; stats["equal"] = equal; stats["changed"] = changed;
            stats["onlyA"] = onlyA; stats["onlyB"] = onlyB;
            stats["rowOnlyA"] = rowOnlyA.Count; stats["rowOnlyB"] = rowOnlyB.Count;
            stats["diffCount"] = diffs.Count;
            stats["matchRate"] = compared == 0 ? 100.0 : Math.Round(equal * 100.0 / compared, 2);
            Dictionary<string, object> res = new Dictionary<string, object>();
            res["ok"] = true; res["cells"] = cells; res["diffs"] = diffs; res["stats"] = stats;
            res["colLabelsA"] = colLabelsA; res["colLabelsB"] = colLabelsB; res["rowLabels"] = rowLabels;
            res["rangeA"] = RangeX.Describe(ra, false) + " 行 × " + RangeX.Describe(ca, true) + " 列";
            res["rangeB"] = RangeX.Describe(rb, false) + " 行 × " + RangeX.Describe(cb, true) + " 列";
            res["align"] = keyMode ? "key" : "position";
            res["rowOnlyA"] = rowOnlyA; res["rowOnlyB"] = rowOnlyB;
            return J.S(res);
        }
    }
}

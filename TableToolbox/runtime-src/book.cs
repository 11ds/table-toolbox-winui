// TableLens core (part 2/2) - spreadsheet reading / writing / report export.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Web.Script.Serialization;

namespace TableLens
{
    public class SheetData { public string Name = ""; public List<List<string>> Rows = new List<List<string>>(); }

    public static class Book
    {
        private const string NS = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private const string RNS = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private const string PNS = "http://schemas.openxmlformats.org/package/2006/relationships";

        private static XmlDocument Doc(ZipArchive z, string path)
        {
            ZipArchiveEntry e = z.GetEntry(path);
            if (e == null) return null;
            using (Stream s = e.Open()) { XmlDocument d = new XmlDocument(); d.Load(s); return d; }
        }
        private static string Attr(XmlNode n, string name, string ns)
        {
            if (n == null || n.Attributes == null) return "";
            XmlAttribute a = string.IsNullOrEmpty(ns) ? n.Attributes[name] : n.Attributes[name, ns];
            return a == null ? "" : a.Value;
        }
        private static List<string> SharedStrings(ZipArchive z)
        {
            List<string> list = new List<string>();
            XmlDocument d = Doc(z, "xl/sharedStrings.xml");
            if (d == null) return list;
            XmlNamespaceManager ns = new XmlNamespaceManager(d.NameTable); ns.AddNamespace("m", NS);
            foreach (XmlNode si in d.SelectNodes("//m:si", ns))
            {
                StringBuilder b = new StringBuilder();
                foreach (XmlNode t in si.SelectNodes(".//m:t", ns)) b.Append(t.InnerText);
                list.Add(b.ToString());
            }
            return list;
        }
        private static Dictionary<int, bool> DateStyles(ZipArchive z)
        {
            Dictionary<int, bool> map = new Dictionary<int, bool>();
            XmlDocument d = Doc(z, "xl/styles.xml");
            if (d == null) return map;
            XmlNamespaceManager ns = new XmlNamespaceManager(d.NameTable); ns.AddNamespace("m", NS);
            Dictionary<int, string> fmts = new Dictionary<int, string>();
            foreach (XmlNode nf in d.SelectNodes("//m:numFmt", ns))
            {
                int id = J.Int(Attr(nf, "numFmtId", ""), -1);
                if (id >= 0) fmts[id] = Attr(nf, "formatCode", "");
            }
            XmlNode xfs = d.SelectSingleNode("//m:cellXfs", ns);
            if (xfs == null) return map;
            int idx = 0;
            foreach (XmlNode xf in xfs.ChildNodes)
            {
                if (xf.Name != "xf") continue;
                int id = J.Int(Attr(xf, "numFmtId", ""), 0);
                string code; fmts.TryGetValue(id, out code);
                bool isDate = (id >= 14 && id <= 22) || (id >= 27 && id <= 36) || (id >= 45 && id <= 47) || (id >= 50 && id <= 58);
                if (!string.IsNullOrEmpty(code) && Regex.IsMatch(code, "[ymdhs]", RegexOptions.IgnoreCase) && !Regex.IsMatch(code, "^[#0.,%\\s;\\-]+$")) isDate = true;
                map[idx] = isDate; idx++;
            }
            return map;
        }
        private static bool Numeric(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            double d; return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out d);
        }
        private static string CellValue(XmlNode c, XmlNamespaceManager ns, List<string> shared, Dictionary<int, bool> dateStyles)
        {
            string t = Attr(c, "t", "");
            if (t == "inlineStr")
            {
                StringBuilder b = new StringBuilder();
                foreach (XmlNode tn in c.SelectNodes(".//m:t", ns)) b.Append(tn.InnerText);
                return b.ToString();
            }
            XmlNode v = c.SelectSingleNode("m:v", ns);
            string raw = v == null ? "" : v.InnerText;
            if (t == "s") { int i; if (int.TryParse(raw, out i) && i >= 0 && i < shared.Count) return shared[i]; return ""; }
            if (t == "b") return raw == "1" ? "TRUE" : "FALSE";
            if (t == "e") return raw;
            if (Numeric(raw))
            {
                int s = J.Int(Attr(c, "s", ""), 0);
                bool isDate;
                if (dateStyles.TryGetValue(s, out isDate) && isDate)
                {
                    double dv;
                    if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out dv) && dv > 0 && dv < 3000000)
                    {
                        try
                        {
                            DateTime dt = DateTime.FromOADate(dv);
                            return dt.ToString(dt.Hour == 0 && dt.Minute == 0 && dt.Second == 0 ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                        }
                        catch { }
                    }
                }
            }
            return raw;
        }
        private static int ColFromRef(string r)
        {
            string s = "";
            foreach (char ch in r ?? "")
            {
                if (ch >= 'A' && ch <= 'Z') s += ch;
                else if (ch >= 'a' && ch <= 'z') s += (char)(ch - 32);
                else break;
            }
            return J.ColIndex(s);
        }
        private static int RowFromRef(string r)
        {
            string s = Regex.Match(r ?? "", "\\d+").Value;
            int n; return int.TryParse(s, out n) ? n - 1 : -1;
        }
        public static string Read(string json)
        {
            Dictionary<string, object> q = J.D(json);
            string name = J.Str(q.ContainsKey("name") ? q["name"] : "");
            byte[] data;
            if (q.ContainsKey("data") && J.Str(q["data"]).Length > 0)
            {
                try { data = Convert.FromBase64String(J.Str(q["data"])); }
                catch { return J.Err("文件内容不是有效的 base64。"); }
            }
            else
            {
                string path = J.Str(q.ContainsKey("path") ? q["path"] : "");
                if (!File.Exists(path)) return J.Err("找不到文件：" + path);
                data = File.ReadAllBytes(path); name = Path.GetFileName(path);
            }
            string ext = Path.GetExtension(name).ToLowerInvariant();
            bool zipMagic = data.Length > 4 && data[0] == 0x50 && data[1] == 0x4B && data[2] == 0x03 && data[3] == 0x04;
            bool oleMagic = data.Length > 8 && data[0] == 0xD0 && data[1] == 0xCF && data[2] == 0x11 && data[3] == 0xE0;
            List<SheetData> sheets = new List<SheetData>();
            try
            {
                if (ext == ".xlsx" || ext == ".xlsm" || ext == ".xltx" || ext == ".xltm")
                    sheets = ReadXlsx(data);
                else if ((ext == ".xls" || ext == ".et" || ext == ".wps" || ext == ".ett") && zipMagic)
                    sheets = ReadXlsx(data);          // OOXML saved with a legacy extension
                else if (ext == ".xls" || ext == ".xlt" || ext == ".et" || ext == ".ett")
                    sheets = Biff8.Read(data);
                else if (ext == ".wps" || ext == ".doc" || ext == ".wpt")
                {
                    // WPS Writer documents are Word 97-2003 (OLE2 + WordDocument) in most versions.
                    if (!oleMagic) return J.Err("无法识别的 WPS 文档（既不是 OOXML 也不是 OLE2 容器）。");
                    try { sheets = Biff8.Read(data); }
                    catch { return J.Err("NEEDS_OFFICE"); }
                }
                else if (ext == ".pdf")
                    return J.Err("NEEDS_PDF");
                else if (ext == ".csv" || ext == ".tsv" || ext == ".txt")
                    sheets.Add(ReadDelimited(data, ext, J.Str(q.ContainsKey("encoding") ? q["encoding"] : "")));
                else return J.Err("不支持的文件类型：" + ext);
            }
            catch (Exception e) { return J.Err("读取失败：" + e.Message); }
            List<object> js = new List<object>();
            foreach (SheetData s in sheets)
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["name"] = s.Name; d["rows"] = J.JsonMatrix(s.Rows);
                d["rowCount"] = s.Rows.Count; d["colCount"] = J.Cols(s.Rows);
                js.Add(d);
            }
            Dictionary<string, object> res = new Dictionary<string, object>();
            res["ok"] = true; res["sheets"] = js; res["name"] = name; return J.S(res);
        }
        private static List<SheetData> ReadXlsx(byte[] data)
        {
            List<SheetData> sheets = new List<SheetData>();
            using (MemoryStream ms = new MemoryStream(data))
            using (ZipArchive z = new ZipArchive(ms, ZipArchiveMode.Read, true))
            {
                List<string> shared = SharedStrings(z);
                Dictionary<int, bool> dateStyles = DateStyles(z);
                XmlDocument wb = Doc(z, "xl/workbook.xml");
                if (wb == null) throw new Exception("不是有效的 xlsx（缺少 workbook.xml）");
                XmlNamespaceManager wns = new XmlNamespaceManager(wb.NameTable);
                wns.AddNamespace("m", NS); wns.AddNamespace("r", RNS);
                Dictionary<string, string> rels = new Dictionary<string, string>();
                XmlDocument rl = Doc(z, "xl/_rels/workbook.xml.rels");
                if (rl != null)
                {
                    XmlNamespaceManager rns = new XmlNamespaceManager(rl.NameTable); rns.AddNamespace("p", PNS);
                    foreach (XmlNode n in rl.SelectNodes("//p:Relationship", rns))
                    {
                        string id = Attr(n, "Id", ""), target = Attr(n, "Target", "");
                        target = target.Replace('\\', '/');
                        if (target.StartsWith("/")) target = target.TrimStart('/');
                        else if (!target.StartsWith("xl/")) target = "xl/" + target;
                        while (target.Contains("/../")) target = Regex.Replace(target, "[^/]+/\\.\\./", "");
                        rels[id] = target;
                    }
                }
                int sheetNo = 0;
                foreach (XmlNode sh in wb.SelectNodes("//m:sheets/m:sheet", wns))
                {
                    sheetNo++;
                    string name = Attr(sh, "name", "");
                    if (name.Length == 0) name = "Sheet" + sheetNo;
                    string rid = Attr(sh, "id", RNS);
                    string target;
                    if (!rels.TryGetValue(rid, out target)) target = "xl/worksheets/sheet" + sheetNo + ".xml";
                    XmlDocument sd = Doc(z, target);
                    if (sd == null)
                    {
                        foreach (ZipArchiveEntry e in z.Entries)
                            if (e.FullName.StartsWith("xl/worksheets/sheet") && e.FullName.EndsWith(".xml"))
                            { sd = Doc(z, e.FullName); if (sd != null) break; }
                    }
                    SheetData sheet = new SheetData(); sheet.Name = name;
                    if (sd != null)
                    {
                        XmlNamespaceManager ns = new XmlNamespaceManager(sd.NameTable); ns.AddNamespace("m", NS);
                        int seqRow = 0;
                        foreach (XmlNode row in sd.SelectNodes("//m:sheetData/m:row", ns))
                        {
                            int rr = RowFromRef(Attr(row, "r", ""));
                            if (rr < 0) rr = seqRow;
                            seqRow = rr + 1;
                            if (rr < 0 || rr > 3000) continue;
                            int nextCol = 0;
                            foreach (XmlNode c in row.SelectNodes("m:c", ns))
                            {
                                string rref = Attr(c, "r", "");
                                int cc = ColFromRef(rref);
                                int cr = RowFromRef(rref);
                                if (cr >= 0) rr = cr;
                                if (cc < 0) cc = nextCol;
                                nextCol = cc + 1;
                                if (cc < 0 || cc > 200) continue;
                                J.Set(sheet.Rows, rr, cc, CellValue(c, ns, shared, dateStyles));
                            }
                        }
                    }
                    while (sheet.Rows.Count > 0 && sheet.Rows[sheet.Rows.Count - 1].All(s => s.Trim().Length == 0)) sheet.Rows.RemoveAt(sheet.Rows.Count - 1);
                    J.Pad(sheet.Rows, J.Cols(sheet.Rows));
                    sheets.Add(sheet);
                }
                if (sheets.Count == 0) throw new Exception("工作簿里没有工作表");
            }
            return sheets;
        }
        private static SheetData ReadDelimited(byte[] data, string ext, string encName)
        {
            Encoding enc;
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) enc = new UTF8Encoding(false);
            else if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) enc = Encoding.Unicode;
            else if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) enc = Encoding.BigEndianUnicode;
            else
            {
                string e = (encName ?? "").ToLowerInvariant();
                if (e == "gbk" || e == "gb18030" || e == "gb2312" || e == "ansi")
                {
                    try { enc = Encoding.GetEncoding(54936); } catch { enc = Encoding.GetEncoding(936); }
                }
                else if (e == "unicode" || e == "utf16") enc = Encoding.Unicode;
                else enc = new UTF8Encoding(false);
            }
            string text = enc.GetString(data);
            if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
            char delim = ext == ".tsv" ? '\t' : GuessDelimiter(text);
            List<List<string>> rows = new List<List<string>>();
            List<string> fields = new List<string>();
            StringBuilder cur = new StringBuilder();
            bool q = false;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (q)
                {
                    if (ch == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { cur.Append('"'); i++; } else q = false; }
                    else cur.Append(ch);
                }
                else if (ch == '"' && cur.Length == 0) q = true;
                else if (ch == delim) { fields.Add(cur.ToString()); cur.Length = 0; }
                else if (ch == '\r' || ch == '\n')
                {
                    if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    fields.Add(cur.ToString()); cur.Length = 0;
                    rows.Add(fields); fields = new List<string>();
                }
                else cur.Append(ch);
            }
            if (cur.Length > 0 || fields.Count > 0) { fields.Add(cur.ToString()); rows.Add(fields); }
            while (rows.Count > 0 && rows[rows.Count - 1].All(s => s.Trim().Length == 0)) rows.RemoveAt(rows.Count - 1);
            SheetData sd = new SheetData(); sd.Name = "Sheet1"; sd.Rows = rows;
            J.Pad(sd.Rows, J.Cols(sd.Rows));
            return sd;
        }
        private static char GuessDelimiter(string text)
        {
            string first = text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            char[] cand = new char[] { ',', '\t', ';', '|' };
            char best = ','; int bc = -1;
            foreach (char ch in cand)
            {
                int n = 0; bool q = false;
                foreach (char c in first) { if (c == '"') q = !q; else if (c == ch && !q) n++; }
                if (n > bc) { bc = n; best = ch; }
            }
            return best;
        }
    }

    // ---------------------------------------------------------------- xlsx writer
    public class SheetDef
    {
        public string Name = "Sheet1";
        public List<List<string>> Rows = new List<List<string>>();
        public int[,] Styles = null;
        public bool FreezeHeader = false;
    }
    public static class Xlsx
    {
        private const string NS = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static int StyleAt(SheetDef s, int r, int c)
        {
            if (s.Styles == null) return 0;
            if (r < s.Styles.GetLength(0) && c < s.Styles.GetLength(1)) return s.Styles[r, c];
            return 0;
        }
        private static int DispLen(string s)
        {
            int n = 0;
            foreach (char c in s) n += (c > 0x2E80) ? 2 : 1;
            return n;
        }
        private static string SheetXml(SheetDef s)
        {
            int rows = s.Rows.Count, cols = J.Cols(s.Rows);
            StringBuilder b = new StringBuilder();
            b.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            b.Append("<worksheet xmlns=\"").Append(NS).Append("\">");
            b.Append("<dimension ref=\"A1:").Append(J.ColName(Math.Max(0, cols - 1))).Append(Math.Max(1, rows)).Append("\"/>");
            b.Append("<sheetViews><sheetView workbookViewId=\"0\">");
            if (s.FreezeHeader) b.Append("<pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/>");
            b.Append("</sheetView></sheetViews><sheetFormatPr defaultRowHeight=\"18\"/>");
            b.Append("<cols>");
            for (int c = 0; c < cols; c++)
            {
                int w = 8;
                for (int r = 0; r < rows; r++) w = Math.Max(w, DispLen(J.At(s.Rows, r, c)) + 3);
                w = Math.Min(46, Math.Max(7, w));
                b.Append("<col min=\"").Append(c + 1).Append("\" max=\"").Append(c + 1).Append("\" width=\"").Append(w).Append("\" customWidth=\"1\"/>");
            }
            b.Append("</cols><sheetData>");
            for (int r = 0; r < rows; r++)
            {
                b.Append("<row r=\"").Append(r + 1).Append("\">");
                for (int c = 0; c < cols; c++)
                {
                    string v = J.At(s.Rows, r, c);
                    if (v.Length == 0 && StyleAt(s, r, c) == 0) continue;
                    int st = StyleAt(s, r, c);
                    string rf = J.Ref(r, c);
                    b.Append("<c r=\"").Append(rf).Append("\"");
                    if (st > 0) b.Append(" s=\"").Append(st).Append("\"");
                    if (v.Length == 0) { b.Append("/>"); continue; }
                    bool num = Regex.IsMatch(v, "^-?[0-9]{1,15}(\\.[0-9]+)?$") && !(v.Length > 1 && v[0] == '0' && (v[1] != '.'));
                    if (num) b.Append("><v>").Append(v).Append("</v></c>");
                    else b.Append(" t=\"inlineStr\"><is><t xml:space=\"preserve\">").Append(J.XmlEsc(v)).Append("</t></is></c>");
                }
                b.Append("</row>");
            }
            b.Append("</sheetData></worksheet>");
            return b.ToString();
        }
        private static string StylesXml()
        {
            const string F = "FF";
            string[] fills = new string[] {
                "<fill><patternFill patternType=\"none\"/></fill>",
                "<fill><patternFill patternType=\"gray125\"/></fill>",
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"" + F + "0078D4\"/><bgColor indexed=\"64\"/></patternFill></fill>",
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"" + F + "FCE8E6\"/><bgColor indexed=\"64\"/></patternFill></fill>",
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"" + F + "DFF6DD\"/><bgColor indexed=\"64\"/></patternFill></fill>",
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"" + F + "FFF4CE\"/><bgColor indexed=\"64\"/></patternFill></fill>",
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"" + F + "F3F3F3\"/><bgColor indexed=\"64\"/></patternFill></fill>",
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"" + F + "E1EFFA\"/><bgColor indexed=\"64\"/></patternFill></fill>" };
            StringBuilder b = new StringBuilder();
            b.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            b.Append("<styleSheet xmlns=\"").Append(NS).Append("\"><numFmts count=\"0\"/>");
            b.Append("<fonts count=\"4\">");
            b.Append("<font><sz val=\"11\"/><color rgb=\"" + F + "1B1B1B\"/><name val=\"Microsoft YaHei\"/></font>");
            b.Append("<font><b/><sz val=\"11\"/><color rgb=\"" + F + "FFFFFF\"/><name val=\"Microsoft YaHei\"/></font>");
            b.Append("<font><sz val=\"11\"/><color rgb=\"" + F + "A4262C\"/><name val=\"Microsoft YaHei\"/></font>");
            b.Append("<font><b/><sz val=\"14\"/><color rgb=\"" + F + "1B1B1B\"/><name val=\"Microsoft YaHei\"/></font>");
            b.Append("</fonts><fills count=\"").Append(fills.Length).Append("\">");
            foreach (string f in fills) b.Append(f);
            b.Append("</fills><borders count=\"2\">");
            b.Append("<border><left/><right/><top/><bottom/><diagonal/></border>");
            b.Append("<border><left style=\"thin\"><color rgb=\"" + F + "D8D8D8\"/></left><right style=\"thin\"><color rgb=\"" + F + "D8D8D8\"/></right><top style=\"thin\"><color rgb=\"" + F + "D8D8D8\"/></top><bottom style=\"thin\"><color rgb=\"" + F + "D8D8D8\"/></bottom><diagonal/></border>");
            b.Append("</borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>");
            // 0 default, 1 header, 2 changed, 3 onlyA, 4 onlyB, 5 equal, 6 rowOnlyA, 7 title, 8 rowOnlyB
            b.Append("<cellXfs count=\"9\">");
            b.Append("<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>");
            b.Append("<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>");
            b.Append("<xf numFmtId=\"0\" fontId=\"2\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"/>");
            b.Append("<xf numFmtId=\"0\" fontId=\"0\" fillId=\"4\" borderId=\"1\" xfId=\"0\" applyFill=\"1\" applyBorder=\"1\"/>");
            b.Append("<xf numFmtId=\"0\" fontId=\"0\" fillId=\"5\" borderId=\"1\" xfId=\"0\" applyFill=\"1\" applyBorder=\"1\"/>");
            b.Append("<xf numFmtId=\"0\" fontId=\"0\" fillId=\"6\" borderId=\"1\" xfId=\"0\" applyFill=\"1\" applyBorder=\"1\"/>");
            b.Append("<xf numFmtId=\"0\" fontId=\"0\" fillId=\"7\" borderId=\"1\" xfId=\"0\" applyFill=\"1\" applyBorder=\"1\"/>");
            b.Append("<xf numFmtId=\"0\" fontId=\"3\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>");
            b.Append("<xf numFmtId=\"0\" fontId=\"0\" fillId=\"5\" borderId=\"1\" xfId=\"0\" applyFill=\"1\" applyBorder=\"1\"/>");
            b.Append("</cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");
            return b.ToString();
        }
        public static byte[] Build(List<SheetDef> sheets)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (ZipArchive z = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    Action<string, string> put = delegate (string path, string content)
                    {
                        ZipArchiveEntry e = z.CreateEntry(path, CompressionLevel.Optimal);
                        using (StreamWriter sw = new StreamWriter(e.Open(), new UTF8Encoding(false))) sw.Write(content);
                    };
                    StringBuilder ct = new StringBuilder();
                    ct.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
                    ct.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
                    ct.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
                    ct.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
                    ct.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
                    ct.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
                    for (int i = 0; i < sheets.Count; i++)
                        ct.Append("<Override PartName=\"/xl/worksheets/sheet").Append(i + 1).Append(".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
                    ct.Append("</Types>");
                    put("[Content_Types].xml", ct.ToString());
                    put("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"" + "http://schemas.openxmlformats.org/package/2006/relationships" + "\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                    StringBuilder wbx = new StringBuilder();
                    wbx.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
                    wbx.Append("<workbook xmlns=\"").Append(NS).Append("\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
                    for (int i = 0; i < sheets.Count; i++)
                        wbx.Append("<sheet name=\"").Append(J.XmlEsc(sheets[i].Name)).Append("\" sheetId=\"").Append(i + 1).Append("\" r:id=\"rId").Append(i + 1).Append("\"/>");
                    wbx.Append("</sheets></workbook>");
                    put("xl/workbook.xml", wbx.ToString());
                    StringBuilder rx = new StringBuilder();
                    rx.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
                    for (int i = 0; i < sheets.Count; i++)
                        rx.Append("<Relationship Id=\"rId").Append(i + 1).Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet").Append(i + 1).Append(".xml\"/>");
                    rx.Append("<Relationship Id=\"rId").Append(sheets.Count + 1).Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
                    rx.Append("</Relationships>");
                    put("xl/_rels/workbook.xml.rels", rx.ToString());
                    put("xl/styles.xml", StylesXml());
                    for (int i = 0; i < sheets.Count; i++) put("xl/worksheets/sheet" + (i + 1) + ".xml", SheetXml(sheets[i]));
                }
                return ms.ToArray();
            }
        }
    }

    // ---------------------------------------------------------------- export
    public static class Export
    {
        private static string G(Dictionary<string, object> d, string k)
        {
            object v; return d != null && d.TryGetValue(k, out v) ? J.Str(v) : "";
        }
        public static byte[] Csv(List<List<string>> rows)
        {
            StringBuilder b = new StringBuilder();
            foreach (List<string> row in rows)
            {
                List<string> f = new List<string>();
                foreach (string v in row)
                {
                    string s = v ?? "";
                    if (s.Contains(",") || s.Contains("\"") || s.Contains("\r") || s.Contains("\n")) s = "\"" + s.Replace("\"", "\"\"") + "\"";
                    f.Add(s);
                }
                b.Append(string.Join(",", f.ToArray())).Append("\r\n");
            }
            byte[] body = new UTF8Encoding(false).GetBytes(b.ToString());
            byte[] outp = new byte[body.Length + 3];
            outp[0] = 0xEF; outp[1] = 0xBB; outp[2] = 0xBF;
            Array.Copy(body, 0, outp, 3, body.Length);
            return outp;
        }
        private static SheetDef TableSheet(string name, List<List<string>> rows, bool header)
        {
            SheetDef s = new SheetDef(); s.Name = name; s.Rows = rows; s.FreezeHeader = header;
            if (header && rows.Count > 0)
            {
                s.Styles = new int[rows.Count, Math.Max(1, J.Cols(rows))];
                for (int c = 0; c < s.Styles.GetLength(1); c++) s.Styles[0, c] = 1;
            }
            return s;
        }
        private static int KindStyle(string kind)
        {
            if (kind == "changed") return 2;
            if (kind == "onlyA") return 3;
            if (kind == "onlyB") return 4;
            if (kind == "rowOnlyA") return 6;
            if (kind == "rowOnlyB") return 8;
            if (kind == "eq") return 5;
            return 0;
        }
        public static byte[] Run(string json)
        {
            Dictionary<string, object> q = J.D(json);
            string kind = G(q, "kind"), format = G(q, "format");
            if (format.Length == 0) format = "xlsx";
            if (kind == "table" || kind == "csv")
            {
                List<List<string>> rows = J.Matrix(q.ContainsKey("rows") ? q["rows"] : null);
                if (format == "csv" || kind == "csv") return Csv(rows);
                bool header = J.Bool(q.ContainsKey("header") ? q["header"] : null, false);
                return Xlsx.Build(new List<SheetDef> { TableSheet("识别结果", rows, header) });
            }
            if (kind == "report")
            {
                Dictionary<string, object> diff = q.ContainsKey("diff") ? q["diff"] as Dictionary<string, object> : null;
                if (diff == null) return Csv(new List<List<string>> { new List<string> { "无报告数据" } });
                Dictionary<string, object> stats = diff.ContainsKey("stats") ? diff["stats"] as Dictionary<string, object> : null;
                List<object> diffs = J.L(diff.ContainsKey("diffs") ? diff["diffs"] : null);
                List<object> cells = J.L(diff.ContainsKey("cells") ? diff["cells"] : null);
                List<object> colA = J.L(diff.ContainsKey("colLabelsA") ? diff["colLabelsA"] : null);
                List<object> colB = J.L(diff.ContainsKey("colLabelsB") ? diff["colLabelsB"] : null);
                List<object> rowLabels = J.L(diff.ContainsKey("rowLabels") ? diff["rowLabels"] : null);
                List<SheetDef> sheets = new List<SheetDef>();
                // sheet 1: summary
                List<List<string>> sum = new List<List<string>>();
                sum.Add(new List<string> { "表格识别 · 差异校对报告", "" });
                sum.Add(new List<string> { "生成时间", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) });
                sum.Add(new List<string> { "", "" });
                sum.Add(new List<string> { "项目", "数值" });
                sum.Add(new List<string> { "识别表范围", G(diff, "rangeA") });
                sum.Add(new List<string> { "电子表格范围", G(diff, "rangeB") });
                sum.Add(new List<string> { "对齐方式", G(diff, "align") == "key" ? "按关键列" : "按位置" });
                sum.Add(new List<string> { "校对格数", G(stats, "compared") });
                sum.Add(new List<string> { "一致", G(stats, "equal") });
                sum.Add(new List<string> { "内容不同", G(stats, "changed") });
                sum.Add(new List<string> { "仅识别表有", G(stats, "onlyA") });
                sum.Add(new List<string> { "仅表格有", G(stats, "onlyB") });
                sum.Add(new List<string> { "整行仅识别表有", G(stats, "rowOnlyA") });
                sum.Add(new List<string> { "整行仅表格有", G(stats, "rowOnlyB") });
                sum.Add(new List<string> { "一致率", G(stats, "matchRate") + "%" });
                sum.Add(new List<string> { "", "" });
                sum.Add(new List<string> { "颜色图例", "" });
                sum.Add(new List<string> { "浅红", "内容不同" });
                sum.Add(new List<string> { "浅绿", "仅识别表有" });
                sum.Add(new List<string> { "浅黄", "仅表格有" });
                sum.Add(new List<string> { "浅灰", "一致" });
                SheetDef s1 = new SheetDef(); s1.Name = "差异总览"; s1.Rows = sum;
                s1.Styles = new int[sum.Count, 2];
                s1.Styles[0, 0] = 7; s1.Styles[3, 0] = 1; s1.Styles[3, 1] = 1; s1.Styles[16, 0] = 1; s1.Styles[16, 1] = 1;
                s1.Styles[17, 0] = 2; s1.Styles[18, 0] = 3; s1.Styles[19, 0] = 4; s1.Styles[20, 0] = 5;
                sheets.Add(s1);
                // sheet 2: cell-by-cell
                List<List<string>> grid = new List<List<string>>();
                List<string> hdr = new List<string> { "位置" };
                for (int c = 0; c < colA.Count; c++) hdr.Add(J.Str(colA[c]) + (c < colB.Count && J.Str(colB[c]) != J.Str(colA[c]) ? "→" + J.Str(colB[c]) : ""));
                grid.Add(hdr);
                int[,] gs = new int[Math.Max(1, cells.Count + 1), Math.Max(1, hdr.Count)];
                for (int c = 0; c < hdr.Count; c++) gs[0, c] = 1;
                for (int r = 0; r < cells.Count; r++)
                {
                    List<object> row = J.L(cells[r]);
                    List<string> gr = new List<string>();
                    gr.Add(r < rowLabels.Count ? J.Str(rowLabels[r]) : (r + 1).ToString());
                    for (int c = 0; c < row.Count; c++)
                    {
                        Dictionary<string, object> cell = row[c] as Dictionary<string, object>;
                        string k = G(cell, "kind"), a = G(cell, "a"), bb = G(cell, "b");
                        string txt = (k == "eq" || k == "skipped") ? a : (a == bb ? a : a + " → " + bb);
                        gr.Add(txt);
                        if (r + 1 < gs.GetLength(0) && c + 1 < gs.GetLength(1)) gs[r + 1, c + 1] = KindStyle(k);
                    }
                    grid.Add(gr);
                }
                SheetDef s2 = new SheetDef(); s2.Name = "逐格对照"; s2.Rows = grid; s2.Styles = gs; s2.FreezeHeader = true;
                sheets.Add(s2);
                // sheet 3: diff list
                List<List<string>> lst = new List<List<string>>();
                lst.Add(new List<string> { "序号", "识别表坐标", "表格坐标", "识别值", "表格值", "差异类型", "字符级差异" });
                int[,] ls = new int[diffs.Count + 1, 7];
                for (int c = 0; c < 7; c++) ls[0, c] = 1;
                for (int i = 0; i < diffs.Count; i++)
                {
                    Dictionary<string, object> d = diffs[i] as Dictionary<string, object>;
                    string kind2 = G(d, "kind");
                    lst.Add(new List<string> { (i + 1).ToString(), G(d, "aRef"), G(d, "bRef"), G(d, "a"), G(d, "b"), G(d, "kindName"), G(d, "detail") });
                    ls[i + 1, 5] = KindStyle(kind2);
                }
                SheetDef s3 = new SheetDef(); s3.Name = "差异清单"; s3.Rows = lst; s3.Styles = ls; s3.FreezeHeader = true;
                sheets.Add(s3);
                if (format == "csv") return Csv(lst);
                return Xlsx.Build(sheets);
            }
            return Csv(new List<List<string>> { new List<string> { "未知的导出类型" } });
        }
    }
}

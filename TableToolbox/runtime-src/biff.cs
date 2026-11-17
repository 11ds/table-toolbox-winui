// TableLens core (part 3/3) - legacy Excel reader: OLE2 (CFB) container + BIFF8 records.
// Supports .xls (Excel 97-2003) and .et (WPS spreadsheet, same BIFF8 workbook stream).
// No Office, no COM, no third-party library.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TableLens
{
    // ---------------------------------------------------------------- OLE2 / CFB
    internal class Cfb
    {
        public class Dir
        {
            public string Name = "";
            public int Type;
            public int Start;
            public long Size;
        }

        private readonly byte[] _d;
        private int _ssz = 512;
        private int _mssz = 64;
        private int _cutoff = 4096;
        private readonly List<int> _fat = new List<int>();
        private readonly List<int> _miniFat = new List<int>();
        private readonly List<Dir> _dirs = new List<Dir>();
        private byte[] _miniStream;

        public Cfb(byte[] data)
        {
            _d = data;
            if (data.Length < 512 || data[0] != 0xD0 || data[1] != 0xCF || data[2] != 0x11 || data[3] != 0xE0)
                throw new Exception("不是 OLE2 复合文档");
            _ssz = 1 << U16(0x1E);
            _mssz = 1 << U16(0x20);
            if (_ssz < 128 || _ssz > 65536) throw new Exception("复合文档扇区大小异常");
            int dirStart = (int)U32(0x30);
            _cutoff = (int)U32(0x38);
            int miniFatStart = (int)U32(0x3C);
            int difatStart = (int)U32(0x44);

            List<int> difat = new List<int>();
            for (int i = 0; i < 109; i++)
            {
                int s = (int)U32(0x4C + i * 4);
                if (s >= 0) difat.Add(s);
            }
            int ds = difatStart;
            int guard = 0;
            while (ds >= 0 && guard++ < 65536)
            {
                int off = SecOff(ds);
                if (off < 0 || off + _ssz > _d.Length) break;
                int n = _ssz / 4 - 1;
                for (int i = 0; i < n; i++)
                {
                    int s = (int)U32(off + i * 4);
                    if (s >= 0 && s < 0x7FFFFFF0) difat.Add(s);
                }
                ds = (int)U32(off + n * 4);
            }
            foreach (int s in difat)
            {
                int off = SecOff(s);
                if (off < 0 || off + _ssz > _d.Length) continue;
                for (int i = 0; i < _ssz / 4; i++) _fat.Add((int)U32(off + i * 4));
            }
            if (_fat.Count == 0) throw new Exception("复合文档 FAT 为空");

            List<int> chain = Chain(dirStart);
            foreach (int s in chain)
            {
                int off = SecOff(s);
                if (off < 0 || off + _ssz > _d.Length) continue;
                for (int i = 0; i < _ssz / 128; i++)
                {
                    int e = off + i * 128;
                    if (e + 128 > _d.Length) break;
                    int nameLen = U16(e + 0x40);
                    int type = _d[e + 0x42];
                    if (type == 0) continue;
                    Dir d = new Dir();
                    if (nameLen > 2) d.Name = Encoding.Unicode.GetString(_d, e, Math.Min(nameLen - 2, 62));
                    d.Type = type;
                    d.Start = (int)U32(e + 0x74);
                    d.Size = (long)U32(e + 0x78);
                    _dirs.Add(d);
                }
            }
            if (_dirs.Count == 0) throw new Exception("复合文档目录为空");

            Dir root = null;
            foreach (Dir d in _dirs) if (d.Type == 5) { root = d; break; }
            if (root != null && root.Size > 0) _miniStream = ReadNormal(root.Start, (int)root.Size);

            if (miniFatStart >= 0)
            {
                foreach (int s in Chain(miniFatStart))
                {
                    int off = SecOff(s);
                    if (off < 0 || off + _ssz > _d.Length) continue;
                    for (int i = 0; i < _ssz / 4; i++) _miniFat.Add((int)U32(off + i * 4));
                }
            }
        }

        private int U16(int off)
        {
            if (off < 0 || off + 2 > _d.Length) return 0;
            return _d[off] | (_d[off + 1] << 8);
        }
        private uint U32(int off)
        {
            if (off < 0 || off + 4 > _d.Length) return 0;
            return (uint)(_d[off] | (_d[off + 1] << 8) | (_d[off + 2] << 16) | (_d[off + 3] << 24));
        }
        private int SecOff(int s)
        {
            long o = ((long)s + 1) * _ssz;
            return o > int.MaxValue ? -1 : (int)o;
        }
        private List<int> Chain(int start)
        {
            List<int> list = new List<int>();
            int cur = start;
            int guard = 0;
            while (cur >= 0 && cur < 0x7FFFFFF0 && guard++ < 2000000)
            {
                list.Add(cur);
                if (cur >= _fat.Count) break;
                cur = _fat[cur];
            }
            return list;
        }
        private byte[] ReadNormal(int start, int size)
        {
            if (size <= 0) return new byte[0];
            List<int> chain = Chain(start);
            byte[] buf = new byte[size];
            int got = 0;
            foreach (int s in chain)
            {
                int off = SecOff(s);
                int take = Math.Min(_ssz, size - got);
                if (take <= 0) break;
                if (off < 0 || off + take > _d.Length) break;
                Array.Copy(_d, off, buf, got, take);
                got += take;
                if (got >= size) break;
            }
            if (got < size) Array.Resize(ref buf, got);
            return buf;
        }
        private byte[] ReadMini(int start, int size)
        {
            if (size <= 0 || _miniStream == null) return new byte[0];
            byte[] buf = new byte[size];
            int got = 0;
            int cur = start;
            int guard = 0;
            while (cur >= 0 && cur < 0x7FFFFFF0 && got < size && guard++ < 2000000)
            {
                int off = cur * _mssz;
                int take = Math.Min(_mssz, size - got);
                if (off < 0 || off + take > _miniStream.Length) break;
                Array.Copy(_miniStream, off, buf, got, take);
                got += take;
                if (cur >= _miniFat.Count) break;
                cur = _miniFat[cur];
            }
            if (got < size) Array.Resize(ref buf, got);
            return buf;
        }

        public bool HasStream(string name)
        {
            foreach (Dir d in _dirs)
                if (d.Type == 2 && string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        public byte[] ReadStream(string name)
        {
            foreach (Dir d in _dirs)
            {
                if (d.Type != 2 || !string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                int size = (int)Math.Min(d.Size, int.MaxValue);
                return size >= _cutoff ? ReadNormal(d.Start, size) : ReadMini(d.Start, size);
            }
            return null;
        }
        public List<string> StreamNames()
        {
            List<string> n = new List<string>();
            foreach (Dir d in _dirs) if (d.Type == 2) n.Add(d.Name);
            return n;
        }
    }

    // ---------------------------------------------------------------- BIFF8
    public static class Biff8
    {
        private const int R_BOF = 0x0809, R_EOF = 0x000A, R_BOUNDSHEET = 0x0085, R_SST = 0x00FC;
        private const int R_CONTINUE = 0x003C, R_LABELSST = 0x00FD, R_LABEL = 0x0204;
        private const int R_NUMBER = 0x0203, R_RK = 0x027E, R_MULRK = 0x00BD, R_BLANK = 0x0201;
        private const int R_MULBLANK = 0x00BE, R_BOOLERR = 0x0205, R_FORMULA = 0x0006;
        private const int R_STRING = 0x0207, R_XF = 0x00E0, R_FORMAT = 0x041E;
        private const int R_DATEMODE = 0x0022, R_FILEPASS = 0x002F;

        private class Bounds
        {
            public int Offset;
            public string Name = "";
        }

        private static int U16(byte[] b, int p) { return b[p] | (b[p + 1] << 8); }
        private static uint U32(byte[] b, int p)
        {
            return (uint)(b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24));
        }
        private static double F64(byte[] b, int p) { return BitConverter.ToDouble(b, p); }

        public static List<SheetData> Read(byte[] data)
        {
            Cfb cfb = new Cfb(data);
            string streamName = null;
            if (cfb.HasStream("Workbook")) streamName = "Workbook";
            else if (cfb.HasStream("Book")) streamName = "Book";
            if (streamName == null)
                throw new Exception("OLE2 中没有 Workbook 流（streams: " + string.Join(",", cfb.StreamNames().ToArray()) + "）");
            byte[] wb = cfb.ReadStream(streamName);
            if (wb == null || wb.Length < 8) throw new Exception("Workbook 流为空");

            List<Bounds> sheets = new List<Bounds>();
            List<string> sst = new List<string>();
            List<int> xfIfmt = new List<int>();
            Dictionary<int, string> customFmt = new Dictionary<int, string>();
            int dateMode = 0;

            int p = 0;
            int guard = 0;
            while (p + 4 <= wb.Length && guard++ < 500000)
            {
                int id = U16(wb, p);
                int sz = U16(wb, p + 2);
                int dp = p + 4;
                if (dp + sz > wb.Length) break;
                if (id == R_FILEPASS) throw new Exception("工作簿已加密，无法读取");
                if (id == R_BOF && sz >= 4)
                {
                    int ver = U16(wb, dp);
                    if (ver != 0x0600 && ver != 0x0000)
                        throw new Exception("仅支持 Excel 97-2003 (BIFF8)，该文件是更旧的 BIFF" + (ver >> 8) + " 格式，请先用 Excel/WPS 另存为 .xlsx");
                }
                if (id == R_BOUNDSHEET && sz >= 8)
                {
                    Bounds b = new Bounds();
                    b.Offset = (int)U32(wb, dp);
                    int cch = wb[dp + 6];
                    int grbit = wb[dp + 7];
                    int q = dp + 8;
                    if ((grbit & 1) != 0)
                    {
                        StringBuilder sb = new StringBuilder();
                        for (int i = 0; i < cch && q + 2 <= dp + sz; i++, q += 2) sb.Append((char)U16(wb, q));
                        b.Name = sb.ToString();
                    }
                    else
                    {
                        for (int i = 0; i < cch && q < dp + sz; i++, q++)
                            b.Name = b.Name + (char)wb[q];
                    }
                    if (b.Name.Length == 0) b.Name = "Sheet" + (sheets.Count + 1);
                    sheets.Add(b);
                }
                else if (id == R_SST)
                {
                    List<byte[]> segs = new List<byte[]>();
                    byte[] first = new byte[sz];
                    Array.Copy(wb, dp, first, 0, sz);
                    segs.Add(first);
                    int q = dp + sz;
                    while (q + 4 <= wb.Length)
                    {
                        int id2 = U16(wb, q), sz2 = U16(wb, q + 2);
                        if (id2 != R_CONTINUE) break;
                        if (q + 4 + sz2 > wb.Length) break;
                        byte[] seg = new byte[sz2];
                        Array.Copy(wb, q + 4, seg, 0, sz2);
                        segs.Add(seg);
                        q += 4 + sz2;
                    }
                    sst = ParseSst(segs);
                    p = q;
                    continue;
                }
                else if (id == R_XF && sz >= 4)
                {
                    // XF layout: ifnt (2) then ifmt (2) - reading offset 0 gives the font index, not the format.
                    xfIfmt.Add(U16(wb, dp + 2));
                }
                else if (id == R_FORMAT && sz >= 4)
                {
                    int ifmt = U16(wb, dp);
                    int cch = U16(wb, dp + 2);
                    int grbit = wb[dp + 4];
                    int q = dp + 5;
                    string code = "";
                    if ((grbit & 1) != 0)
                    {
                        StringBuilder sb = new StringBuilder();
                        for (int i = 0; i < cch && q + 2 <= dp + sz; i++, q += 2) sb.Append((char)U16(wb, q));
                        code = sb.ToString();
                    }
                    else
                    {
                        for (int i = 0; i < cch && q < dp + sz; i++, q++) code = code + (char)wb[q];
                    }
                    customFmt[ifmt] = code;
                }
                else if (id == R_DATEMODE && sz >= 2)
                {
                    dateMode = U16(wb, dp);
                }
                else if (id == R_EOF)
                {
                    break;
                }
                p = dp + sz;
            }

            bool[] isDateXf = new bool[xfIfmt.Count];
            for (int i = 0; i < xfIfmt.Count; i++) isDateXf[i] = IsDateFormat(xfIfmt[i], customFmt);


            List<SheetData> result = new List<SheetData>();
            foreach (Bounds bs in sheets)
            {
                SheetData sd = new SheetData();
                sd.Name = bs.Name;
                ReadSheet(wb, bs.Offset, sd, sst, isDateXf, dateMode == 1);
                while (sd.Rows.Count > 0)
                {
                    bool empty = true;
                    foreach (string s in sd.Rows[sd.Rows.Count - 1]) if (s.Trim().Length > 0) { empty = false; break; }
                    if (!empty) break;
                    sd.Rows.RemoveAt(sd.Rows.Count - 1);
                }
                J.Pad(sd.Rows, J.Cols(sd.Rows));
                if (sd.Rows.Count > 0) result.Add(sd);
            }
            if (result.Count == 0) throw new Exception("工作簿里没有工作表数据");
            return result;
        }

        private static bool IsDateFormat(int ifmt, Dictionary<int, string> custom)
        {
            if ((ifmt >= 14 && ifmt <= 22) || (ifmt >= 27 && ifmt <= 36) || (ifmt >= 45 && ifmt <= 47) || (ifmt >= 50 && ifmt <= 58)) return true;
            string code;
            if (custom.TryGetValue(ifmt, out code) && !string.IsNullOrEmpty(code))
            {
                string c = code.ToLowerInvariant();
                if (c.IndexOf('y') >= 0 || c.IndexOf('d') >= 0 || c.IndexOf('m') >= 0 || c.IndexOf('h') >= 0 || c.IndexOf('s') >= 0)
                {
                    bool onlyNum = true;
                    foreach (char ch in c) if ("#0.,% -;".IndexOf(ch) < 0) { onlyNum = false; break; }
                    if (!onlyNum) return true;
                }
            }
            return false;
        }

        private static void ReadSheet(byte[] wb, int start, SheetData sd, List<string> sst, bool[] isDateXf, bool date1904)
        {
            int p = start;
            int guard = 0;
            int pendingRow = -1, pendingCol = -1;
            while (p + 4 <= wb.Length && guard++ < 500000)
            {
                int id = U16(wb, p);
                int sz = U16(wb, p + 2);
                int dp = p + 4;
                if (dp + sz > wb.Length) break;
                if (id == R_EOF) break;
                if (id == R_LABELSST && sz >= 10)
                {
                    int row = U16(wb, dp), col = U16(wb, dp + 2), isst = (int)U32(wb, dp + 6);
                    if (isst >= 0 && isst < sst.Count) J.Set(sd.Rows, row, col, sst[isst]);
                }
                else if (id == R_LABEL && sz >= 9)
                {
                    int row = U16(wb, dp), col = U16(wb, dp + 2);
                    int cch = U16(wb, dp + 6), grbit = wb[dp + 8];
                    int q = dp + 9;
                    string s = ReadInline(wb, q, cch, grbit, dp + sz);
                    J.Set(sd.Rows, row, col, s);
                }
                else if (id == R_NUMBER && sz >= 14)
                {
                    int row = U16(wb, dp), col = U16(wb, dp + 2), xf = U16(wb, dp + 4);
                    double v = F64(wb, dp + 6);
                    J.Set(sd.Rows, row, col, NumOrDate(v, xf, isDateXf, date1904));
                }
                else if (id == R_RK && sz >= 10)
                {
                    int row = U16(wb, dp), col = U16(wb, dp + 2), xf = U16(wb, dp + 4);
                    J.Set(sd.Rows, row, col, NumOrDate(DecodeRk((int)U32(wb, dp + 6)), xf, isDateXf, date1904));
                }
                else if (id == R_MULRK && sz >= 6)
                {
                    int row = U16(wb, dp);
                    int colFirst = U16(wb, dp + 2);
                    int n = (sz - 6) / 6;
                    for (int i = 0; i < n; i++)
                    {
                        int q = dp + 4 + i * 6;
                        int xf = U16(wb, q);
                            int rk = (int)U32(wb, q + 2);
                        J.Set(sd.Rows, row, colFirst + i, NumOrDate(DecodeRk(rk), xf, isDateXf, date1904));
                    }
                }
                else if (id == R_BOOLERR && sz >= 8)
                {
                    int row = U16(wb, dp), col = U16(wb, dp + 2);
                    int val = wb[dp + 6], isErr = wb[dp + 7];
                    J.Set(sd.Rows, row, col, isErr != 0 ? "#ERR" : (val != 0 ? "TRUE" : "FALSE"));
                }
                else if (id == R_FORMULA && sz >= 16)
                {
                    int row = U16(wb, dp), col = U16(wb, dp + 2), xf = U16(wb, dp + 4);
                    byte b0 = wb[dp + 6], b1 = wb[dp + 7];
                    if (b0 == 0xFF && b1 == 0xFF)
                    {
                        if (sz >= 9)
                        {
                            int kind = wb[dp + 8];
                            if (kind == 1) J.Set(sd.Rows, row, col, (sz >= 11 && wb[dp + 10] != 0) ? "TRUE" : "FALSE");
                            else if (kind == 2) J.Set(sd.Rows, row, col, "#ERR");
                            else { pendingRow = row; pendingCol = col; }
                        }
                    }
                    else
                    {
                        J.Set(sd.Rows, row, col, NumOrDate(F64(wb, dp + 6), xf, isDateXf, date1904));
                    }
                }
                else if (id == R_STRING && pendingRow >= 0 && sz >= 3)
                {
                    int cch = U16(wb, dp), grbit = wb[dp + 2];
                    J.Set(sd.Rows, pendingRow, pendingCol, ReadInline(wb, dp + 3, cch, grbit, dp + sz));
                    pendingRow = -1; pendingCol = -1;
                }
                p = dp + sz;
            }
        }

        private static string ReadInline(byte[] b, int q, int cch, int grbit, int end)
        {
            StringBuilder sb = new StringBuilder();
            if ((grbit & 1) != 0)
            {
                for (int i = 0; i < cch && q + 2 <= end; i++, q += 2) sb.Append((char)U16(b, q));
            }
            else
            {
                for (int i = 0; i < cch && q < end; i++, q++) sb.Append((char)b[q]);
            }
            return sb.ToString();
        }

        private static double DecodeRk(int rk)
        {
            bool div100 = (rk & 1) != 0;
            bool isInt = (rk & 2) != 0;
            double v;
            if (isInt)
            {
                v = (double)(rk >> 2);
            }
            else
            {
                long bits = ((long)(rk & unchecked((int)0xFFFFFFFC))) << 32;
                v = BitConverter.Int64BitsToDouble(bits);
            }
            if (div100) v /= 100.0;
            return v;
        }

        private static string NumOrDate(double v, int xf, bool[] isDateXf, bool date1904)
        {
            bool isDate = xf >= 0 && xf < isDateXf.Length && isDateXf[xf];
            if (isDate && v > 0 && v < 2958466)
            {
                try
                {
                    DateTime dt = DateTime.FromOADate(date1904 ? v + 1462 : v);
                    bool dateOnly = (v - Math.Floor(v)) < 1e-9;
                    return dt.ToString(dateOnly ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                }
                catch { }
            }
            return Num(v);
        }

        private static string Num(double v)
        {
            if (Math.Abs(v) < 1e-10) v = 0;
            if (Math.Abs(v) < 1e15 && Math.Abs(v - Math.Round(v)) < 1e-9)
                return ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
            string s = v.ToString("0.##########", CultureInfo.InvariantCulture);
            return s;
        }

        // ------------------------------------------------------------ SST reader
        private class SegReader
        {
            private readonly List<byte[]> _segs;
            private int _si;
            private int _off;

            public SegReader(List<byte[]> segs) { _segs = segs; _si = 0; _off = 0; }

            private bool Ensure(int k)
            {
                while (_si < _segs.Count && _off + k > _segs[_si].Length)
                {
                    if (_si + 1 >= _segs.Count) return false;
                    _si++;
                    _off = 0;
                }
                return _si < _segs.Count && _off + k <= _segs[_si].Length;
            }
            public int U8()
            {
                if (!Ensure(1)) return 0;
                return _segs[_si][_off++];
            }
            public int U16()
            {
                if (!Ensure(2)) return 0;
                int v = _segs[_si][_off] | (_segs[_si][_off + 1] << 8);
                _off += 2;
                return v;
            }
            public long U32()
            {
                if (!Ensure(4)) return 0;
                long v = (long)(_segs[_si][_off] | (_segs[_si][_off + 1] << 8) | (_segs[_si][_off + 2] << 16) | (_segs[_si][_off + 3] << 24));
                _off += 4;
                return v;
            }
            public void Skip(int n)
            {
                while (n > 0)
                {
                    if (!Ensure(1)) return;
                    int avail = _segs[_si].Length - _off;
                    int take = Math.Min(avail, n);
                    _off += take;
                    n -= take;
                }
            }
            public string ReadChars(int cch, int grbit)
            {
                StringBuilder sb = new StringBuilder();
                bool wide = (grbit & 1) != 0;
                int remaining = cch;
                while (remaining > 0)
                {
                    if (_si >= _segs.Count) break;
                    if (_off >= _segs[_si].Length)
                    {
                        if (_si + 1 >= _segs.Count) break;
                        _si++;
                        _off = 0;
                        grbit = _segs[_si][_off];
                        _off++;
                        wide = (grbit & 1) != 0;
                        continue;
                    }
                    int avail = _segs[_si].Length - _off;
                    int can = wide ? avail / 2 : avail;
                    if (can <= 0)
                    {
                        if (_si + 1 >= _segs.Count) break;
                        _si++;
                        _off = 0;
                        grbit = _segs[_si][_off];
                        _off++;
                        wide = (grbit & 1) != 0;
                        continue;
                    }
                    int take = Math.Min(can, remaining);
                    for (int i = 0; i < take; i++)
                    {
                        if (wide)
                        {
                            sb.Append((char)(_segs[_si][_off] | (_segs[_si][_off + 1] << 8)));
                            _off += 2;
                        }
                        else
                        {
                            sb.Append((char)_segs[_si][_off]);
                            _off++;
                        }
                    }
                    remaining -= take;
                }
                return sb.ToString();
            }
        }

        private static List<string> ParseSst(List<byte[]> segs)
        {
            List<string> list = new List<string>();
            SegReader r = new SegReader(segs);
            r.U32();
            long unique = r.U32();
            if (unique < 0 || unique > 1000000) unique = 1000000;
            for (int i = 0; i < unique; i++)
            {
                int cch = r.U16();
                int grbit = r.U8();
                int cRun = 0;
                long cbExt = 0;
                if ((grbit & 0x08) != 0) cRun = r.U16();
                if ((grbit & 0x04) != 0) cbExt = r.U32();
                string s = r.ReadChars(cch, grbit);
                list.Add(s);
                if (cRun > 0) r.Skip(cRun * 4);
                if (cbExt > 0 && cbExt < int.MaxValue) r.Skip((int)cbExt);
            }
            return list;
        }
    }
}

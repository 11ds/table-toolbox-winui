using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TableToolbox
{
    // Calls the proven TableLens engine through a hidden system PowerShell process.
    // All spreadsheet read / OCR / export runs locally. No browser, no network.
    public static class EngineBridge
    {
        public class Sheet
        {
            public string Name;
            public List<List<string>> Rows = new List<List<string>>();
        }

        public class LoadedTable
        {
            public string FileName;
            public List<Sheet> Sheets = new List<Sheet>();
        }

        private static JsonElement Prop(JsonElement o, string name)
        {
            JsonElement v;
            if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out v)) return v;
            return default(JsonElement);
        }

        private static bool Bool(JsonElement e, bool d)
        {
            if (e.ValueKind == JsonValueKind.True) return true;
            if (e.ValueKind == JsonValueKind.False) return false;
            return d;
        }

        private static string Str(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.String) return e.GetString() ?? "";
            if (e.ValueKind == JsonValueKind.Number || e.ValueKind == JsonValueKind.True || e.ValueKind == JsonValueKind.False)
                return e.ToString();
            return "";
        }

        private static List<List<string>> Matrix(JsonElement e)
        {
            var m = new List<List<string>>();
            if (e.ValueKind != JsonValueKind.Array) return m;
            foreach (var row in e.EnumerateArray())
            {
                var r = new List<string>();
                if (row.ValueKind == JsonValueKind.Array)
                    foreach (var c in row.EnumerateArray()) r.Add(Str(c));
                m.Add(r);
            }
            return m;
        }

        public static LoadedTable ReadTable(string path, string runtimeDir)
        {
            var q = new Dictionary<string, object> { { "path", path } };
            string res = RunEngine("read", JsonSerializer.Serialize(q), runtimeDir, 90000);
            using (JsonDocument doc = JsonDocument.Parse(res))
            {
                JsonElement d = doc.RootElement;
                if (!Bool(Prop(d, "ok"), false))
                {
                    string err = Str(Prop(d, "error"));
                    throw new Exception(string.IsNullOrEmpty(err) ? "读取失败" : err);
                }
                LoadedTable t = new LoadedTable { FileName = Path.GetFileName(path) };
                JsonElement sheets = Prop(d, "sheets");
                if (sheets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var sd in sheets.EnumerateArray())
                    {
                        t.Sheets.Add(new Sheet
                        {
                            Name = Str(Prop(sd, "name")),
                            Rows = Matrix(Prop(sd, "rows"))
                        });
                    }
                }
                return t;
            }
        }

        public static LoadedTable Recognize(string path, string runtimeDir, int rotate, bool enhance, string pages,
                                            out string warnings)
        {
            warnings = "";
            string ext = Path.GetExtension(path).ToLowerInvariant();
            bool isPdf = ext == ".pdf";
            string action = isPdf ? "read" : "ocr";
            var q = new Dictionary<string, object>();
            q["path"] = path;
            var opt = new Dictionary<string, object>
            {
                { "rotate", rotate },
                { "enhance", enhance },
                { "minWidth", 2200 }
            };
            if (isPdf) q["pages"] = string.IsNullOrEmpty(pages) ? "1" : pages;
            else { opt["mode"] = "auto"; opt["rows"] = 0; opt["cols"] = 0; }
            q["options"] = opt;
            string res = RunEngine(action, JsonSerializer.Serialize(q), runtimeDir, isPdf ? 180000 : 90000);

            using (JsonDocument doc = JsonDocument.Parse(res))
            {
                JsonElement d = doc.RootElement;
                if (!Bool(Prop(d, "ok"), false))
                {
                    string err = Str(Prop(d, "error"));
                    throw new Exception(string.IsNullOrEmpty(err) ? "识别失败" : err);
                }
                LoadedTable t = new LoadedTable { FileName = Path.GetFileName(path) };
                JsonElement sheets = Prop(d, "sheets");
                if (sheets.ValueKind == JsonValueKind.Array && sheets.GetArrayLength() > 0)
                {
                    foreach (var sd in sheets.EnumerateArray())
                    {
                        t.Sheets.Add(new Sheet
                        {
                            Name = Str(Prop(sd, "name")),
                            Rows = Matrix(Prop(sd, "rows"))
                        });
                    }
                }
                else
                {
                    t.Sheets.Add(new Sheet { Name = "识别结果", Rows = Matrix(Prop(d, "rows")) });
                }
                int miss = 0;
                JsonElement mj = Prop(d, "missCells");
                if (mj.ValueKind == JsonValueKind.Number) mj.TryGetInt32(out miss);
                if (miss > 0) warnings += "有 " + miss + " 个格疑似漏识别（已标色），请人工核对；";
                return t;
            }
        }

        public static byte[] Export(List<List<string>> rows, bool csv, string sheetName)
        {
            var q = new Dictionary<string, object>
            {
                { "kind", "table" },
                { "format", csv ? "csv" : "xlsx" },
                { "header", true },
                { "name", sheetName ?? "表格" },
                { "rows", rows }
            };
            // Run export through the engine (returns base64 data)
            string runtimeDir = Runtime.EnsureRuntime();
            string res = RunEngine("export", JsonSerializer.Serialize(q), runtimeDir, 60000);
            using (JsonDocument doc = JsonDocument.Parse(res))
            {
                JsonElement d = doc.RootElement;
                if (!Bool(Prop(d, "ok"), false)) throw new Exception(Str(Prop(d, "error")));
                string b64 = Str(Prop(d, "data"));
                return Convert.FromBase64String(b64);
            }
        }

        public static string SaveExport(byte[] data, string fileName)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(dir)) dir = Path.GetTempPath();
            string path = Path.Combine(dir, fileName);
            File.WriteAllBytes(path, data);
            return path;
        }

        private static string RunEngine(string action, string json, string runtimeDir, int timeoutMs)
        {
            string ps = FindPowerShell();
            string engine = Path.Combine(runtimeDir, "run_engine.ps1");
            if (!File.Exists(engine)) throw new Exception("找不到识别引擎：" + engine);

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = ps;
            // Pass the request as base64 so quotes, backslashes and non-ASCII
            // paths survive the command line intact (PowerShell 5.1 mangles them).
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json ?? "{}"));
            psi.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + engine +
                            "\" -Action " + action + " -B64 \"" + b64 + "\"";
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.WorkingDirectory = runtimeDir;

            Process p = new Process();
            p.StartInfo = psi;
            p.Start();
            string outText = p.StandardOutput.ReadToEnd();
            string errText = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch { }
                throw new Exception("识别超时，请换更小的图片或更少页码重试。");
            }
            outText = (outText ?? "").Trim();
            if (outText.Length == 0)
                throw new Exception("识别引擎没有返回结果。" + (errText.Length > 0 ? " " + FirstLine(errText) : ""));
            return outText;
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.IndexOf('\n');
            return i < 0 ? s : s.Substring(0, i);
        }

        private static string FindPowerShell()
        {
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string p = Path.Combine(sys, "WindowsPowerShell", "v1.0", "powershell.exe");
            return File.Exists(p) ? p : "powershell.exe";
        }
    }
}

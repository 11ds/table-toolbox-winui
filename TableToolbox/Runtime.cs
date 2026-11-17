using System;
using System.IO;
using System.Reflection;

namespace TableToolbox
{
    // Extracts embedded OCR engine scripts to %LOCALAPPDATA%\表格工具箱\runtime-winui on first run.
    // These are local files only; OCR itself is Windows.Media.Ocr (built into Windows).
    public static class Runtime
    {
        private static string _dir;

        public static string EnsureRuntime()
        {
            if (!string.IsNullOrEmpty(_dir) && Directory.Exists(_dir)) return _dir;

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = Path.Combine(local, "表格工具箱", "runtime-winui-v1");
            string marker = Path.Combine(dir, ".ok");
            string[] names = { "engine.ps1", "core.cs", "book.cs", "biff.cs", "office_helper.ps1", "run_engine.ps1" };

            bool need = !File.Exists(marker);
            if (!need)
                foreach (string n in names)
                    if (!File.Exists(Path.Combine(dir, n))) { need = true; break; }

            if (need)
            {
                Directory.CreateDirectory(dir);
                Assembly asm = typeof(Runtime).Assembly;
                foreach (string n in names)
                {
                    string resName = FindResource(asm, n);
                    if (resName == null) throw new Exception("缺少内嵌引擎脚本：" + n);
                    using (Stream s = asm.GetManifestResourceStream(resName))
                    {
                        if (s == null) throw new Exception("无法读取内嵌脚本：" + n);
                        using (FileStream fs = File.Create(Path.Combine(dir, n)))
                        {
                            s.CopyTo(fs);
                        }
                    }
                }
                File.WriteAllText(marker, "1");
            }
            _dir = dir;
            return dir;
        }

        private static string FindResource(Assembly asm, string fileName)
        {
            foreach (string r in asm.GetManifestResourceNames())
            {
                if (r.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase) ||
                    r.EndsWith("\\" + fileName, StringComparison.OrdinalIgnoreCase))
                    return r;
            }
            return null;
        }
    }
}

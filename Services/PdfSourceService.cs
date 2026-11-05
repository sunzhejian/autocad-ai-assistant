using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AutoCAD.AI.Assistant.Services
{
    internal sealed class PdfPageSource
    {
        public string source_hash { get; set; }
        public string pdf_hash { get; set; }
        public int page { get; set; }
        public int pages { get; set; }
        public string kind { get; set; }
        public int paths { get; set; }
        public int images { get; set; }
        public int text_chars { get; set; }
        public string text { get; set; }
        public double width_mm { get; set; }
        public double height_mm { get; set; }
        public string preview { get; set; }
        public string preview_hash { get; set; }
        public string pdf { get; set; }
        public string error { get; set; }
        public bool CanImport { get { return kind == "vector" || kind == "mixed"; } }
        public string Description
        {
            get { return "第 " + page + "/" + pages + " 页 · " + (kind == "vector" ? "矢量" : kind == "mixed" ? "矢量/扫描混合" : kind == "raster" ? "扫描图" : "空白页")
                + " · 路径 " + paths + " · 文字 " + text_chars; }
        }
        public void VerifySnapshot()
        {
            if (!CanImport || !File.Exists(pdf) || !String.Equals(Hash(pdf), pdf_hash, StringComparison.Ordinal))
                throw new InvalidOperationException("PDF 页面快照已改变或无法导入，请重新附加。");
        }
        public void VerifyPreview()
        {
            if (!File.Exists(preview) || !String.Equals(Hash(preview), preview_hash, StringComparison.Ordinal))
                throw new InvalidOperationException("PDF 页面预览已改变，请重新附加。");
        }
        internal static string Hash(string path)
        {
            using (var stream = File.OpenRead(path)) using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }

    internal static class PdfSourceService
    {
        public static Task<PdfPageSource> LoadAsync(string source, int page, CancellationToken cancellation)
        {
            return Task.Run(() => Load(source, page, cancellation), cancellation);
        }
        private static PdfPageSource Load(string source, int page, CancellationToken cancellation)
        {
            var worker = Path.Combine(Path.GetDirectoryName(typeof(PdfSourceService).Assembly.Location), "pdf_worker.py");
            if (!File.Exists(worker)) throw new InvalidOperationException("缺少 PDF 组件 pdf_worker.py，请重新安装插件。");
            var python = Environment.GetEnvironmentVariable("CADAI_PYTHON");
            if (String.IsNullOrWhiteSpace(python)) python = "python";
            var args = Quote(worker) + " " + Quote(Path.GetFullPath(source)) + " "
                + Quote(Path.Combine(DiagnosticsLog.Folder, "sources")) + " " + page;
            using (var process = new Process { StartInfo = new ProcessStartInfo(python, args) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 } })
            {
                try { process.Start(); }
                catch (Exception ex) { throw new InvalidOperationException("无法启动 PDF 处理。请安装 Python/PyMuPDF，或设置 CADAI_PYTHON 为 python.exe 路径。", ex); }
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                var timer = Stopwatch.StartNew();
                while (!process.WaitForExit(100))
                {
                    if (cancellation.IsCancellationRequested || timer.Elapsed.TotalSeconds > 45)
                    {
                        try { process.Kill(); process.WaitForExit(2000); } catch { }
                        cancellation.ThrowIfCancellationRequested();
                        throw new TimeoutException("PDF 页面处理超过 45 秒，请选取更简单的页面。");
                    }
                }
                cancellation.ThrowIfCancellationRequested();
                var json = output.GetAwaiter().GetResult();
                var stderr = errors.GetAwaiter().GetResult();
                PdfPageSource result;
                try { result = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 }.Deserialize<PdfPageSource>(json); }
                catch { throw new InvalidOperationException("PDF 处理未返回有效结果。" + stderr.Substring(0, Math.Min(300, stderr.Length))); }
                if (result == null || result.error != null || process.ExitCode != 0)
                    throw new InvalidOperationException(result == null ? "PDF 处理失败。" : result.error ?? "PDF 处理失败。");
                return result;
            }
        }
        // Windows CommandLineToArgvW quoting, including paths ending in a slash.
        internal static string Quote(string value)
        {
            var text = new StringBuilder("\""); var slashes = 0;
            foreach (var ch in value)
            {
                if (ch == '\\') { slashes++; continue; }
                if (ch == '"') { text.Append('\\', slashes * 2 + 1); text.Append(ch); slashes = 0; continue; }
                text.Append('\\', slashes); slashes = 0; text.Append(ch);
            }
            text.Append('\\', slashes * 2); return text.Append('"').ToString();
        }
    }
}

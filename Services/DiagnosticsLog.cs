using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AutoCAD.AI.Assistant.Services
{
    /// <summary>
    /// 轻量本地日志：环形缓冲最近 200 条，同时追加到 ai.log，便于导出诊断。
    /// 不记录密钥与图片内容。
    /// </summary>
    internal static class DiagnosticsLog
    {
        private const int Capacity = 200;
        private const long MaxBytes = 512 * 1024;
        private static readonly object Sync = new object();
        private static readonly Queue<string> Buffer = new Queue<string>();

        public static string Folder
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoCAD.AI.Assistant");
            }
        }

        public static string FilePath
        {
            get { return Path.Combine(Folder, "ai.log"); }
        }

        public static void Write(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message;
            lock (Sync)
            {
                Buffer.Enqueue(line);
                while (Buffer.Count > Capacity) Buffer.Dequeue();
                try
                {
                    if (!Directory.Exists(Folder)) Directory.CreateDirectory(Folder);
                    Trim();
                    using (var stream = new StreamWriter(FilePath, true, Encoding.UTF8))
                    {
                        stream.WriteLine(line);
                    }
                }
                catch
                {
                }
            }
        }

        public static void Error(string scope, Exception ex)
        {
            Write(scope + " 失败：" + (ex == null ? "未知错误" : ex.GetType().Name + " " + ex.Message));
        }

        /// <summary>保存模型返回的绘图计划，便于复盘复刻质量（不含图片与密钥）。</summary>
        public static string SavePlan(string plan)
        {
            if (string.IsNullOrEmpty(plan)) return null;
            try
            {
                if (!Directory.Exists(Folder)) Directory.CreateDirectory(Folder);
                var file = Path.Combine(Folder, "plan_" + DateTime.Now.ToString("yyyyMMddHHmmssfff") + ".json");
                File.WriteAllText(file, plan, new UTF8Encoding(false));
                return file;
            }
            catch (Exception ex)
            {
                Write("保存绘图计划失败：" + ex.Message);
                return null;
            }
        }

        public static string Snapshot()
        {
            lock (Sync)
            {
                var text = new StringBuilder();
                foreach (var line in Buffer) text.AppendLine(line);
                return text.ToString();
            }
        }

        /// <summary>导出诊断包（日志副本 + 环境信息），返回文件路径。</summary>
        public static string Export()
        {
            lock (Sync)
            {
                try
                {
                    if (!Directory.Exists(Folder)) Directory.CreateDirectory(Folder);
                    var file = Path.Combine(Folder, "diagnostics_" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".txt");
                    var text = new StringBuilder();
                    text.AppendLine("AutoCAD.AI.Assistant 诊断");
                    text.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    text.AppendLine("版本：" + typeof(DiagnosticsLog).Assembly.GetName().Version);
                    text.AppendLine("系统：" + Environment.OSVersion + " / " + Environment.UserName);
                    text.AppendLine("日志条数：" + Buffer.Count);
                    text.AppendLine();
                    text.Append(Snapshot());
                    File.WriteAllText(file, text.ToString(), Encoding.UTF8);
                    return file;
                }
                catch (Exception ex)
                {
                    Write("导出诊断失败：" + ex.Message);
                    return null;
                }
            }
        }

        private static void Trim()
        {
            try
            {
                var info = new FileInfo(FilePath);
                if (!info.Exists || info.Length < MaxBytes) return;
                var backup = FilePath + ".old";
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(FilePath, backup);
            }
            catch
            {
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoCAD.AI.Assistant.Services
{
    internal static class CadPreviewSettings
    {
        private static readonly string[] Names = { "SELECTIONPREVIEW", "COMMANDPREVIEW", "HPQUICKPREVIEW", "ROLLOVERTIPS" };
        private static string FilePath { get { return Path.Combine(DiagnosticsLog.Folder, "preview-settings.json"); } }
        private sealed class Backup
        {
            public int schema { get; set; }
            public bool active { get; set; }
            public Dictionary<string, int> values { get; set; }
        }
        private static Backup Read()
        {
            if (!File.Exists(FilePath)) return null;
            if (new FileInfo(FilePath).Length > 4096) throw new InvalidOperationException("预览设置备份过大，请核查备份文件。");
            var backup = new JavaScriptSerializer().Deserialize<Backup>(File.ReadAllText(FilePath));
            if (backup == null || backup.schema != 1 || backup.values == null || backup.values.Count != Names.Length)
                throw new InvalidOperationException("预览设置备份格式不正确。");
            foreach (var name in Names)
            {
                int value;
                if (!backup.values.TryGetValue(name, out value) || value < 0 || value > (name == "SELECTIONPREVIEW" ? 3 : 1))
                    throw new InvalidOperationException("预览备份包含无效变量或数值。");
            }
            return backup;
        }
        private static void Save(Backup backup)
        {
            Directory.CreateDirectory(DiagnosticsLog.Folder);
            var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(backup));
                if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null); else File.Move(temporary, FilePath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private static void Set(string name, int value)
        { AcApplication.SetSystemVariable(name, Convert.ChangeType(value, AcApplication.GetSystemVariable(name).GetType(), CultureInfo.InvariantCulture)); }
        public static string Apply()
        {
            var previous = new Dictionary<string, int>();
            foreach (var name in Names) previous[name] = Convert.ToInt32(AcApplication.GetSystemVariable(name));
            var backup = Read();
            if (backup == null || !backup.active) backup = new Backup { schema = 1, active = true, values = previous };
            Save(backup); // Preserve the original values before changing anything.
            try { foreach (var name in Names) Set(name, 0); }
            catch { foreach (var pair in previous) try { Set(pair.Key, pair.Value); } catch { } throw; }
            return "已减少悬停、命令和填充实时预览。输入 AIPERFRESTORE 可恢复原设置。";
        }
        public static string Restore()
        {
            var backup = Read();
            if (backup == null || !backup.active) return "没有待恢复的预览设置。";
            foreach (var name in Names) Set(name, backup.values[name]);
            backup.active = false; Save(backup);
            return "已恢复优化前的 CAD 实时预览设置。";
        }
    }
}

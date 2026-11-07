using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace AutoCAD.AI.Assistant.Services
{
    internal static class StartupSettingsBackup
    {
        internal static readonly string[] Names = { "STARTUP", "STARTMODE", "FILEDIA", "CMDDIA", "MENUBAR", "STATUSBAR" };
        public static Dictionary<string, int> Read(string path)
        {
            if (new FileInfo(path).Length > 4096) throw new InvalidDataException("启动设置备份过大。");
            var raw = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
            if (raw == null || raw.Count != Names.Length) throw new InvalidDataException("启动设置备份格式无效。");
            var values = new Dictionary<string, int>();
            foreach (var name in Names)
            {
                object value;
                if (!raw.TryGetValue(name, out value) || !(value is int) || (int)value < 0 || (int)value > (name == "STARTUP" ? 3 : 1))
                    throw new InvalidDataException("启动设置备份含有无效变量或数值。");
                values[name] = (int)value;
            }
            return values;
        }
        public static void Ensure(string path, Dictionary<string, int> current)
        {
            if (File.Exists(path)) { Read(path); return; }
            if (current == null || current.Count != Names.Length) throw new InvalidDataException("启动设置项数错误。");
            foreach (var name in Names)
                if (!current.ContainsKey(name) || current[name] < 0 || current[name] > (name == "STARTUP" ? 3 : 1))
                    throw new InvalidDataException("启动设置数值无效。");
            AtomicFile.Write(path, new JavaScriptSerializer().Serialize(current));
        }
    }
}

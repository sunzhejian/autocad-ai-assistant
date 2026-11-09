using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using System.Threading.Tasks;
using AutoCAD.AI.Assistant.Models;

namespace AutoCAD.AI.Assistant.Services
{
    /// <summary>记录 CAD 命令使用次数，按次数或字母排序，高频指令自动加入指令集。</summary>
    internal static class ShortcutCatalog
    {
        private const int AutoAddThreshold = 3;
        private const int MaxItems = 45;
        private static readonly object Sync = new object();
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();
        private static string FilePath { get { return Path.Combine(DiagnosticsLog.Folder, "shortcuts.json"); } }
        private static readonly BackgroundWork Writer = new BackgroundWork(2000);

        private static readonly HashSet<string> IgnoredCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AI", "UNDO", "U", "REDO", "ZOOM", "-ZOOM", "PAN", "-PAN", "REGEN", "REGENALL", "REDRAW", "REDRAWALL",
            "QUIT", "NEW", "OPEN", "SAVE", "QSAVE", "SAVEAS", "CLOSE", "PLOT", "PREVIEW", "OPTIONS", "DSETTINGS",
            "HELP", "PROPERTIES", "LAYER", "-LAYER", "PURGE", "AUDIT", "GRID", "SNAP", "ORTHO", "DYNMODE", "OSMODE",
            "MENUBAR", "RIBBON", "TOOLBAR", "-TOOLBAR", "CUI", "UNITS", "-UNITS", "LIMITS", "LTSCALE",
            "DIMSTYLE", "-DIMSTYLE", "STYLE", "-STYLE", "EXPORTPDF", "PUBLISH", "DWGPROPS", "SELECTIONCYCLING",
            "TEMPOVERRIDES", "CLIPROMPTLINES", "COMMANDLINE", "COMMANDLINEHIDE", "STATUSBAR",
            "SETVAR", "DXFIN", "DXFOUT", "FILEDIA", "SCRIPT", "LOAD", "APPLOAD", "REINIT", "MENU", "-MENU"
        };

        private static readonly Dictionary<string, string> KnownCommands = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "LINE", "直线" }, { "PLINE", "多段线" }, { "CIRCLE", "圆" }, { "RECTANG", "矩形" }, { "ARC", "圆弧" },
            { "MOVE", "移动" }, { "COPY", "复制" }, { "OFFSET", "偏移" }, { "TRIM", "修剪" }, { "EXTEND", "延伸" },
            { "ERASE", "删除" }, { "MATCHPROP", "特性匹配" }, { "ROTATE", "旋转" }, { "SCALE", "缩放" },
            { "MIRROR", "镜像" }, { "ARRAY", "阵列" }, { "FILLET", "圆角" }, { "CHAMFER", "倒角" },
            { "HATCH", "图案填充" }, { "GRADIENT", "渐变填充" }, { "SPLINE", "样条曲线" }, { "ELLIPSE", "椭圆" },
            { "POLYGON", "正多边形" }, { "POINT", "点" }, { "DIVIDE", "定数等分" }, { "MEASURE", "定距等分" }, { "DONUT", "圆环" }, { "SOLID", "实心区域" },
            { "STRETCH", "拉伸" }, { "LENGTHEN", "拉长" }, { "BREAK", "打断" }, { "JOIN", "合并" },
            { "EXPLODE", "分解" }, { "BLOCK", "创建块" }, { "INSERT", "插入块" }, { "WBLOCK", "写块" },
            { "BEDIT", "块编辑" }, { "XLINE", "构造线" }, { "RAY", "射线" }, { "MLINE", "多线" },
            { "TEXT", "单行文字" }, { "MTEXT", "多行文字" }, { "DDEDIT", "编辑文字" }, { "STYLE", "文字样式" },
            { "DIMLINEAR", "线性标注" }, { "DIMALIGNED", "对齐标注" }, { "DIMANGULAR", "角度标注" },
            { "DIMRADIUS", "半径标注" }, { "DIMDIAMETER", "直径标注" }, { "DIMBASELINE", "基线标注" },
            { "DIMCONTINUE", "连续标注" }, { "DIMSTYLE", "标注样式" }, { "QLEADER", "快速引线" },
            { "MLEADER", "多重引线" }, { "TOLERANCE", "公差" }, { "TABLE", "表格" }, { "FIELD", "字段" },
            { "GROUP", "编组" }, { "IMAGECLIP", "图像剪裁" }, { "IMAGEATTACH", "附着图像" },
            { "DIST", "距离" }, { "AREA", "面积" }, { "LIST", "列表" }, { "ID", "点坐标" },
            { "OVERKILL", "消除重线" }, { "FLATTEN", "压平" }, { "XREF", "外部参照" },
            { "VIEWPORT", "视口" }, { "MVIEW", "布局视口" }, { "PSLTSCALE", "布局线型比例" },
            { "CHSPACE", "空间转换" }, { "ALIGN", "对齐" }, { "3DARRAY", "三维阵列" },
            { "PDFIMPORT", "PDF导入" }, { "DWGCOMPARE", "图形比较" }
        };

        private static List<CadShortcut> _items;
        private static string _sortMode = "count";
        private static long _revision;
        private static long _savedRevision;
        private static bool _recordUsage;

        public static bool UsageTrackingEnabled { get { EnsureLoaded(); lock (Sync) return _recordUsage; } }
        public static void SetUsageTracking(bool enabled)
        {
            EnsureLoaded();
            lock (Sync) { if (_recordUsage == enabled) return; _recordUsage = enabled; _revision++; }
            Writer.Request(SaveCurrent);
            var handler = UsageTrackingChanged; if (handler != null) handler();
            RaiseChanged();
        }

        public static event Action Changed;
        public static event Action UsageTrackingChanged;

        public static string SortMode
        {
            get { EnsureLoaded(); return _sortMode; }
        }

        public static List<CadShortcut> GetSorted()
        {
            EnsureLoaded();
            lock (Sync)
            {
                var copy = new List<CadShortcut>();
                foreach (var item in _items)
                {
                    if (item == null) continue;
                    if (!item.BuiltIn && item.UsageCount < AutoAddThreshold) continue;
                    copy.Add(Clone(item));
                }
                if (_sortMode == "name") copy.Sort(CompareByName);
                else copy.Sort(CompareByUsage);
                return copy;
            }
        }

        public static void SetSortMode(string mode)
        {
            EnsureLoaded();
            if (mode != "name") mode = "count";
            lock (Sync) { _sortMode = mode; _revision++; }
            Writer.Request(SaveCurrent);
            RaiseChanged();
        }

        public static void RecordCommand(string globalCommandName)
        {
            var key = Normalize(globalCommandName);
            if (key.Length == 0 || IgnoredCommands.Contains(key)) return;
            if (key.StartsWith("(", StringComparison.Ordinal) || key.StartsWith("*", StringComparison.Ordinal)) return;
            if (key.Length > 24) return;
            if (key.IndexOf('_') >= 0) return;
            if (key.StartsWith("CMPH", StringComparison.OrdinalIgnoreCase)) return;
            if (key.StartsWith("LOG", StringComparison.OrdinalIgnoreCase)) return;
            foreach (var character in key)
            {
                if (character < 'A' || character > 'Z') return;
            }
            if (key.Length > 10 && !KnownCommands.ContainsKey(key)) return;

            EnsureLoaded();
            lock (Sync)
            {
                if (!_recordUsage) return;
                var item = FindByCommand(key);
                if (item == null)
                {
                    var existing = FindByCommandName(key);
                    if (existing != null) item = existing;
                }

                if (item == null)
                {
                    if (_items.Count >= MaxItems) return;
                    item = new CadShortcut
                    {
                        Name = key,
                        Description = KnownCommands.ContainsKey(key) ? KnownCommands[key] : "自定义",
                        Command = "_." + key + " ",
                        UsageCount = 0,
                        BuiltIn = false
                    };
                    _items.Add(item);
                }

                item.UsageCount++;
                item.LastUsedTicks = DateTime.Now.Ticks;
                _revision++;
            }
            Writer.Request(SaveCurrent);
            RaiseChanged();
        }

        public static void Flush()
        {
            Writer.Request(SaveCurrent);
        }
        public static Task FlushAsync() { Flush(); return Writer.FlushAsync(); }
        public static string PersistenceError { get { return Writer.LastError; } }

        private static void EnsureLoaded()
        {
            if (_items != null) return;
            lock (Sync)
            {
                if (_items != null) return;
                _items = Load();
            }
        }

        private static List<CadShortcut> Load()
        {
            var defaults = CreateDefaults();
            List<CadShortcut> stored = null;
            string sort = "count";
            try
            {
                if (File.Exists(FilePath))
                {
                    var file = Serializer.Deserialize<ShortcutFile>(File.ReadAllText(FilePath));
                    if (file != null)
                    {
                        _recordUsage = file.recordUsage;
                        stored = file.items;
                        if (!string.IsNullOrEmpty(file.sort)) sort = file.sort;
                    }
                }
            }
            catch
            {
            }
            _sortMode = sort == "name" ? "name" : "count";

            var result = new List<CadShortcut>();
            if (stored != null)
            {
                foreach (var item in stored)
                {
                    if (item == null || string.IsNullOrEmpty(item.Command)) continue;
                    if (FindByCommandIn(result, item.Command) != null) continue;
                    if (result.Count < MaxItems) result.Add(item);
                }
            }
            foreach (var item in defaults)
            {
                var existing = FindByCommandIn(result, item.Command);
                if (existing != null)
                {
                    existing.BuiltIn = true;
                    if (string.IsNullOrEmpty(existing.Name)) existing.Name = item.Name;
                    if (string.IsNullOrEmpty(existing.Description)) existing.Description = item.Description;
                    continue;
                }
                result.Add(item);
            }
            return result;
        }

        private static List<CadShortcut> CreateDefaults()
        {
            return new List<CadShortcut>
            {
                New("L", "直线", "_.LINE "),
                New("PL", "多段线", "_.PLINE "),
                New("C", "圆", "_.CIRCLE "),
                New("REC", "矩形", "_.RECTANG "),
                New("A", "圆弧", "_.ARC "),
                New("M", "移动", "_.MOVE "),
                New("CO", "复制", "_.COPY "),
                New("O", "偏移", "_.OFFSET "),
                New("TR", "修剪", "_.TRIM "),
                New("EX", "延伸", "_.EXTEND "),
                New("E", "删除", "_.ERASE "),
                New("MA", "特性匹配", "_.MATCHPROP "),
                New("H", "图案填充", "_.HATCH "),
                New("DLI", "线性标注", "_.DIMLINEAR "),
                New("DAL", "对齐标注", "_.DIMALIGNED "),
                New("Z", "缩放", "_.ZOOM "),
                New("P", "平移", "_.PAN "),
                New("U", "放弃", "_.U ")
            };
        }

        private static CadShortcut New(string name, string description, string command)
        {
            var item = new CadShortcut { Name = name, Description = description, Command = command, UsageCount = 0, BuiltIn = true };
            item.LastUsedTicks = 0;
            return item;
        }

        private static int CompareByUsage(CadShortcut a, CadShortcut b)
        {
            if (a.UsageCount != b.UsageCount) return b.UsageCount - a.UsageCount;
            if (a.LastUsedTicks != b.LastUsedTicks) return b.LastUsedTicks.CompareTo(a.LastUsedTicks);
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        }

        private static int CompareByName(CadShortcut a, CadShortcut b)
        {
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        }

        private static CadShortcut FindByCommand(string normalized)
        {
            foreach (var item in _items)
            {
                if (item == null || item.Command == null) continue;
                if (Normalize(item.Command) == normalized) return item;
            }
            return null;
        }

        private static CadShortcut FindByCommandName(string name)
        {
            foreach (var item in _items)
            {
                if (item != null && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)) return item;
            }
            return null;
        }

        private static CadShortcut FindByCommandIn(List<CadShortcut> list, string command)
        {
            var normalized = Normalize(command);
            foreach (var item in list)
            {
                if (item != null && item.Command != null && Normalize(item.Command) == normalized) return item;
            }
            return null;
        }

        private static string Normalize(string command)
        {
            if (string.IsNullOrEmpty(command)) return string.Empty;
            var text = command.Trim().ToUpperInvariant();
            while (text.StartsWith("_.", StringComparison.Ordinal) || text.StartsWith("-", StringComparison.Ordinal))
            {
                text = text.StartsWith("_.", StringComparison.Ordinal) ? text.Substring(2).Trim() : text.Substring(1).Trim();
            }
            if (text.EndsWith(";", StringComparison.Ordinal)) text = text.Substring(0, text.Length - 1).Trim();
            return text;
        }

        private static void SaveCurrent()
        {
            string json; long revision;
            lock (Sync)
            {
                if (_items == null || _revision == _savedRevision) return;
                revision = _revision;
                var snapshot = new List<CadShortcut>(); foreach (var item in _items) snapshot.Add(Clone(item));
                json = Serializer.Serialize(new ShortcutFile { sort = _sortMode, items = snapshot, recordUsage = _recordUsage });
            }
            var folder = Path.GetDirectoryName(FilePath);
            Directory.CreateDirectory(folder);
            var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, json);
                if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null); else File.Move(temporary, FilePath);
                lock (Sync) _savedRevision = revision;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static CadShortcut Clone(CadShortcut item)
        {
            return new CadShortcut { Name = item.Name, Description = item.Description, Command = item.Command,
                UsageCount = item.UsageCount, LastUsedTicks = item.LastUsedTicks, BuiltIn = item.BuiltIn };
        }

        private static void RaiseChanged()
        {
            var handler = Changed;
            if (handler != null) handler();
        }

        private sealed class ShortcutFile
        {
            public string sort { get; set; }
            public List<CadShortcut> items { get; set; }
            public bool recordUsage { get; set; }
        }
    }
}

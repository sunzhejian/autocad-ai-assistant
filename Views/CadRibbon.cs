using System;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Windows;
using AutoCAD.AI.Assistant.Services;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(AutoCAD.AI.Assistant.Views.CadRibbon))]

namespace AutoCAD.AI.Assistant.Views
{
    public sealed class CadRibbon
    {
        private const string TabId = "CADAI_DRAWING_TOOLS";
        private static readonly ICommand Handler = new NativeCommand();
        [CommandMethod("AIUITAB", CommandFlags.Modal)]
        public void Show()
        {
            if (!CadStartupUi.IsDesktopHost) return;
            var ribbon = ComponentManager.Ribbon;
            if (ribbon == null) return;
            RibbonTab tab = null;
            foreach (var item in ribbon.Tabs) if (item.Id == TabId) { tab = item; break; }
            if (tab == null)
            {
                tab = new RibbonTab { Id = TabId, Title = "AI 制图" };
                AddPanel(tab, "AI 助手", "AI 助手 / 图纸复刻", "AI", "执行本地计划", "AIPLAN");
                AddPanel(tab, "绘图与填充", "直线", "_.LINE", "多段线", "_.PLINE", "矩形", "_.RECTANG", "圆", "_.CIRCLE", "图案填充", "_.HATCH", "编辑填充", "_.HATCHEDIT");
                AddPanel(tab, "曲线与注释", "圆弧", "_.ARC", "椭圆", "_.ELLIPSE", "样条曲线", "_.SPLINE", "圆环", "_.DONUT", "实心区域", "_.SOLID", "多重引线", "_.MLEADER");
                AddPanel(tab, "修改", "移动", "_.MOVE", "复制", "_.COPY", "偏移", "_.OFFSET", "修剪", "_.TRIM", "圆角", "_.FILLET", "镜像", "_.MIRROR");
                AddPanel(tab, "标注与图层", "线性标注", "_.DIMLINEAR", "对齐标注", "_.DIMALIGNED", "半径标注", "_.DIMRADIUS", "角度标注", "_.DIMANGULAR", "直径标注", "_.DIMDIAMETER", "图层管理", "_.LAYER", "对象特性", "_.PROPERTIES", "快速选择", "_.QSELECT");
                AddPanel(tab, "视图与性能", "显示全图", "_.ZOOM _E", "平移", "_.PAN", "减少实时预览", "AIPERF", "恢复实时预览", "AIPERFRESTORE", "恢复完整界面", "AIUI", "关闭启动整理", "AIUIOFF", "恢复启动偏好", "AIUIRESTORE");
                ribbon.Tabs.Add(tab);
            }
            ribbon.AutoHideMode = Autodesk.Internal.Windows.RibbonAutoHideMode.None;
            tab.IsVisible = true; tab.IsActive = true;
        }
        private static void AddPanel(RibbonTab tab, string title, params string[] entries)
        {
            var source = new RibbonPanelSource { Title = title };
            var panel = new RibbonPanel { Source = source };
            for (int i = 0; i < entries.Length; i += 6)
            {
                var column = new RibbonRowPanel();
                for (int j = i; j < Math.Min(i + 6, entries.Length); j += 2)
                {
                    if (j != i) column.Items.Add(new RibbonRowBreak());
                    column.Items.Add(new RibbonButton { Text = entries[j], ShowText = true, ShowImage = true,
                        Image = Icon(entries[j + 1]), MinWidth = title == "AI 助手" ? 150 : 112,
                        CommandParameter = entries[j + 1], CommandHandler = Handler, ToolTip = entries[j] });
                }
                source.Items.Add(column);
            }
            tab.Panels.Add(panel);
        }
        private static ImageSource Icon(string command)
        {
            string path;
            switch (command)
            {
                case "_.LINE": path = "M2,14 L14,2 M1,12 L4,15 M12,1 L15,4"; break;
                case "_.PLINE": path = "M2,13 L5,3 L10,10 L14,2"; break;
                case "_.RECTANG": path = "M2,3 L14,3 L14,13 L2,13 Z"; break;
                case "_.CIRCLE": path = "M8,2 A6,6 0 1 1 7.99,2 Z"; break;
                case "_.HATCH": case "_.HATCHEDIT": path = "M2,2 L14,2 L14,14 L2,14 Z M2,8 L8,2 M2,14 L14,2 M8,14 L14,8"; break;
                case "_.MOVE": case "_.PAN": path = "M8,1 L8,15 M1,8 L15,8 M5,4 L8,1 L11,4 M5,12 L8,15 L11,12 M4,5 L1,8 L4,11 M12,5 L15,8 L12,11"; break;
                case "_.COPY": path = "M2,2 L10,2 L10,10 L2,10 Z M6,6 L14,6 L14,14 L6,14 Z"; break;
                case "_.OFFSET": path = "M2,14 L2,2 L14,2 M6,14 L6,6 L14,6 M10,14 L10,10 L14,10"; break;
                case "_.TRIM": path = "M2,3 L14,13 M2,13 L14,3 M8,1 L8,15"; break;
                case "_.FILLET": path = "M2,14 L2,8 Q2,2 8,2 L14,2"; break;
                case "_.MIRROR": path = "M8,1 L8,15 M2,12 L2,4 L6,12 Z M14,12 L14,4 L10,12 Z"; break;
                case "_.LAYER": path = "M1,5 L8,1 L15,5 L8,9 Z M1,8 L8,12 L15,8 M1,11 L8,15 L15,11"; break;
                case "_.ZOOM _E": path = "M7,1 A6,6 0 1 1 6.99,1 Z M11,11 L15,15 M4,7 L10,7 M7,4 L7,10"; break;
                case "AIPERF": case "AIPERFRESTORE": path = "M9,1 L3,9 L8,9 L6,15 L14,6 L9,6 Z"; break;
                case "AI": path = "M3,5 L13,5 L13,13 L3,13 Z M8,1 L8,5 M5,8 L5,9 M11,8 L11,9 M5,11 L11,11 M1,7 L3,7 M13,7 L15,7"; break;
                default: path = command.StartsWith("_.DIM", StringComparison.Ordinal)
                    ? "M2,2 L2,14 M14,2 L14,14 M2,10 L14,10 M5,7 L2,10 L5,13 M11,7 L14,10 L11,13"
                    : "M3,1 L11,1 L14,4 L14,15 L3,15 Z M6,6 L11,6 M6,9 L11,9 M6,12 L11,12"; break;
            }
            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
            group.Children.Add(new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromRgb(119, 199, 250)), 1.3), Geometry.Parse(path)));
            var image = new DrawingImage(group); image.Freeze(); return image;
        }
        private sealed class NativeCommand : ICommand
        {
            public bool CanExecute(object parameter) { return true; }
            public event EventHandler CanExecuteChanged { add { } remove { } }
            public void Execute(object parameter)
            {
                var doc = AcApplication.DocumentManager.MdiActiveDocument;
                if (doc == null) return;
                if (!doc.Editor.IsQuiescent)
                { doc.Editor.WriteMessage("\n请先完成当前命令或按 Esc，再选择工具。\n"); return; }
                // AdWindows passes the clicked RibbonButton to ICommand, rather
                // than always passing its CommandParameter as WPF normally does.
                var button = parameter as RibbonButton;
                var command = Convert.ToString(button == null ? parameter : button.CommandParameter);
                if (!String.IsNullOrWhiteSpace(command)) doc.SendStringToExecute(command + " ", true, false, true);
            }
        }
    }
}

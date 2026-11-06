using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace AutoCAD.AI.Assistant.Services
{
    internal static class CadDrawingContext
    {
        public static string Read(Document document, bool allowCommandRead = false)
        {
            if (document == null) return "当前没有活动图形。";
            var db = document.Database;
            var text = new StringBuilder("图形=" + Path.GetFileName(document.Name) + "；单位=" + db.Insunits);
            if (!allowCommandRead && !String.IsNullOrEmpty(document.CommandInProgress))
                return text + "；当前命令执行中，未扫描图元。";
            var timer = Stopwatch.StartNew();
            var seen = 0; var sampled = 0; var boxAttempts = 0; var truncated = false;
            var types = new Dictionary<string, int>();
            Autodesk.AutoCAD.DatabaseServices.Extents3d? bounds = null;
            using (document.LockDocument())
            using (var tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var space = (BlockTableRecord)tr.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    if (id.IsErased) continue;
                    seen++;
                    if (seen > 512 || timer.ElapsedMilliseconds >= 25) { truncated = true; break; }
                    try
                    {
                        var entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (entity == null) continue;
                        sampled++;
                        var name = entity.GetType().Name;
                        types[name] = types.ContainsKey(name) ? types[name] + 1 : 1;
                        // Extents of a single complex block can recursively traverse
                        // its entire definition. Sample only inexpensive primitives.
                        if (boxAttempts >= 128 || !(entity is Line || entity is Circle || entity is Polyline || entity is Arc || entity is DBPoint)) continue;
                        var polyline = entity as Polyline;
                        if (polyline != null && polyline.NumberOfVertices > 256) continue;
                        boxAttempts++;
                        var box = entity.GeometricExtents;
                        if (bounds.HasValue) { var combined = bounds.Value; combined.AddExtents(box); bounds = combined; }
                        else bounds = box;
                    }
                    catch { }
                }
                text.Append("；模型空间图元" + (truncated ? "≥" : "=") + seen + "；类型抽样=" + sampled + "（");
                var parts = new List<string>(); foreach (var pair in types) parts.Add(pair.Key + " " + pair.Value);
                text.Append(String.Join("、", parts.ToArray()) + "）");
                if (bounds.HasValue)
                {
                    var b = bounds.Value;
                    text.Append(String.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "；简单图元抽样范围=({0:0.##},{1:0.##})~({2:0.##},{3:0.##})", b.MinPoint.X, b.MinPoint.Y, b.MaxPoint.X, b.MaxPoint.Y));
                }
                var layers = new List<string>(); var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in layerTable)
                {
                    if (layers.Count >= 20) break;
                    layers.Add(((LayerTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name);
                }
                text.Append("；图层抽样=" + String.Join(",", layers.ToArray()));
                text.Append("；当前层=" + ((LayerTableRecord)tr.GetObject(db.Clayer, OpenMode.ForRead)).Name + "；DIMSCALE=" + db.Dimscale);
            }
            DiagnosticsLog.Write("上下文扫描 " + timer.ElapsedMilliseconds + "ms 类型抽样=" + sampled + " 范围读取=" + boxAttempts + " 截断=" + truncated);
            return text.ToString();
        }
    }
}

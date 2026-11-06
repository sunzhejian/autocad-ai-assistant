using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoCAD.AI.Assistant.Services
{
    internal static class PdfDrawingImporter
    {
        public static string Execute(Document document, PdfPageSource source, double paperScale)
        {
            source.VerifySnapshot();
            if (Double.IsNaN(paperScale) || Double.IsInfinity(paperScale) || paperScale <= 0 || paperScale > 100000)
                throw new InvalidOperationException("纸面放大倍数须大于 0 且不超过 100000。");
            var database = document.Database;
            var perMm = 1.0;
            switch (database.Insunits)
            {
                case UnitsValue.Undefined: case UnitsValue.Millimeters: break;
                case UnitsValue.Centimeters: perMm = .1; break;
                case UnitsValue.Meters: perMm = .001; break;
                case UnitsValue.Inches: perMm = 1 / 25.4; break;
                case UnitsValue.Feet: perMm = 1 / 304.8; break;
                default: throw new InvalidOperationException("当前单位不受支持，请先设置毫米、厘米、米、英寸或英尺。");
            }
            var old = new Dictionary<string, object>();
            foreach (var name in new[] { "FILEDIA", "CMDECHO", "PDFIMPORTMODE", "PDFIMPORTFILTER", "PDFIMPORTIMAGEPATH", "MEASUREMENT", "CTAB", "CLAYER" })
                old[name] = AcApplication.GetSystemVariable(name);
            var before = new HashSet<ObjectId>(ModelEntities(database));
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                AcApplication.SetSystemVariable("FILEDIA", 0);
                AcApplication.SetSystemVariable("CMDECHO", 0);
                AcApplication.SetSystemVariable("PDFIMPORTMODE", 15);
                AcApplication.SetSystemVariable("PDFIMPORTFILTER", 0);
                AcApplication.SetSystemVariable("MEASUREMENT", 1);
                var imagesFolder = Path.Combine(Path.GetDirectoryName(source.pdf), "images");
                Directory.CreateDirectory(imagesFolder);
                AcApplication.SetSystemVariable("PDFIMPORTIMAGEPATH", imagesFolder);
                AcApplication.SetSystemVariable("CTAB", "Model");
                // Native PDF geometry uses inches (72 PDF points per drawing unit),
                // independently of INSUNITS. Convert explicitly to the target unit.
                document.Editor.Command("_.-PDFIMPORT", "_F", source.pdf, 1, Point3d.Origin, 25.4 * paperScale * perMm, 0.0);
                var created = new List<ObjectId>();
                foreach (var id in ModelEntities(database)) if (!before.Contains(id)) created.Add(id);
                var records = new List<object>(); var visible = 0; var entities = 0; var hatches = 0;
                Extents3d? importedBounds = null;
                using (var tr = database.TransactionManager.StartTransaction())
                {
                    var pending = new Queue<ObjectId>(created);
                    var visitedBlocks = new HashSet<ObjectId>();
                    while (pending.Count > 0)
                    {
                        var id = pending.Dequeue();
                        if (!id.IsValid || id.IsErased) continue;
                        var entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (entity == null) continue;
                        var owner = tr.GetObject(entity.OwnerId, OpenMode.ForRead) as BlockTableRecord;
                        if (owner == null || owner.IsAnonymous) continue;
                        if (String.Equals(owner.Name, BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase))
                        { visible++; importedBounds = entity.GeometricExtents; }
                        entities++; if (entity is Hatch) hatches++;
                        records.Add(new { handle = entity.Handle.ToString(), type = entity.GetType().Name, space = owner.Name, layer = entity.Layer });
                        var reference = entity as BlockReference;
                        if (reference != null && visitedBlocks.Add(reference.BlockTableRecord))
                        {
                            var block = (BlockTableRecord)tr.GetObject(reference.BlockTableRecord, OpenMode.ForRead);
                            foreach (ObjectId child in block) pending.Enqueue(child);
                        }
                    }
                    if (visible != 1 || entities <= 1) throw new InvalidOperationException("原生 PDF 导入未生成一个有效图形块（新增=" + created.Count + "，模型=" + visible + "，对象=" + entities + "），已移除本次新增图元。");
                    tr.Commit();
                }
                if (database.Insunits == UnitsValue.Undefined) database.Insunits = UnitsValue.Millimeters;
                try { CadViewRefresh.Refresh(document, importedBounds, false); } catch { }
                var receiptPath = Path.Combine(DiagnosticsLog.Folder, "receipts", "pdf_" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(receiptPath));
                    File.WriteAllText(receiptPath, new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Serialize(new {
                        schema = 1, route = "native_pdf", source_hash = source.source_hash, page_hash = source.pdf_hash,
                        source_page = source.page, paper_scale = paperScale, drawing_units = database.Insunits.ToString(),
                        entities = entities, hatches = hatches, milliseconds = timer.ElapsedMilliseconds,
                        source_accuracy_verified = false, objects = records
                    }), new UTF8Encoding(false));
                }
                catch (Exception ex) { receiptPath = null; DiagnosticsLog.Error("PDF 回执", ex); }
                DiagnosticsLog.Write("原生PDF导入 对象=" + entities + " 用时ms=" + timer.ElapsedMilliseconds + " 回执=" + receiptPath);
                return "PDF 第 " + source.page + " 页已导入为块：" + entities + " 个图元，填充 " + hatches
                    + "，用时 " + (timer.ElapsedMilliseconds / 1000.0).ToString("0.##") + " 秒。U 撤销。纸面放大 " + paperScale
                    + " 倍；需核对各视图比例，PDF 文字标注未转为智能尺寸。" + (receiptPath == null ? " 回执保存失败。" : " 回执已保存。");
            }
            catch
            {
                using (var tr = database.TransactionManager.StartTransaction())
                {
                    foreach (var id in ModelEntities(database))
                    {
                        try { if (!before.Contains(id) && id.IsValid && !id.IsErased) tr.GetObject(id, OpenMode.ForWrite).Erase(); } catch { }
                    }
                    tr.Commit();
                }
                throw;
            }
            finally
            {
                foreach (var pair in old) try { AcApplication.SetSystemVariable(pair.Key, pair.Value); } catch (Exception ex) { DiagnosticsLog.Error("恢复 " + pair.Key, ex); }
            }
        }

        private static List<ObjectId> ModelEntities(Database database)
        {
            using (var tr = database.TransactionManager.StartOpenCloseTransaction())
            {
                var table = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var space = (BlockTableRecord)tr.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                var result = new List<ObjectId>(); foreach (ObjectId id in space) result.Add(id); return result;
            }
        }
    }
}

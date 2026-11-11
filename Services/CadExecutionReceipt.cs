using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.DatabaseServices;

namespace AutoCAD.AI.Assistant.Services
{
    internal sealed class CadExecutionReceipt : IDisposable
    {
        private readonly Database _database;
        [ThreadStatic] private static CadExecutionReceipt _active;
        private readonly CadExecutionReceipt _previous;
        private readonly Dictionary<ObjectId, string> _objects = new Dictionary<ObjectId, string>();
        private readonly Dictionary<string, Dictionary<string, object>> _commands = new Dictionary<string, Dictionary<string, object>>();
        private readonly List<object> _records = new List<object>();
        private string _current;
        public int Dimensions { get; private set; }
        public int Hatches { get; private set; }
        public int ObjectCount { get { return _records.Count; } }
        public Extents3d? ModelBounds { get; private set; }
        public CadExecutionReceipt(Database database) { _database = database; _previous = _active; _active = this; }
        public void Begin(string path, Dictionary<string, object> command) { _current = path; _commands[path] = command; }
        internal static void Record(Entity entity)
        {
            var receipt = _active;
            if (receipt != null && receipt._current != null && entity.Database == receipt._database)
                receipt._objects[entity.ObjectId] = receipt._current;
        }
        public void Inspect(Transaction transaction)
        {
            var checkedPaths = new HashSet<string>();
            // Snapshot: RecomputeDimensionBlock may append internal objects.
            foreach (var pair in new List<KeyValuePair<ObjectId, string>>(_objects))
            {
                if (pair.Key.IsErased || !pair.Key.IsValid) continue;
                var entity = transaction.GetObject(pair.Key, OpenMode.ForRead) as Entity;
                if (entity == null) continue;
                try
                {
                    var owner = transaction.GetObject(entity.OwnerId, OpenMode.ForRead) as BlockTableRecord;
                    if (owner == null || owner.IsAnonymous) continue;
                    var record = new Dictionary<string, object> {
                        { "command", pair.Value }, { "handle", entity.Handle.ToString() }, { "type", entity.GetType().Name },
                        { "layer", entity.Layer }, { "space", owner.Name }
                    };
                    var command = _commands[pair.Value];
                    var dimension = entity as Dimension;
                    if (dimension != null)
                    {
                        dimension.RecomputeDimensionBlock(true);
                        var actual = dimension.Measurement;
                        if (dimension is Point3AngularDimension)
                        {
                            record["native_measurement_radians"] = actual;
                            actual *= 180 / Math.PI; record["measurement_unit"] = "deg";
                        }
                        if (Double.IsNaN(actual) || Double.IsInfinity(actual) || actual <= 0)
                            throw new InvalidOperationException("原生标注测量失败：第 " + pair.Value + " 条。");
                        record["measurement"] = actual; record["unit_factor"] = dimension.Dimlfac;
                        record["text"] = dimension.DimensionText;
                        record["dimension_scale"] = dimension.Dimscale;
                        var sizes = command.ContainsKey("_modelDimensionSizes") ? command["_modelDimensionSizes"] as Dictionary<string, double> : null;
                        if (sizes != null)
                        {
                            var paper = new Dictionary<string, double>();
                            var model = new Dictionary<string, double>();
                            foreach (var expectedSize in sizes)
                            {
                                var value = DimensionLength(dimension, expectedSize.Key);
                                paper[expectedSize.Key] = value;
                                var actualSize = value * dimension.Dimscale;
                                model[expectedSize.Key] = actualSize;
                                if (Double.IsNaN(actualSize) || Double.IsInfinity(actualSize)
                                    || Math.Abs(actualSize - expectedSize.Value) > Math.Max(1e-8, Math.Abs(expectedSize.Value) * 1e-8))
                                    throw new InvalidOperationException("原生标注格式长度重复缩放或不一致：" + expectedSize.Key);
                            }
                            record["dimension_lengths_paper_mm"] = paper;
                            record["dimension_lengths_model_mm"] = model;
                        }
                        if (command.ContainsKey("value"))
                        {
                            var expected = CadPlanQuality.Number(command["value"]);
                            record["expected"] = expected;
                            if (Math.Abs(actual - expected) > Math.Max(.01, expected * .00001))
                                throw new InvalidOperationException("原生标注与计划读数不一致：第 " + pair.Value + " 条，实际=" + actual + "，计划=" + expected);
                        }
                        Dimensions++; checkedPaths.Add(pair.Value);
                    }
                    var hatch = entity as Hatch;
                    if (hatch != null)
                    {
                        var expectedArea = Area((object[])command["points"]);
                        var holes = command.ContainsKey("holes") ? command["holes"] as object[] : null;
                        if (holes != null) foreach (object[] hole in holes) expectedArea -= Area(hole);
                        var actualArea = hatch.Area;
                        if (expectedArea <= 0 || Double.IsNaN(actualArea) || Double.IsInfinity(actualArea)
                            || Math.Abs(actualArea - expectedArea) > Math.Max(.001, expectedArea * .00001)
                            || hatch.NumberOfLoops != 1 + (holes == null ? 0 : holes.Length))
                            throw new InvalidOperationException("原生填充面积或内孔与计划不一致：第 " + pair.Value + " 条。");
                        record["area_mm2"] = actualArea; record["expected_area_mm2"] = expectedArea;
                        record["loops"] = hatch.NumberOfLoops; record["pattern"] = hatch.PatternName;
                        record["pattern_scale"] = hatch.PatternScale;
                        Hatches++; checkedPaths.Add(pair.Value);
                    }
                    _records.Add(record);
                    if (String.Equals(owner.Name, BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var box = entity.GeometricExtents;
                            record["bounds_min_mm"] = new[] { box.MinPoint.X, box.MinPoint.Y, box.MinPoint.Z };
                            record["bounds_max_mm"] = new[] { box.MaxPoint.X, box.MaxPoint.Y, box.MaxPoint.Z };
                            if (ModelBounds.HasValue) { var combined = ModelBounds.Value; combined.AddExtents(box); ModelBounds = combined; }
                            else ModelBounds = box;
                        }
                        catch { record["bounds_available"] = false; }
                    }
                }
                catch (Exception ex) { throw new InvalidOperationException("原生校核第 " + pair.Value + " 条（" + entity.GetType().Name + "）：" + ex.Message, ex); }
            }
            foreach (var pair in _commands)
            {
                var type = Convert.ToString(pair.Value["type"]).ToLowerInvariant();
                if ((type.StartsWith("dim", StringComparison.Ordinal) || type == "hatch") && !checkedPaths.Contains(pair.Key))
                    throw new InvalidOperationException("原生结果缺少标注或填充：第 " + pair.Key + " 条。");
            }
        }
        private static double Area(object[] points)
        {
            var total = 0.0;
            for (var i = 0; i < points.Length; i++)
            {
                var a = CadPlanQuality.ReadPoint(points[i]); var b = CadPlanQuality.ReadPoint(points[(i + 1) % points.Length]);
                total += a[0] * b[1] - b[0] * a[1];
            }
            return Math.Abs(total) / 2;
        }
        private static double DimensionLength(Dimension dimension, string name)
        {
            switch(name)
            {
                case "DIMTXT": return dimension.Dimtxt;
                case "DIMASZ": return dimension.Dimasz;
                case "DIMTSZ": return dimension.Dimtsz;
                case "DIMEXO": return dimension.Dimexo;
                case "DIMEXE": return dimension.Dimexe;
                case "DIMGAP": return dimension.Dimgap;
                default: throw new InvalidOperationException("未知标注长度变量：" + name);
            }
        }
        public string Save(string plan, string name)
        {
            try
            {
                Directory.CreateDirectory(Path.Combine(DiagnosticsLog.Folder, "receipts"));
                var path = Path.Combine(DiagnosticsLog.Folder, "receipts", "receipt_" + Guid.NewGuid().ToString("N") + ".json");
                string hash;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(plan))).Replace("-", "").ToLowerInvariant();
                File.WriteAllText(path, new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.Serialize(new {
                    schema = 1, utc = DateTime.UtcNow.ToString("o"), drawing = Path.GetFileName(name), plan_sha256 = hash,
                    native_checks_passed = true, source_accuracy_verified = false, dimensions = Dimensions, hatches = Hatches, objects = _records
                }), new UTF8Encoding(false));
                return path;
            }
            catch (Exception ex) { DiagnosticsLog.Error("保存原生校核回执", ex); return null; }
        }
        public void Dispose() { _active = _previous; }
    }
}

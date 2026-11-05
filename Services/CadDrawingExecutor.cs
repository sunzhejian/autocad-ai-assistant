using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoCAD.AI.Assistant.Services
{
    internal sealed class CadDrawingExecutor
    {
        public static string ExtractPlan(string answer)
        {
            return CadPlanText.Extract(answer);
        }

        public static string RemovePlan(string answer)
        {
            return CadPlanText.Remove(answer);
        }

        public static string BuildContext()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return "当前没有活动图形。";
            var database = document.Database;
            var text = new StringBuilder();

            var summary = new Dictionary<string, int>();
            var total = 0;
            var minX = 0.0;
            var minY = 0.0;
            var maxX = 0.0;
            var maxY = 0.0;
            var hasBox = false;
            try
            {
                var table = (BlockTable)database.BlockTableId.GetObject(OpenMode.ForRead);
                var space = (BlockTableRecord)table[BlockTableRecord.ModelSpace].GetObject(OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    total++;
                    if (total > 20000) break;
                    if (total > 400) continue;
                    try
                    {
                        var entity = (Entity)id.GetObject(OpenMode.ForRead);
                        var name = entity.GetType().Name;
                        summary[name] = summary.ContainsKey(name) ? summary[name] + 1 : 1;
                        var box = entity.GeometricExtents;
                        if (!hasBox)
                        {
                            minX = box.MinPoint.X; minY = box.MinPoint.Y;
                            maxX = box.MaxPoint.X; maxY = box.MaxPoint.Y;
                            hasBox = true;
                        }
                        else
                        {
                            if (box.MinPoint.X < minX) minX = box.MinPoint.X;
                            if (box.MinPoint.Y < minY) minY = box.MinPoint.Y;
                            if (box.MaxPoint.X > maxX) maxX = box.MaxPoint.X;
                            if (box.MaxPoint.Y > maxY) maxY = box.MaxPoint.Y;
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            text.Append("图形=" + document.Name + "；单位=" + UnitName(database) + "；模型空间图元=" + total);
            if (summary.Count > 0)
            {
                var parts = new List<string>();
                foreach (var pair in summary)
                {
                    if (parts.Count >= 10) break;
                    parts.Add(pair.Key + " " + pair.Value);
                }
                text.Append("（" + string.Join("、", parts.ToArray()) + "）");
            }

            if (hasBox)
            {
                text.Append("；范围=(" + Round(minX) + "," + Round(minY) + ")~(" + Round(maxX) + "," + Round(maxY) + ")");
            }

            try
            {
                var layerTable = (LayerTable)database.LayerTableId.GetObject(OpenMode.ForRead);
                var layers = new List<string>();
                foreach (ObjectId id in layerTable)
                {
                    if (layers.Count >= 20) break;
                    var record = (LayerTableRecord)id.GetObject(OpenMode.ForRead);
                    layers.Add(record.Name);
                }
                text.Append("；图层=" + (layers.Count == 0 ? "无" : string.Join(",", layers.ToArray())));
            }
            catch
            {
            }

            try
            {
                var current = (LayerTableRecord)database.Clayer.GetObject(OpenMode.ForRead);
                text.Append("；当前层=" + current.Name);
            }
            catch
            {
            }

            try
            {
                text.Append("；DIMSCALE=" + Round(database.Dimscale));
            }
            catch
            {
            }

            return text.ToString();
        }

        private static string UnitName(Database database)
        {
            try
            {
                switch (database.Insunits)
                {
                    case UnitsValue.Millimeters: return "毫米";
                    case UnitsValue.Centimeters: return "厘米";
                    case UnitsValue.Meters: return "米";
                    case UnitsValue.Inches: return "英寸";
                    case UnitsValue.Feet: return "英尺";
                    default: return database.Insunits.ToString();
                }
            }
            catch
            {
                return "未知";
            }
        }

        private static string Round(double value)
        {
            return Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);
        }

        public string Execute(string json, out string message)
        {
            var validation = CadPlanValidator.Validate(json);
            if (validation != null) { message = "计划校验失败：" + validation; return null; }
            json = CadPlanNormalizer.Normalize(json);
            validation = CadPlanValidator.Validate(json);
            if (validation != null) { message = "单位换算后校验失败：" + validation; return null; }
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var serializer = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
            Dictionary<string, object> root;
            try
            {
                root = serializer.DeserializeObject(json) as Dictionary<string, object>;
            }
            catch (Exception ex)
            {
                message = "绘图指令不是合法 JSON：" + ex.Message;
                return null;
            }

            var commands = root == null ? null : root.ContainsKey("commands") ? root["commands"] as object[] : null;
            if (commands == null || commands.Length == 0)
            {
                message = "AI 返回的绘图指令为空。";
                return null;
            }

            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                message = "没有活动图形，无法绘图。";
                return null;
            }

            var created = 0;
            var applied = 0;
            var errors = new List<string>();
            var layers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Database database = document.Database;
            var previousVariables = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var variable in CadPlanValidator.DrawingVariables)
            {
                try { previousVariables[variable] = AcApplication.GetSystemVariable(variable); } catch { }
            }

            using (document.LockDocument())
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                try
                {
                    var table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                    var space = (BlockTableRecord)transaction.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    var layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
                    var automaticScale = CadPlanQuality.SuggestDimensionScale(root);
                    if (automaticScale > 0) database.Dimscale = automaticScale;
                    var unit = root.ContainsKey("sourceUnit") ? Convert.ToString(root["sourceUnit"]) : "mm";
                    PrepareDimensionUnits(commands, 1.0 / CadPlanQuality.UnitFactor(unit));
                    ExecuteCommands(commands, transaction, database, table, space, layerTable, layers, ref created, ref applied, errors);
                    database.Insunits = UnitsValue.Millimeters;
                    transaction.Commit();
                }
                catch (Exception ex)
                {
                    transaction.Abort();
                    foreach (var variable in previousVariables)
                        try { AcApplication.SetSystemVariable(variable.Key, variable.Value); } catch { }
                    message = "整批绘图已回滚：" + ex.Message;
                    return null;
                }
            }

            if (created > 0)
            {
                try
                {
                    AcApplication.SetSystemVariable("LWDISPLAY", 1);
                    document.Editor.Regen();
                    // Zoom through the view API: a queued ZOOM command would consume the next undo step.
                    database.UpdateExt(true);
                    using (var view = document.Editor.GetCurrentView())
                    {
                        var min = database.Extmin; var max = database.Extmax;
                        var aspect = view.Width / Math.Max(view.Height, 1e-9);
                        view.Height = Math.Max(max.Y - min.Y, (max.X - min.X) / aspect) * 1.1 + 1;
                        view.Width = view.Height * aspect;
                        view.CenterPoint = new Point2d((min.X + max.X) / 2, (min.Y + max.Y) / 2);
                        document.Editor.SetCurrentView(view);
                    }
                }
                catch
                {
                }
            }

            var summary = new StringBuilder();
            if (created > 0)
                summary.Append("已在模型空间绘制 " + created + " 个对象，可输入 U 撤销。");
            else if (applied > 0)
                summary.Append("已执行 " + applied + " 条设置类指令，未新建图元。");
            else
                summary.Append("没有创建任何对象。");
            if (layers.Count > 0) summary.Append(" 涉及图层：" + string.Join("、", new List<string>(layers).ToArray()) + "。");
            if (errors.Count > 0)
            {
                summary.Append(" 跳过 " + errors.Count + " 条：" + errors[0]);
                if (errors.Count > 1) summary.Append("；" + errors[1]);
            }
            message = summary.ToString();
            DiagnosticsLog.Write("CAD执行 " + timer.ElapsedMilliseconds + "ms 对象=" + created);
            return created > 0 || applied > 0 ? message : null;
        }

        private static void ExecuteCommands(object[] commands, Transaction transaction, Database database, BlockTable blockTable,
            BlockTableRecord space, LayerTable layerTable, HashSet<string> layers, ref int created, ref int applied, List<string> errors)
        {
            for (var index = 0; index < commands.Length; index++)
            {
                var command = commands[index] as Dictionary<string, object>;
                if (command == null) continue;
                try
                {
                    var type = Text(command, "type").ToLowerInvariant();
                    if (type.Length == 0) continue;

                    if (type == "layer")
                    {
                        var name = Text(command, "name");
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        EnsureLayer(transaction, database, layerTable, name, ColorIndex(command, 7), Text(command, "linetype"), Number(command, "lineweight", 0));
                        layers.Add(name);
                        applied++;
                        continue;
                    }

                    if (type == "textstyle")
                    {
                        EnsureTextStyle(transaction, database, Text(command, "name"), Text(command, "font"), Text(command, "bigfont"));
                        applied++;
                        continue;
                    }

                    if (type == "setvar")
                    {
                        var name = Text(command, "name");
                        if (name.Length == 0) continue;
                        var value = command.ContainsKey("value") ? command["value"] : null;
                        if (value is bool) value = ((bool)value) ? 1 : 0;
                        if (!TrySetVariable(name, value)) throw new InvalidOperationException("系统变量设置失败：" + name);
                        applied++;
                        continue;
                    }

                    if (type == "standard")
                    {
                        ApplyStandard(transaction, database, layerTable, layers);
                        applied++;
                        continue;
                    }

                    if (type == "frame")
                    {
                        created += AddFrame(transaction, database, space, command, layers);
                        continue;
                    }

                    if (type == "block")
                    {
                        created += DefineBlock(transaction, database, blockTable, layerTable, layers, command, errors, index);
                        continue;
                    }

                    var layerName = Text(command, "layer");
                    if (string.IsNullOrWhiteSpace(layerName)) layerName = "0";
                    EnsureLayer(transaction, database, layerTable, layerName, 7, null, 0);
                    layers.Add(layerName);

                    switch (type)
                    {
                        case "line":
                            created += AddLines(transaction, space, command, layerName);
                            break;
                        case "polyline":
                            created += AddPolyline(transaction, space, command, layerName);
                            break;
                        case "rect":
                        case "rectangle":
                            created += AddRectangle(transaction, space, command, layerName);
                            break;
                        case "ngon":
                        case "polygon":
                            created += AddPolygon(transaction, space, command, layerName);
                            break;
                        case "circle":
                            created += AddCircle(transaction, space, command, layerName);
                            break;
                        case "arc":
                            created += AddArc(transaction, space, command, layerName);
                            break;
                        case "ellipse":
                            created += AddEllipse(transaction, space, command, layerName);
                            break;
                        case "spline":
                            created += AddSpline(transaction, space, command, layerName);
                            break;
                        case "point":
                            created += AddPoint(transaction, space, command, layerName);
                            break;
                        case "text":
                            created += AddText(transaction, space, command, layerName);
                            break;
                        case "mtext":
                            created += AddMText(transaction, space, command, layerName);
                            break;
                        case "hatch":
                            created += AddHatch(transaction, space, command, layerName);
                            break;
                        case "dimlinear":
                        case "dim":
                            created += AddLinearDimension(transaction, space, database, command, layerName);
                            break;
                        case "dimaligned":
                            created += AddAlignedDimension(transaction, space, database, command, layerName);
                            break;
                        case "dimradius":
                            created += AddRadialDimension(transaction, space, database, command, layerName);
                            break;
                        case "dimdiameter":
                            created += AddDiameterDimension(transaction, space, database, command, layerName);
                            break;
                        case "insert":
                            created += AddBlockReference(transaction, space, blockTable, command, layerName);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    var commandType = Text(command, "type");
                    errors.Add("第 " + (index + 1) + " 条（" + commandType + "）：" + ex.Message);
                    DiagnosticsLog.Write("绘图第 " + (index + 1) + " 条失败 " + commandType + "：" + ex.GetType().Name + " " + ex.Message + "\r\n" + ex.StackTrace);
                    throw new InvalidOperationException("第 " + (index + 1) + " 条（" + commandType + "）：" + ex.Message, ex);
                }
            }
        }

        private static void EnsureLayer(Transaction transaction, Database database, LayerTable layerTable, string name, int colorIndex, string linetype, double lineweight)
        {
            ResolveLayerDefaults(name, ref linetype, ref lineweight);
            if (layerTable.Has(name)) return;
            layerTable.UpgradeOpen();
            var record = new LayerTableRecord { Name = name };
            if (colorIndex >= 1 && colorIndex <= 255) record.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)colorIndex);
            if (linetype != null && linetype.Trim().Length > 0)
            {
                try
                {
                    var linetypeTable = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
                    if (!linetypeTable.Has(linetype))
                    {
                        linetypeTable.UpgradeOpen();
                        database.LoadLineTypeFile(linetype, "acad.lin");
                    }
                    if (linetypeTable.Has(linetype)) record.LinetypeObjectId = linetypeTable[linetype];
                }
                catch
                {
                }
            }
            var weight = MapLineWeight(lineweight);
            if (weight.HasValue) record.LineWeight = weight.Value;
            layerTable.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static void ResolveLayerDefaults(string name, ref string linetype, ref double lineweight)
        {
            var text = name ?? string.Empty;
            if (string.IsNullOrWhiteSpace(linetype))
            {
                if (text.Contains("双点画")) linetype = "PHANTOM";
                else if (text.Contains("中心") || text.Contains("轴线") || text.Contains("点画") || text.Contains("对称")) linetype = "CENTER";
                else if (text.Contains("虚线") || text.Contains("不可见") || text.Contains("隐藏")) linetype = "DASHED";
            }
            if (lineweight > 0) return;
            if (text.Contains("图框") || text.Contains("标题栏")) lineweight = 0.7;
            else if (text.Contains("粗") || text.Contains("轮廓") || text.Contains("外框") || text.Contains("可见")) lineweight = 0.5;
            else if (text.Contains("细") || text.Contains("填充") || text.Contains("剖面") || text.Contains("标注") ||
                     text.Contains("文字") || text.Contains("尺寸") || text.Contains("中心") || text.Contains("轴线") ||
                     text.Contains("点画") || text.Contains("虚线")) lineweight = 0.25;
        }

        private static void ApplyStandard(Transaction transaction, Database database, LayerTable layerTable, HashSet<string> layers)
        {
            EnsureLayer(transaction, database, layerTable, "粗实线", 7, "CONTINUOUS", 0.5);
            EnsureLayer(transaction, database, layerTable, "细实线", 7, "CONTINUOUS", 0.25);
            EnsureLayer(transaction, database, layerTable, "点画线", 7, "CENTER", 0.25);
            EnsureLayer(transaction, database, layerTable, "虚线", 8, "DASHED", 0.25);
            EnsureLayer(transaction, database, layerTable, "标注", 7, "CONTINUOUS", 0.25);
            EnsureLayer(transaction, database, layerTable, "文字", 7, "CONTINUOUS", 0.25);
            EnsureLayer(transaction, database, layerTable, "剖面线", 8, "CONTINUOUS", 0.25);
            EnsureLayer(transaction, database, layerTable, "图框", 7, "CONTINUOUS", 0.7);
            EnsureTextStyle(transaction, database, "仿宋", "simfang.shx", "gbcbig.shx");
            EnsureDimStyle(transaction, database, "SL73");
            var previousScale = database.Dimscale;
            var dimTable = (DimStyleTable)transaction.GetObject(database.DimStyleTableId, OpenMode.ForRead);
            database.Dimstyle = dimTable["SL73"];
            database.SetDimstyleData((DimStyleTableRecord)transaction.GetObject(dimTable["SL73"], OpenMode.ForRead));
            database.Dimscale = previousScale;
            try
            {
                AcApplication.SetSystemVariable("LWDISPLAY", 1);
            }
            catch
            {
            }
            layers.Add("粗实线");
            layers.Add("细实线");
            layers.Add("点画线");
            layers.Add("虚线");
            layers.Add("标注");
            layers.Add("文字");
            layers.Add("剖面线");
            layers.Add("图框");
        }

        private static void EnsureDimStyle(Transaction transaction, Database database, string name)
        {
            var table = (DimStyleTable)transaction.GetObject(database.DimStyleTableId, OpenMode.ForRead);
            if (table.Has(name)) return;
            table.UpgradeOpen();
            var record = new DimStyleTableRecord { Name = name };
            record.Dimtxt = 3.5;
            record.Dimasz = 2.5;
            record.Dimexo = 1.0;
            record.Dimexe = 1.5;
            record.Dimgap = 1.0;
            record.Dimdec = 0;
            record.Dimtad = 1;
            record.Dimzin = 8;
            record.Dimscale = 1.0;
            record.Dimclrd = Color.FromColorIndex(ColorMethod.ByAci, 7);
            record.Dimclre = Color.FromColorIndex(ColorMethod.ByAci, 7);
            record.Dimclrt = Color.FromColorIndex(ColorMethod.ByAci, 7);
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static ObjectId DimensionStyle(Transaction transaction, Database database)
        {
            try
            {
                var table = (DimStyleTable)transaction.GetObject(database.DimStyleTableId, OpenMode.ForRead);
                if (table.Has("SL73")) return table["SL73"];
            }
            catch
            {
            }
            return database.Dimstyle;
        }

        private static void AddPlainText(Transaction transaction, BlockTableRecord space, string content, Point3d position, double height, string layer, bool centered)
        {
            if (string.IsNullOrEmpty(content)) return;
            var text = new DBText { Layer = layer, Position = position, Height = height, TextString = content };
            var styleTable = (TextStyleTable)transaction.GetObject(space.Database.TextStyleTableId, OpenMode.ForRead);
            if (styleTable.Has("仿宋")) text.TextStyleId = styleTable["仿宋"];
            if (centered)
            {
                var approxWidth = content.Length * height * 0.9;
                text.Position = new Point3d(position.X - approxWidth / 2, position.Y, 0);
            }
            space.AppendEntity(text);
            transaction.AddNewlyCreatedDBObject(text, true);
        }

        private static int AddFrame(Transaction transaction, Database database, BlockTableRecord space,
            Dictionary<string, object> command, HashSet<string> layers)
        {
            var size = Text(command, "size").ToUpperInvariant();
            double width;
            double height;
            switch (size)
            {
                case "A0": width = 1189; height = 841; break;
                case "A1": width = 841; height = 594; break;
                case "A2": width = 594; height = 420; break;
                case "A4": width = 210; height = 297; break;
                default: width = 420; height = 297; break;
            }

            var factor = FrameScale(command);
            width *= factor;
            height *= factor;

            var layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            EnsureLayer(transaction, database, layerTable, "图框", 7, "CONTINUOUS", 0.7);
            EnsureLayer(transaction, database, layerTable, "细实线", 7, "CONTINUOUS", 0.25);
            EnsureLayer(transaction, database, layerTable, "文字", 7, "CONTINUOUS", 0.25);
            EnsureTextStyle(transaction, database, "仿宋", "simfang.shx", "gbcbig.shx");
            layers.Add("图框");
            layers.Add("细实线");
            layers.Add("文字");

            var origin = new Point3d(0, 0, 0);
            if (command.ContainsKey("origin")) origin = Point(command, "origin");
            var x0 = origin.X;
            var y0 = origin.Y;
            var created = 0;

            var border = new Polyline { Layer = "细实线", Closed = true };
            border.AddVertexAt(0, new Point2d(x0, y0), 0, 0, 0);
            border.AddVertexAt(1, new Point2d(x0 + width, y0), 0, 0, 0);
            border.AddVertexAt(2, new Point2d(x0 + width, y0 + height), 0, 0, 0);
            border.AddVertexAt(3, new Point2d(x0, y0 + height), 0, 0, 0);
            space.AppendEntity(border);
            transaction.AddNewlyCreatedDBObject(border, true);
            created++;

            var inner = new Polyline { Layer = "图框", Closed = true };
            inner.AddVertexAt(0, new Point2d(x0 + 25 * factor, y0 + 5 * factor), 0, 0, 0);
            inner.AddVertexAt(1, new Point2d(x0 + width - 5 * factor, y0 + 5 * factor), 0, 0, 0);
            inner.AddVertexAt(2, new Point2d(x0 + width - 5 * factor, y0 + height - 5 * factor), 0, 0, 0);
            inner.AddVertexAt(3, new Point2d(x0 + 25 * factor, y0 + height - 5 * factor), 0, 0, 0);
            space.AppendEntity(inner);
            transaction.AddNewlyCreatedDBObject(inner, true);
            created++;

            var tx2 = x0 + width - 5 * factor;
            var tx1 = tx2 - 180 * factor;
            var ty1 = y0 + 5 * factor;
            var blockBottom = ty1;
            var blockTop = ty1 + 56 * factor;
            var block = new Polyline { Layer = "图框", Closed = true };
            block.AddVertexAt(0, new Point2d(tx1, blockBottom), 0, 0, 0);
            block.AddVertexAt(1, new Point2d(tx2, blockBottom), 0, 0, 0);
            block.AddVertexAt(2, new Point2d(tx2, blockTop), 0, 0, 0);
            block.AddVertexAt(3, new Point2d(tx1, blockTop), 0, 0, 0);
            space.AppendEntity(block);
            transaction.AddNewlyCreatedDBObject(block, true);
            created++;

            foreach (var offset in new[] { 10.0, 20.0, 30.0, 40.0 })
            {
                var line = new Line(
                    new Point3d(tx1, ty1 + offset * factor, 0),
                    new Point3d(tx2, ty1 + offset * factor, 0)) { Layer = "细实线" };
                space.AppendEntity(line);
                transaction.AddNewlyCreatedDBObject(line, true);
                created++;
            }
            foreach (var offset in new[] { 80.0, 130.0 })
            {
                var line = new Line(
                    new Point3d(tx1 + offset * factor, ty1, 0),
                    new Point3d(tx1 + offset * factor, ty1 + 30 * factor, 0)) { Layer = "细实线" };
                space.AppendEntity(line);
                transaction.AddNewlyCreatedDBObject(line, true);
                created++;
            }

            var title = Text(command, "title");
            if (title.Length == 0) title = "图名";
            AddPlainText(transaction, space, title, new Point3d(tx1 + 90 * factor, ty1 + 46 * factor, 0), 6.0 * factor, "文字", true);
            AddPlainText(transaction, space, Text(command, "company").Length > 0 ? Text(command, "company") : "单位名称",
                new Point3d(tx1 + 5 * factor, ty1 + 4 * factor, 0), 2.5 * factor, "文字", false);
            AddPlainText(transaction, space, "设计", new Point3d(tx1 + 5 * factor, ty1 + 14 * factor, 0), 2.5 * factor, "文字", false);
            AddPlainText(transaction, space, "制图", new Point3d(tx1 + 5 * factor, ty1 + 24 * factor, 0), 2.5 * factor, "文字", false);
            AddPlainText(transaction, space, "审核", new Point3d(tx1 + 85 * factor, ty1 + 24 * factor, 0), 2.5 * factor, "文字", false);
            AddPlainText(transaction, space, "日期", new Point3d(tx1 + 85 * factor, ty1 + 14 * factor, 0), 2.5 * factor, "文字", false);
            AddPlainText(transaction, space, Text(command, "date").Length > 0 ? Text(command, "date") : DateTime.Now.ToString("yyyy.MM.dd"),
                new Point3d(tx1 + 100 * factor, ty1 + 14 * factor, 0), 2.2 * factor, "文字", false);
            AddPlainText(transaction, space, "比例", new Point3d(tx1 + 5 * factor, ty1 + 34 * factor, 0), 2.5 * factor, "文字", false);
            AddPlainText(transaction, space, Text(command, "scale").Length > 0 ? Text(command, "scale") : "1:100",
                new Point3d(tx1 + 40 * factor, ty1 + 34 * factor, 0), 3.5 * factor, "文字", false);
            AddPlainText(transaction, space, "图号", new Point3d(tx1 + 85 * factor, ty1 + 34 * factor, 0), 2.5 * factor, "文字", false);
            AddPlainText(transaction, space, Text(command, "number").Length > 0 ? Text(command, "number") : "01",
                new Point3d(tx1 + 135 * factor, ty1 + 34 * factor, 0), 3.5 * factor, "文字", false);
            created += 7;
            ApplySheetScale(transaction, database, factor);
            return created;
        }

        /// <summary>解析出图比例：支持 1:100、1∶100、100；返回放大倍数。</summary>
        private static double FrameScale(Dictionary<string, object> command)
        {
            var raw = Text(command, "scale").Replace(" ", string.Empty)
                .Replace("：", ":").Replace("∶", ":");
            if (raw.Length == 0) return 1.0;

            var colon = raw.IndexOf(':');
            if (colon > 0)
            {
                double front;
                double back;
                if (!double.TryParse(raw.Substring(0, colon), NumberStyles.Float, CultureInfo.InvariantCulture, out front)) return 1.0;
                if (!double.TryParse(raw.Substring(colon + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out back)) return 1.0;
                if (front <= 0 || back <= 0) return 1.0;
                return ClampScale(back / front);
            }

            double value;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0) return ClampScale(value);
            return 1.0;
        }

        private static double ClampScale(double value)
        {
            if (value < 0.2) return 0.2;
            if (value > 2000) return 2000;
            return value;
        }

        /// <summary>图框按比例放大后，同步标注与线型比例，避免 1:100 图形中的标注文字过小。</summary>
        private static void ApplySheetScale(Transaction transaction, Database database, double factor)
        {
            EnsureDimStyle(transaction, database, "SL73");
            try
            {
                var table = (DimStyleTable)transaction.GetObject(database.DimStyleTableId, OpenMode.ForRead);
                var record = (DimStyleTableRecord)transaction.GetObject(table["SL73"], OpenMode.ForWrite);
                record.Dimscale = factor;
            }
            catch
            {
            }
            try
            {
                AcApplication.SetSystemVariable("DIMSCALE", factor);
            }
            catch
            {
            }
            try
            {
                AcApplication.SetSystemVariable("LTSCALE", factor);
            }
            catch
            {
            }
        }

        /// <summary>设置系统变量：明显非法的可选变量直接忽略并记日志，避免打断整批绘制。</summary>
        private static bool TrySetVariable(string name, object value)
        {
            var key = name.Trim().ToUpperInvariant();
            if (value == null) return false;

            if (MustBePositive(key))
            {
                var number = ToDouble(value);
                if (!double.IsNaN(number) && number <= 0) value = 1.0;
            }

            try
            {
                AcApplication.SetSystemVariable(key, value);
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Write("忽略无效系统变量 " + key + "=" + Convert.ToString(value) + "：" + ex.Message);
                return false;
            }
        }

        private static bool MustBePositive(string name)
        {
            switch (name)
            {
                case "LTSCALE":
                case "CELTSCALE":
                case "TEXTSIZE":
                case "DIMTXT":
                case "DIMASZ":
                    return true;
                default:
                    return false;
            }
        }

        private static double ToDouble(object value)
        {
            if (value is double) return (double)value;
            if (value is int) return (int)value;
            if (value is long) return (long)value;
            if (value is bool) return ((bool)value) ? 1 : 0;
            double parsed;
            return double.TryParse(Convert.ToString(value), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                ? parsed
                : double.NaN;
        }

        private static LineWeight? MapLineWeight(double millimeters)
        {
            if (millimeters <= 0) return null;
            if (millimeters < 0.14) return LineWeight.LineWeight013;
            if (millimeters < 0.16) return LineWeight.LineWeight015;
            if (millimeters < 0.19) return LineWeight.LineWeight018;
            if (millimeters < 0.23) return LineWeight.LineWeight020;
            if (millimeters < 0.28) return LineWeight.LineWeight025;
            if (millimeters < 0.33) return LineWeight.LineWeight030;
            if (millimeters < 0.38) return LineWeight.LineWeight035;
            if (millimeters < 0.45) return LineWeight.LineWeight040;
            if (millimeters < 0.55) return LineWeight.LineWeight050;
            if (millimeters < 0.65) return LineWeight.LineWeight060;
            if (millimeters < 0.75) return LineWeight.LineWeight070;
            if (millimeters < 0.85) return LineWeight.LineWeight080;
            if (millimeters < 1.0) return LineWeight.LineWeight090;
            return LineWeight.LineWeight100;
        }

        private static void EnsureTextStyle(Transaction transaction, Database database, string name, string font, string bigFont)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("textstyle 缺少 name。");
            var table = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
            if (table.Has(name)) return;
            table.UpgradeOpen();
            var record = new TextStyleTableRecord { Name = name };
            var chosen = string.IsNullOrWhiteSpace(font) ? "txt.shx" : font;
            var chosenBig = bigFont;
            if (!FontExists(chosen))
            {
                // 缺失字体在 REGEN 时会弹出字体替换对话框并卡住 AutoCAD，这里回退到内置字体。
                chosen = "txt.shx";
                chosenBig = "gbcbig.shx";
            }
            try
            {
                // SHX 的 typeface 用去掉扩展名的名称；字体不可用时保持默认字体，避免 eInvalidInput。
                var typeface = chosen.EndsWith(".shx", StringComparison.OrdinalIgnoreCase)
                    ? System.IO.Path.GetFileNameWithoutExtension(chosen)
                    : chosen;
                record.Font = new Autodesk.AutoCAD.GraphicsInterface.FontDescriptor(typeface, false, false, 0, 0);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Write("文字样式字体设置失败 " + chosen + "：" + ex.Message);
            }
            try
            {
                if (!string.IsNullOrWhiteSpace(chosenBig)) record.BigFontFileName = chosenBig;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Write("大字库设置失败 " + chosenBig + "：" + ex.Message);
            }
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static bool FontExists(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return false;
            try
            {
                var prefix = Convert.ToString(AcApplication.GetSystemVariable("ACADPREFIX"));
                if (!string.IsNullOrEmpty(prefix))
                {
                    foreach (var root in prefix.Split(';'))
                    {
                        var folder = root.Trim();
                        if (folder.Length == 0) continue;
                        if (System.IO.File.Exists(System.IO.Path.Combine(folder, "Fonts", fileName))) return true;
                        if (System.IO.File.Exists(System.IO.Path.Combine(folder, fileName))) return true;
                    }
                }
            }
            catch
            {
            }
            try
            {
                var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                return System.IO.File.Exists(System.IO.Path.Combine(fonts, fileName));
            }
            catch
            {
                return false;
            }
        }

        private static int DefineBlock(Transaction transaction, Database database, BlockTable blockTable, LayerTable layerTable,
            HashSet<string> layers, Dictionary<string, object> command, List<string> errors, int index)
        {
            var name = Text(command, "name");
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("block 缺少 name。");
            if (blockTable.Has(name)) throw new InvalidOperationException("块 " + name + " 已存在，请换一个名称。");

            blockTable.UpgradeOpen();
            var record = new BlockTableRecord { Name = name, Origin = new Point3d(0, 0, 0) };
            blockTable.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);

            var inner = command.ContainsKey("commands") ? command["commands"] as object[] : null;
            if (inner == null) return 0;

            var created = 0;
            var applied = 0;
            var innerLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ExecuteCommands(inner, transaction, database, blockTable, record, layerTable, innerLayers, ref created, ref applied, errors);
            foreach (var layer in innerLayers) layers.Add(layer);
            return created;
        }

        private static int AddBlockReference(Transaction transaction, BlockTableRecord space, BlockTable blockTable,
            Dictionary<string, object> command, string layer)
        {
            var name = Text(command, "name");
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("insert 缺少 name。");
            if (!blockTable.Has(name)) throw new InvalidOperationException("块 " + name + " 不存在，请先定义。");
            blockTable.UpgradeOpen();
            var reference = new BlockReference(Point(command, "position"), blockTable[name]) { Layer = layer };
            var rotation = Number(command, "rotation", 0);
            if (rotation != 0) reference.Rotation = rotation * Math.PI / 180.0;
            var scale = Number(command, "scale", 1);
            if (scale <= 0) scale = 1;
            reference.ScaleFactors = new Scale3d(scale, scale, scale);
            ApplyProperties(reference, command);
            space.AppendEntity(reference);
            transaction.AddNewlyCreatedDBObject(reference, true);
            return 1;
        }

        private static void ApplyProperties(Entity entity, Dictionary<string, object> command)
        {
            if (command.ContainsKey("color"))
            {
                var raw = command["color"];
                if (raw != null)
                {
                    var text = Convert.ToString(raw).Trim();
                    var applied = false;
                    if (text.StartsWith("#") && text.Length == 7)
                    {
                        try
                        {
                            var r = Convert.ToInt32(text.Substring(1, 2), 16);
                            var g = Convert.ToInt32(text.Substring(3, 2), 16);
                            var b = Convert.ToInt32(text.Substring(5, 2), 16);
                            entity.Color = Color.FromRgb((byte)r, (byte)g, (byte)b);
                            applied = true;
                        }
                        catch
                        {
                        }
                    }
                    if (!applied)
                    {
                        var index = ToNumber(raw);
                        if (index >= 1 && index <= 255) entity.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)index);
                    }
                }
            }
            if (command.ContainsKey("lineweight"))
            {
                var weight = MapLineWeight(ToNumber(command["lineweight"]));
                if (weight.HasValue) entity.LineWeight = weight.Value;
            }
        }

        private static int ColorIndex(Dictionary<string, object> command, int fallback)
        {
            var raw = command.ContainsKey("color") ? command["color"] : null;
            if (raw == null) return fallback;
            var text = Convert.ToString(raw).Trim();
            if (text.StartsWith("#")) return 7;
            var index = (int)ToNumber(raw);
            return index >= 1 && index <= 255 ? index : fallback;
        }

        private static int AddLines(Transaction transaction, BlockTableRecord space, Dictionary<string, object> command, string layer)
        {
            var points = Points(command, "points");
            var count = 0;
            for (var i = 0; i + 1 < points.Count; i++)
            {
                var line = new Line(points[i], points[i + 1]) { Layer = layer };
                ApplyProperties(line, command);
                space.AppendEntity(line);
                transaction.AddNewlyCreatedDBObject(line, true);
                count++;
            }
            return count;
        }

        private static int AddPolyline(Transaction transaction, BlockTableRecord space, Dictionary<string, object> command, string layer)
        {
            var points = Points(command, "points");
            if (points.Count < 2) throw new InvalidOperationException("polyline 至少需要 2 个点。");
            var width = Number(command, "width", 0);
            var polyline = new Polyline { Layer = layer };
            for (var i = 0; i < points.Count; i++)
            {
                polyline.AddVertexAt(i, new Point2d(points[i].X, points[i].Y), 0, width, width);
            }
            polyline.Closed = Boolean(command, "closed");
            ApplyProperties(polyline, command);
            space.AppendEntity(polyline);
            transaction.AddNewlyCreatedDBObject(polyline, true);
            return 1;
        }

        private static int AddRectangle(Transaction transaction, BlockTableRecord space, Dictionary<string, object> command, string layer)
        {
            var p1 = Point(command, "p1");
            var p2 = Point(command, "p2");
            var polyline = new Polyline { Layer = layer, Closed = true };
            polyline.AddVertexAt(0, new Point2d(p1.X, p1.Y), 0, 0, 0);
            polyline.AddVertexAt(1, new Point2d(p2.X, p1.Y), 0, 0, 0);
            polyline.AddVertexAt(2, new Point2d(p2.X, p2.Y), 0, 0, 0);
            polyline.AddVertexAt(3, new Point2d(p1.X, p2.Y), 0, 0, 0);
            ApplyProperties(polyline, command);
            space.AppendEntity(polyline);
            transaction.AddNewlyCreatedDBObject(polyline, true);
            return 1;
        }

        private static int AddPolygon(Transaction transaction, BlockTableRecord space, Dictionary<string, object> command, string layer)
        {
            var center = Point(command, "center");
            var sides = (int)Number(command, "sides", 6);
            var radius = Number(command, "radius", 0);
            if (sides < 3) throw new InvalidOperationException("ngon 的 sides 至少为 3。");
            if (radius <= 0) throw new InvalidOperationException("ngon 的 radius 必须大于 0。");
            var rotation = Number(command, "rotation", 90) * Math.PI / 180.0;
            var polyline = new Polyline { Layer = layer, Closed = true };
            for (var i = 0; i < sides; i++)
            {
                var angle = rotation + 2 * Math.PI * i / sides;
                polyline.AddVertexAt(i, new Point2d(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)), 0, 0, 0);
            }
            ApplyProperties(polyline, command);
            space.AppendEntity(polyline);
            transaction.AddNewlyCreatedDBObject(polyline, true);
            return 1;
        }

        private static int AddCircle(Transaction transaction, BlockTableRecord space, Dictionary<string, object> command, string layer)
        {
            var center = Point(command, "center");
            var radius = Number(command, "radius", 0);
            if (radius <= 0) throw new InvalidOperationException("circle 的 radius 必须大于 0。");
            var circle = new Circle(center, Vector3d.ZAxis, radius) { Layer = layer };
            ApplyProperties(circle, command);
            space.AppendEntity(circle);
            transaction.AddNewlyCreatedDBObject(circle, true);
            return 1;
        }

        private static int AddArc(Transaction transaction, BlockTableRecord space, Dictionary<string, object> command, string layer)
        {
            var center = Point(command, "center");
            var radius = Number(command, "radius", 0);
            if (radius <= 0) throw new InvalidOperationException("arc 的 radius 必须大于 0。");
            var start = Number(command, "start", 0) * Math.PI / 180.0;
            var end = Number(command, "end", 90) * Math.PI / 180.0;
            var arc = new Arc(center, radius, start, end) { Layer = layer };
            ApplyProperties(arc, command);
            space.AppendEntity(arc);
            transaction.AddNewlyCreatedDBObject(arc, true);
            return 1;
        }

        private static int AddEllipse(Transaction transaction, BlockTableRecord space, Dictionary<string, object> command, string layer)
        {
            var center = Point(command, "center");
            var major = Number(command, "major", 0);
            if (major <= 0) throw new InvalidOperationException("ellipse 的 major 必须大于 0。");
            var ratio = Number(command, "ratio", 0.6);
            if (ratio <= 0 || ratio > 1) ratio = 0.6;
            var rotation = Number(command, "rotation", 0) * Math.PI / 180.0;
            var majorAxis = new Vector3d(major * Math.Cos(rotation), major * Math.Sin(rotation), 0);
            var ellipse = new Ellipse(center, Vector3d.ZAxis, majorAxis, ratio, 0, 2 * Math.PI) { Layer = layer };
            ApplyProperties(ellipse, command);
            space.AppendEntity(ellipse);
            transaction.AddNewlyCreatedDBObject(ellipse, true);
            return 1;
        }

        private static int AddSpline(Transaction transaction, BlockTableRecord space, Dictionary<string, object> command, string layer)
        {
            var points = Points(command, "points");
            if (points.Count < 3) throw new InvalidOperationException("spline 至少需要 3 个点。");
            var collection = new Point3dCollection();
            foreach (var point in points) collection.Add(point);
            var spline = new Spline(collection, 3, 0.0) { Layer = layer };
            ApplyProperties(spline, command);
            space.AppendEntity(spline);
            transaction.AddNewlyCreatedDBObject(spline, true);
            return 1;
        }

        private static int AddPoint(Transaction transaction, BlockTableRecord space, Dictionary<string, object> command, string layer)
        {
            var point = new DBPoint(Point(command, "position")) { Layer = layer };
            ApplyProperties(point, command);
            space.AppendEntity(point);
            transaction.AddNewlyCreatedDBObject(point, true);
            return 1;
        }

        private static int AddText(Transaction transaction, BlockTableRecord space, Dictionary<string, object> command, string layer)
        {
            var content = Text(command, "content");
            if (string.IsNullOrEmpty(content)) throw new InvalidOperationException("text 的 content 为空。");
            var text = new DBText
            {
                Layer = layer,
                Position = Point(command, "position"),
                Height = Math.Max(Number(command, "height", 3.5), 0.1),
                TextString = content,
                Rotation = Number(command, "rotation", 0) * Math.PI / 180.0
            };
            var style = Text(command, "style");
            if (style.Length == 0) style = "仿宋";
            if (style.Length > 0)
            {
                var table = (TextStyleTable)transaction.GetObject(space.Database.TextStyleTableId, OpenMode.ForRead);
                if (table.Has(style)) text.TextStyleId = table[style];
            }
            ApplyProperties(text, command);
            space.AppendEntity(text);
            transaction.AddNewlyCreatedDBObject(text, true);
            return 1;
        }

        private static int AddMText(Transaction transaction, BlockTableRecord space, Dictionary<string, object> command, string layer)
        {
            var content = Text(command, "content");
            if (string.IsNullOrEmpty(content)) throw new InvalidOperationException("mtext 的 content 为空。");
            var mtext = new MText
            {
                Layer = layer,
                Location = Point(command, "position"),
                Height = Math.Max(Number(command, "height", 3.5), 0.1),
                Width = Math.Max(Number(command, "width", 0), 0),
                Contents = content,
                Rotation = Number(command, "rotation", 0) * Math.PI / 180.0
            };
            var style = Text(command, "style");
            if (style.Length == 0) style = "仿宋";
            if (style.Length > 0)
            {
                var table = (TextStyleTable)transaction.GetObject(space.Database.TextStyleTableId, OpenMode.ForRead);
                if (table.Has(style)) mtext.TextStyleId = table[style];
            }
            ApplyProperties(mtext, command);
            space.AppendEntity(mtext);
            transaction.AddNewlyCreatedDBObject(mtext, true);
            return 1;
        }

        private static int AddHatch(Transaction transaction, BlockTableRecord space, Dictionary<string, object> command, string layer)
        {
            var points = Points(command, "points");
            if (points.Count < 3) throw new InvalidOperationException("hatch 至少需要 3 个点。");
            var pattern = Text(command, "pattern");
            if (pattern.Length == 0) pattern = "ANSI31";
            var solid = string.Equals(pattern, "SOLID", StringComparison.OrdinalIgnoreCase);

            var hatch = new Hatch { Layer = layer };
            hatch.SetHatchPattern(HatchPatternType.PreDefined, solid ? "SOLID" : pattern);
            hatch.Associative = false;
            var requestedScale = Number(command, "scale", 0);
            var autoScale = ComputeHatchScale(points);
            hatch.PatternScale = solid ? 1.0 : Math.Max(0.01, autoScale * (requestedScale <= 0 ? 1.0 : requestedScale));
            hatch.PatternAngle = Number(command, "angle", 0) * Math.PI / 180.0;

            var vertices = new Point2dCollection();
            var bulges = new DoubleCollection();
            foreach (var point in points)
            {
                vertices.Add(new Point2d(point.X, point.Y));
                bulges.Add(0);
            }
            var first = points[0];
            var last = points[points.Count - 1];
            if (Math.Abs(first.X - last.X) > 1e-6 || Math.Abs(first.Y - last.Y) > 1e-6)
            {
                vertices.Add(new Point2d(first.X, first.Y));
                bulges.Add(0);
            }
            hatch.AppendLoop(HatchLoopTypes.External, vertices, bulges);
            var holes = command.ContainsKey("holes") ? command["holes"] as object[] : null;
            if (holes != null)
            {
                foreach (var hole in holes)
                {
                    var innerVertices = new Point2dCollection();
                    var innerBulges = new DoubleCollection();
                    var innerPoints = (object[])hole;
                    foreach (var item in innerPoints)
                    {
                        var p = ToPoint(item); innerVertices.Add(new Point2d(p.X, p.Y)); innerBulges.Add(0);
                    }
                    var firstInner = ToPoint(innerPoints[0]); var lastInner = ToPoint(innerPoints[innerPoints.Length - 1]);
                    if (firstInner.DistanceTo(lastInner) > 1e-6) { innerVertices.Add(new Point2d(firstInner.X, firstInner.Y)); innerBulges.Add(0); }
                    hatch.AppendLoop(HatchLoopTypes.Default, innerVertices, innerBulges);
                }
                hatch.HatchStyle = HatchStyle.Normal;
            }
            hatch.EvaluateHatch(true);
            ApplyProperties(hatch, command);
            space.AppendEntity(hatch);
            transaction.AddNewlyCreatedDBObject(hatch, true);
            return 1;
        }

        private static int AddLinearDimension(Transaction transaction, BlockTableRecord space, Database database,
            Dictionary<string, object> command, string layer)
        {
            var p1 = Point(command, "p1");
            var p2 = Point(command, "p2");
            var position = Point(command, "position");
            var rotation = Number(command, "rotation", 0) * Math.PI / 180.0;
            var dimension = new RotatedDimension(rotation, p1, p2, position, Text(command, "text"), DimensionStyle(transaction, database));
            dimension.Layer = layer;
            ConfigureDimension(dimension, database);
            ApplyProperties(dimension, command);
            MarkUncertain(dimension, command);
            space.AppendEntity(dimension);
            transaction.AddNewlyCreatedDBObject(dimension, true);
            return 1;
        }

        private static int AddAlignedDimension(Transaction transaction, BlockTableRecord space, Database database,
            Dictionary<string, object> command, string layer)
        {
            var p1 = Point(command, "p1");
            var p2 = Point(command, "p2");
            var position = Point(command, "position");
            var dimension = new AlignedDimension(p1, p2, position, Text(command, "text"), DimensionStyle(transaction, database));
            dimension.Layer = layer;
            ConfigureDimension(dimension, database);
            ApplyProperties(dimension, command);
            MarkUncertain(dimension, command);
            space.AppendEntity(dimension);
            transaction.AddNewlyCreatedDBObject(dimension, true);
            return 1;
        }

        private static int AddRadialDimension(Transaction transaction, BlockTableRecord space, Database database,
            Dictionary<string, object> command, string layer)
        {
            var center = Point(command, "center");
            var point = Point(command, "point");
            var dimension = new RadialDimension(center, point, Number(command, "leader", 10), Text(command, "text"), DimensionStyle(transaction, database));
            dimension.Layer = layer;
            ConfigureDimension(dimension, database);
            ApplyProperties(dimension, command);
            MarkUncertain(dimension, command);
            space.AppendEntity(dimension);
            transaction.AddNewlyCreatedDBObject(dimension, true);
            return 1;
        }

        private static int AddDiameterDimension(Transaction transaction, BlockTableRecord space, Database database,
            Dictionary<string, object> command, string layer)
        {
            var center = Point(command, "center");
            var point = Point(command, "point");
            var dimension = new DiametricDimension(center, point, Number(command, "leader", 10), Text(command, "text"), DimensionStyle(transaction, database));
            dimension.Layer = layer;
            ConfigureDimension(dimension, database);
            ApplyProperties(dimension, command);
            MarkUncertain(dimension, command);
            space.AppendEntity(dimension);
            transaction.AddNewlyCreatedDBObject(dimension, true);
            return 1;
        }

        private static void ConfigureDimension(Dimension dimension, Database database)
        {
            using (var style = database.GetDimstyleData()) dimension.SetDimstyleData(style);
        }

        private static void MarkUncertain(Dimension dimension, Dictionary<string, object> command)
        {
            if (command.ContainsKey("_dimensionFactor")) dimension.Dimlfac = CadPlanQuality.Number(command["_dimensionFactor"]);
            if (command.ContainsKey("confidence") && Text(command, "confidence") != "read")
            {
                dimension.Color = Color.FromColorIndex(ColorMethod.ByAci, 30);
                dimension.DimensionText = "<>（估）";
            }
        }

        private static void PrepareDimensionUnits(object[] commands, double factor)
        {
            foreach (Dictionary<string, object> c in commands)
            {
                var type = Text(c, "type").ToLowerInvariant();
                if (type == "block") PrepareDimensionUnits((object[])c["commands"], factor);
                else if (type.StartsWith("dim", StringComparison.Ordinal)) c["_dimensionFactor"] = factor;
            }
        }

        private static double ComputeHatchScale(List<Point3d> points)
        {
            var minX = points[0].X;
            var minY = points[0].Y;
            var maxX = minX;
            var maxY = minY;
            foreach (var point in points)
            {
                if (point.X < minX) minX = point.X;
                if (point.Y < minY) minY = point.Y;
                if (point.X > maxX) maxX = point.X;
                if (point.Y > maxY) maxY = point.Y;
            }
            var dx = maxX - minX;
            var dy = maxY - minY;
            var diagonal = Math.Sqrt(dx * dx + dy * dy);
            if (diagonal <= 0) return 1.0;
            return Math.Max(0.05, diagonal / 100.0);
        }

        private static List<Point3d> Points(Dictionary<string, object> command, string key)
        {
            var raw = command.ContainsKey(key) ? command[key] as object[] : null;
            if (raw == null || raw.Length < 2) throw new InvalidOperationException("缺少 " + key + " 坐标。");
            var points = new List<Point3d>();
            foreach (var item in raw) points.Add(ToPoint(item));
            return points;
        }

        private static Point3d Point(Dictionary<string, object> command, string key)
        {
            if (!command.ContainsKey(key)) throw new InvalidOperationException("缺少 " + key + " 坐标。");
            return ToPoint(command[key]);
        }

        private static Point3d ToPoint(object value)
        {
            var array = value as object[];
            if (array != null && array.Length >= 2)
            {
                return new Point3d(ToNumber(array[0]), ToNumber(array[1]), array.Length > 2 ? ToNumber(array[2]) : 0);
            }
            var map = value as Dictionary<string, object>;
            if (map != null) return new Point3d(Number(map, "x", 0), Number(map, "y", 0), Number(map, "z", 0));
            throw new InvalidOperationException("坐标格式应为 [x,y]。");
        }

        private static string Text(Dictionary<string, object> command, string key)
        {
            if (!command.ContainsKey(key) || command[key] == null) return string.Empty;
            return Convert.ToString(command[key]).Trim();
        }

        private static double Number(Dictionary<string, object> command, string key, double fallback)
        {
            return command.ContainsKey(key) ? ToNumber(command[key]) : fallback;
        }

        private static double ToNumber(object value)
        {
            if (value == null) return 0;
            if (value is double) return (double)value;
            if (value is int) return (int)value;
            if (value is decimal) return (double)(decimal)value;
            if (value is long) return (long)value;
            double result;
            return double.TryParse(Convert.ToString(value), NumberStyles.Float, CultureInfo.InvariantCulture, out result) ? result : 0;
        }

        private static bool Boolean(Dictionary<string, object> command, string key)
        {
            if (!command.ContainsKey(key)) return false;
            var value = command[key];
            if (value is bool) return (bool)value;
            return string.Equals(Convert.ToString(value), "true", StringComparison.OrdinalIgnoreCase);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace AutoCAD.AI.Assistant.Services
{
    /// <summary>
    /// 绘图计划本地预校验：坏计划不执行，并给出“第 N 条 + 原因”。
    /// </summary>
    internal static class CadPlanValidator
    {
        private static readonly HashSet<string> KnownTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "layer", "textstyle", "setvar", "standard", "frame", "block", "insert",
            "line", "polyline", "rect", "rectangle", "ngon", "polygon", "circle", "arc",
            "ellipse", "spline", "point", "text", "mtext", "hatch",
            "dimlinear", "dim", "dimaligned", "dimradius", "dimdiameter", "dimangular", "leader", "donut", "solid"
        };

        /// <summary>返回 null 表示通过；否则返回可直接展示的错误说明。</summary>
        public static string Validate(string json)
        {
            var errors = ValidateAll(json);
            return errors.Count == 0 ? null : string.Join("\n", errors.ToArray());
        }

        public static List<string> ValidateAll(string json)
        {
            var errors = new List<string>();
            ValidateCore(json, errors);
            return errors;
        }

        private static void ValidateCore(string json, List<string> errors)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
            Dictionary<string, object> root;
            try
            {
                root = serializer.DeserializeObject(json) as Dictionary<string, object>;
            }
            catch (Exception ex)
            {
                errors.Add("绘图指令不是合法 JSON：" + ex.Message); return;
            }

            if (root == null) { errors.Add("绘图指令必须是 JSON 对象。"); return; }
            var commands = root.ContainsKey("commands") ? root["commands"] as object[] : null;
            if (commands == null) { errors.Add("缺少 commands 数组。"); return; }
            if (commands.Length == 0) { errors.Add("commands 为空。"); return; }
            var budgetError = CadPlanBudget.Check(commands);
            if (budgetError != null) { errors.Add(budgetError); return; }
            bool anchorsChanged;
            var anchorError = CadDimensionAnchors.Resolve(root, out anchorsChanged);
            if (anchorError != null) { errors.Add(anchorError); return; }
            if (anchorsChanged)
            {
                budgetError = CadPlanBudget.Check(commands);
                if (budgetError != null) { errors.Add(budgetError); return; }
            }
            if (root.ContainsKey("sourceUnit") && !CadPlanQuality.IsUnit(Text(root, "sourceUnit")))
                errors.Add("sourceUnit 仅支持 mm/cm/m，源单位坐标由执行器换算为毫米。");
            if (root.ContainsKey("coordinateUnit") && Text(root, "coordinateUnit") != "mm" && Text(root, "coordinateUnit") != "source")
                errors.Add("coordinateUnit 仅支持 mm/source。");
            if (Text(root, "coordinateUnit") == "source" && !root.ContainsKey("sourceUnit")) errors.Add("源单位坐标需声明 sourceUnit。");

            var remaining = 400;
            ValidateCommands(commands, new HashSet<string>(StringComparer.OrdinalIgnoreCase), errors, "", 0, ref remaining);
            if (errors.Count == 0) errors.AddRange(CadPlanQuality.DimensionErrors(root));
            if (errors.Count == 0) errors.AddRange(CadPlanQuality.MaterialLeaderErrors(commands, ""));
            if (errors.Count == 0) errors.AddRange(CadGeometryReferences.Errors(root));
        }

        private static void ValidateCommands(object[] commands, HashSet<string> definedBlocks, List<string> errors,
            string prefix, int depth, ref int remaining)
        {
            if (depth > 8) { errors.Add("块嵌套超过 8 层。"); return; }
            foreach (var item in commands)
            {
                if (--remaining < 0) { errors.Add("指令总数（含块内部）超过上限 400。"); return; }
                if (errors.Count >= 12) return;
                var command = item as Dictionary<string, object>;
                var position = prefix + (Array.IndexOf(commands, item) + 1);
                if (command == null) { errors.Add("第 " + position + " 条：指令必须是 JSON 对象。"); continue; }

                var type = Text(command, "type").ToLowerInvariant();
                if (!KnownTypes.Contains(type)) { errors.Add("第 " + position + " 条：缺少或未知 type：" + type); continue; }

                var error = CheckCommand(type, command, definedBlocks);
                if (error == null) error = CheckOptionalNumbers(command);
                if (error != null) errors.Add("第 " + position + " 条（" + type + "）：" + error);
                if (type == "block" && error == null)
                {
                    var name = Text(command, "name");
                    // Do not expose a block until its body is checked: self references are invalid.
                    var nested = (object[])command["commands"];
                    var nestedBlocks = new HashSet<string>(definedBlocks, StringComparer.OrdinalIgnoreCase);
                    ValidateCommands(nested, nestedBlocks, errors, position + ".", depth + 1, ref remaining);
                    definedBlocks.Add(name);
                }
            }
        }

        /// <summary>统计计划构成，用于判断复刻是否过简。</summary>
        public static string Summarize(string json)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
            Dictionary<string, object> root;
            try
            {
                root = serializer.DeserializeObject(json) as Dictionary<string, object>;
            }
            catch
            {
                return "无法解析";
            }
            var commands = root == null || !root.ContainsKey("commands") ? null : root["commands"] as object[];
            if (commands == null) return "无指令";
            var counts = new Dictionary<string, int>();
            foreach (var item in commands)
            {
                var command = item as Dictionary<string, object>;
                if (command == null) continue;
                var type = Text(command, "type").ToLowerInvariant();
                if (type.Length == 0) type = "未知";
                counts[type] = counts.ContainsKey(type) ? counts[type] + 1 : 1;
            }
            var parts = new List<string>();
            foreach (var pair in counts)
            {
                parts.Add(pair.Key + " " + pair.Value);
            }
            return "指令 " + commands.Length + "：" + string.Join("、", parts.ToArray());
        }

        private static string CheckCommand(string type, Dictionary<string, object> command, HashSet<string> definedBlocks)
        {
            if (type == "layer")
            {
                return string.IsNullOrEmpty(Text(command, "name")) ? "缺少 name（图层名）。" : null;
            }
            if (type == "insert")
            {
                var name = Text(command, "name");
                if (name.Length == 0) return "缺少 name（块名）。";
                if (!definedBlocks.Contains(name)) return "块“" + name + "”尚未定义，需先输出 block 指令。";
                return Point(command, "position");
            }
            if (type == "block")
            {
                var name = Text(command, "name");
                if (name.Length == 0) return "缺少 name（块名）。";
                var nested = command.ContainsKey("commands") ? command["commands"] as object[] : null;
                if (nested == null || nested.Length == 0) return "缺少 commands（块内图元）。";
                if (definedBlocks.Contains(name)) return "重复定义块：" + name;
                return null;
            }
            if (type == "spline") return CheckPoints(command, "points", 3, "spline 至少需要 3 个点。");
            if (type == "line" || type == "polyline")
            {
                var error = CheckPoints(command, "points", 2, "缺少 points 坐标（至少 2 个点）。");
                if (type == "polyline" && command.ContainsKey("closed") && !(command["closed"] is bool)) return "closed 必须为布尔值。";
                if (command.ContainsKey("width") && (!IsNumber(command["width"]) || ToNumber(command["width"]) < 0)) return "width 不得小于 0。";
                if (error != null || type != "polyline" || !command.ContainsKey("bulges")) return error;
                var bulges = command["bulges"] as object[];
                var points = (object[])command["points"];
                if (bulges == null || bulges.Length != points.Length) return "bulges 须与 points 等长，每个值控制从该顶点出发的弧段。";
                for (var i = 0; i < bulges.Length; i++)
                {
                    if (!IsNumber(bulges[i]) || Math.Abs(ToNumber(bulges[i])) > 10) return "bulge 须为 -10 到 10 的有限数字。";
                    if (i == points.Length - 1 && !(command.ContainsKey("closed") && (bool)command["closed"]) && ToNumber(bulges[i]) != 0)
                        return "开放多段线最后一个 bulge 必须为 0。";
                    if (ToNumber(bulges[i]) != 0 && Distance(points[i], points[(i + 1) % points.Length]) < 1e-8) return "圆弧段端点不能重合。";
                }
                return null;
            }
            if (type == "donut")
            {
                var error = Point(command, "center") ?? Numeric(command, "innerDiameter") ?? Positive(command, "outerDiameter");
                if (error != null) return error;
                return Number(command, "innerDiameter") >= 0 && Number(command, "innerDiameter") < Number(command, "outerDiameter") ? null : "圆环要求 0 ≤ innerDiameter < outerDiameter。";
            }
            if (type == "solid")
            {
                var error = CheckPoints(command, "points", 3, "实心区域需要 3 或 4 个沿边界顺序排列的点。");
                if (error != null) return error;
                var points = (object[])command["points"];
                if (points.Length > 4) return "solid 仅支持三角形或凸四边形。";
                var sign = 0;
                for (var i = 0; i < points.Length; i++)
                {
                    var a = CadPlanQuality.ReadPoint(points[i]); var b = CadPlanQuality.ReadPoint(points[(i + 1) % points.Length]); var c = CadPlanQuality.ReadPoint(points[(i + 2) % points.Length]);
                    var cross = (b[0] - a[0]) * (c[1] - b[1]) - (b[1] - a[1]) * (c[0] - b[0]);
                    if (Math.Abs(cross) < 1e-8 || (sign != 0 && Math.Sign(cross) != sign)) return "solid 边界不得退化、自交或凹陷。";
                    sign = Math.Sign(cross);
                }
                return null;
            }
            if (type == "leader")
            {
                var error = CheckPoints(command, "points", 2, "引线 points 至少需要箭头点和折点。") ?? Point(command, "position");
                if (error != null) return error;
                var points = (object[])command["points"];
                if (points.Length > 32) return "单条引线最多支持 32 个顶点。";
                for (var i = 1; i < points.Length; i++) if (Distance(points[i - 1], points[i]) < 1e-8) return "引线相邻顶点不能重合。";
                if (Text(command, "content").Length == 0) return "引线缺少 content。";
                if (command.ContainsKey("arrowSize")) return Positive(command, "arrowSize");
                return null;
            }
            if (type == "dimangular")
            {
                var error = Point(command, "center") ?? Point(command, "p1") ?? Point(command, "p2") ?? Point(command, "position");
                if (error != null) return error;
                var angle = CadPlanQuality.AngularDegrees(command);
                if (Double.IsNaN(angle) || angle < .00001 || angle > 179.99999) return "角度标注需两条非零、不共线射线，范围为 0 到 180 度的小角。";
                return CadPlanQuality.IsInsideAngularSector(command) ? null : "角度标注 position 必须位于两射线之间的小角扇区。";
            }
            if (type == "rect" || type == "rectangle")
            {
                if (command.ContainsKey("rotation") && IsNumber(command["rotation"]) && ToNumber(command["rotation"]) != 0)
                    return "rect仅支持世界XY轴对齐矩形；旋转轮廓请明确给出polyline顶点。";
                return Point(command, "p1") ?? Point(command, "p2");
            }
            if (type == "ngon" || type == "polygon")
            {
                var error = Point(command, "center");
                if (error != null) return error;
                var sides = Number(command, "sides");
                if (sides < 3 || sides > 128 || sides != Math.Floor(sides)) return "sides 应为 3–128 之间的整数。";
                return Positive(command, "radius");
            }
            if (type == "circle")
            {
                return Point(command, "center") ?? Positive(command, "radius");
            }
            if (type == "arc")
            {
                return Point(command, "center") ?? Positive(command, "radius")
                    ?? Numeric(command, "start") ?? Numeric(command, "end");
            }
            if (type == "ellipse")
            {
                var error = Point(command, "center");
                if (error != null) return error;
                return Positive(command, "major");
            }
            if (type == "point")
            {
                return Point(command, "position");
            }
            if (type == "text" || type == "mtext")
            {
                var error = Point(command, "position");
                if (error != null) return error;
                if (Text(command, "content").Length == 0) return "缺少 content（文字内容）。";
                return null;
            }
            if (type == "hatch")
            {
                var error = CheckPoints(command, "points", 3, "填充边界至少需要 3 个点。");
                if (error != null) return error;
                var material = Text(command, "material");
                if (material.Length > 0)
                {
                    string pattern; double paperSize;
                    if (!HatchMaterialPolicy.TryResolve(material, out pattern, out paperSize)) return "未知填充材料：" + material;
                    if (Text(command, "pattern").Length > 0 && !Text(command, "pattern").Equals(pattern, StringComparison.OrdinalIgnoreCase))
                        return "材料与 pattern 不一致，请仅指定 material 或使用对应预设。";
                }
                if (command.ContainsKey("spacing")) { error = Positive(command, "spacing"); if (error != null) return error; }
                if (command.ContainsKey("holes"))
                {
                    var holes = command["holes"] as object[];
                    if (holes == null || holes.Length > 64) return "holes 必须为最多 64 个内环组成的数组。";
                    foreach (var hole in holes)
                    {
                        error = CheckPoints(new Dictionary<string, object> { { "points", hole } }, "points", 3, "内环至少需要 3 个点。");
                        if (error != null) return error;
                    }
                }
                return HatchBoundaryValidator.Check(command);
            }
            if (type == "dimlinear" || type == "dim")
            {
                return Point(command, "p1") ?? Point(command, "p2") ?? Point(command, "position");
            }
            if (type == "dimaligned")
            {
                return Point(command, "p1") ?? Point(command, "p2") ?? Point(command, "position");
            }
            if (type == "dimradius" || type == "dimdiameter")
            {
                return Point(command, "center") ?? Point(command, "point");
            }
            if (type == "frame")
            {
                var size = Text(command, "size").ToUpperInvariant();
                if (size.Length > 0 && size != "A0" && size != "A1" && size != "A2" && size != "A3" && size != "A4")
                {
                    return "size 仅支持 A0–A4。";
                }
                if (command.ContainsKey("origin")) return Point(command, "origin");
                return null;
            }
            if (type == "textstyle" && Text(command, "name").Length == 0) return "缺少文字样式名。";
            if (type == "setvar")
            {
                var name = Text(command, "name").ToUpperInvariant();
                if (!DrawingVariables.Contains(name)) return "不支持的绘图变量：" + name;
                var numeric = Numeric(command, "value");
                if (numeric != null) return numeric;
                var value = ToNumber(command["value"]);
                if (name == "DIMTAD" || name == "DIMLUNIT" || name == "DIMZIN" || name == "DIMTIH" || name == "DIMTOH")
                {
                    var minimum = name == "DIMLUNIT" ? 1 : 0;
                    var maximum = name == "DIMTAD" ? 4 : name == "DIMLUNIT" ? 6 : name == "DIMZIN" ? 15 : 1;
                    return value >= minimum && value <= maximum && value == Math.Floor(value) ? null : name + " 的整数值超出允许范围。";
                }
                if (name == "DIMTSZ") return value >= 0 ? null : "DIMTSZ 不得小于 0。";
                if (name == "LWDISPLAY" && value != 0 && value != 1) return "LWDISPLAY 仅支持 0/1。";
                if (name == "DIMDEC" && (value < 0 || value > 8 || value != Math.Floor(value))) return "DIMDEC 需为 0–8 整数。";
                if (name != "LWDISPLAY" && name != "DIMDEC" && value <= 0) return name + " 必须大于 0。";
                return null;
            }
            return null;
        }

        internal static readonly HashSet<string> DrawingVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "DIMSCALE", "LTSCALE", "CELTSCALE", "TEXTSIZE", "DIMTXT", "DIMASZ", "DIMEXO", "DIMEXE", "DIMGAP", "DIMDEC", "LWDISPLAY", "DIMLFAC", "DIMTAD", "DIMLUNIT", "DIMZIN", "DIMTIH", "DIMTOH", "DIMTSZ" };

        private static string CheckOptionalNumbers(Dictionary<string, object> command)
        {
            foreach (var key in new[] { "rotation", "angle", "height", "width", "ratio", "lineweight", "color", "leader", "value" })
            {
                if (command.ContainsKey(key) && !IsNumber(command[key])) return key + " 必须是有限数字。";
            }
            foreach (var key in new[] { "height", "ratio" })
                if (command.ContainsKey(key) && ToNumber(command[key]) <= 0) return key + " 必须大于 0。";
            if (command.ContainsKey("ratio") && ToNumber(command["ratio"]) > 1) return "ratio 不得大于 1。";
            if (Text(command, "type").Equals("insert", StringComparison.OrdinalIgnoreCase) && command.ContainsKey("scale"))
                return Positive(command, "scale");
            return null;
        }

        private static string CheckPoints(Dictionary<string, object> command, string key, int min, string message)
        {
            var raw = command.ContainsKey(key) ? command[key] as object[] : null;
            if (raw == null || raw.Length < min) return message;
            if (raw.Length > 10000) return "单条指令点数超过 10000。";
            for (var index = 0; index < raw.Length; index++)
            {
                var point = raw[index];
                if (!IsPoint(point)) return message + "（第 " + (index + 1) + " 个点格式错误）";
            }
            return null;
        }

        private static string Point(Dictionary<string, object> command, string key)
        {
            if (!command.ContainsKey(key) || command[key] == null) return "缺少 " + key + " 坐标。";
            return IsPoint(command[key]) ? null : key + " 坐标格式应为 [x,y]。";
        }

        private static double Distance(object a, object b)
        {
            var x = CadPlanQuality.ReadPoint(a); var y = CadPlanQuality.ReadPoint(b);
            return Math.Sqrt((x[0] - y[0]) * (x[0] - y[0]) + (x[1] - y[1]) * (x[1] - y[1]));
        }

        private static string Positive(Dictionary<string, object> command, string key)
        {
            if (!command.ContainsKey(key)) return "缺少 " + key + "。";
            return IsNumber(command[key]) && ToNumber(command[key]) > 0 ? null : key + " 必须是正数。";
        }

        private static string Numeric(Dictionary<string, object> command, string key)
        {
            if (!command.ContainsKey(key)) return "缺少 " + key + "。";
            return IsNumber(command[key]) ? null : key + " 必须是数字。";
        }

        private static bool IsPoint(object value)
        {
            var array = value as object[];
            if (array != null) return array.Length == 2 && Coordinate(array[0]) && Coordinate(array[1]);
            var map = value as Dictionary<string, object>;
            if (map != null)
            {
                return map.ContainsKey("x") && map.ContainsKey("y") && Coordinate(map["x"]) && Coordinate(map["y"]);
            }
            return false;
        }

        private static bool IsNumber(object value)
        {
            if (value == null) return false;
            double parsed;
            return !(value is bool) && double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float,
                CultureInfo.InvariantCulture, out parsed) && !double.IsNaN(parsed) && !double.IsInfinity(parsed);
        }

        private static bool Coordinate(object value) { return IsNumber(value) && Math.Abs(ToNumber(value)) <= 1e8; }

        private static double ToNumber(object value)
        {
            if (value is double) return (double)value;
            if (value is int) return (int)value;
            if (value is long) return (long)value;
            if (value is decimal) return (double)(decimal)value;
            double parsed;
            return double.TryParse(Convert.ToString(value), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
        }

        private static double Number(Dictionary<string, object> command, string key)
        {
            return command.ContainsKey(key) ? ToNumber(command[key]) : 0;
        }

        private static string Text(Dictionary<string, object> command, string key)
        {
            if (!command.ContainsKey(key) || command[key] == null) return string.Empty;
            return Convert.ToString(command[key]).Trim();
        }
    }
}

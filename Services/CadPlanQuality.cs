using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace AutoCAD.AI.Assistant.Services
{
    /// <summary>Checks stated dimensions against actual geometry, without inventing missing measurements.</summary>
    internal static class CadPlanQuality
    {
        public static bool IsUnit(string unit) { return unit == "mm" || unit == "cm" || unit == "m"; }
        public static double UnitFactor(string unit) { return unit == "cm" ? 10 : unit == "m" ? 1000 : 1; }

        public static double SuggestDimensionScale(Dictionary<string, object> root)
        {
            if (!root.ContainsKey("views")) return 0;
            var minX = double.MaxValue; var minY = double.MaxValue;
            var maxX = double.MinValue; var maxY = double.MinValue;
            foreach (Dictionary<string, object> c in (object[])root["commands"])
            {
                var type = Convert.ToString(c["type"]).ToLowerInvariant();
                if (type == "frame" || type == "block" || type == "insert") return 0;
                if (type == "setvar" && (Convert.ToString(c["name"]).Equals("DIMSCALE", StringComparison.OrdinalIgnoreCase)
                    || Convert.ToString(c["name"]).Equals("DIMTXT", StringComparison.OrdinalIgnoreCase))) return 0;
                if (type.StartsWith("dim", StringComparison.Ordinal) || type == "text" || type == "mtext") continue;
                var points = new List<object>();
                foreach (var field in new[] { "p1", "p2", "center", "position" }) if (c.ContainsKey(field)) points.Add(c[field]);
                if (c.ContainsKey("points")) points.AddRange((object[])c["points"]);
                var radius = c.ContainsKey("radius") ? Number(c["radius"]) : 0;
                foreach (var point in points)
                {
                    var p = ReadPoint(point);
                    minX = Math.Min(minX, p[0] - radius); maxX = Math.Max(maxX, p[0] + radius);
                    minY = Math.Min(minY, p[1] - radius); maxY = Math.Max(maxY, p[1] + radius);
                }
            }
            if (minX == double.MaxValue) return 0;
            var target = Math.Max(maxX - minX, maxY - minY) / 150;
            foreach (var scale in new[] { 1.0, 2, 5, 10, 20, 25, 50, 100, 200, 500, 1000, 2000 })
                if (scale >= target) return scale;
            return 2000;
        }

        public static List<string> DimensionErrors(Dictionary<string, object> root)
        {
            var errors = new List<string>();
            var unit = root.ContainsKey("sourceUnit") ? Convert.ToString(root["sourceUnit"]) : "mm";
            var factor = root.ContainsKey("coordinateUnit") && Convert.ToString(root["coordinateUnit"]) == "source" ? 1 : UnitFactor(unit);
            CheckDimensions((object[])root["commands"], factor, errors, "");
            return errors;
        }

        private static void CheckDimensions(object[] commands, double factor, List<string> errors, string prefix)
        {
            for (var i = 0; i < commands.Length && errors.Count < 12; i++)
            {
                var c = (Dictionary<string, object>)commands[i];
                var type = Convert.ToString(c["type"]).ToLowerInvariant();
                var position = prefix + (i + 1);
                if (type == "block") { CheckDimensions((object[])c["commands"], factor, errors, position + "."); continue; }
                if (!type.StartsWith("dim", StringComparison.Ordinal)) continue;
                var a = ReadPoint(c[type == "dimradius" || type == "dimdiameter" ? "center" : "p1"]);
                var b = ReadPoint(c[type == "dimradius" || type == "dimdiameter" ? "point" : "p2"]);
                var dx = b[0] - a[0]; var dy = b[1] - a[1];
                var length = Math.Sqrt(dx * dx + dy * dy);
                if (type == "dimdiameter") length *= 2;
                if (type == "dimlinear" || type == "dim")
                {
                    var angle = c.ContainsKey("rotation") ? Number(c["rotation"]) * Math.PI / 180 : 0;
                    length = Math.Abs(dx * Math.Cos(angle) + dy * Math.Sin(angle));
                }
                if (length < 1e-7) { errors.Add("第 " + position + " 条：标注测量长度为零。"); continue; }
                if (c.ContainsKey("value"))
                {
                    var expected = Number(c["value"]) * factor;
                    if (expected <= 0 || Math.Abs(length - expected) > Math.Max(0.01, expected * 0.00001))
                        errors.Add("第 " + position + " 条：value=" + c["value"] + "，当前坐标单位下应测得 "
                            + expected.ToString("0.###", CultureInfo.InvariantCulture) + "，实际测得 "
                            + length.ToString("0.###", CultureInfo.InvariantCulture) + "；单位系数=" + factor
                            + "。请核对 sourceUnit/coordinateUnit 和坐标，勿覆盖标注文字或改动原图读数。");
                }
                if (c.ContainsKey("text"))
                {
                    var text = Convert.ToString(c["text"]).Trim();
                    if (text.Length > 0 && !text.Contains("<>"))
                        errors.Add("第 " + position + " 条：标注 text 必须保留 <> 实际测量占位符，不能用固定文字伪装尺寸。");
                }
                if (c.ContainsKey("confidence"))
                {
                    var confidence = Convert.ToString(c["confidence"]);
                    if (confidence != "read" && confidence != "inferred" && confidence != "unknown")
                        errors.Add("第 " + position + " 条：confidence 仅支持 read/inferred/unknown。");
                }
            }
        }

        internal static double Number(object value) { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
        internal static double[] ReadPoint(object value)
        {
            var a = value as object[];
            if (a != null) return new[] { Number(a[0]), Number(a[1]) };
            var d = (Dictionary<string, object>)value;
            return new[] { Number(d["x"]), Number(d["y"]) };
        }

        public static string ImageContractError(string json)
        {
            var root = (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.DeserializeObject(json);
            if (!root.ContainsKey("sourceUnit") || !IsUnit(Convert.ToString(root["sourceUnit"])))
                return "图片复刻必须声明 sourceUnit（mm/cm/m）；源图单位不明时请说明，停止绘图。";
            var views = root.ContainsKey("views") ? root["views"] as object[] : null;
            if (views == null || views.Length == 0) return "图片复刻必须列出实际可见 views 名称，不补画题面要求但未给出的视图。";
            return CheckImageCommands((object[])root["commands"]);
        }

        private static string CheckImageCommands(object[] commands)
        {
            foreach (Dictionary<string, object> c in commands)
            {
                var type = Convert.ToString(c["type"]).ToLowerInvariant();
                if (type == "block") { var nested = CheckImageCommands((object[])c["commands"]); if (nested != null) return nested; }
                if (type.StartsWith("dim", StringComparison.Ordinal) && (!c.ContainsKey("value") || !c.ContainsKey("confidence")))
                    return "图片中的每条尺寸标注需附 value（源图单位）与 confidence（read/inferred/unknown）。";
            }
            return null;
        }

        public static string Describe(string json)
        {
            var root = (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.DeserializeObject(json);
            var read = 0; var estimated = 0;
            Count((object[])root["commands"], ref read, ref estimated);
            return "尺寸：读数 " + read + "，推断/未核实 " + estimated + "（几何一致性通过不代表源图识别正确）。";
        }

        private static void Count(object[] commands, ref int read, ref int estimated)
        {
            foreach (Dictionary<string, object> c in commands)
            {
                var type = Convert.ToString(c["type"]).ToLowerInvariant();
                if (type == "block") Count((object[])c["commands"], ref read, ref estimated);
                else if (type.StartsWith("dim", StringComparison.Ordinal))
                { if (c.ContainsKey("confidence") && Convert.ToString(c["confidence"]) == "read") read++; else estimated++; }
            }
        }
    }
}

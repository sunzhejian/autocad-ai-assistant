using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace AutoCAD.AI.Assistant.Services
{
    /// <summary>Optional, scoped geometric evidence for linear/aligned endpoints.</summary>
    internal static class CadGeometryReferences
    {
        public static List<string> Errors(Dictionary<string, object> root)
        {
            var errors = new List<string>();
            var factor = root.ContainsKey("coordinateUnit") && Convert.ToString(root["coordinateUnit"]) == "source"
                ? CadPlanQuality.UnitFactor(Convert.ToString(root["sourceUnit"])) : 1;
            Check((object[])root["commands"], "", factor, errors);
            return errors;
        }

        internal static string Id(object value)
        {
            var text = value as string;
            return text != null && text.Length <= 64 && !String.IsNullOrWhiteSpace(text)
                && text.IndexOfAny(new[] { '\r', '\n', '\t' }) < 0 ? text.Trim() : null;
        }

        private static void Check(object[] commands, string prefix, double factor, List<string> errors)
        {
            var hasReferences = false;
            foreach (Dictionary<string, object> c in commands)
                if (Convert.ToString(c["type"]).StartsWith("dim",StringComparison.OrdinalIgnoreCase) && c.ContainsKey("reference")) hasReferences = true;
            var targets = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            var duplicates = new HashSet<string>(StringComparer.Ordinal);
            if (hasReferences)
                foreach (Dictionary<string, object> c in commands)
                {
                    var id = c.ContainsKey("id") ? Id(c["id"]) : null;
                    if (id == null) continue;
                    if (targets.ContainsKey(id)) duplicates.Add(id); else targets[id] = c;
                }
            for (var i = 0; i < commands.Length && errors.Count < 12; i++)
            {
                var c = (Dictionary<string, object>)commands[i];
                var path = prefix + (i + 1);
                var type = Convert.ToString(c["type"]).ToLowerInvariant();
                if (type == "block") Check((object[])c["commands"], path + ".", factor, errors);
                if (!type.StartsWith("dim",StringComparison.Ordinal) || !c.ContainsKey("reference")) continue;
                var label = "第 " + path + " 条：";
                if (type != "dimlinear" && type != "dim" && type != "dimaligned")
                { errors.Add(label + "reference 目前仅核对线性/对齐标注端点，不假装支持其他关联类型。"); continue; }
                var reference = Id(c["reference"]);
                if (reference == null)
                { errors.Add(label + "reference 必须是1—64字符的稳定几何id。"); continue; }
                if (duplicates.Contains(reference))
                { errors.Add(label + "引用的几何id重复，无法确定目标：" + reference); continue; }
                Dictionary<string, object> geometry;
                if (!targets.TryGetValue(reference, out geometry))
                { errors.Add(label + "找不到同一作用域的几何reference：" + reference + "；不得跨块或另一个视图猜测目标。"); continue; }
                string unsupported;
                var span = Span(geometry, out unsupported);
                if (unsupported != null) { errors.Add(label + unsupported); continue; }
                var tolerance = Math.Max(.001 / factor, span * 1e-12);
                foreach (var field in new[] { "p1", "p2" })
                {
                    var point = CadPlanQuality.ReadPoint(c[field]);
                    if (!IsFlat(c[field])) { errors.Add(label + field + "不是XY平面点，不能把投影当作已核对。"); continue; }
                    if (Distance(point, geometry) > tolerance)
                        errors.Add(label + field + "不在 reference '" + reference + "' 的实际XY轮廓上（最近距离="
                            + Distance(point, geometry).ToString("G10", CultureInfo.InvariantCulture)
                            + "，当前坐标单位）。核对被测轮廓与端点；不要改读数或虚构辅助轮廓来绕过检查。");
                }
            }
        }

        internal static double NativeTolerance(Dictionary<string, object> geometry)
        {
            string error;
            var span = Span(geometry, out error);
            if (error != null) throw new InvalidOperationException(error);
            return Math.Max(.001, span * 1e-12);
        }

        internal static string RepairCoverageError(string original, string repaired)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
            Dictionary<string, int> before;
            try { before = ReferenceCounts(serializer.DeserializeObject(original) as Dictionary<string, object>); }
            catch (ArgumentException) { return null; } // A syntax repair has no readable prior contract.
            if (before.Count == 0) return null;
            var after = ReferenceCounts(serializer.DeserializeObject(repaired) as Dictionary<string, object>);
            foreach (var pair in before)
            {
                int count;
                if (!after.TryGetValue(pair.Key, out count) || count < pair.Value)
                    return "修正删除了已声明的线性尺寸几何引用，不能把未核对当作已修好。保留同一块作用域的引用标注数量；可以改正reference目标、轮廓和端点。";
            }
            return null;
        }

        private static Dictionary<string, int> ReferenceCounts(Dictionary<string, object> root)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            if (root != null && root.ContainsKey("commands")) CountReferences(root["commands"] as object[], "", 0, result);
            return result;
        }

        private static void CountReferences(object[] commands, string scope, int depth, Dictionary<string, int> counts)
        {
            if (commands == null || depth > 8) return;
            foreach (var item in commands)
            {
                var command = item as Dictionary<string, object>;
                if (command == null || !command.ContainsKey("type")) continue;
                var type = Convert.ToString(command["type"]).ToLowerInvariant();
                if ((type == "dimlinear" || type == "dim" || type == "dimaligned") && command.ContainsKey("reference"))
                {
                    int count; counts.TryGetValue(scope, out count); counts[scope] = count + 1;
                }
                if (type != "block" || !command.ContainsKey("commands")) continue;
                var name = command.ContainsKey("name") ? Convert.ToString(command["name"]) : "";
                CountReferences(command["commands"] as object[], scope + name.Length + ":" + name + "/", depth + 1, counts);
            }
        }

        private static bool IsFlat(object value)
        {
            var a = value as object[];
            if (a != null) return a.Length < 3 || Math.Abs(CadPlanQuality.Number(a[2])) <= 1e-12;
            var d = value as Dictionary<string, object>;
            return d == null || !d.ContainsKey("z") || Math.Abs(CadPlanQuality.Number(d["z"])) <= 1e-12;
        }

        private static double Span(Dictionary<string, object> c, out string error)
        {
            error = null;
            var type = Convert.ToString(c["type"]).ToLowerInvariant();
            if (type == "circle")
            {
                if (!IsFlat(c["center"])) error = "几何引用当前仅支持XY平面，不把3D投影当作已核对。";
                return 2 * CadPlanQuality.Number(c["radius"]);
            }
            if (type != "line" && type != "polyline" && type != "rect" && type != "rectangle")
            { error = "几何reference暂支持直线、矩形、直边多段线和圆；当前类型不能近似判为正确。"; return 0; }
            if (type == "polyline" && c.ContainsKey("bulges"))
                foreach (var b in (object[])c["bulges"])
                    if (Math.Abs(CadPlanQuality.Number(b)) > 1e-12)
                    { error = "含圆弧的多段线reference尚未实现精确核对，不能用弦近似通过。"; return 0; }
            var values = type == "rect" || type == "rectangle" ? new[] { c["p1"], c["p2"] } : (object[])c["points"];
            double minX = Double.MaxValue, minY = Double.MaxValue, maxX = Double.MinValue, maxY = Double.MinValue;
            foreach (var value in values)
            {
                if (!IsFlat(value)) error = "几何引用当前仅支持XY平面，不把3D投影当作已核对。";
                var p = CadPlanQuality.ReadPoint(value);
                minX = Math.Min(minX,p[0]); maxX = Math.Max(maxX,p[0]); minY = Math.Min(minY,p[1]); maxY = Math.Max(maxY,p[1]);
            }
            return Math.Max(maxX-minX,maxY-minY);
        }

        private static double SegmentDistance(double[] p, double[] a, double[] b)
        {
            var x=b[0]-a[0];var y=b[1]-a[1];var square=x*x+y*y;
            var t=square<=1e-24 ? 0 : Math.Max(0,Math.Min(1,((p[0]-a[0])*x+(p[1]-a[1])*y)/square));
            var dx=p[0]-a[0]-t*x;var dy=p[1]-a[1]-t*y;
            return Math.Sqrt(dx*dx+dy*dy);
        }

        private static double Distance(double[] point, Dictionary<string, object> c)
        {
            var type=Convert.ToString(c["type"]).ToLowerInvariant();
            if(type=="circle")
            {
                var center=CadPlanQuality.ReadPoint(c["center"]);var dx=point[0]-center[0];var dy=point[1]-center[1];
                return Math.Abs(Math.Sqrt(dx*dx+dy*dy)-CadPlanQuality.Number(c["radius"]));
            }
            var points=new List<double[]>();
            var closed=false;
            if(type=="rect"||type=="rectangle")
            {
                var a=CadPlanQuality.ReadPoint(c["p1"]);var b=CadPlanQuality.ReadPoint(c["p2"]);
                points.Add(a);points.Add(new[]{b[0],a[1]});points.Add(b);points.Add(new[]{a[0],b[1]});closed=true;
            }
            else
            {
                foreach(var p in (object[])c["points"])points.Add(CadPlanQuality.ReadPoint(p));
                closed=type=="polyline"&&c.ContainsKey("closed")&&(bool)c["closed"];
            }
            var nearest=Double.MaxValue;
            for(var i=0;i<points.Count-1;i++)nearest=Math.Min(nearest,SegmentDistance(point,points[i],points[i+1]));
            if(closed)nearest=Math.Min(nearest,SegmentDistance(point,points[points.Count-1],points[0]));
            return nearest;
        }
    }
}

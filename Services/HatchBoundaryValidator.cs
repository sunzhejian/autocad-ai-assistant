using System;
using System.Collections.Generic;

namespace AutoCAD.AI.Assistant.Services
{
    /// <summary>Polygon topology checks for the straight-edged hatch protocol.</summary>
    internal static class HatchBoundaryValidator
    {
        public static string Check(Dictionary<string, object> command)
        {
            var rings = new List<List<double[]>> { Points((object[])command["points"]) };
            if (command.ContainsKey("holes")) foreach (object[] hole in (object[])command["holes"]) rings.Add(Points(hole));
            var count = 0; var span = 0.0;
            foreach (var ring in rings)
            {
                count += ring.Count;
                foreach (var p in ring) span = Math.Max(span, Math.Max(Math.Abs(p[0] - ring[0][0]), Math.Abs(p[1] - ring[0][1])));
            }
            if (count > 2048) return "填充全部边界合计不得超过 2048 个顶点，请简化边界。";
            var tolerance = Math.Max(1e-9, span * 1e-10);
            for (var r = 0; r < rings.Count; r++)
            {
                var ring = rings[r];
                var label = r == 0 ? "外环" : "内环 " + r;
                if (ring.Count < 3) return label + "至少需要 3 个不同顶点。";
                for (var i = 0; i < ring.Count; i++)
                {
                    var a = ring[i]; var b = ring[(i + 1) % ring.Count]; var c = ring[(i + 2) % ring.Count];
                    if (Distance(a, b) <= tolerance) return label + "含重复顶点或零长度边。";
                    if (Orientation(a, b, c, tolerance) == 0 && (b[0] - a[0]) * (c[0] - b[0]) + (b[1] - a[1]) * (c[1] - b[1]) < 0)
                        return label + "存在回折尖刺，请按边界顺序输出顶点。";
                    for (var j = i + 2; j < ring.Count; j++)
                    {
                        if (i == 0 && j == ring.Count - 1) continue;
                        if (Intersects(a, b, ring[j], ring[(j + 1) % ring.Count], tolerance))
                            return label + "自交（边 " + (i + 1) + " 与 " + (j + 1) + "），请修正边界，勿用固定面积掩盖。";
                    }
                }
                var area = 0.0;
                for (var i = 1; i < ring.Count - 1; i++) area += Cross(ring[0], ring[i], ring[i + 1]);
                if (Math.Abs(area) <= tolerance * tolerance) return label + "面积为零。";
                if (r == 0) continue;
                for (var other = 0; other < r; other++)
                {
                    foreach (var i in Edges(ring)) foreach (var j in Edges(rings[other]))
                        if (Intersects(i[0], i[1], j[0], j[1], tolerance)) return "内孔与外边界或其他内孔相交/接触。";
                    if (other > 0 && (Inside(ring[0], rings[other]) || Inside(rings[other][0], ring))) return "内孔不得重叠或嵌套。";
                }
                if (!Inside(ring[0], rings[0])) return "内孔越出填充外边界。";
            }
            return null;
        }
        internal static bool ContainsMaterial(Dictionary<string, object> command, double[] point)
        {
            var outer = Points((object[])command["points"]);
            if (!Inside(point, outer)) return false;
            if (command.ContainsKey("holes"))
                foreach (object[] hole in (object[])command["holes"])
                    if (Inside(point, Points(hole))) return false;
            return true;
        }

        private static List<double[]> Points(object[] values)
        {
            var points = new List<double[]>(); foreach (var value in values) points.Add(CadPlanQuality.ReadPoint(value));
            if (points.Count > 1 && Distance(points[0], points[points.Count - 1]) < 1e-9) points.RemoveAt(points.Count - 1);
            return points;
        }
        private static IEnumerable<double[][]> Edges(List<double[]> points)
        { for (var i = 0; i < points.Count; i++) yield return new[] { points[i], points[(i + 1) % points.Count] }; }
        private static double Distance(double[] a, double[] b) { var x = b[0] - a[0]; var y = b[1] - a[1]; return Math.Sqrt(x * x + y * y); }
        private static double Cross(double[] a, double[] b, double[] c) { return (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]); }
        private static int Orientation(double[] a, double[] b, double[] c, double tolerance)
        { var value = Cross(a, b, c); var error = tolerance * (Distance(a, b) + Distance(a, c)); return Math.Abs(value) <= error ? 0 : value > 0 ? 1 : -1; }
        private static bool OnSegment(double[] a, double[] b, double[] p, double tolerance)
        { return p[0] >= Math.Min(a[0], b[0]) - tolerance && p[0] <= Math.Max(a[0], b[0]) + tolerance && p[1] >= Math.Min(a[1], b[1]) - tolerance && p[1] <= Math.Max(a[1], b[1]) + tolerance; }
        private static bool Intersects(double[] a, double[] b, double[] c, double[] d, double tolerance)
        {
            var x = Orientation(a, b, c, tolerance); var y = Orientation(a, b, d, tolerance);
            var z = Orientation(c, d, a, tolerance); var w = Orientation(c, d, b, tolerance);
            return (x * y < 0 && z * w < 0) || (x == 0 && OnSegment(a, b, c, tolerance)) || (y == 0 && OnSegment(a, b, d, tolerance))
                || (z == 0 && OnSegment(c, d, a, tolerance)) || (w == 0 && OnSegment(c, d, b, tolerance));
        }
        private static bool Inside(double[] p, List<double[]> ring)
        {
            var inside = false;
            for (var i = 0; i < ring.Count; i++)
            {
                var a = ring[i]; var b = ring[(i + 1) % ring.Count];
                if ((a[1] > p[1]) != (b[1] > p[1]) && p[0] < (b[0] - a[0]) * (p[1] - a[1]) / (b[1] - a[1]) + a[0]) inside = !inside;
            }
            return inside;
        }
    }
}

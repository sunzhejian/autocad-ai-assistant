using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace AutoCAD.AI.Assistant.Services
{
    /// <summary>Converts explicitly declared source-unit geometry to millimeters once, locally.</summary>
    internal static class CadPlanNormalizer
    {
        public static string Normalize(string json)
        {
            bool modelDimensionLengths;
            return Normalize(json, out modelDimensionLengths);
        }

        public static string Normalize(string json, out bool modelDimensionLengths)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
            var root = (Dictionary<string, object>)serializer.DeserializeObject(json);
            modelDimensionLengths = root.ContainsKey("_dimensionLengthsInModelUnits") && root["_dimensionLengthsInModelUnits"] is bool && (bool)root["_dimensionLengthsInModelUnits"];
            if (!root.ContainsKey("coordinateUnit") || Convert.ToString(root["coordinateUnit"]) != "source") return json;
            modelDimensionLengths = true;
            var factor = CadPlanQuality.UnitFactor(Convert.ToString(root["sourceUnit"]));
            Transform((object[])root["commands"], factor);
            root["coordinateUnit"] = "mm";
            root["_dimensionLengthsInModelUnits"] = true;
            return serializer.Serialize(root);
        }

        private static void Transform(object[] commands, double factor)
        {
            foreach (Dictionary<string, object> c in commands)
            {
                var type = Convert.ToString(c["type"]).ToLowerInvariant();
                if (type == "block") Transform((object[])c["commands"], factor);
                if (type == "setvar" && c.ContainsKey("value"))
                {
                    var name = Convert.ToString(c["name"]).ToUpperInvariant();
                    if (name == "TEXTSIZE" || name == "DIMTXT" || name == "DIMASZ" || name == "DIMEXO" || name == "DIMEXE" || name == "DIMGAP" || name == "DIMTSZ")
                        c["value"] = CadPlanQuality.Number(c["value"]) * factor;
                }
                foreach (var field in new[] { "p1", "p2", "center", "position", "point", "origin" })
                    if (c.ContainsKey(field)) c[field] = Point(c[field], factor);
                if (c.ContainsKey("points")) c["points"] = Points((object[])c["points"], factor);
                if (c.ContainsKey("holes"))
                {
                    var holes = (object[])c["holes"];
                    for (var i = 0; i < holes.Length; i++) holes[i] = Points((object[])holes[i], factor);
                }
                // lineweight is always a paper width in mm; scale, rotation, ratio and value are dimensionless/source readings.
                if (type != "frame" && type != "layer" && type != "setvar" && type != "standard" && type != "textstyle")
                    foreach (var field in new[] { "radius", "major", "height", "width", "leader", "spacing", "innerDiameter", "outerDiameter", "arrowSize", "dashLength" })
                        if (c.ContainsKey(field)) c[field] = CadPlanQuality.Number(c[field]) * factor;
            }
        }
        private static object[] Points(object[] points, double factor)
        {
            var result = new object[points.Length];
            for (var i = 0; i < points.Length; i++) result[i] = Point(points[i], factor);
            return result;
        }
        private static object Point(object point, double factor)
        {
            var p = CadPlanQuality.ReadPoint(point);
            return new object[] { p[0] * factor, p[1] * factor };
        }
    }
}

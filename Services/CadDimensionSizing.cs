using System;
using System.Collections.Generic;

namespace AutoCAD.AI.Assistant.Services
{
    /// <summary>Tracks explicitly source-sized dimension lengths in execution order.</summary>
    internal static class CadDimensionSizing
    {
        private static readonly HashSet<string> Lengths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "DIMTXT", "DIMASZ", "DIMEXO", "DIMEXE", "DIMGAP", "DIMTSZ" };

        public static Dictionary<string, double> Prepare(object[] commands, bool modelLengths)
        {
            var active = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            Visit(commands, modelLengths, active);
            return active;
        }

        private static void Visit(object[] commands, bool modelLengths, Dictionary<string, double> active)
        {
            foreach (Dictionary<string, object> command in commands)
            {
                command.Remove("_modelDimensionSizes");
                var type = Convert.ToString(command["type"]).ToLowerInvariant();
                if (type == "block") Visit((object[])command["commands"], modelLengths, active);
                else if (type == "standard") active.Clear();
                else if (type == "setvar" && modelLengths)
                {
                    var name = Convert.ToString(command["name"]);
                    if (Lengths.Contains(name)) active[name.ToUpperInvariant()] = CadPlanQuality.Number(command["value"]);
                }
                else if (type.StartsWith("dim", StringComparison.Ordinal) && active.Count > 0)
                    command["_modelDimensionSizes"] = new Dictionary<string, double>(active, StringComparer.OrdinalIgnoreCase);
            }
        }

        public static double PaperLength(double modelLength, double scale)
        {
            if (scale <= 0 || Double.IsNaN(scale) || Double.IsInfinity(scale))
                throw new InvalidOperationException("源单位标注长度需要明确且大于0的 DIMSCALE，不能猜测注释比例。");
            var result = modelLength / scale;
            if (Double.IsNaN(result) || Double.IsInfinity(result)) throw new InvalidOperationException("标注长度与比例的换算超出数值范围。");
            return result;
        }
    }
}

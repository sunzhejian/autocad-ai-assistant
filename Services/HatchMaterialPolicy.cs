using System;
using System.Collections.Generic;

namespace AutoCAD.AI.Assistant.Services
{
    /// <summary>Material presets describe visible feature size on paper, independently of boundary length.</summary>
    internal static class HatchMaterialPolicy
    {
        public static bool TryResolve(string material, out string pattern, out double paperSize)
        {
            pattern = null; paperSize = 0;
            switch ((material ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "riprap": case "块石": case "堆石": case "砌石": pattern = "GRAVEL"; paperSize = 3; return true;
                case "gravel": case "碎石": case "砾石": case "砂砾": pattern = "GRAVEL"; paperSize = .8; return true;
                case "sand": case "粗砂": case "砂": pattern = "AR-SAND"; paperSize = .45; return true;
                case "earth": case "土": case "土体": pattern = "EARTH"; paperSize = 2.5; return true;
                case "clay": case "粘土": case "黏土": pattern = "ANSI31"; paperSize = 1.5; return true;
                case "concrete": case "混凝土": pattern = "AR-CONC"; paperSize = 3; return true;
                default: return false;
            }
        }

        public static double FeatureSize(string pattern, List<double> positiveDashes, List<double> negativeDashes, List<double> offsets)
        {
            switch ((pattern ?? string.Empty).ToUpperInvariant())
            {
                case "GRAVEL": case "AR-CONC": return 2 * Median(positiveDashes, 1);
                case "AR-SAND": case "DOTS": return Median(negativeDashes, 1) / 2;
                case "EARTH": return Median(positiveDashes, 1);
                default: return Median(offsets, 1);
            }
        }

        public static bool TooDense(double area, double spacing) { return spacing > 0 && area / (spacing * spacing) > 250000; }
        private static double Median(List<double> values, double fallback)
        {
            if (values.Count == 0) return fallback;
            var sorted = new List<double>(values); sorted.Sort();
            return Math.Max(1e-8, sorted[sorted.Count / 2]);
        }
    }
}

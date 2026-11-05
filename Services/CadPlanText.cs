using System;
using System.Text.RegularExpressions;

namespace AutoCAD.AI.Assistant.Services
{
    internal static class CadPlanText
    {
        private static readonly Regex PlanBlock = new Regex(@"```(?:cad|json)\s*\r?\n([\s\S]*?)```", RegexOptions.IgnoreCase);

        public static string Extract(string answer)
        {
            if (string.IsNullOrWhiteSpace(answer)) return null;
            var matches = PlanBlock.Matches(answer);
            if (matches.Count > 1) throw new InvalidOperationException("返回了多个绘图计划，无法确定执行范围。");
            if (matches.Count == 1) return matches[0].Groups[1].Value.Trim();
            var raw = answer.Trim();
            return raw.StartsWith("{", StringComparison.Ordinal) ? raw : null;
        }

        public static string Remove(string answer)
        {
            return string.IsNullOrEmpty(answer) ? string.Empty : PlanBlock.Replace(answer, string.Empty).Trim();
        }
    }
}

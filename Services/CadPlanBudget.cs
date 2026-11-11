using System;
using System.Collections.Generic;

namespace AutoCAD.AI.Assistant.Services
{
    /// <summary>Bound work before expensive polygon checks or the CAD command thread.</summary>
    internal sealed class CadPlanBudget
    {
        internal const int MaxEntities = 5000;
        internal const int MaxInputPoints = 20000;
        internal const int MaxHatchPoints = 8192;
        internal const int MaxTextCharacters = 40000;
        internal const int MaxExpandedEntities = 25000;
        internal const int MaxExpandedWork = 200000;
        private int _commands, _entities, _inputPoints, _hatchPoints, _textCharacters;
        private string _error;
        private readonly Dictionary<string, Cost> _blocks = new Dictionary<string, Cost>(StringComparer.OrdinalIgnoreCase);

        private struct Cost
        {
            public int Entities;
            public int Work;
            public void Add(Cost other)
            {
                Entities = Math.Min(MaxExpandedEntities + 1, Entities + other.Entities);
                Work = Math.Min(MaxExpandedWork + 1, Work + other.Work);
            }
        }

        internal static string Check(object[] commands)
        {
            var budget = new CadPlanBudget();
            budget.Walk(commands, "", 0);
            return budget._error;
        }

        private Cost Walk(object[] commands, string prefix, int depth)
        {
            var total = new Cost();
            if (commands == null) return total; // The structural validator supplies detailed errors.
            if (depth > 8) { _error = "块嵌套超过 8 层。"; return total; }
            for (var index = 0; index < commands.Length && _error == null; index++)
            {
                if (++_commands > 400) { _error = "指令总数（含块内部）超过上限 400。"; break; }
                var command = commands[index] as Dictionary<string, object>;
                if (command == null) continue;
                var path = prefix + (index + 1);
                var type = Text(command, "type").ToLowerInvariant();
                if (type == "block")
                {
                    var body = Walk(Array(command, "commands"), path + ".", depth + 1);
                    _blocks[Text(command, "name")] = body;
                    continue;
                }
                if (type == "setvar" || type == "standard" || type == "layer" || type == "textstyle") continue;
                var points = Array(command, "points");
                var pointCount = points == null ? 0 : points.Length;
                var holeCount = 0;
                var holes = Array(command, "holes");
                if (holes != null)
                    foreach (var hole in holes) { var ring = hole as object[]; if (ring != null) holeCount += ring.Length; }
                _inputPoints += pointCount + holeCount;
                foreach (var field in new[] { "p1", "p2", "center", "position", "point", "origin" })
                    if (command.ContainsKey(field)) _inputPoints++;
                var characters = type == "text" || type == "mtext" || type == "leader" ? Text(command, "content").Length : 0;
                if (type.StartsWith("dim", StringComparison.Ordinal)) characters += Text(command, "text").Length;
                if (type == "frame")
                    foreach (var field in new[] { "title", "number", "company", "date" }) characters += Text(command, field).Length;
                _textCharacters += characters;
                var entities = type == "line" ? Math.Max(0, pointCount - 1) : type == "frame" ? 100 : 1;
                _entities += entities;
                if (type == "hatch") _hatchPoints += pointCount + holeCount;
                var cost = new Cost { Entities = entities, Work = Math.Max(4, pointCount + holeCount) + characters };
                if (type == "line") cost.Work = 2 * entities;
                if (type == "circle" || type == "arc" || type == "ellipse" || type == "donut") cost.Work = 64;
                if (type == "frame") cost.Work = 500 + characters;
                if (type.StartsWith("dim", StringComparison.Ordinal)) cost.Work = 32 + characters;
                if (type == "ngon" || type == "polygon") cost.Work = Sides(command);
                if (type == "insert")
                {
                    Cost definition;
                    if (_blocks.TryGetValue(Text(command, "name"), out definition)) cost.Add(definition);
                }
                total.Add(cost);
                var reason = _entities > MaxEntities ? "预计新建图元超过 " + MaxEntities + "，连续线段优先改用polyline"
                    : _inputPoints > MaxInputPoints ? "全计划输入顶点超过 " + MaxInputPoints
                    : _hatchPoints > MaxHatchPoints ? "全计划填充边界顶点超过 " + MaxHatchPoints
                    : _textCharacters > MaxTextCharacters ? "全计划文字超过 " + MaxTextCharacters + " 字符"
                    : total.Entities > MaxExpandedEntities ? "块及插入后的展开图元估计超过 " + MaxExpandedEntities
                    : total.Work > MaxExpandedWork ? "块及插入后的绘制工作量估计超过 " + MaxExpandedWork
                    : null;
                if (reason != null) _error = "第 " + path + " 条：" + reason + "。请分批处理或简化重复结构，保留关键轮廓和源图尺寸；尚未进入CAD执行。";
            }
            return total;
        }

        private static object[] Array(Dictionary<string, object> command, string key)
        { object value; return command.TryGetValue(key, out value) ? value as object[] : null; }
        private static string Text(Dictionary<string, object> command, string key)
        { object value; return command.TryGetValue(key, out value) ? Convert.ToString(value) : ""; }
        private static int Sides(Dictionary<string, object> command)
        {
            object value;
            if (!command.TryGetValue("sides", out value)) return 6;
            try { var number = Convert.ToDouble(value); return Double.IsNaN(number) ? 0 : (int)Math.Max(0, Math.Min(129, number)); }
            catch (FormatException) { return 0; }
            catch (InvalidCastException) { return 0; }
            catch (OverflowException) { return 0; }
        }
    }
}

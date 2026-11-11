using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace AutoCAD.AI.Assistant.Services
{
    /// <summary>Resolve explicit rectangle-side intent, never infer a target or change a source reading.</summary>
    internal static class CadDimensionAnchors
    {
        internal static string ResolveJson(string json)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
            var root = (Dictionary<string, object>)serializer.DeserializeObject(json);
            bool changed;
            var error = Resolve(root, out changed);
            if (error != null) throw new InvalidOperationException(error);
            return changed ? serializer.Serialize(root) : json;
        }

        internal static string Resolve(Dictionary<string, object> root, out bool changed)
        {
            changed = false;
            var errors = new List<string>();
            object commands;
            if (root.TryGetValue("commands", out commands))
                ResolveCommands(commands as object[], "", errors, ref changed);
            return errors.Count == 0 ? null : String.Join("\n", errors.ToArray());
        }

        private static void ResolveCommands(object[] commands, string prefix, List<string> errors, ref bool changed)
        {
            if (commands == null) return;
            for (var i = 0; i < commands.Length; i++)
            {
                var command = commands[i] as Dictionary<string, object>;
                if (command == null) continue;
                var type = Text(command, "type").ToLowerInvariant();
                if (type != "rect" && type != "rectangle") continue;
                if (!command.ContainsKey("points")) continue;
                if (command.ContainsKey("p1") || command.ContainsKey("p2"))
                { errors.Add("第 " + prefix + (i + 1) + " 条：矩形points简写不能与p1/p2混用。"); continue; }
                var points = command["points"] as object[];
                if (points == null || points.Length != 2)
                { errors.Add("第 " + prefix + (i + 1) + " 条：矩形points必须恰好是两个对角点，不从任意多边形猜测包围框。"); continue; }
                // Exact format adaptation only: no coordinate is inferred, rounded,
                // translated or changed. The normal point validator still runs.
                command["p1"] = points[0]; command["p2"] = points[1]; command.Remove("points"); changed = true;
            }
            if (errors.Count > 0) return;
            var targets = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            var duplicates = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in commands)
            {
                var command = item as Dictionary<string, object>;
                if (command == null || !command.ContainsKey("id")) continue;
                var id = CadGeometryReferences.Id(command["id"]);
                if (id == null) continue;
                if (targets.ContainsKey(id)) duplicates.Add(id); else targets[id] = command;
            }
            for (var i = 0; i < commands.Length && errors.Count < 12; i++)
            {
                var command = commands[i] as Dictionary<string, object>;
                if (command == null) continue;
                var type = Text(command, "type").ToLowerInvariant();
                var path = prefix + (i + 1);
                if (type == "block")
                {
                    object body;
                    if (command.TryGetValue("commands", out body)) ResolveCommands(body as object[], path + ".", errors, ref changed);
                }
                if (!type.StartsWith("dim", StringComparison.Ordinal) || !command.ContainsKey("side")) continue;
                var error = ResolveCommand(command, type, targets, duplicates);
                if (error != null) errors.Add("第 " + path + " 条：" + error); else changed = true;
            }
        }

        private static string ResolveCommand(Dictionary<string, object> command, string type,
            Dictionary<string, Dictionary<string, object>> targets, HashSet<string> duplicates)
        {
            if (type != "dimlinear" && type != "dim") return "side端点计算目前仅支持矩形的线性标注；其他标注请明确给出端点。";
            if (command.ContainsKey("p1") || command.ContainsKey("p2"))
                return "side简写不能与p1/p2混用，避免端点意图冲突。";
            var side = command["side"] as string;
            if (side != "top" && side != "bottom" && side != "left" && side != "right")
                return "side只支持top/bottom/left/right，按世界坐标XY方向计算。";
            var reference = command.ContainsKey("reference") ? CadGeometryReferences.Id(command["reference"]) : null;
            if (reference == null) return "side必须同时声明reference，指向实际被测矩形的id，不能猜测目标。";
            Dictionary<string, object> target;
            if (duplicates.Contains(reference)) return "side引用的矩形id重复：" + reference;
            if (!targets.TryGetValue(reference, out target)) return "side找不到同一块作用域的reference：" + reference;
            var targetType = Text(target, "type").ToLowerInvariant();
            if (targetType != "rect" && targetType != "rectangle")
                return "side目前只计算rect/rectangle的整体边长；曲线、多段线或局部尺寸请给明确p1/p2。";
            double[] a, b;
            if (!TryPoint(target, "p1", out a) || !TryPoint(target, "p2", out b))
                return "side目标矩形缺少合法p1/p2坐标。";
            var x0 = Math.Min(a[0], b[0]); var x1 = Math.Max(a[0], b[0]);
            var y0 = Math.Min(a[1], b[1]); var y1 = Math.Max(a[1], b[1]);
            var vertical = side == "left" || side == "right";
            if (command.ContainsKey("rotation"))
            {
                try { if (CadPlanQuality.Number(command["rotation"]) != (vertical ? 90 : 0)) return "rotation与side方向不一致，不能悄悄覆盖。"; }
                catch (FormatException) { return "rotation必须是与side一致的数字。"; }
                catch (InvalidCastException) { return "rotation必须是与side一致的数字。"; }
                catch (OverflowException) { return "rotation必须是与side一致的数字。"; }
            }
            var x = side == "left" ? x0 : x1;
            var y = side == "bottom" ? y0 : y1;
            command["p1"] = vertical ? new object[] { x, y0 } : new object[] { x0, y };
            command["p2"] = vertical ? new object[] { x, y1 } : new object[] { x1, y };
            command["rotation"] = vertical ? 90 : 0;
            command.Remove("side");
            return null;
        }

        private static bool TryPoint(Dictionary<string, object> command, string key, out double[] point)
        {
            point = null; object value;
            if (!command.TryGetValue(key, out value)) return false;
            var array = value as object[];
            var map = value as Dictionary<string, object>;
            if ((array == null || array.Length < 2) && (map == null || !map.ContainsKey("x") || !map.ContainsKey("y"))) return false;
            try { point = CadPlanQuality.ReadPoint(value); }
            catch (InvalidCastException) { return false; }
            catch (FormatException) { return false; }
            catch (OverflowException) { return false; }
            return !Double.IsNaN(point[0]) && !Double.IsInfinity(point[0]) && !Double.IsNaN(point[1]) && !Double.IsInfinity(point[1]);
        }

        private static string Text(Dictionary<string, object> command, string key)
        { object value; return command.TryGetValue(key, out value) ? Convert.ToString(value) : ""; }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace AutoCAD.AI.Assistant.Services
{
    /// <summary>Pure style/layout patching of a hash-bound plan, never direct editing of a drawing.</summary>
    internal static class CadPlanPatch
    {
        private static readonly Regex Block = new Regex(@"```(?:cadpatch|json)\s*\r?\n([\s\S]*?)```", RegexOptions.IgnoreCase);
        private static readonly HashSet<string> Variables = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "LTSCALE", "DIMSCALE", "DIMTXT", "DIMASZ", "DIMTSZ", "DIMEXO", "DIMEXE", "DIMGAP", "DIMTAD", "DIMTIH", "DIMTOH"
        };

        internal static string Hash(string text)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        internal static string Extract(string answer)
        {
            if (String.IsNullOrWhiteSpace(answer)) throw new InvalidOperationException("缺少修正补丁。");
            var matches = Block.Matches(answer);
            if (matches.Count > 1) throw new InvalidOperationException("修正补丁必须唯一。");
            if (matches.Count == 1) return matches[0].Groups[1].Value.Trim();
            if (answer.TrimStart().StartsWith("{", StringComparison.Ordinal)) return answer.Trim();
            throw new InvalidOperationException("没有可解析的cadpatch补丁。");
        }

        internal static string Context(string original)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
            var root = (Dictionary<string, object>)json.DeserializeObject(original);
            var commands = (object[])root["commands"];
            var targets = new List<object>(); var geometry = new List<object>(); var styles = new Dictionary<string, object>();
            Describe(commands, "", targets, geometry, styles);
            var complex = root.ContainsKey("views") && root["views"] is object[] && ((object[])root["views"]).Length > 1;
            var afterGeometry = false;
            foreach (Dictionary<string, object> command in commands)
            {
                var type = Convert.ToString(command["type"]).ToLowerInvariant();
                if (type == "block" || type == "insert" || type == "frame" || (afterGeometry && type == "setvar")) complex = true;
                if (!Setting(command)) afterGeometry = true;
            }
            var result = new Dictionary<string, object> {
                {"base_sha256",Hash(original)}, {"sourceUnit",root.ContainsKey("sourceUnit") ? root["sourceUnit"] : "mm"},
                {"coordinateUnit",root.ContainsKey("coordinateUnit") ? root["coordinateUnit"] : "mm"},
                {"can_setvars",!complex}, {"current_style_settings",styles}, {"geometry_context",geometry}, {"editable_targets",targets}
            };
            var text = json.Serialize(result);
            if (text.Length > 24000) throw new InvalidOperationException("可编辑目标摘要过大，请缩小修正范围，不会截断目标列表。");
            return text;
        }

        private static void Describe(object[] commands, string prefix, List<object> targets, List<object> geometry, Dictionary<string, object> styles)
        {
            for (var i = 0; i < commands.Length; i++)
            {
                var command = (Dictionary<string, object>)commands[i];
                var type = Convert.ToString(command["type"]).ToLowerInvariant(); var path = prefix + (i + 1);
                if (type == "block") Describe((object[])command["commands"], path + ".", targets, geometry, styles);
                if (type == "setvar" && Variables.Contains(Convert.ToString(command["name"]))) styles[Convert.ToString(command["name"])] = command["value"];
                if (type == "rect" || type == "rectangle")
                {
                    var reference = new Dictionary<string, object> { {"command",path}, {"type",type} };
                    foreach (var key in new[] {"id","p1","p2","points"}) if (command.ContainsKey(key)) reference[key] = command[key];
                    geometry.Add(reference);
                }
                var allowed = new List<string>();
                foreach (var field in new[] {"position","height","width","rotation","arrowSize","spacing","scale","angle","lineweight","dashLength"}) if (Allowed(type,field)) allowed.Add(field);
                if (allowed.Count == 0) continue;
                var target = new Dictionary<string, object> { {"command",path}, {"type",type}, {"allowed_fields",allowed} };
                foreach (var key in new[] {"reference","side","value","confidence","position","height","width","rotation","arrowSize","spacing","scale","angle","lineweight","pattern","material","layer","dashLength"})
                    if (command.ContainsKey(key)) target[key] = command[key];
                if (command.ContainsKey("content"))
                {
                    var content = Convert.ToString(command["content"]);
                    target["content_preview"] = content.Length > 128 ? content.Substring(0,128) : content;
                    target["content_characters"] = content.Length;
                }
                targets.Add(target);
            }
        }

        internal static string Apply(string original, string patchJson)
        {
            if (original == null || patchJson == null || patchJson.Length > 65536)
                throw new InvalidOperationException("原计划或补丁大小无效。");
            var json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
            var patch = json.DeserializeObject(patchJson) as Dictionary<string, object>;
            if (patch == null) throw new InvalidOperationException("补丁必须是JSON对象。");
            OnlyKeys(patch, new[] { "base_sha256", "updates", "setvars" });
            object hash;
            if (!patch.TryGetValue("base_sha256", out hash) || !(hash is string) || (string)hash != Hash(original))
                throw new InvalidOperationException("原计划哈希已改变，拒绝应用过期补丁。");
            var originalError = CadPlanValidator.Validate(original);
            if (originalError != null) throw new InvalidOperationException("原计划不具备有效几何，不能用样式补丁掩盖：" + originalError);
            var root = (Dictionary<string, object>)json.DeserializeObject(original);
            var commands = (object[])root["commands"];
            var edits = 0;
            object raw;
            if (patch.TryGetValue("updates", out raw))
            {
                var updates = raw as object[];
                if (updates == null || updates.Length > 16) throw new InvalidOperationException("updates必须是最多16项的数组。");
                var used = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in updates)
                {
                    var update = item as Dictionary<string, object>;
                    if (update == null) throw new InvalidOperationException("每个修改必须是对象。");
                    OnlyKeys(update, new[] { "command", "fields" });
                    object commandPath, values;
                    if (!update.TryGetValue("command", out commandPath) || !(commandPath is string) || !used.Add((string)commandPath))
                        throw new InvalidOperationException("修改必须有唯一command路径。");
                    var command = Find(commands, (string)commandPath);
                    var fields = update.TryGetValue("fields", out values) ? values as Dictionary<string, object> : null;
                    if (fields == null || fields.Count == 0) throw new InvalidOperationException("缺少需要修改的fields。");
                    var type = Convert.ToString(command["type"]).ToLowerInvariant();
                    foreach (var field in fields)
                    {
                        if (!Allowed(type, field.Key)) throw new InvalidOperationException("样式补丁不允许修改 " + type + "." + field.Key);
                        if (field.Key == "position")
                        {
                            var point = field.Value as object[];
                            if (point == null || point.Length != 2 || !Finite(point[0]) || !Finite(point[1]))
                                throw new InvalidOperationException("position必须是两个有限数字组成的XY点。");
                        }
                        else
                        {
                            if (!Finite(field.Value)) throw new InvalidOperationException(field.Key + "必须是有限数字。");
                            var number = CadPlanQuality.Number(field.Value);
                            if ((field.Key == "height" || field.Key == "width" || field.Key == "spacing" || field.Key == "scale" || field.Key == "arrowSize" || field.Key == "dashLength") && number <= 0)
                                throw new InvalidOperationException(field.Key + "必须大于0。");
                            if (field.Key == "lineweight" && (number < 0 || number > 2.11)) throw new InvalidOperationException("lineweight超出允许的纸面毫米范围。");
                            if (Math.Abs(number) > 1000000) throw new InvalidOperationException(field.Key + "超出样式补丁允许范围。");
                        }
                        command[field.Key] = field.Value;
                    }
                    edits++;
                }
            }
            if (patch.TryGetValue("setvars", out raw))
            {
                var variables = raw as Dictionary<string, object>;
                if (variables == null || variables.Count > Variables.Count) throw new InvalidOperationException("setvars必须是有限样式变量的对象。");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in variables)
                {
                    if (!Variables.Contains(pair.Key) || !seen.Add(pair.Key) || !Finite(pair.Value) || Math.Abs(CadPlanQuality.Number(pair.Value)) > 1000000)
                        throw new InvalidOperationException("补丁包含不允许的样式变量或值：" + pair.Key);
                }
                if (variables.Count > 0)
                {
                    var views = root.ContainsKey("views") ? root["views"] as object[] : null;
                    if (views != null && views.Length > 1) throw new InvalidOperationException("多视图计划不能应用全局样式补丁。");
                    foreach (Dictionary<string, object> command in commands)
                        if (new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "block", "insert", "frame" }.Contains(Convert.ToString(command["type"])))
                            throw new InvalidOperationException("含块或图框的计划不能应用全局样式补丁。");
                    var prefix = 0;
                    while (prefix < commands.Length && Setting((Dictionary<string, object>)commands[prefix])) prefix++;
                    var settingsEnd = prefix;
                    var list = new List<object>(commands);
                    foreach (var pair in variables)
                    {
                        var found = false;
                        for (var i = 0; i < commands.Length; i++)
                        {
                            var command = (Dictionary<string, object>)commands[i];
                            if (!Convert.ToString(command["type"]).Equals("setvar", StringComparison.OrdinalIgnoreCase)
                                || !Convert.ToString(command["name"]).Equals(pair.Key, StringComparison.OrdinalIgnoreCase)) continue;
                            if (i >= settingsEnd) throw new InvalidOperationException("计划中途改变样式，不能用一个全局补丁覆盖多阶段意图。");
                            command["value"] = pair.Value; found = true;
                        }
                        if (!found) list.Insert(prefix++, new Dictionary<string, object> { { "type", "setvar" }, { "name", pair.Key.ToUpperInvariant() }, { "value", pair.Value } });
                        edits++;
                    }
                    root["commands"] = list.ToArray();
                }
            }
            if (edits == 0) throw new InvalidOperationException("补丁没有明确修改项。");
            var next = json.Serialize(root);
            PreparedCadPlan.Create(next); // Includes source normalization, geometry/reference and budget checks.
            return next;
        }

        private static bool Allowed(string type, string field)
        {
            if (type == "line" || type == "polyline") return field == "dashLength";
            if (type == "dim" || type == "dimlinear" || type == "dimaligned") return field == "position";
            if (type == "text" || type == "mtext") return field == "position" || field == "height" || field == "rotation" || (type == "mtext" && field == "width");
            if (type == "leader") return field == "position" || field == "height" || field == "arrowSize";
            if (type == "hatch") return field == "spacing" || field == "scale" || field == "angle" || field == "lineweight";
            return false;
        }

        private static bool Setting(Dictionary<string, object> command)
        {
            var type = Convert.ToString(command["type"]).ToLowerInvariant();
            return type == "layer" || type == "textstyle" || type == "setvar" || type == "standard";
        }

        private static Dictionary<string, object> Find(object[] commands, string path)
        {
            if (!Regex.IsMatch(path, @"^[1-9][0-9]*(\.[1-9][0-9]*){0,8}$")) throw new InvalidOperationException("command路径格式无效。");
            Dictionary<string, object> command = null;
            var parts = path.Split('.');
            for (var i = 0; i < parts.Length; i++)
            {
                int number;
                if (!Int32.TryParse(parts[i], out number) || number > commands.Length) throw new InvalidOperationException("command路径不存在。");
                command = (Dictionary<string, object>)commands[number - 1];
                if (i + 1 == parts.Length) break;
                if (!Convert.ToString(command["type"]).Equals("block", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("command路径不能穿过普通图元。");
                commands = (object[])command["commands"];
            }
            return command;
        }

        private static void OnlyKeys(Dictionary<string, object> value, string[] keys)
        {
            var allowed = new HashSet<string>(keys, StringComparer.Ordinal);
            foreach (var key in value.Keys) if (!allowed.Contains(key)) throw new InvalidOperationException("补丁包含未支持的字段：" + key);
        }

        private static bool Finite(object value)
        {
            if (!(value is int || value is long || value is double || value is decimal)) return false;
            var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return !Double.IsNaN(number) && !Double.IsInfinity(number);
        }
    }
}

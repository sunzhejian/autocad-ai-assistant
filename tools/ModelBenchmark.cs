using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

// Uses the actual plugin DLL and the user's DPAPI-protected configuration; no credentials are exported.
internal static class ModelBenchmark
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
    private static int Main(string[] args)
    {
        System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072;
        if (args.Length < 3) { Console.WriteLine("ModelBenchmark plugin.dll image.png|- result.json [prompt]; CADAI_BENCH_MODEL selects a configured model ID"); return 2; }
        var report = new Dictionary<string, object>();
        var timer = Stopwatch.StartNew();
        try
        {
            var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
            var storeType = assembly.GetType("AutoCAD.AI.Assistant.Services.ModelSettingsStore", true);
            var store = Activator.CreateInstance(storeType, true);
            var configs = ((IEnumerable)storeType.GetMethod("Load").Invoke(store, null)).Cast<object>().ToList();
            if (configs.Count == 0) throw new Exception("No configured model.");
            var modelType = assembly.GetType("AutoCAD.AI.Assistant.Models.ModelConfiguration", true);
            var requested = Environment.GetEnvironmentVariable("CADAI_BENCH_MODEL");
            var model = String.IsNullOrWhiteSpace(requested) ? configs[0] : configs.SingleOrDefault(c => String.Equals(Convert.ToString(modelType.GetProperty("ModelId").GetValue(c, null)), requested, StringComparison.OrdinalIgnoreCase));
            if (model == null) throw new Exception("The requested model ID is not configured.");
            report["model"] = modelType.GetProperty("ModelId").GetValue(model, null);
            if (args.Length == 5 && args[3] == "--cache-plan")
            {
                var cacheType = assembly.GetType("AutoCAD.AI.Assistant.Services.DrawingPlanCache", true);
                var cache = Activator.CreateInstance(cacheType, new object[] { Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[2])), "cache-benchmark") });
                var cachedPlanText = File.ReadAllText(args[4]);
                var keyMethod = cacheType.GetMethod("Key");
                var key = keyMethod.Invoke(null, new[] { Path.GetFullPath(args[1]), "benchmark", model, false });
                cacheType.GetMethod("SaveExecuted").Invoke(cache, new[] { key, cachedPlanText });
                var timings = new List<double>();
                for (var i = 0; i < 25; i++)
                {
                    var clock = Stopwatch.StartNew();
                    key = keyMethod.Invoke(null, new[] { Path.GetFullPath(args[1]), "benchmark", model, false });
                    var hit = cacheType.GetMethod("TryRead").Invoke(cache, new[] { key });
                    if (!string.Equals(Convert.ToString(hit), cachedPlanText)) throw new Exception("Cache lookup failed.");
                    timings.Add(clock.Elapsed.TotalMilliseconds);
                }
                timings.Sort();
                report["success"] = true;
                report["median_lookup_ms"] = timings[timings.Count / 2];
                report["samples"] = timings.Count;
                report["seconds"] = timer.Elapsed.TotalSeconds;
                File.WriteAllText(args[2], Json.Serialize(report), new UTF8Encoding(false));
                Console.WriteLine("cache median=" + report["median_lookup_ms"] + "ms");
                return 0;
            }
            var chatType = assembly.GetType("AutoCAD.AI.Assistant.Models.ChatItem", true);
            var chat = Activator.CreateInstance(chatType);
            chatType.GetProperty("IsUser").SetValue(chat, true, null);
            var prompt = args.Length > 3 ? args[3] : "复刻图片中实际可见的工程视图和尺寸，不补画未给出的视图。先读图中单位和比例；保证尺寸与坐标一致。";
            chatType.GetProperty("Content").SetValue(chat, prompt, null);
            var history = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(chatType));
            history.Add(chat);
            var clientType = assembly.GetType("AutoCAD.AI.Assistant.Services.OpenAiCompatibleClient", true);
            var client = Activator.CreateInstance(clientType, true);
            var streaming = Environment.GetEnvironmentVariable("CADAI_BENCH_STREAM") == "1";
            var method = clientType.GetMethods().Single(m => m.Name == "CompleteAsync" && m.GetParameters().Length == (streaming ? 5 : 4));
            var image = args[1] == "-" ? null : Path.GetFullPath(args[1]);
            var deltaCharacters = 0;
            Action<string> delta = value => deltaCharacters += value.Length;
            var call = streaming ? new object[] { model, history, "", image, delta } : new object[] { model, history, "", image };
            var answer = ((Task<string>)method.Invoke(client, call)).GetAwaiter().GetResult();
            report["stream_requested"] = streaming; report["stream_callback_characters"] = deltaCharacters;
            report["answer"] = answer;
            var match = Regex.Match(answer, @"```(?:cad|json)\s*\r?\n([\s\S]*?)```", RegexOptions.IgnoreCase);
            var plan = match.Success ? match.Groups[1].Value.Trim() : answer.Trim().StartsWith("{") ? answer.Trim() : null;
            report["plan"] = plan;
            report["validation"] = plan == null ? (image == null ? null : "No plan") : assembly.GetType("AutoCAD.AI.Assistant.Services.CadPlanValidator", true)
                .GetMethod("Validate").Invoke(null, new object[] { plan });
            if (plan != null) File.WriteAllText(Path.ChangeExtension(args[2], ".plan.json"), plan, new UTF8Encoding(false));
            report["success"] = !String.IsNullOrWhiteSpace(answer) && report["validation"] == null;
        }
        catch (Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            report["success"] = false;
            report["error"] = ex.GetType().Name + ": " + ex.Message;
        }
        report["seconds"] = Math.Round(timer.Elapsed.TotalSeconds, 3);
        report["image"] = Path.GetFileName(args[1]);
        File.WriteAllText(args[2], Json.Serialize(report), new UTF8Encoding(false));
        Console.WriteLine("success=" + report["success"] + " seconds=" + report["seconds"]);
        return Convert.ToBoolean(report["success"]) ? 0 : 1;
    }
}

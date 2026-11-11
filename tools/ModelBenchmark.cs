using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Threading;
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
            using (var sha = SHA256.Create())
                report["plugin_sha256"] = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(args[0]))).Replace("-", "").ToLowerInvariant();
            var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
            report["plugin_version"] = assembly.GetName().Version.ToString();
            var storeType = assembly.GetType("AutoCAD.AI.Assistant.Services.ModelSettingsStore", true);
            var store = Activator.CreateInstance(storeType, true);
            var configs = ((IEnumerable)storeType.GetMethod("Load").Invoke(store, null)).Cast<object>().ToList();
            if (configs.Count == 0) throw new Exception("No configured model.");
            var modelType = assembly.GetType("AutoCAD.AI.Assistant.Models.ModelConfiguration", true);
            var requested = Environment.GetEnvironmentVariable("CADAI_BENCH_MODEL");
            var configuration = Environment.GetEnvironmentVariable("CADAI_BENCH_CONFIG_ID");
            var candidates = !String.IsNullOrWhiteSpace(configuration)
                ? configs.Where(c => String.Equals(Convert.ToString(modelType.GetProperty("Id").GetValue(c, null)), configuration, StringComparison.OrdinalIgnoreCase)).ToList()
                : String.IsNullOrWhiteSpace(requested) ? configs.Take(1).ToList()
                : configs.Where(c => String.Equals(Convert.ToString(modelType.GetProperty("ModelId").GetValue(c, null)), requested, StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count > 1) throw new Exception("Multiple profiles share this model ID; select CADAI_BENCH_CONFIG_ID explicitly.");
            var model = candidates.SingleOrDefault();
            if (model == null) throw new Exception("The requested model ID is not configured.");
            var requestedEffort = Environment.GetEnvironmentVariable("CADAI_BENCH_REASONING");
            var effortProperty = modelType.GetProperty("ReasoningEffort");
            if (!String.IsNullOrWhiteSpace(requestedEffort))
            {
                if (effortProperty == null) throw new Exception("This plugin does not expose reasoning-budget configuration.");
                effortProperty.SetValue(model, requestedEffort == "default" ? null : requestedEffort.Trim().ToLowerInvariant(), null);
            }
            var effectiveEffort = effortProperty == null ? null : Convert.ToString(effortProperty.GetValue(model, null));
            report["reasoning_effort_requested"] = effectiveEffort;
            report["model"] = modelType.GetProperty("ModelId").GetValue(model, null);
            var identity = new Dictionary<string, object>();
            foreach (var property in new[] { "ModelId", "RequestUrl", "UseFullUrl", "SystemPrompt" })
                identity[property] = modelType.GetProperty(property).GetValue(model, null);
            if (!String.IsNullOrWhiteSpace(effectiveEffort)) identity["ReasoningEffort"] = effectiveEffort.Trim().ToLowerInvariant();
            using (var sha = SHA256.Create())
                report["model_configuration_sha256"] = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Json.Serialize(identity)))).Replace("-", "").ToLowerInvariant();
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
            var responses = new List<Dictionary<string, object>>();
            report["raw_responses"] = responses;
            var attempts = new List<Dictionary<string, object>>(); report["request_attempts"] = attempts;
            var requestProperty = clientType.GetProperty("RequestObserver");
            report["request_options_capture_available"] = requestProperty != null;
            if (requestProperty != null)
            {
                Action<Dictionary<string, object>> observe = options => attempts.Add(new Dictionary<string, object>(options));
                requestProperty.SetValue(client, observe, null);
            }
            var observerProperty = clientType.GetProperty("ResponseObserver");
            report["response_capture_available"] = observerProperty != null;
            if (observerProperty != null)
            {
                Action<string, string> capture = delegate(string stage, string body)
                {
                    var path = Path.ChangeExtension(args[2], ".response-" + (responses.Count + 1).ToString("00") + ".json");
                    File.WriteAllText(path, body, new UTF8Encoding(false));
                    string hash;
                    using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(body))).Replace("-", "").ToLowerInvariant();
                    var record = new Dictionary<string, object> { { "stage", stage }, { "file", Path.GetFileName(path) }, { "sha256", hash } };
                    try
                    {
                        var payload = Json.DeserializeObject(body) as Dictionary<string, object>;
                        if (payload != null && payload.ContainsKey("model") && payload["model"] is string)
                            record["provider_reported_model"] = payload["model"];
                        var usage = payload != null && payload.ContainsKey("usage") ? payload["usage"] as Dictionary<string, object> : null;
                        if (usage != null)
                        {
                            var counts = new Dictionary<string, object>();
                            foreach (var name in new[] { "prompt_tokens", "completion_tokens", "total_tokens" })
                                if (usage.ContainsKey(name) && (usage[name] is int || usage[name] is long)) counts[name] = usage[name];
                            record["provider_reported_usage"] = counts;
                        }
                    }
                    catch { /* Keep raw invalid responses; metadata is optional, not a parser fallback. */ }
                    responses.Add(record);
                };
                observerProperty.SetValue(client, capture, null);
            }
            var streaming = Environment.GetEnvironmentVariable("CADAI_BENCH_STREAM") == "1";
            var details = Environment.GetEnvironmentVariable("CADAI_BENCH_DETAILS") == "1";
            var method = clientType.GetMethods().Single(m => m.Name == "CompleteAsync" && m.GetParameters().Length == (details ? 8 : streaming ? 5 : 4));
            var image = args[1] == "-" ? null : Path.GetFullPath(args[1]);
            var deltaCharacters = 0;
            Action<string> delta = value => deltaCharacters += value.Length;
            var call = details ? new object[] { model, history, "", image, streaming ? delta : null, CancellationToken.None, null, true }
                : streaming ? new object[] { model, history, "", image, delta } : new object[] { model, history, "", image };
            var answer = ((Task<string>)method.Invoke(client, call)).GetAwaiter().GetResult();
            report["stream_requested"] = streaming; report["stream_callback_characters"] = deltaCharacters;
            report["detailed_images"] = details;
            report["answer"] = answer;
            var warning = clientType.GetProperty("LastCompatibilityWarning");
            report["compatibility_warning"] = warning == null ? null : warning.GetValue(client, null);
            var plan = (string)assembly.GetType("AutoCAD.AI.Assistant.Services.CadPlanText", true).GetMethod("Extract").Invoke(null, new object[] { answer });
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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using AutoCAD.AI.Assistant.Models;
using AutoCAD.AI.Assistant.Services;

internal static class RegressionTests
{
    private static int Passed;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static string ImagePath;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Passed++; Console.WriteLine("PASS " + name);
    }
    private static string Plan(string commands) { return "{\"commands\":[" + commands + "]}"; }
    private static string Reply(string plan) { return Json.Serialize(new { choices = new[] { new { message = new { content = "```cad\n" + plan + "\n```" }, finish_reason = "stop" } } }); }

    private static int Main()
    {
        var folder = Path.Combine(Path.GetTempPath(), "cadai-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        ImagePath = Path.Combine(folder, "scan.png");
        try
        {
            using (var b = new Bitmap(200, 100))
            using (var g = Graphics.FromImage(b)) { g.Clear(Color.White); g.DrawRectangle(Pens.Black, 10, 10, 180, 80); b.Save(ImagePath); }
            TestValidation();
            foreach (var fixture in Directory.GetFiles("tests/fixtures", "*.json"))
            {
                var plan = File.ReadAllText(fixture, Encoding.UTF8);
                Check(CadPlanValidator.Validate(plan) == null && CadPlanValidator.Validate(CadPlanNormalizer.Normalize(plan)) == null,
                    "real model plan " + Path.GetFileNameWithoutExtension(fixture));
            }
            TestImages();
            TestCache(folder);
            TestClient().GetAwaiter().GetResult();
            Console.WriteLine("TOTAL " + Passed + " passed"); return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
        finally { Directory.Delete(folder, true); }
    }

    private static void TestValidation()
    {
        var circle = "{\"type\":\"circle\",\"center\":[0,0],\"radius\":20}";
        Check(CadPlanValidator.Validate(Plan(circle)) == null, "valid circle");
        Check(CadPlanValidator.Validate(Plan(circle.Replace("circle", "CIRCLE"))) == null, "case insensitive commands");
        Check(CadPlanValidator.Validate(Plan(circle.Replace(":20", ":-1"))) != null, "negative radius rejected");
        Check(CadPlanValidator.Validate(Plan(circle.Replace("[0,0]", "[\"NaN\",0]"))) != null, "NaN rejected");
        Check(CadPlanValidator.Validate(Plan(circle.Replace("[0,0]", "[\"Infinity\",0]"))) != null, "infinity rejected");
        Check(CadPlanValidator.Validate(Plan(circle.Replace("[0,0]", "[100000001,0]"))) != null, "coordinate bounds");
        Check(CadPlanValidator.Validate(Plan("{\"type\":\"spline\",\"points\":[[0,0],[1,1]]}")) != null, "spline requires three points");
        Check(CadPlanValidator.Validate(Plan("{\"type\":\"ellipse\",\"center\":[0,0],\"major\":10,\"ratio\":2}")) != null, "ellipse ratio bounds");
        Check(CadPlanValidator.Validate(Plan("{\"type\":\"ngon\",\"center\":[0,0],\"radius\":10,\"sides\":3.5}")) != null, "polygon sides integral");
        Check(CadPlanValidator.Validate(Plan("{\"type\":\"block\",\"name\":\"b\",\"commands\":[{\"type\":\"insert\",\"name\":\"b\",\"position\":[0,0]}]}")) != null, "recursive block rejected");
        var commands = new List<string>(); for (var i = 0; i < 401; i++) commands.Add(circle);
        Check(CadPlanValidator.Validate(Plan(string.Join(",", commands.ToArray()))) != null, "total command limit");
        Check(CadPlanValidator.Validate(Plan("{\"type\":\"block\",\"name\":\"b\",\"commands\":[" + string.Join(",", commands.ToArray()) + "]}")) != null, "nested command limit");
        Check(CadPlanValidator.Validate(Plan("{\"type\":\"setvar\",\"name\":\"SECURELOAD\",\"value\":0}")) != null, "non drawing variable rejected");
        Check(CadPlanValidator.ValidateAll(Plan(circle.Replace(":20", ":-1") + ",{\"type\":\"bogus\"}")).Count == 2, "aggregate errors");
        var dim = "{\"type\":\"dimlinear\",\"p1\":[0,0],\"p2\":[6000,0],\"position\":[0,-100],\"value\":600,\"confidence\":\"read\"}";
        var cmPlan = "{\"sourceUnit\":\"cm\",\"views\":[\"剖视图\"],\"commands\":[" + dim + "]}";
        Check(CadPlanValidator.Validate(cmPlan) == null, "cm to mm physical dimensions");
        var sourcePlan = cmPlan.Replace("\"sourceUnit\":\"cm\"", "\"sourceUnit\":\"cm\",\"coordinateUnit\":\"source\"").Replace("6000", "600");
        Check(CadPlanValidator.Validate(sourcePlan) == null && CadPlanValidator.Validate(CadPlanNormalizer.Normalize(sourcePlan)) == null, "source coordinates converted locally once");
        var normalized = CadPlanNormalizer.Normalize(sourcePlan);
        Check(CadPlanNormalizer.Normalize(normalized) == normalized, "normalization is idempotent");
        Check(CadPlanValidator.Validate(cmPlan.Replace("6000", "600")) != null, "tenfold unit error rejected");
        Check(CadPlanValidator.Validate(cmPlan.Replace("6000", "6010")) != null, "small dimension drift rejected");
        Check(CadPlanValidator.Validate(cmPlan.Replace("cm", "inch")) != null, "unsupported units rejected");
        Check(CadPlanValidator.Validate(sourcePlan.Replace("\"source\"", "\"pixels\"")) != null, "unknown coordinate unit rejected");
        Check(CadPlanValidator.Validate(cmPlan.Replace("\"read\"", "\"read\",\"text\":\"600\"")) != null, "fixed dimension text rejected");
        Check(CadPlanQuality.ImageContractError(cmPlan) == null, "image metadata accepted");
        Check(CadPlanQuality.ImageContractError(Plan(circle)) != null, "image source units required");
        Check(CadPlanText.Extract("```CAD\r\n" + Plan(circle) + "\r\n```") == Plan(circle), "CRLF fenced plan");
        Check(CadPlanText.Extract(Plan(circle)) == Plan(circle), "raw JSON plan");
        Check(CadPlanText.Extract("```cad\n{\"commands\":[]}") == null, "truncated plan never executed");
        try { CadPlanText.Extract("```cad\n{}\n```\n```json\n{}\n```"); Check(false, "multiple plans rejected"); }
        catch (InvalidOperationException) { Check(true, "multiple plans rejected"); }
    }

    private static void TestImages()
    {
        var small = ImagePreprocessor.PrepareViews(ImagePath, CancellationToken.None);
        Check(small.Count == 1, "small screenshot uses one image");
        using (var ms = new MemoryStream(Convert.FromBase64String(small[0].DataUrl.Split(',')[1])))
        using (var image = Image.FromStream(ms)) Check(image.Width <= 200 && image.Height <= 100, "no wasteful upscaling");
        var largePath = Path.ChangeExtension(ImagePath, "large.png");
        using (var b = new Bitmap(2500, 1800))
        using (var g = Graphics.FromImage(b)) { g.Clear(Color.White); g.DrawRectangle(Pens.Black, 5, 5, 2490, 1790); b.Save(largePath); }
        var large = ImagePreprocessor.PrepareViews(largePath, CancellationToken.None);
        Check(large.Count == 5, "large sheet overview plus four details");
        Check(ImagePreprocessor.PrepareViews(largePath, CancellationToken.None, false).Count == 1, "fast mode avoids detail overhead");
    }

    private static void TestCache(string folder)
    {
        var model = new ModelConfiguration { ModelId = "test", RequestUrl = "http://localhost/" };
        var key = DrawingPlanCache.Key(ImagePath, "copy", model, false);
        var cache = new DrawingPlanCache(Path.Combine(folder, "cache"));
        var plan = "{\"sourceUnit\":\"mm\",\"views\":[\"平面图\"],\"commands\":[{\"type\":\"circle\",\"center\":[0,0],\"radius\":20}]}";
        Check(cache.TryRead(key) == null, "cache miss does not invent a plan");
        cache.SaveExecuted(key, plan);
        Check(cache.TryRead(key) == plan, "executed plan cache round trip");
        Check(key != DrawingPlanCache.Key(ImagePath, "copy changed", model, false), "prompt changes invalidate cache");
        model.ModelId = "other";
        Check(key != DrawingPlanCache.Key(ImagePath, "copy", model, false), "model changes invalidate cache");
        var path = Path.Combine(folder, "cache", key + ".json");
        File.WriteAllText(path, "{}");
        Check(cache.TryRead(key) == null, "corrupt cache rejected");
        File.WriteAllText(path, plan); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-8));
        Check(cache.TryRead(key) == null, "expired cache rejected");
    }

    private sealed class Server : IDisposable
    {
        private readonly HttpListener Listener = new HttpListener();
        public readonly List<string> Requests = new List<string>();
        public string Url;
        public Func<int, Tuple<int, string>> Response;
        public Server()
        {
            var port = new TcpListener(IPAddress.Loopback, 0); port.Start(); var number = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
            Url = "http://127.0.0.1:" + number + "/"; Listener.Prefixes.Add(Url); Listener.Start();
            Task.Run(async delegate
            {
                try
                {
                    while (Listener.IsListening)
                    {
                        var context = await Listener.GetContextAsync();
                        string request; using (var reader = new StreamReader(context.Request.InputStream)) request = await reader.ReadToEndAsync();
                        int index; lock (Requests) { Requests.Add(request); index = Requests.Count; }
                        var response = Response(index); context.Response.StatusCode = response.Item1;
                        var bytes = Encoding.UTF8.GetBytes(response.Item2); context.Response.ContentLength64 = bytes.Length;
                        await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length); context.Response.Close();
                    }
                }
                catch (HttpListenerException) { } catch (ObjectDisposedException) { }
            });
        }
        public void Dispose() { Listener.Close(); }
    }

    private static ModelConfiguration Model(Server server)
    { return new ModelConfiguration { ModelId = "test", RequestUrl = server.Url, UseFullUrl = true, ApiKeyProtected = SecretProtector.Protect("local-test-key") }; }
    private static List<ChatItem> History()
    { return new List<ChatItem> { new ChatItem { IsUser = true, Content = "OLD_HISTORY_1_1000" }, new ChatItem { IsUser = true, Content = "复刻可见视图" } }; }

    private static async Task TestClient()
    {
        var valid = Plan("{\"type\":\"circle\",\"center\":[0,0],\"radius\":20}");
        using (var s = new Server())
        {
            s.Response = i => Tuple.Create(400, "{\"error\":{\"message\":\"no vision\"}}");
            try { await new OpenAiCompatibleClient().CompleteAsync(Model(s), History(), "", ImagePath); Check(false, "image rejection"); }
            catch (InvalidOperationException) { Check(s.Requests.Count == 1, "400 image never falls back to text"); }
        }
        using (var s = new Server())
        {
            s.Response = i => Tuple.Create(200, Reply(i == 1 ? valid.Replace(":20", ":-1") : valid));
            var answer = await new OpenAiCompatibleClient().CompleteAsync(Model(s), History(), "", null);
            Check(s.Requests.Count == 2 && CadPlanValidator.Validate(CadPlanText.Extract(answer)) == null, "one automatic plan repair");
        }
        using (var s = new Server())
        {
            s.Response = i => Tuple.Create(200, Reply(valid.Replace(":20", ":-1")));
            try { await new OpenAiCompatibleClient().CompleteAsync(Model(s), History(), "", null); Check(false, "repair stops"); }
            catch (InvalidOperationException) { Check(s.Requests.Count == 2, "invalid repair stops without drawing"); }
        }
        using (var s = new Server())
        using (var cancel = new CancellationTokenSource(150))
        {
            s.Response = i => Tuple.Create(429, "{}");
            var timer = Stopwatch.StartNew();
            try { await new OpenAiCompatibleClient().CompleteAsync(Model(s), History(), "", null, null, cancel.Token, null); Check(false, "cancel retry"); }
            catch (OperationCanceledException) { Check(timer.ElapsedMilliseconds < 1200 && s.Requests.Count == 1, "cancellation interrupts retry delay"); }
        }
        using (var s = new Server())
        {
            s.Response = i => Tuple.Create(200, Reply("{\"sourceUnit\":\"mm\",\"views\":[\"平面图\"],\"commands\":[{\"type\":\"circle\",\"center\":[0,0],\"radius\":20}]}"));
            await new OpenAiCompatibleClient().CompleteAsync(Model(s), History(), "", ImagePath);
            Check(!s.Requests[0].Contains("OLD_HISTORY_1_1000"), "new image excludes prior drawing history");
        }
    }
}

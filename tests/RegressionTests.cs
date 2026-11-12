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
        Environment.SetEnvironmentVariable("CADAI_DATA_DIR", Path.Combine(folder, "plugin-data"));
        ImagePath = Path.Combine(folder, "scan.png");
        try
        {
            using (var b = new Bitmap(200, 100))
            using (var g = Graphics.FromImage(b)) { g.Clear(Color.White); g.DrawRectangle(Pens.Black, 10, 10, 180, 80); b.Save(ImagePath); }
            TestBackgroundWork();
            TestShortcutPersistence();
            TestChatRetention();
            TestSettingsRecovery(folder);
            TestBufferedLogs();
            TestValidation();
            TestPreparedPlan();
            TestPlanBudgets();
            TestDimensionSizeSemantics();
            TestGeometryReferences();
            TestDimensionAnchors();
            TestPlanPatches();
            TestPatchClient().GetAwaiter().GetResult();
            TestReferenceRepair().GetAwaiter().GetResult();
            TestImageLinetypes();
            TestDashLengths();
            TestDimensionFormatVariables();
            TestNewDrawingFeatures();
            TestMaterials();
            TestPointSpacing();
            TestHatchBoundaries();
            TestMaterialLeaders();
            foreach (var fixture in Directory.GetFiles("tests/fixtures", "*.json"))
            {
                var plan = File.ReadAllText(fixture, Encoding.UTF8);
                var error = CadPlanValidator.Validate(plan) ?? CadPlanValidator.Validate(CadPlanNormalizer.Normalize(plan));
                Check(error == null, "saved drawing plan " + Path.GetFileNameWithoutExtension(fixture) + (error == null ? "" : ": " + error));
            }
            TestImages();
            TestCache(folder);
            TestPdfSnapshot(folder);
            TestClient().GetAwaiter().GetResult();
            TestAutoCompatibility().GetAwaiter().GetResult();
            TestRuntimeBehavior().GetAwaiter().GetResult();
            TestCompletionReasons().GetAwaiter().GetResult();
            TestRepairEvidence().GetAwaiter().GetResult();
            TestReasoningEffort().GetAwaiter().GetResult();
            Console.WriteLine("TOTAL " + Passed + " passed"); return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
        finally
        {
            ShortcutCatalog.FlushAsync().Wait(5000); DiagnosticsLog.FlushAsync().Wait(5000);
            Directory.Delete(folder, true);
        }
    }

    private static void TestSettingsRecovery(string folder)
    {
        var path = Path.Combine(folder, "models.json");
        var store = new ModelSettingsStore(path);
        var model = new ModelConfiguration { ModelId = "local-model", RequestUrl = "http://localhost:1234/v1", ApiKeyProtected = SecretProtector.Protect("test-local-secret") };
        store.Save(new[] { model });
        Check(SecretProtector.Unprotect(store.Load()[0].ApiKeyProtected) == "test-local-secret", "atomic model save preserves DPAPI encrypted credentials");
        model.ReasoningEffort = "low"; store.Save(new[] { model });
        Check(store.Load()[0].ReasoningEffort == "low" && SecretProtector.Unprotect(store.Load()[0].ApiKeyProtected) == "test-local-secret",
            "reasoning profile persists while preserving the encrypted existing credential");
        var beforeInvalidBudget = File.ReadAllText(path); model.ReasoningEffort = "invented";
        try { store.Save(new[] { model }); Check(false,"invalid reasoning persistence"); }
        catch (InvalidDataException) { Check(File.ReadAllText(path) == beforeInvalidBudget, "invalid reasoning setting cannot overwrite a healthy profile"); }
        model.ReasoningEffort = "low";
        model.DisplayName = "renamed"; store.Save(new[] { model });
        File.WriteAllText(path, "{truncated");
        Check(store.Load()[0].DisplayName == null && store.LastWarning != null, "damaged model primary recovers the last valid backup visibly");
        store.Save(new[] { model });
        Check(Directory.GetFiles(folder, "models.json.corrupt-*").Length == 1 && File.ReadAllText(path + ".bak").Contains("local-model"), "recovery save preserves the damaged primary and healthy backup");
        var unchanged = File.ReadAllText(path);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try { store.Save(new[] { model }); Check(false, "locked model save fails without truncation"); }
            catch (IOException) { Check(true, "locked model save fails without truncation"); }
        }
        Check(File.ReadAllText(path) == unchanged && Directory.GetFiles(folder, "*.tmp").Length == 0, "failed atomic save retains original data and removes temporary files");
        model.RequestUrl = "invalid-url";
        try { store.Save(new[] { model }); Check(false, "invalid model settings cannot replace valid settings"); }
        catch (InvalidDataException) { Check(File.ReadAllText(path) == unchanged, "invalid model settings cannot replace valid settings"); }
        File.WriteAllText(path, "broken"); File.WriteAllText(path + ".bak", "broken");
        try { store.Load(); Check(false, "unrecoverable model configuration reports a safe error"); }
        catch (InvalidDataException ex) { Check(!ex.Message.Contains("broken") && File.Exists(path), "unrecoverable model configuration reports a safe error"); }

        var backup = Path.Combine(folder, "startup.json");
        var values = new Dictionary<string, int> { { "STARTUP", 3 }, { "STARTMODE", 0 }, { "FILEDIA", 1 }, { "CMDDIA", 1 }, { "MENUBAR", 0 }, { "STATUSBAR", 1 } };
        StartupSettingsBackup.Ensure(backup, values); values["STARTUP"] = 0;
        StartupSettingsBackup.Ensure(backup, values);
        Check(StartupSettingsBackup.Read(backup)["STARTUP"] == 3, "repeated startup setup preserves the original preferences");
        File.WriteAllText(backup, File.ReadAllText(backup).Replace("STATUSBAR", "SECURELOAD"));
        try { StartupSettingsBackup.Read(backup); Check(false, "startup restore rejects unexpected variables"); }
        catch (InvalidDataException) { Check(true, "startup restore rejects unexpected variables"); }
        File.WriteAllText(backup, Json.Serialize(values).Replace("\"STARTUP\":0", "\"STARTUP\":9"));
        try { StartupSettingsBackup.Read(backup); Check(false, "startup restore rejects out-of-range values"); }
        catch (InvalidDataException) { Check(true, "startup restore rejects out-of-range values"); }
        File.WriteAllText(backup, Json.Serialize(values).Replace("\"STARTUP\":0", "\"STARTUP\":0.5"));
        try { StartupSettingsBackup.Read(backup); Check(false, "startup restore rejects fractional values"); }
        catch (InvalidDataException) { Check(true, "startup restore rejects fractional values"); }
    }

    private static void TestNewDrawingFeatures()
    {
        var angular = "{\"type\":\"dimangular\",\"center\":[0,0],\"p1\":[10,0],\"p2\":[0,10],\"position\":[5,5],\"value\":90}";
        Check(CadPlanValidator.Validate(Plan(angular)) == null, "native angular dimension accepts a real 90 degree sector");
        Check(CadPlanValidator.Validate(Plan(angular.Replace("\"value\":90", "\"value\":60"))) != null, "angular dimension rejects a falsely stated angle");
        Check(CadPlanValidator.Validate(Plan(angular.Replace("[5,5]", "[-5,-5]"))) != null, "angular dimension rejects a reflex sector position");
        Check(CadPlanValidator.Validate(Plan(angular.Replace("[0,10]", "[20,0]"))) != null, "angular dimension rejects collinear rays");
        var source = "{\"sourceUnit\":\"cm\",\"coordinateUnit\":\"source\",\"commands\":[" + angular + "]}";
        Check(CadPlanValidator.Validate(source) == null && CadPlanValidator.Validate(CadPlanNormalizer.Normalize(source)) == null,
            "angle values remain degrees across source length normalization");
        Check(CadPlanValidator.Validate(Plan(angular.Replace("\"value\":90", "\"value\":90,\"text\":\"60°\""))) != null, "angle text cannot conceal the native measurement");
        var ring = "{\"type\":\"donut\",\"center\":[0,0],\"innerDiameter\":6,\"outerDiameter\":10}";
        Check(CadPlanValidator.Validate(Plan(ring)) == null && CadPlanValidator.Validate(Plan(ring.Replace(":6", ":0"))) == null, "donut supports hollow rings and filled disks");
        Check(CadPlanValidator.Validate(Plan(ring.Replace(":6", ":10"))) != null && CadPlanValidator.Validate(Plan(ring.Replace(":6", ":-1"))) != null,
            "donut rejects reversed and negative inner diameters");
        var sourceRing = CadPlanNormalizer.Normalize("{\"sourceUnit\":\"cm\",\"coordinateUnit\":\"source\",\"commands\":[" + ring + "]}");
        var c = (Dictionary<string, object>)((object[])((Dictionary<string, object>)Json.DeserializeObject(sourceRing))["commands"])[0];
        Check(CadPlanQuality.Number(c["innerDiameter"]) == 60 && CadPlanQuality.Number(c["outerDiameter"]) == 100, "donut diameters convert to millimeters once");
        Check(CadPlanValidator.Validate(Plan("{\"type\":\"solid\",\"points\":[[0,0],[10,0],[10,5],[0,5]]}")) == null, "solid accepts perimeter-ordered convex vertices");
        Check(CadPlanValidator.Validate(Plan("{\"type\":\"solid\",\"points\":[[0,0],[10,5],[0,5],[10,0]]}")) != null, "solid rejects crossed quadrilateral vertices");
        Check(CadPlanValidator.Validate(Plan("{\"type\":\"solid\",\"points\":[[0,0],[5,0],[10,0]]}")) != null, "solid rejects zero area triangles");
        var curve = "{\"type\":\"polyline\",\"points\":[[0,0],[10,0]],\"bulges\":[1,0]}";
        Check(CadPlanValidator.Validate(Plan(curve)) == null, "polyline supports a native semicircular segment");
        Check(CadPlanValidator.Validate(Plan(curve.Replace("[1,0]", "[1]"))) != null, "polyline rejects mismatched bulge count");
        Check(CadPlanValidator.Validate(Plan(curve.Replace("[1,0]", "[1,1]"))) != null, "open polyline cannot hide an unused last arc");
        Check(CadPlanValidator.Validate(Plan(curve.Replace("[1,0]", "[1e99,0]"))) != null, "polyline rejects extreme bulges");
        Check(CadPlanValidator.Validate(Plan(curve.Replace("[10,0]", "[0,0]"))) != null, "polyline rejects coincident arc endpoints");
        var leader = "{\"type\":\"leader\",\"points\":[[0,0],[10,10]],\"position\":[20,10],\"height\":3.5,\"content\":\"引线测试\"}";
        Check(CadPlanValidator.Validate(Plan(leader)) == null, "leader accepts a tip bend and annotation");
        Check(CadPlanValidator.Validate(Plan(leader.Replace("引线测试", ""))) != null, "leader rejects missing annotation text");
        Check(CadPlanValidator.Validate(Plan(leader.Replace("[10,10]", "[0,0]"))) != null, "leader rejects zero length segments");
        Check(CadPlanValidator.Validate(Plan(curve.Replace("\"type\":\"polyline\"", "\"type\":\"polyline\",\"closed\":\"true\""))) != null, "polyline rejects a nonboolean closure flag");
        Check(CadPlanValidator.Validate(Plan(curve.Replace("\"type\":\"polyline\"", "\"type\":\"polyline\",\"width\":-1"))) != null, "polyline rejects negative widths");
    }

    private static void TestMaterialLeaders()
    {
        var hatch = "{\"type\":\"hatch\",\"layer\":\"MAT\",\"material\":\"concrete\",\"points\":[[0,0],[100,0],[100,100],[0,100]],\"holes\":[[[20,20],[80,20],[80,80],[20,80]]]}";
        var leader = "{\"type\":\"leader\",\"points\":[[10,50],[-20,80]],\"position\":[-60,80],\"height\":3.5,\"content\":\"C30混凝土\",\"material\":\"concrete\",\"targetLayer\":\"MAT\"}";
        var valid = Plan(hatch + "," + leader);
        Check(CadPlanValidator.Validate(valid) == null, "material leader targets a real concrete hatch region");
        Check(CadPlanValidator.Validate(Plan(hatch + "," + leader.Replace(",\"targetLayer\":\"MAT\"", ""))) != null,
            "material leader cannot omit its target layer");
        Check(CadPlanValidator.Validate(Plan(hatch + "," + leader.Replace("[10,50]", "[-10,50]"))) != null,
            "material leader cannot point at blank space");
        Check(CadPlanValidator.Validate(Plan(hatch + "," + leader.Replace("[10,50]", "[50,50]"))) != null,
            "material leader cannot point into an unfilled hole");
        Check(CadPlanValidator.Validate(Plan("{\"type\":\"solid\",\"layer\":\"MAT\",\"points\":[[0,0],[100,0],[100,100],[0,100]]}," + leader)) != null,
            "black solid is not accepted as a concrete material hatch");
        Check(CadPlanValidator.Validate(valid.Replace("\"material\":\"concrete\",\"points\":[[0,0]", "\"material\":\"earth\",\"points\":[[0,0]")) != null,
            "concrete label cannot target earth material");
        var diagonal = hatch.Replace("\"material\":\"concrete\"", "\"pattern\":\"ANSI31\"");
        var particles = hatch.Replace("\"material\":\"concrete\"", "\"pattern\":\"AR-SAND\"");
        Check(CadPlanValidator.Validate(Plan(diagonal + "," + particles + "," + leader)) == null,
            "source diagonal and particle concrete textures can share a material leader");
        var source = valid.Replace("{\"commands\":", "{\"sourceUnit\":\"cm\",\"coordinateUnit\":\"source\",\"commands\":");
        Check(CadPlanValidator.Validate(source) == null && CadPlanValidator.Validate(CadPlanNormalizer.Normalize(source)) == null,
            "material target relationship survives source unit conversion");
        Check(CadPlanValidator.Validate(Plan("{\"type\":\"block\",\"name\":\"MATERIAL_BLOCK\",\"commands\":[" + hatch + "," + leader + "]}")) == null,
            "material targets inside a block are checked in their own coordinate scope");
    }

    private static void TestDimensionFormatVariables()
    {
        var ticks = Plan("{\"type\":\"setvar\",\"name\":\"DIMTSZ\",\"value\":2}");
        ticks = ticks.Replace("{\"commands\":", "{\"sourceUnit\":\"cm\",\"coordinateUnit\":\"source\",\"commands\":");
        var normalizedTicks = (Dictionary<string, object>)Json.DeserializeObject(CadPlanNormalizer.Normalize(ticks));
        Check(CadPlanQuality.Number(((Dictionary<string, object>)((object[])normalizedTicks["commands"])[0])["value"]) == 20,
            "dimension tick size follows source length units");
        Check(CadPlanValidator.Validate(Plan("{\"type\":\"setvar\",\"name\":\"DIMTSZ\",\"value\":0}")) == null
            && CadPlanValidator.Validate(Plan("{\"type\":\"setvar\",\"name\":\"DIMTSZ\",\"value\":-1}")) != null,
            "zero tick size enables arrows and negative tick size is rejected");
        Check(CadPlanValidator.Validate(Plan("{\"type\":\"setvar\",\"name\":\"DIMTAD\",\"value\":1},{\"type\":\"setvar\",\"name\":\"DIMLUNIT\",\"value\":2},{\"type\":\"setvar\",\"name\":\"DIMZIN\",\"value\":8},{\"type\":\"setvar\",\"name\":\"DIMTIH\",\"value\":0},{\"type\":\"setvar\",\"name\":\"DIMTOH\",\"value\":0}")) == null,
            "safe native dimension formatting does not require an AI repair");
        Check(CadPlanValidator.Validate(Plan("{\"type\":\"setvar\",\"name\":\"DIMTIH\",\"value\":2}")) != null
            && CadPlanValidator.Validate(Plan("{\"type\":\"setvar\",\"name\":\"DIMLUNIT\",\"value\":0}")) != null
            && CadPlanValidator.Validate(Plan("{\"type\":\"setvar\",\"name\":\"DIMTAD\",\"value\":0.5}")) != null,
            "dimension format variables reject invalid enum and fractional values");
    }

    private static void TestImageLinetypes()
    {
        var plan = "{\"sourceUnit\":\"cm\",\"coordinateUnit\":\"source\",\"views\":[\"section\"],\"commands\":[{\"type\":\"setvar\",\"name\":\"DIMTXT\",\"value\":35},{\"type\":\"rect\",\"p1\":[0,0],\"p2\":[1000,1100]}]}";
        var root = (Dictionary<string, object>)Json.DeserializeObject(CadPlanNormalizer.Normalize(plan));
        Check(CadPlanQuality.SuggestDimensionScale(root) == 0 && CadPlanQuality.SuggestLinetypeScale(root) == 100,
            "source text height does not suppress visible centerline scale");
        root = (Dictionary<string, object>)Json.DeserializeObject(CadPlanNormalizer.Normalize(plan.Replace("DIMTXT", "LTSCALE")));
        Check(CadPlanQuality.SuggestLinetypeScale(root) == 0, "explicit source linetype scale is preserved");
        root.Remove("views");
        Check(CadPlanQuality.SuggestLinetypeScale(root) == 0, "ordinary text drawing does not trigger source linetype scaling");
    }

    private static void TestPreparedPlan()
    {
        var source = "{\"sourceUnit\":\"cm\",\"coordinateUnit\":\"source\",\"commands\":[{\"type\":\"circle\",\"center\":[2,3],\"radius\":5}]}";
        var prepared = Task.Run(() => PreparedCadPlan.Create(source)).GetAwaiter().GetResult();
        var root = prepared.Claim(); var command = (Dictionary<string, object>)((object[])root["commands"])[0];
        Check(CadPlanQuality.Number(command["radius"]) == 50 && source.Contains("\"radius\":5"), "background preflight converts source units while preserving source JSON");
        try { prepared.Claim(); Check(false, "prepared plan cannot execute twice"); }
        catch (InvalidOperationException) { Check(true, "prepared plan cannot execute twice"); }
        try { PreparedCadPlan.Create(source.Replace("\"radius\":5", "\"radius\":-5")); Check(false, "invalid background plan is rejected before queueing"); }
        catch (InvalidOperationException) { Check(true, "invalid background plan is rejected before queueing"); }
    }

    private static void TestBackgroundWork()
    {
        var caller = Thread.CurrentThread.ManagedThreadId;
        using (var worker = new BackgroundWork(60000))
        {
            var writes = 0; var latest = -1; var writerThread = caller;
            for (var i = 0; i < 1000; i++) { var value = i; worker.Request(() => { writes++; latest = value; writerThread = Thread.CurrentThread.ManagedThreadId; }); }
            worker.FlushAsync().GetAwaiter().GetResult();
            Check(writes == 1 && latest == 999 && writerThread != caller, "1000 persistence updates coalesce off the calling thread");
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                worker.Request(() => { entered.Set(); release.Wait(5000); });
                var flush = worker.FlushAsync();
                Check(entered.Wait(5000) && !flush.IsCompleted, "slow persistence does not block the caller");
                worker.Request(() => { latest = 1000; writes++; });
                worker.Request(() => { latest = 2000; writes++; });
                release.Set(); flush.GetAwaiter().GetResult();
                Check(latest == 2000 && writes == 2, "updates during a write are drained without overlapping writers");
            }
            worker.Request(() => { throw new IOException("simulated disk error"); }); worker.FlushAsync().Wait();
            Check(worker.LastError != null, "persistence failure is reported without throwing on the caller");
            worker.Request(() => { }); worker.FlushAsync().Wait();
            Check(worker.LastError == null, "persistence recovers on a later write");
        }
    }

    private static void TestShortcutPersistence()
    {
        Check(!ShortcutCatalog.UsageTrackingEnabled, "command usage tracking defaults to off");
        var switches = 0;
        Action onSwitch = () => switches++;
        ShortcutCatalog.UsageTrackingChanged += onSwitch;
        ShortcutCatalog.SetUsageTracking(true);
        var timer = Stopwatch.StartNew();
        for (var i = 0; i < 10000; i++) ShortcutCatalog.RecordCommand("LINE");
        Console.WriteLine("PERF 10000 command counters queued in " + timer.ElapsedMilliseconds + "ms");
        ShortcutCatalog.RecordCommand("ZOOM"); ShortcutCatalog.RecordCommand("PAN");
        ShortcutCatalog.FlushAsync().GetAwaiter().GetResult();
        var file = Path.Combine(DiagnosticsLog.Folder, "shortcuts.json");
        var root = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(file));
        Func<int> count = () => ShortcutCatalog.GetSorted().Find(x => x.Command.Contains("LINE ")).UsageCount;
        Check(count() == 10000 && root.ContainsKey("recordUsage"), "batched shortcut counts are persisted without loss");
        var copy = ShortcutCatalog.GetSorted(); copy.Find(x => x.Command.Contains("LINE ")).UsageCount = -1;
        Check(count() == 10000, "shortcut views do not share mutable persistence records");
        using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            ShortcutCatalog.RecordCommand("LINE"); ShortcutCatalog.FlushAsync().Wait();
            Check(ShortcutCatalog.PersistenceError != null, "locked shortcut file does not block or erase counts");
        }
        ShortcutCatalog.FlushAsync().Wait();
        Check(ShortcutCatalog.PersistenceError == null && File.ReadAllText(file).Contains("10001"), "shortcut persistence retries the unsaved revision");
        ShortcutCatalog.SetUsageTracking(false); ShortcutCatalog.RecordCommand("LINE"); ShortcutCatalog.FlushAsync().Wait();
        Check(count() == 10001, "disabled command tracking does not update counts");
        Check(switches == 2, "command counters do not re-enter listener lifecycle updates");
        ShortcutCatalog.UsageTrackingChanged -= onSwitch;
    }

    private static void TestChatRetention()
    {
        var visible = new List<ChatItem>(); var history = new List<ChatItem>();
        for (var i = 0; i < 200; i++)
        {
            var item = new ChatItem { Content = "message-" + i };
            ChatRetention.Append(visible, item, ChatRetention.VisibleLimit); ChatRetention.Append(history, item, ChatRetention.HistoryLimit);
        }
        Check(visible.Count == 40 && visible[0].Content == "message-160", "visible chat retains only 40 recent messages");
        Check(history.Count == 12 && history[history.Count - 1].Content == "message-199", "model history retains the latest 12 full messages");
        var text = new string('x', 20000); var longItem = new ChatItem { Content = text };
        Check(longItem.Content == text && longItem.DisplayContent.Length < 8200, "large message layout is capped without truncating model content");
    }

    private static void TestBufferedLogs()
    {
        Directory.CreateDirectory(DiagnosticsLog.Folder);
        using (var locked = new FileStream(DiagnosticsLog.FilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
        {
            for (var i = 0; i < 1200; i++) DiagnosticsLog.Write("buffer-" + i);
            DiagnosticsLog.FlushAsync().Wait();
            Check(locked.Length == 0 && DiagnosticsLog.Snapshot().Contains("buffer-1199"), "locked log keeps an in-memory diagnostic snapshot");
        }
        DiagnosticsLog.FlushAsync().Wait();
        var lines = File.ReadAllLines(DiagnosticsLog.FilePath);
        Check(lines.Length == 1000 && lines[lines.Length - 1].Contains("buffer-1199"), "bounded log batch retries after the file becomes writable");
        Check(!DiagnosticsLog.Snapshot().Contains("buffer-0") && DiagnosticsLog.Snapshot().Contains("buffer-1199"), "diagnostic snapshot retains only recent messages");
    }

    private static void TestDashLengths()
    {
        var line="{\"type\":\"line\",\"points\":[[0,0],[1000,0]],\"dashLength\":300,\"layer\":\"中心线\"}";
        var source="{\"sourceUnit\":\"cm\",\"coordinateUnit\":\"source\",\"commands\":["+line+"]}";
        var prepared=PreparedCadPlan.Create(source);var root=prepared.Claim();
        Check(CadPlanQuality.Number(((Dictionary<string,object>)((object[])root["commands"])[0])["dashLength"])==3000,
            "declared source dash length becomes model millimeters exactly once");
        Check(CadPlanNormalizer.Normalize(prepared.Json)==prepared.Json,"dash length normalization remains idempotent");
        Check(CadPlanValidator.Validate(Plan(line.Replace(":300",":0")))!=null,
            "zero dash length cannot be treated as an explicit long-dash request");
        Check(CadPlanValidator.Validate(Plan(line.Replace(":300",":-1")))!=null,
            "negative dash length never reaches native linetype scaling");
        Check(CadPlanValidator.Validate(Plan("{\"type\":\"circle\",\"center\":[0,0],\"radius\":10,\"dashLength\":10}"))!=null,
            "unsupported dashLength fields are rejected rather than silently ignored");
        var patch=Json.Serialize(new{base_sha256=CadPlanPatch.Hash(source),updates=new[]{new{command="1",fields=new{dashLength=400}}}});
        Check(CadPlanPatch.Apply(source,patch).Contains("\"dashLength\":400")&&source.Contains("\"dashLength\":300"),
            "a per-entity dash patch leaves source line geometry and original plan intact");
    }

    private static void TestPlanBudgets()
    {
        var points = new List<object>();
        for (var i = 0; i < 6002; i++) points.Add(new[] { i, i % 2 });
        var longLine = Json.Serialize(new { commands = new[] { new { type = "line", points = points.ToArray() } } });
        var timer = Stopwatch.StartNew();
        var error = CadPlanValidator.Validate(longLine);
        Check(error != null && error.Contains("5000") && timer.ElapsedMilliseconds < 2000,
            "one line command cannot expand into 6001 native entities on the CAD thread");
        var compact = longLine.Replace("\"line\"", "\"polyline\"");
        Check(CadPlanValidator.Validate(compact) == null,
            "the same 6002 input vertices remain valid as one compact native polyline");
        var boundary = new List<object>();
        for (var i = 0; i <= 5000; i++) boundary.Add(new[] { i, i % 2 });
        Check(CadPlanValidator.Validate(Json.Serialize(new { commands = new[] { new { type = "line", points = boundary.ToArray() } } })) == null,
            "exactly 5000 finite native segments stay within the explicit budget");
        var manyPoints = new[] {
            new { type="polyline", points=points.ToArray() }, new { type="polyline", points=points.ToArray() },
            new { type="polyline", points=points.ToArray() }, new { type="polyline", points=points.ToArray() }
        };
        Check(CadPlanValidator.Validate(Json.Serialize(new { commands=manyPoints })).Contains("20000"),
            "input vertices are bounded across the whole plan rather than per command only");
        var definitions = new List<Dictionary<string, object>>();
        definitions.Add(new Dictionary<string, object> { {"type","block"}, {"name","b0"},
            {"commands",new object[] { new { type="circle", center=new[]{0,0}, radius=1 } }} });
        for (var level = 1; level <= 5; level++)
        {
            var inserts = new List<object>();
            for (var i = 0; i < 10; i++) inserts.Add(new { type="insert", name="b"+(level-1), position=new[]{i*10,0} });
            definitions.Add(new Dictionary<string, object> { {"type","block"}, {"name","b"+level}, {"commands",inserts.ToArray()} });
        }
        error = CadPlanValidator.Validate(Json.Serialize(new { commands=definitions.ToArray() }));
        Check(error != null && (error.Contains("展开") || error.Contains("工作量")),
            "a small sequence of reusable blocks cannot create exponential display work");
        Check(CadPlanValidator.Validate(Json.Serialize(new { commands=new[] { definitions[0],definitions[1] } })) == null,
            "ordinary repeated blocks remain valid instead of being expanded into duplicate geometry");
        var ring = new List<object>();
        for (var i = 0; i < 1800; i++) { var angle=i*Math.PI/900;ring.Add(new[] {1000*Math.Cos(angle),1000*Math.Sin(angle)}); }
        var hatches = new List<object>();
        for (var i=0;i<5;i++)hatches.Add(new { type="hatch",points=ring.ToArray() });
        timer.Restart();
        error=CadPlanValidator.Validate(Json.Serialize(new {commands=hatches.ToArray()}));
        Check(error!=null && error.Contains("8192") && timer.ElapsedMilliseconds<2000,
            "aggregate hatch budget is checked before quadratic ring topology work");
        var text = new List<object>();
        for (var i=0;i<9;i++) text.Add(new { type="mtext",position=new[]{0,i},height=3,content=new string('x',5000) });
        Check(CadPlanValidator.Validate(Json.Serialize(new {commands=text.ToArray()})).Contains("40000"),
            "large annotation batches are limited before native text layout");
        var circles=new List<object>();for(var i=0;i<400;i++)circles.Add(new {type="circle",center=new[]{i,0},radius=1});
        Check(CadPlanValidator.Validate(Json.Serialize(new {commands=circles.ToArray()}))==null,
            "existing 400-command ordinary geometry workload remains valid");
        Check(CadPlanValidator.Validate(Plan("{\"type\":\"ngon\",\"center\":[0,0],\"radius\":1,\"sides\":\"1e9999\"}")) != null,
            "the budget prepass leaves malformed numeric details to normal validation without throwing");
    }

    private static void TestGeometryReferences()
    {
        var outer = "{\"type\":\"rect\",\"id\":\"outer\",\"p1\":[0,0],\"p2\":[1000,1100]}";
        var hole = "{\"type\":\"rect\",\"id\":\"opening\",\"p1\":[150,150],\"p2\":[850,950]}";
        var dimension = "{\"type\":\"dimlinear\",\"reference\":\"opening\",\"p1\":[850,150],\"p2\":[850,950],\"rotation\":90,\"position\":[1050,550],\"value\":800}";
        var plan = Plan(outer + "," + hole + "," + dimension);
        Check(CadPlanValidator.Validate(plan) == null,"dimension reference binds to the actual inner contour");
        Check(CadPlanValidator.Validate(plan.Replace("\"dimlinear\"", "\"dim\"")) == null,
            "legacy dim alias keeps the same linear reference semantics");
        var wrong = Plan(outer + "," + hole + "," + dimension.Replace("[850,150]","[1000,150]").Replace("[850,950]","[1000,950]"));
        var error = CadPlanValidator.Validate(wrong);
        Check(error != null && error.Contains("opening") && error.Contains("p1"),
            "correct 800 reading on the outer wall cannot pass as an inner-contour reference");
        Check(CadPlanValidator.Validate(Plan(outer + "," + hole + "," + dimension.Replace("[850,150]","[850,300]").Replace("[850,950]","[850,800]").Replace(":800",":500"))) == null,
            "valid mid-edge extension points are not incorrectly forced to rectangle corners");
        Check(CadPlanValidator.Validate(plan.Replace("\"opening\",\"p1\":[850","\"missing\",\"p1\":[850")) != null,
            "unknown geometry reference cannot silently bind to another outline");
        Check(CadPlanValidator.Validate(Plan(hole + "," + hole + "," + dimension)) != null,
            "ambiguous duplicate geometry IDs are rejected");
        Check(CadPlanValidator.Validate(Plan(outer.Replace("\"id\":\"outer\"","\"id\":42"))) == null,
            "legacy unreferenced metadata does not change geometric execution");
        var block = "{\"type\":\"block\",\"name\":\"local\",\"commands\":[" + hole + "," + dimension + "]}";
        Check(CadPlanValidator.Validate(Plan(hole + "," + block)) == null,
            "geometry IDs are local to the current block scope");
        Check(CadPlanValidator.Validate(Plan(hole + ",{\"type\":\"block\",\"name\":\"other\",\"commands\":[" + dimension + "]}")) != null,
            "dimension references do not guess across block scopes");
        var source = plan.Replace("{\"commands\":","{\"sourceUnit\":\"cm\",\"coordinateUnit\":\"source\",\"commands\":");
        Check(CadPlanValidator.Validate(source) == null && CadPlanValidator.Validate(CadPlanNormalizer.Normalize(source)) == null,
            "reference IDs and endpoint relations survive source-unit normalization");
        var offset = Plan(outer + "," + hole + "," + dimension.Replace("[850,150]","[850.00005,150]").Replace("[850,950]","[850.00005,950]"));
        offset = offset.Replace("{\"commands\":","{\"sourceUnit\":\"cm\",\"coordinateUnit\":\"source\",\"commands\":");
        Check(CadPlanValidator.Validate(offset) == null && CadPlanValidator.Validate(CadPlanNormalizer.Normalize(offset)) == null,
            "reference tolerance is a physical 0.001 mm before and after cm normalization");
        var circle = "{\"type\":\"circle\",\"id\":\"c\",\"center\":[0,0],\"radius\":20}";
        var chord = "{\"type\":\"dimaligned\",\"reference\":\"c\",\"p1\":[-20,0],\"p2\":[20,0],\"position\":[0,-30],\"value\":40}";
        Check(CadPlanValidator.Validate(Plan(circle + "," + chord)) == null,"linear references can use actual circle boundary points");
        Check(CadPlanValidator.Validate(Plan(circle + "," + chord.Replace("[-20,0]","[0,0]").Replace(":40",":20"))) != null,
            "circle center is not mistaken for its boundary");
        var curve = "{\"type\":\"polyline\",\"id\":\"c\",\"points\":[[-20,0],[20,0]],\"bulges\":[1,0]}";
        Check(CadPlanValidator.Validate(Plan(curve + "," + chord)) != null,
            "unimplemented arc references cannot pass by testing only the chord");
        Check(CadPlanValidator.Validate(plan.Replace("[150,150]","[150,150,10]")) != null,
            "3D geometry is not certified by an XY-only reference check");
        Check(CadPlanValidator.Validate(Plan(hole + "," + dimension.Replace("\"opening\"", "42"))) != null,
            "non-string dimension references are rejected rather than coerced to IDs");
        Check(CadPlanValidator.Validate(Plan(circle + ",{\"type\":\"dimradius\",\"reference\":\"c\",\"center\":[0,0],\"point\":[20,0],\"value\":20}")) != null,
            "unimplemented radial reference is reported explicitly instead of being marked checked");
        var line = "{\"type\":\"line\",\"id\":\"edge\",\"points\":[[0,0],[20,0]]}";
        var past = "{\"type\":\"dimlinear\",\"reference\":\"edge\",\"p1\":[30,0],\"p2\":[50,0],\"position\":[30,-10],\"value\":20}";
        Check(CadPlanValidator.Validate(Plan(line + "," + past)) != null,
            "extension of a finite line is not its actual reference boundary");
        var meter = Plan(outer + "," + hole + "," + dimension.Replace("[850,150]","[850.000002,150]"));
        meter = meter.Replace("{\"commands\":", "{\"sourceUnit\":\"m\",\"coordinateUnit\":\"source\",\"commands\":");
        Check(CadPlanValidator.Validate(meter) != null && CadPlanValidator.Validate(CadPlanNormalizer.Normalize(meter)) != null,
            "meter source units do not accidentally enlarge the reference tolerance by 1000");
    }

    private static void TestPlanPatches()
    {
        var original="{\"sourceUnit\":\"cm\",\"coordinateUnit\":\"source\",\"views\":[\"3-3\"],\"commands\":["
            +"{\"type\":\"setvar\",\"name\":\"DIMSCALE\",\"value\":300},"
            +"{\"type\":\"rect\",\"id\":\"opening\",\"p1\":[150,150],\"p2\":[850,950]},"
            +"{\"type\":\"dimlinear\",\"reference\":\"opening\",\"side\":\"right\",\"position\":[800,550],\"value\":800},"
            +"{\"type\":\"text\",\"position\":[400,-100],\"height\":30,\"content\":\"3-3\"}]}";
        var hash=CadPlanPatch.Hash(original);
        Func<object,string> patch=editList=>Json.Serialize(new{base_sha256=hash,updates=editList});
        var updates=new object[]{new {command="3",fields=new{position=new[]{1100,550}}},new{command="4",fields=new{height=100}}};
        var request=Json.Serialize(new{base_sha256=hash,updates=updates,setvars=new{LTSCALE=4000,DIMTSZ=25}});
        var next=CadPlanPatch.Apply(original,request);
        var root=(Dictionary<string,object>)Json.DeserializeObject(next);var commands=(object[])root["commands"];
        Check(commands.Length==6 && ((Dictionary<string,object>)commands[1])["name"].ToString()=="LTSCALE"
            && ((Dictionary<string,object>)commands[2])["name"].ToString()=="DIMTSZ",
            "source style patches are inserted after settings and before the first actual entity");
        var dim=(Dictionary<string,object>)commands[4];var shape=(Dictionary<string,object>)commands[3];
        Check(CadPlanQuality.Number(dim["value"])==800 && Convert.ToString(dim["reference"])=="opening"
            && CadPlanQuality.ReadPoint(dim["position"])[0]==1100 && CadPlanQuality.ReadPoint(shape["p1"])[0]==150,
            "patch updates resolve original command paths before insertion without changing readings or geometry");
        Check(!original.Contains("4000") && original.Contains("\"height\":30") && CadPlanValidator.Validate(next)==null,
            "applying a valid patch leaves the original source string intact");
        Check(CadPlanPatch.Extract("```cadpatch\n"+request+"\n```")==request,
            "one explicit patch block is extracted without being mistaken for a full drawing plan");
        var context=CadPlanPatch.Context(original);
        Check(!context.Contains("\"commands\"") && context.Contains("editable_targets") && context.Contains("geometry_context")
            && context.Contains(hash) && !context.Contains("\"allowed_fields\":[\"value\""),
            "patch context is a hash-bound target summary rather than a copy of the original drawing program");
        Action<string,string> rejected=(candidate,name)=>{try{CadPlanPatch.Apply(original,candidate);Check(false,name);}catch(InvalidOperationException){Check(true,name);}};
        rejected(request.Replace(hash,new string('0',64)),"stale plan hashes cannot receive a style patch");
        foreach(var field in new[]{"value","p1","reference","side","type","confidence","text"})
        {
            var fields=new Dictionary<string,object>{{field,1}};
            rejected(patch(new[]{new{command="3",fields=fields}}),"style patch cannot alter dimension "+field);
        }
        rejected(patch(new[]{new{command="2",fields=new{p1=new[]{0,0}}}}),"style patches cannot edit a contour");
        rejected(patch(new[]{new{command="4",fields=new{content="changed"}}}),"style patches cannot replace source title content");
        rejected(patch(new[]{new{command="4",fields=new{height=-1}}}),"negative text height is rejected before CAD");
        rejected(patch(new[]{new{command="3",fields=new{position=new[]{1,2,3}}}}),"patch positions cannot smuggle 3D projection changes");
        rejected(patch(new[]{new{command="99999999999999999",fields=new{position=new[]{1,2}}}}),"overflowing command paths cannot address other objects");
        rejected(patch(new[]{new{command="3",fields=new{position=new[]{1,2}}},new{command="3",fields=new{position=new[]{3,4}}}}),"duplicate command updates are ambiguous and rejected");
        foreach(var name in new[]{"SECURELOAD","DIMLFAC","INSUNITS"})
            rejected(Json.Serialize(new{base_sha256=hash,setvars=new Dictionary<string,object>{{name,1}}}),"patch cannot change "+name);
        rejected(Json.Serialize(new{base_sha256=hash,setvars=new{LTSCALE=Double.PositiveInfinity}}),"non-finite style values cannot reach native CAD");
        rejected(patch(new object[0]),"empty patches do not claim to repair a drawing");
        var late=original.Replace("\"commands\":[", "\"commands\":[{\"type\":\"circle\",\"center\":[0,0],\"radius\":1},");
        try{CadPlanPatch.Apply(late,Json.Serialize(new{base_sha256=CadPlanPatch.Hash(late),setvars=new{LTSCALE=4000,DIMTSZ=25,DIMSCALE=100}}));Check(false,"late style");}
        catch(InvalidOperationException){Check(true,"adding several style fields cannot bypass the original settings boundary");}
        var multi=original.Replace("[\"3-3\"]","[\"3-3\",\"4-4\"]");
        try{CadPlanPatch.Apply(multi,Json.Serialize(new{base_sha256=CadPlanPatch.Hash(multi),setvars=new{LTSCALE=4000}}));Check(false,"multi-view style");}
        catch(InvalidOperationException){Check(true,"global style patches cannot affect another declared view");}
        var nested=Plan("{\"type\":\"BLOCK\",\"name\":\"patch_scope\",\"commands\":[{\"type\":\"text\",\"position\":[0,0],\"height\":3,\"content\":\"SOURCE\"}]}");
        var localPatch=Json.Serialize(new{base_sha256=CadPlanPatch.Hash(nested),updates=new[]{new{command="1.1",fields=new{height=4}}}});
        Check(CadPlanPatch.Apply(nested,localPatch).Contains("\"height\":4") && CadPlanPatch.Context(nested).Contains("\"can_setvars\":false"),
            "block-local annotation updates retain scope and disable global style suggestions");
    }

    private static async Task TestPatchClient()
    {
        var original="{\"sourceUnit\":\"mm\",\"coordinateUnit\":\"source\",\"views\":[\"section\"],\"commands\":["
            +"{\"type\":\"rect\",\"id\":\"r\",\"p1\":[0,0],\"p2\":[100,80]},"
            +"{\"type\":\"dimlinear\",\"reference\":\"r\",\"side\":\"right\",\"position\":[110,40],\"value\":80,\"confidence\":\"read\"}]}";
        var patch=Json.Serialize(new {base_sha256=CadPlanPatch.Hash(original),updates=new[]{new{command="2",fields=new{position=new[]{130,40}}}}});
        Func<string,string> response=messageText=>Json.Serialize(new{choices=new[]{new{message=new{content=messageText},finish_reason="stop"}}});
        var answerText="```cadpatch\n"+patch+"\n```";
        using(var server=new Server())
        {
            server.Response=i=>Tuple.Create(200,response(answerText));
            var model=Model(server);model.ModelId="auto";
            var client=new OpenAiCompatibleClient();
            var answer=await client.CompletePlanPatchAsync(model,original,"Only adjust the dimension location",ImagePath,CancellationToken.None);
            Check(answer==answerText && server.Requests.Count==1 && server.Requests[0].Contains("image_url")
                && !HasTemperature(server.Requests[0]) && !server.Requests[0].Contains("绘图协议："),
                "patch AI request uses the real image and dedicated small-patch contract without full-plan regeneration");
            Check(CadPlanPatch.Apply(original,CadPlanPatch.Extract(answer)).Contains("130"),
                "validated AI patch can be applied locally while preserving the original geometry");
        }
        foreach(var bad in new[]{answerText.Replace("\"position\":[130,40]","\"value\":90"),
            "```cad\n"+original+"\n```",answerText+"\n"+answerText})
        using(var server=new Server())
        {
            server.Response=i=>Tuple.Create(200,response(bad));
            try{await new OpenAiCompatibleClient().CompletePlanPatchAsync(Model(server),original,"review",ImagePath,CancellationToken.None);Check(false,"bad patch response");}
            catch(InvalidOperationException){Check(server.Requests.Count==1,"unsafe, full-plan or multiple patch output stops without drawing or an extra repair request");}
        }
        using(var server=new Server())
        {
            server.Response=i=>Tuple.Create(200,response(answerText).Replace("\"stop\"","\"content_filter\""));
            try{await new OpenAiCompatibleClient().CompletePlanPatchAsync(Model(server),original,"review",ImagePath,CancellationToken.None);Check(false,"filtered patch");}
            catch(InvalidOperationException){Check(server.Requests.Count==1,"filtered patch output cannot trigger a correction loop");}
        }
        using(var server=new Server())
        {
            var token=new CancellationToken(true);
            try{await new OpenAiCompatibleClient().CompletePlanPatchAsync(Model(server),original,"review",ImagePath,token);Check(false,"cancelled patch");}
            catch(OperationCanceledException){Check(server.Requests.Count==0,"cancelled patch tasks cannot consume a model request");}
            try{await new OpenAiCompatibleClient().CompletePlanPatchAsync(Model(server),new string('x',32001),"review",ImagePath,CancellationToken.None);Check(false,"oversized patch base");}
            catch(InvalidOperationException){Check(server.Requests.Count==0,"patch input limits reject instead of silently truncating the source plan");}
        }
        using(var server=new Server())
        {
            var model=Model(server);model.ModelId="auto";model.ReasoningEffort="low";
            server.Response=i=>i==1?Tuple.Create(400,"{\"error\":{\"message\":\"auto does not support parameter(s): reasoning_effort; please remove them\"}}")
                :Tuple.Create(200,response(answerText));
            var client=new OpenAiCompatibleClient();
            var answer=await client.CompletePlanPatchAsync(model,original,"review",ImagePath,CancellationToken.None);
            Check(answer==answerText && server.Requests.Count==2 && !String.IsNullOrWhiteSpace(client.LastCompatibilityWarning)
                && server.Requests[0].Contains("reasoning_effort") && Reasoning(server.Requests[1])==null
                && server.Requests.TrueForAll(r=>r.Contains("image_url") && r.Contains("cadpatch") && !r.Contains("绘图协议：")),
                "patch parameter compatibility retries preserve image and patch mode while reporting budget fallback");
        }
        using(var server=new Server())
        {
            try{await new OpenAiCompatibleClient().CompletePlanPatchAsync(Model(server),original.Replace(",\"confidence\":\"read\"",""),"review",ImagePath,CancellationToken.None);Check(false,"invalid image patch base");}
            catch(InvalidOperationException){Check(server.Requests.Count==0,"missing image dimension evidence blocks a patch request before it consumes quota");}
        }
    }

    private static void TestDimensionAnchors()
    {
        var geometry = "{\"type\":\"rect\",\"id\":\"opening\",\"p1\":[850,950],\"p2\":[150,150]}";
        foreach (var side in new[] { "top", "bottom", "left", "right" })
        {
            var vertical = side == "left" || side == "right";
            var command = "{\"type\":\"dimlinear\",\"reference\":\"opening\",\"side\":\"" + side
                + "\",\"position\":[1050,550],\"value\":" + (vertical ? 800 : 700) + "}";
            var plan = "{\"sourceUnit\":\"cm\",\"coordinateUnit\":\"source\",\"commands\":[" + geometry + "," + command + "]}";
            Check(CadPlanValidator.Validate(plan) == null,"rectangle-side " + side + " validates without model-invented extension endpoints");
            var prepared = PreparedCadPlan.Create(plan);
            var root = prepared.Claim(); var resolved = (Dictionary<string, object>)((object[])root["commands"])[1];
            var a = CadPlanQuality.ReadPoint(resolved["p1"]); var b = CadPlanQuality.ReadPoint(resolved["p2"]);
            var correct = vertical ? a[0] == (side == "left" ? 1500 : 8500) && b[0] == a[0] && a[1] == 1500 && b[1] == 9500
                : a[1] == (side == "bottom" ? 1500 : 9500) && b[1] == a[1] && a[0] == 1500 && b[0] == 8500;
            Check(correct && !resolved.ContainsKey("side") && Convert.ToInt32(resolved["rotation"]) == (vertical ? 90 : 0),
                "rectangle-side " + side + " resolves reversed corners and converts cm exactly once");
            Check(CadDimensionAnchors.ResolveJson(prepared.Json) == prepared.Json,
                "prepared " + side + " anchor plan replays without generating or scaling endpoints again");
        }
        var dimension = "{\"type\":\"dimlinear\",\"reference\":\"opening\",\"side\":\"right\",\"position\":[1050,550],\"value\":800}";
        var valid = Plan(geometry + "," + dimension);
        Check(CadPlanValidator.Validate(valid.Replace(":800", ":850")) != null,
            "local endpoint calculation does not overwrite an inconsistent source reading to force success");
        Check(CadPlanValidator.Validate(valid.Replace("\"side\":\"right\"", "\"side\":\"right\",\"p1\":[1000,150]")) != null,
            "explicit endpoints cannot conflict with rectangle-side intent");
        Check(CadPlanValidator.Validate(valid.Replace("\"side\":\"right\"", "\"side\":\"right\",\"rotation\":0")) != null,
            "a contradictory explicit rotation is rejected rather than silently changed");
        Check(CadPlanValidator.Validate(valid.Replace("\"side\":\"right\"", "\"side\":\"right\",\"rotation\":90")) == null,
            "a redundant but correct vertical rotation from a real AI failure avoids needless repair");
        Check(CadPlanValidator.Validate(valid.Replace("\"side\":\"right\"", "\"side\":\"top\",\"rotation\":0").Replace(":800", ":700")) == null,
            "a redundant horizontal rotation retains exact side endpoints and source value");
        Check(CadPlanValidator.Validate(valid.Replace("\"reference\":\"opening\",", "")) != null,
            "automatic rectangle endpoints never guess a missing geometry reference");
        Check(CadPlanValidator.Validate(valid.Replace("\"side\":\"right\"", "\"side\":\"nearest\"")) != null,
            "unsupported side selection cannot guess the nearest or visible edge");
        Check(CadPlanValidator.Validate(Plan(dimension + "," + geometry)) == null,
            "side intent can refer to a later rectangle in the same scope");
        var block = "{\"type\":\"block\",\"name\":\"view\",\"commands\":[" + geometry + "," + dimension + "]}";
        Check(CadPlanValidator.Validate(Plan(block)) == null,
            "side resolution uses the local block geometry");
        Check(CadPlanValidator.Validate(Plan(geometry + ",{\"type\":\"block\",\"name\":\"other\",\"commands\":[" + dimension + "]}")) != null,
            "side resolution never borrows a rectangle from another block scope");
        Check(CadPlanValidator.Validate(valid.Replace("\"dimlinear\"", "\"dimaligned\"")) != null,
            "unimplemented aligned side intent is explicit rather than approximated");
        var polygon = "{\"type\":\"polyline\",\"id\":\"opening\",\"points\":[[150,150],[850,150],[850,950],[150,950]],\"closed\":true}";
        Check(CadPlanValidator.Validate(Plan(polygon + "," + dimension)) != null,
            "arbitrary polyline bounds are not treated as an implicit rectangle measurement");
        var p1=new List<object>();var p2=new List<object>();
        for(var i=0;i<9998;i++)p1.Add(new[]{i,0});for(var i=0;i<9999;i++)p2.Add(new[]{i,1});
        var raw=Json.Serialize(new {commands=new object[]{new{type="polyline",points=p1.ToArray()},new{type="polyline",points=p2.ToArray()},
            Json.DeserializeObject(geometry),Json.DeserializeObject(dimension)}});
        Check(CadPlanValidator.Validate(raw).Contains("20000"),
            "generated anchor endpoints count toward the whole-plan input budget");
        Check(CadPlanValidator.Validate(valid.Replace("[850,950]", "[850]")) != null,
            "malformed target coordinates produce a validation error instead of an anchor resolver crash");
        var alias=valid.Replace("\"p1\":[850,950],\"p2\":[150,150]", "\"points\":[[850,950],[150,150]]");
        Check(CadPlanValidator.Validate(alias)==null,
            "actual AI rect points-pair format can be adapted locally without a second model call");
        var resolvedAlias=CadDimensionAnchors.ResolveJson(alias);
        Check(!resolvedAlias.Contains("\"points\"") && resolvedAlias.Contains("\"p1\":[850,150]")
            && CadPlanValidator.Validate(resolvedAlias)==null,
            "rectangle input adaptation preserves both opposite corners and computes the same referenced side");
        Check(CadPlanValidator.Validate(Plan("{\"type\":\"rect\",\"points\":[[0,0],[30,40]]}"))==null,
            "unreferenced rectangle points-pair format uses the same exact adapter");
        Check(CadPlanValidator.Validate(alias.Replace("[[850,950],[150,150]]","[[850,950],[150,150],[0,0]]"))!=null,
            "arbitrary polygon points cannot become an invented rectangular bounding box");
        Check(CadPlanValidator.Validate(alias.Replace("\"points\":", "\"p1\":[0,0],\"points\":"))!=null,
            "ambiguous mixed rectangle formats cannot silently select one coordinate source");
        Check(CadPlanValidator.Validate(valid.Replace("\"id\":\"opening\"", "\"id\":\"opening\",\"rotation\":45"))!=null,
            "rectangle rotation cannot be silently ignored while claiming accurate geometry");
    }

    private static async Task TestReferenceRepair()
    {
        var geometry = "{\"type\":\"rect\",\"id\":\"opening\",\"p1\":[150,150],\"p2\":[850,950]}";
        var dimension = "{\"type\":\"dimlinear\",\"reference\":\"opening\",\"p1\":[850,150],\"p2\":[850,950],\"rotation\":90,\"position\":[1050,550],\"value\":800}";
        var valid = Plan(geometry + "," + dimension);
        var invalid = Plan(geometry + "," + dimension.Replace("[850,150]","[1000,150]").Replace("[850,950]","[1000,950]"));
        using (var server = new Server())
        {
            server.Response = i => Tuple.Create(200, Reply(i == 1 ? invalid : valid));
            var answer = await new OpenAiCompatibleClient().CompleteAsync(Model(server), History(), "", null);
            Check(server.Requests.Count == 2 && server.Requests[1].Contains("opening")
                && server.Requests[1].Contains("p1") && CadPlanText.Extract(answer) == valid,
                "one AI repair receives the exact wrong-contour evidence and retains reference coverage");
        }
        using (var server = new Server())
        {
            server.Response = i => Tuple.Create(200, Reply(i == 1 ? invalid : invalid.Replace("\"reference\":\"opening\",", "")));
            try { await new OpenAiCompatibleClient().CompleteAsync(Model(server), History(), "", null); Check(false, "removed reference"); }
            catch (InvalidOperationException ex) { Check(server.Requests.Count == 2 && ex.Message.Contains("删除"),
                "repair cannot hide wrong-contour evidence by silently deleting its reference"); }
        }
        Check(CadGeometryReferences.RepairCoverageError(invalid, valid.Replace("opening", "corrected-opening")) == null,
            "repair can correct the referenced target ID without retaining an initially wrong ID");
        var before = "{\"commands\":[{\"type\":\"block\",\"name\":\"section\",\"commands\":[" + geometry + "," + dimension + "]}]}";
        Check(CadGeometryReferences.RepairCoverageError(before, valid) != null,
            "repair cannot replace a block-local checked dimension with unrelated model-space coverage");
        Check(CadGeometryReferences.RepairCoverageError("{broken", valid) == null,
            "unreadable JSON can still receive its single syntax repair");
    }

    private static void TestDimensionSizeSemantics()
    {
        var source = "{\"sourceUnit\":\"cm\",\"coordinateUnit\":\"source\",\"commands\":[{\"type\":\"setvar\",\"name\":\"DIMTSZ\",\"value\":25},{\"type\":\"frame\",\"size\":\"A3\",\"scale\":100},{\"type\":\"dimlinear\",\"p1\":[0,0],\"p2\":[100,0],\"position\":[0,-20]}]}";
        var prepared = PreparedCadPlan.Create(source);
        var root = prepared.Claim();
        var commands = (object[])root["commands"];
        var sizes = (Dictionary<string, double>)((Dictionary<string, object>)commands[2])["_modelDimensionSizes"];
        Check(sizes["DIMTSZ"] == 250 && CadDimensionSizing.PaperLength(sizes["DIMTSZ"],100) == 2.5,
            "25 cm source ticks become 250 model mm and 2.5 paper mm at 1:100, without double scaling");
        var repeated = PreparedCadPlan.Create(prepared.Json);
        Check(repeated.FinalModelDimensionSizes["DIMTSZ"] == 250 && CadPlanNormalizer.Normalize(prepared.Json) == prepared.Json,
            "normalized plan replay retains dimension-length semantics and is idempotent");
        Check(PreparedCadPlan.Create(source.Replace("\"source\"", "\"mm\"")).FinalModelDimensionSizes.Count == 0,
            "legacy millimeter plans preserve native dimension-variable semantics");
        var reset = source.Replace("{\"type\":\"frame\"", "{\"type\":\"standard\"},{\"type\":\"frame\"");
        var afterStandard = PreparedCadPlan.Create(reset);
        Check(afterStandard.FinalModelDimensionSizes.Count == 0,
            "standard command resets previous source-size overrides in execution order");
        var plain = Plan("{\"type\":\"dimlinear\",\"p1\":[0,0],\"p2\":[100,0],\"position\":[0,-20],\"_modelDimensionSizes\":{\"DIMTSZ\":999}}");
        var ignored = PreparedCadPlan.Create(plain).Claim();
        Check(!((Dictionary<string,object>)((object[])ignored["commands"])[0]).ContainsKey("_modelDimensionSizes"),
            "untrusted internal dimension-size annotations are discarded before preparation");
        try { CadDimensionSizing.PaperLength(250,0); Check(false,"unknown annotation scale"); }
        catch(InvalidOperationException) { Check(true,"unknown annotation scale cannot silently invent paper lengths"); }
    }

    private static void TestHatchBoundaries()
    {
        Func<string, string, string> hatch = (points, holes) => Plan("{\"type\":\"hatch\",\"points\":" + points + ",\"holes\":" + holes + "}");
        var square = "[[0,0],[10,0],[10,10],[0,10]]";
        Check(CadPlanValidator.Validate(hatch(square, "[[[2,2],[8,2],[8,8],[2,8]]]")) == null, "simple hatch with inner hole accepted");
        Check(CadPlanValidator.Validate(hatch("[[0,0],[10,10],[0,10],[10,0]]", "[]")) != null, "self crossing hatch rejected before CAD");
        Check(CadPlanValidator.Validate(hatch("[[0,0],[10,0],[5,0],[5,10],[0,10]]", "[]")) != null, "backtracking hatch spike rejected");
        Check(CadPlanValidator.Validate(hatch(square, "[[[12,2],[18,2],[18,8],[12,8]]]")) != null, "outside hatch hole rejected");
        Check(CadPlanValidator.Validate(hatch(square, "[[[0,2],[8,2],[8,8],[0,8]]]")) != null, "touching hatch hole rejected");
        Check(CadPlanValidator.Validate(hatch(square, "[[[1,1],[7,1],[7,7],[1,7]],[[5,5],[9,5],[9,9],[5,9]]]")) != null, "overlapping hatch holes rejected");
        Check(CadPlanValidator.Validate(hatch("[[0,0],[10,0],[10,10],[0,10],[0,0]]", "[]")) == null, "explicit hatch closure accepted");
        var openSection = "[[0,0],[10,0],[10,10],[8,10],[8,2],[2,2],[2,10],[0,10]]";
        Check(CadPlanValidator.Validate(hatch(openSection, "[]")) == null, "U-shaped open channel is an actual concave outer boundary without a false hole");
        var contact = CadPlanValidator.Validate(hatch(square, "[[[2,2],[8,2],[8,10],[2,10]]]"));
        Check(contact != null && contact.Contains("凹多边形") && contact.Contains("[8,10]") && contact.Contains("不得删除孔洞"),
            "touching-hole repair explains offending segments and open-boundary topology without deleting source voids");
    }

    private static void TestPdfSnapshot(string folder)
    {
        Check(PdfSourceService.Quote("D:\\a b\\") == "\"D:\\a b\\\\\"", "PDF process quotes trailing slash in paths");
        Check(PdfSourceService.Quote("a\"b") == "\"a\\\"b\"", "PDF process escapes embedded quotes");
        var path = Path.Combine(folder, "page.pdf"); File.WriteAllText(path, "source snapshot");
        var source = new PdfPageSource { kind = "vector", pdf = path, pdf_hash = PdfPageSource.Hash(path) };
        source.VerifySnapshot(); Check(source.CanImport, "vector PDF snapshot accepted");
        File.WriteAllText(path, "modified snapshot");
        try { source.VerifySnapshot(); Check(false, "PDF mutation rejected"); } catch (InvalidOperationException) { Check(true, "PDF mutation rejected"); }
        source.kind = "raster"; source.pdf_hash = PdfPageSource.Hash(path);
        try { source.VerifySnapshot(); Check(false, "raster PDF cannot use vector import"); } catch (InvalidOperationException) { Check(true, "raster PDF cannot use vector import"); }
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

    private static void TestMaterials()
    {
        string pattern; double paperSize;
        Check(HatchMaterialPolicy.TryResolve("堆石",out pattern,out paperSize) && pattern=="GRAVEL", "riprap material uses stone pattern");
        Check(HatchMaterialPolicy.TryResolve("sand",out pattern,out paperSize) && pattern=="AR-SAND", "sand material uses stipple pattern");
        var shape="{\"type\":\"hatch\",\"points\":[[0,0],[100,0],[100,100],[0,100]],\"material\":\"sand\",\"spacing\":5}";
        Check(CadPlanValidator.Validate(Plan(shape))==null, "material hatch accepts explicit spacing");
        Check(CadPlanValidator.Validate(Plan(shape.Replace("sand","mystery")))!=null, "unknown hatch material rejected");
        Check(CadPlanValidator.Validate(Plan(shape.Replace("\"spacing\":5","\"spacing\":-1")))!=null, "negative texture spacing rejected");
        var normalized=CadPlanNormalizer.Normalize("{\"sourceUnit\":\"cm\",\"coordinateUnit\":\"source\",\"commands\":["+shape+"]}");
        var root=(Dictionary<string,object>)Json.DeserializeObject(normalized);
        var c=(Dictionary<string,object>)((object[])root["commands"])[0];
        Check(Convert.ToDouble(c["spacing"])==50, "texture spacing converts with source geometry");
        Check(HatchMaterialPolicy.TooDense(1000000,.1), "excessive hatch density detected");
    }

    private static void TestPointSpacing()
    {
        var hatch = "{\"type\":\"hatch\",\"pattern\":\"AR-SAND\",\"pointSpacing\":2,\"points\":[[0,0],[100,0],[100,100],[0,100]]}";
        Check(CadPlanValidator.Validate(Plan(hatch)) == null, "area-equivalent point spacing accepted for AR-SAND");
        Check(CadPlanValidator.Validate(Plan(hatch.Replace("AR-SAND", "DOTS"))) == null, "point spacing accepted for DOTS");
        Check(CadPlanValidator.Validate(Plan(hatch.Replace("\"pattern\":\"AR-SAND\"", "\"material\":\"sand\""))) == null, "sand preset resolves to a supported pure point pattern");
        foreach (var value in new[] { "0", "-1", "1e309", "100000001" })
            Check(CadPlanValidator.Validate(Plan(hatch.Replace("\"pointSpacing\":2", "\"pointSpacing\":" + value))) != null, "invalid point spacing rejected: " + value);
        foreach (var value in new[] { "ANSI31", "AR-CONC", "SOLID" })
            Check(CadPlanValidator.Validate(Plan(hatch.Replace("AR-SAND", value))) != null, "point spacing cannot pretend to size non-point pattern " + value);
        foreach (var field in new[] { "spacing", "scale" })
            Check(CadPlanValidator.Validate(Plan(hatch.Replace("\"pointSpacing\":2", "\"pointSpacing\":2,\"" + field + "\":1"))) != null, "point spacing rejects ambiguous " + field);
        Check(CadPlanValidator.Validate(Plan("{\"type\":\"circle\",\"center\":[0,0],\"radius\":1,\"pointSpacing\":2}")) != null, "point spacing cannot be silently ignored on other entities");
        var normalized = CadPlanNormalizer.Normalize("{\"sourceUnit\":\"cm\",\"coordinateUnit\":\"source\",\"commands\":[" + hatch + "]}");
        var root = (Dictionary<string, object>)Json.DeserializeObject(normalized);
        var command = (Dictionary<string, object>)((object[])root["commands"])[0];
        Check(Convert.ToDouble(command["pointSpacing"]) == 20 && CadPlanNormalizer.Normalize(normalized) == normalized,
            "point spacing source units convert exactly once");
        var d = HatchMaterialPolicy.PointFamilyDensity(0, 3, 2, new[] { 0.0, -4.0 });
        var a = .7;
        var rotated = HatchMaterialPolicy.PointFamilyDensity(a, 3 * Math.Cos(a) - 2 * Math.Sin(a), 3 * Math.Sin(a) + 2 * Math.Cos(a), new[] { 0.0, -4.0 });
        Check(Math.Abs(d - .125) < 1e-10 && Math.Abs(d - rotated) < 1e-10,
            "point rate uses perpendicular line gap and repeat period, invariant under rotation and along-line shift");
        Check(Math.Abs(HatchMaterialPolicy.PointFamilyDensity(0, 6, 4, new[] { 0.0, -8.0 }) - d / 4) < 1e-10,
            "doubling physical point pattern scale quarters its area density");
        foreach (var dash in new[] { new[] { 1.0, -4.0 }, new[] { 0.0, 0.0 }, new[] { Double.NaN, -1.0 } })
        {
            var rejected = false;
            try { HatchMaterialPolicy.PointFamilyDensity(0, 0, 2, dash); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "invalid or mixed line-and-dot pattern cannot produce a false point rate");
        }
        var basePlan = Plan(hatch);
        var patched = CadPlanPatch.Apply(basePlan, Json.Serialize(new { base_sha256 = CadPlanPatch.Hash(basePlan), updates = new[] { new { command = "1", fields = new { pointSpacing = 3 } } } }));
        Check(patched.Contains("\"pointSpacing\":3") && basePlan.Contains("\"pointSpacing\":2"), "density-only patch preserves original geometry and plan text");
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
        model.ReasoningEffort = "low";
        Check(key != DrawingPlanCache.Key(ImagePath, "copy", model, false), "reasoning changes invalidate the executed-plan cache");
        model.ReasoningEffort = " ";
        Check(key == DrawingPlanCache.Key(ImagePath, "copy", model, false), "service-default reasoning preserves the previous cache identity");
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
        private static readonly HashSet<int> UsedPorts = new HashSet<int>();
        private readonly HttpListener Listener = new HttpListener();
        public readonly List<string> Requests = new List<string>();
        public string Url;
        public Func<int, Tuple<int, string>> Response;
        public Server()
        {
            int number;
            while (true)
            {
                var port = new TcpListener(IPAddress.Loopback, 0); port.Start(); number = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
                lock (UsedPorts) { if (UsedPorts.Add(number)) break; }
            }
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

    private static bool HasTemperature(string request)
    { return ((Dictionary<string, object>)Json.DeserializeObject(request)).ContainsKey("temperature"); }
    private static string StreamReply(string content)
    { return "data: " + Json.Serialize(new { choices = new[] { new { delta = new { content = content } } } }) + "\n\ndata: [DONE]\n\n"; }
    private static async Task TestRuntimeBehavior()
    {
        Func<string, string> reply = value => Json.Serialize(new { choices = new[] { new { message = new { content = value }, finish_reason = "stop" } } });
        using (var s = new Server())
        {
            s.Response = i => Tuple.Create(200, reply("JSON_OK"));
            var delta = new StringBuilder();
            var answer = await new OpenAiCompatibleClient().CompleteAsync(Model(s), History(), "", null, value => delta.Append(value));
            Check(answer == "JSON_OK" && delta.ToString() == answer && s.Requests.Count == 1,
                "a JSON reply to a streaming request is consumed once without a second provider call");
        }
        using (var s = new Server())
        {
            s.Response = i => Tuple.Create(200, reply("PRETTY_JSON").Replace("{", "{\n"));
            var answer = await new OpenAiCompatibleClient().CompleteAsync(Model(s), History(), "", null, value => { });
            Check(answer == "PRETTY_JSON" && s.Requests.Count == 1, "multiline JSON is accepted without a fallback request");
        }
        using (var s = new Server())
        {
            s.Response = i => Tuple.Create(401, "{\"error\":{\"message\":\"invalid credential\"}}");
            try { await new OpenAiCompatibleClient().CompleteAsync(Model(s), History(), "", null, value => { }); Check(false, "authentication failure"); }
            catch (InvalidOperationException) { Check(s.Requests.Count == 1, "stream authentication errors do not repeat an identical failing request"); }
        }
        using (var s = new Server())
        {
            s.Response = i => s.Requests[i - 1].Contains("\"stream\":true")
                ? Tuple.Create(400, "{\"error\":{\"message\":\"stream is not supported\"}}") : Tuple.Create(200, reply("NO_STREAM"));
            var client = new OpenAiCompatibleClient(); var model = Model(s);
            await client.CompleteAsync(model, History(), "", null, value => { });
            await client.CompleteAsync(model, History(), "", null, value => { });
            Check(s.Requests.Count == 3, "explicit stream rejection is remembered for the same endpoint and model");
            model.ModelId = "another-stream-model";
            await client.CompleteAsync(model, History(), "", null, value => { });
            Check(s.Requests.Count == 5, "stream compatibility is scoped to its model");
        }
        var plan = Plan("{\"type\":\"circle\",\"center\":[0,0],\"radius\":5}");
        Func<string, string> eventLine = content => "data: " + Json.Serialize(new { choices = new[] { new { delta = new { content = content } } } }) + "\n\n";
        var truncated = "data: " + Json.Serialize(new { choices = new[] { new { delta = new { content = "```cad\n" + plan + "\n```" }, finish_reason = "length" } } }) + "\n\ndata: [DONE]\n\n";
        foreach (var body in new[] { truncated, truncated.Replace("length", "content_filter"), eventLine("partial"), eventLine("partial") + "data: {\"error\":{\"message\":\"upstream failed\"}}\n\n", "data: invalid-json\n\n" })
        using (var s = new Server())
        {
            s.Response = i => Tuple.Create(200, body);
            try { await new OpenAiCompatibleClient().CompleteAsync(Model(s), History(), "", null, value => { }); Check(false, "incomplete stream"); }
            catch (InvalidOperationException) { Check(s.Requests.Count == 1, "truncated interrupted or invalid streams never execute or silently restart"); }
        }
        using (var s = new Server())
        {
            var stop = "data: " + Json.Serialize(new { choices = new[] { new { delta = new { }, finish_reason = "stop" } } }) + "\n\n";
            s.Response = i => Tuple.Create(200, eventLine("FINISHED") + stop);
            var answer = await new OpenAiCompatibleClient().CompleteAsync(Model(s), History(), "", null, value => { });
            Check(answer == "FINISHED" && s.Requests.Count == 1, "finish_reason stop completes a stream without an extra DONE marker");
        }
        var preview = new StreamingPreview();
        preview.Append("说明`"); preview.Append("``cad\n");
        Check(preview.Take() == "说明\n（正在生成绘图指令…）", "preview recognizes a code fence split across deltas");
        var timer = Stopwatch.StartNew();
        for (var i = 0; i < 100000; i++) preview.Append("ignored plan data");
        Check(preview.BufferedCharacters < 100 && preview.Take() == null && timer.ElapsedMilliseconds < 1000,
            "100000 plan chunks create no more preview work after the fence");
        preview = new StreamingPreview(); preview.Append(new String('x', 100000));
        Check(preview.BufferedCharacters <= 8000 && preview.Take().Length < 8100 && preview.Take() == null,
            "stream preview is bounded and emits each changed snapshot only once");
    }

    private static string Reasoning(string request)
    {
        var payload = (Dictionary<string, object>)Json.DeserializeObject(request);
        return payload.ContainsKey("reasoning_effort") ? Convert.ToString(payload["reasoning_effort"]) : null;
    }

    private static async Task TestReasoningEffort()
    {
        Func<string, string> reply = text => Json.Serialize(new { choices = new[] { new { message = new { content = text }, finish_reason = "stop" } } });
        using (var s = new Server())
        {
            s.Response = i => Tuple.Create(200, reply("DEFAULT"));
            var client = new OpenAiCompatibleClient();
            var answer = await client.CompleteAsync(Model(s), History(), "", null);
            Check(answer == "DEFAULT" && Reasoning(s.Requests[0]) == null && client.LastCompatibilityWarning == null,
                "service default sends no reasoning field and changes no existing behavior");
        }
        foreach (var streaming in new[] { false, true })
        using (var s = new Server())
        {
            s.Response = i => Tuple.Create(200, streaming ? StreamReply("LOW") : reply("LOW"));
            var model = Model(s); model.ModelId = "auto"; model.ReasoningEffort = " LOW ";
            var requests = new List<Dictionary<string, object>>();
            var client = new OpenAiCompatibleClient { RequestObserver = options => requests.Add(options) };
            var answer = await client.CompleteAsync(model, History(), "", null, streaming ? (Action<string>)(value => { }) : null);
            Check(answer == "LOW" && Reasoning(s.Requests[0]) == "low" && !HasTemperature(s.Requests[0]),
                "auto preserves its model and temperature fix with explicitly selected reasoning (stream=" + streaming + ")");
            Check(requests.Count == 1 && (bool)requests[0]["reasoning_effort_sent"] && requests[0].Count == 5
                && !requests[0].ContainsKey("messages") && !requests[0].ContainsKey("Authorization"),
                "request diagnostics expose only option metadata, not prompts or credentials");
        }
        const string unsupported = "{\"error\":{\"message\":\"Unsupported parameter: reasoning_effort\"}}";
        foreach (var streaming in new[] { false, true })
        using (var s = new Server())
        {
            s.Response = i => i == 1 ? Tuple.Create(400, unsupported) : Tuple.Create(200, streaming ? StreamReply("FALLBACK") : reply("FALLBACK"));
            var model = Model(s); model.ModelId = "auto"; model.ReasoningEffort = "low";
            var client = new OpenAiCompatibleClient();
            var answer = await client.CompleteAsync(model, History(), "", null, streaming ? (Action<string>)(value => { }) : null);
            Check(answer == "FALLBACK" && s.Requests.Count == 2 && Reasoning(s.Requests[0]) == "low" && Reasoning(s.Requests[1]) == null
                && client.LastCompatibilityWarning != null, "explicit unsupported reasoning field retries once and visibly records service-default fallback");
            var first = (Dictionary<string, object>)Json.DeserializeObject(s.Requests[0]);
            var second = (Dictionary<string, object>)Json.DeserializeObject(s.Requests[1]); first.Remove("reasoning_effort");
            Check(Json.Serialize(first) == Json.Serialize(second), "reasoning fallback preserves model, image/messages and stream mode");
            await client.CompleteAsync(model, History(), "", null, streaming ? (Action<string>)(value => { }) : null);
            Check(s.Requests.Count == 3 && Reasoning(s.Requests[2]) == null && client.LastCompatibilityWarning != null,
                "unsupported reasoning capability is remembered, with a warning on later requests");
            model.ModelId = "different-model";
            await client.CompleteAsync(model, History(), "", null, streaming ? (Action<string>)(value => { }) : null);
            Check(Reasoning(s.Requests[3]) == "low" && client.LastCompatibilityWarning == null,
                "reasoning capability memory does not leak to another model");
        }
        using (var s = new Server())
        {
            s.Response = i => Tuple.Create(400, "{\"error\":{\"message\":\"Unsupported value: reasoning_effort low; supported values are high\"}}");
            var model = Model(s); model.ReasoningEffort = "low";
            try { await new OpenAiCompatibleClient().CompleteAsync(model, History(), "", null); Check(false, "unsupported reasoning value"); }
            catch (InvalidOperationException) { Check(s.Requests.Count == 1, "unsupported reasoning value is not silently replaced by another budget"); }
        }
        using (var s = new Server())
        {
            var valid = Plan("{\"type\":\"circle\",\"center\":[0,0],\"radius\":20}");
            s.Response = i => Tuple.Create(200, Reply(i == 1 ? valid.Replace(":20", ":-1") : valid));
            var model = Model(s); model.ReasoningEffort = "medium";
            await new OpenAiCompatibleClient().CompleteAsync(model, History(), "", null);
            Check(s.Requests.Count == 2 && Reasoning(s.Requests[0]) == "medium" && Reasoning(s.Requests[1]) == "medium",
                "single plan repair keeps the explicit reasoning budget");
        }
        using (var s = new Server())
        {
            var valid = "{\"sourceUnit\":\"mm\",\"coordinateUnit\":\"source\",\"views\":[\"section\"],\"commands\":[{\"type\":\"circle\",\"center\":[0,0],\"radius\":20}]}";
            s.Response = i => i == 1 ? Tuple.Create(400, unsupported) : Tuple.Create(200, Reply(valid));
            var model = Model(s); model.ModelId = "auto"; model.ReasoningEffort = "low";
            await new OpenAiCompatibleClient().CompleteAsync(model, History(), "", ImagePath);
            Check(s.Requests.Count == 2 && s.Requests[0].Contains("image_url") && s.Requests[1].Contains("image_url") && Reasoning(s.Requests[1]) == null,
                "reasoning fallback retains the source image and never becomes text-only reproduction");
        }
        using (var s = new Server())
        {
            var model = Model(s); model.ReasoningEffort = "unrecognised-budget";
            try { await new OpenAiCompatibleClient().CompleteAsync(model, History(), "", null); Check(false,"invalid reasoning option"); }
            catch (InvalidOperationException) { Check(s.Requests.Count == 0, "invalid reasoning configuration never reaches the provider"); }
        }
        using (var s = new Server())
        {
            s.Response = i => Tuple.Create(400, "{\"error\":{\"message\":\"Unsupported value: reasoning_effort low; supported values are high\"}}");
            var model = Model(s); model.ReasoningEffort = "low";
            try { await new OpenAiCompatibleClient().CompleteAsync(model, History(), "", ImagePath); Check(false, "image reasoning diagnostic"); }
            catch (InvalidOperationException ex) { Check(s.Requests.Count == 1 && ex.Message.Contains("reasoning_effort"),
                "image request errors preserve the actual rejected parameter instead of claiming missing vision support"); }
        }
        Check(ModelConfiguration.IsValidReasoningEffort(null) && ModelConfiguration.IsValidReasoningEffort("HIGH")
            && ModelConfiguration.IsValidReasoningEffort("none") && !ModelConfiguration.IsValidReasoningEffort("invented"),
            "reasoning configuration validates documented options without requiring every provider to support them");
    }

    private static async Task TestRepairEvidence()
    {
        var invalid = Plan("{\"type\":\"hatch\",\"points\":[[0,0],[10,0],[10,10],[0,10]],\"holes\":[[[2,2],[8,2],[8,10],[2,10]]]} ");
        var repaired = Plan("{\"type\":\"hatch\",\"points\":[[0,0],[10,0],[10,10],[8,10],[8,2],[2,2],[2,10],[0,10]]}");
        using (var s = new Server())
        {
            var raw = new List<string>();
            var client = new OpenAiCompatibleClient { ResponseObserver = (stage, body) => raw.Add(stage + ":" + body) };
            s.Response = i => Tuple.Create(200, Reply(i == 1 ? invalid : repaired));
            var answer = await client.CompleteAsync(Model(s), History(), "", null);
            Check(raw.Count == 2 && raw[0] == "http-200:" + Reply(invalid) && raw[1] == "http-200:" + Reply(repaired),
                "optional observer retains both original and repaired response bodies without changing them");
            Check(s.Requests.Count == 2 && s.Requests[1].Contains("凹多边形") && s.Requests[1].Contains("不得删除孔洞")
                && CadPlanText.Extract(answer) == repaired, "single repair receives actionable opening diagnostics and preserves the real U-section");
        }
        using (var s = new Server())
        {
            string captured = null;
            var client = new OpenAiCompatibleClient { ResponseObserver = (stage, body) => captured = body };
            var filtered = Reply(repaired).Replace("\"stop\"", "\"content_filter\"");
            s.Response = i => Tuple.Create(200, filtered);
            try { await client.CompleteAsync(Model(s), History(), "", null); Check(false, "filtered response diagnostics"); }
            catch (InvalidOperationException) { Check(captured == filtered && s.Requests.Count == 1, "rejected model output remains inspectable without an extra request"); }
        }
        using (var s = new Server())
        {
            var events = new List<string>();
            var client = new OpenAiCompatibleClient { ResponseObserver = (stage, body) => events.Add(stage) };
            s.Response = i => Tuple.Create(200, StreamReply("OBSERVED"));
            var answer = await client.CompleteAsync(Model(s), History(), "", null, value => { });
            Check(answer == "OBSERVED" && events.Count == 1 && events[0] == "sse", "optional stream observer captures provider events without duplicate model calls");
        }
        Check(new OpenAiCompatibleClient().ResponseObserver == null, "normal plugin requests do not opt into raw-response persistence");
    }

    private static async Task TestCompletionReasons()
    {
        var plan = Plan("{\"type\":\"circle\",\"center\":[0,0],\"radius\":20}");
        Func<string, string> reply = finish => Json.Serialize(new { choices = new[] {
            new { message = new { content = "```cad\n" + plan + "\n```" }, finish_reason = finish } } });
        foreach (var finish in new[] { "content_filter", "tool_calls", "function_call", "upstream_error", "length" })
        foreach (var streaming in new[] { false, true })
        using (var s = new Server())
        {
            s.Response = i => Tuple.Create(200, reply(finish));
            var emitted = new StringBuilder();
            try
            {
                await new OpenAiCompatibleClient().CompleteAsync(Model(s), History(), "", null,
                    streaming ? (Action<string>)(value => emitted.Append(value)) : null);
                Check(false, "abnormal JSON finish must not release a valid-looking drawing plan");
            }
            catch (InvalidOperationException)
            {
                Check(s.Requests.Count == 1 && emitted.Length == 0,
                    "JSON finish " + finish + " blocks execution and fallback (stream=" + streaming + ")");
            }
        }
        foreach (var finish in new[] { "stop", null, "" })
        using (var s = new Server())
        {
            s.Response = i => Tuple.Create(200, reply(finish));
            var answer = await new OpenAiCompatibleClient().CompleteAsync(Model(s), History(), "", null);
            Check(CadPlanText.Extract(answer) == plan && s.Requests.Count == 1,
                "normal or omitted JSON finish stays compatible: " + (finish ?? "null"));
        }
        using (var s = new Server())
        {
            var invalid = Plan("{\"type\":\"circle\",\"center\":[0,0],\"radius\":-1}");
            s.Response = i => Tuple.Create(200, i == 1 ? Reply(invalid) : reply("content_filter"));
            try { await new OpenAiCompatibleClient().CompleteAsync(Model(s), History(), "", null); Check(false, "repair finish"); }
            catch (InvalidOperationException) { Check(s.Requests.Count == 2, "repair cannot accept a filtered plan or make a third request"); }
        }
        using (var s = new Server())
        {
            var imagePlan = "{\"sourceUnit\":\"mm\",\"views\":[\"section\"],\"commands\":[{\"type\":\"circle\",\"center\":[0,0],\"radius\":20}]}";
            s.Response = i => Tuple.Create(200, Reply(imagePlan).Replace("\"stop\"", "\"content_filter\""));
            try { await new OpenAiCompatibleClient().CompleteAsync(Model(s), History(), "", ImagePath); Check(false, "vision finish"); }
            catch (InvalidOperationException) { Check(s.Requests.Count == 1 && s.Requests[0].Contains("image_url"), "filtered image output does not trigger repair or text-only fallback"); }
        }
    }

    private static async Task TestAutoCompatibility()
    {
        const string unsupported = "{\"error\":{\"message\":\"model does not support parameter(s): temperature; please remove them from your request\"}}";
        Func<string, string> text = value => Json.Serialize(new { choices = new[] { new { message = new { content = value } } } });
        var valid = Plan("{\"type\":\"circle\",\"center\":[0,0],\"radius\":20}");
        using (var s = new Server())
        {
            s.Response = i => Tuple.Create(200, text("AUTO_OK"));
            var model = Model(s); model.ModelId = "auto";
            var answer = await new OpenAiCompatibleClient().CompleteAsync(model, History(), "", null);
            Check(answer == "AUTO_OK" && !HasTemperature(s.Requests[0]), "auto omits temperature entirely rather than sending zero or null");
        }
        using (var s = new Server())
        {
            s.Response = i => Tuple.Create(200, Reply(i == 1 ? valid.Replace(":20", ":-1") : valid));
            var model = Model(s); model.ModelId = "AUTO";
            await new OpenAiCompatibleClient().CompleteAsync(model, History(), "", null);
            Check(s.Requests.Count == 2 && !HasTemperature(s.Requests[0]) && !HasTemperature(s.Requests[1]), "auto plan repair also omits temperature");
        }
        using (var s = new Server())
        {
            var imagePlan = "{\"sourceUnit\":\"mm\",\"views\":[\"section\"],\"commands\":[{\"type\":\"circle\",\"center\":[0,0],\"radius\":20}]}";
            s.Response = i => Tuple.Create(200, Reply(imagePlan));
            var model = Model(s); model.ModelId = "auto";
            await new OpenAiCompatibleClient().CompleteAsync(model, History(), "", ImagePath);
            Check(!HasTemperature(s.Requests[0]) && s.Requests[0].Contains("image_url"), "auto vision keeps its image while omitting temperature");
        }
        using (var s = new Server())
        {
            var bad = Plan("{\"type\":\"hatch\",\"points\":[[[0,0],[20,0],[20,10],[0,10]]],\"pattern\":\"ANSI31\"}");
            var repaired = "{\"sourceUnit\":\"mm\",\"coordinateUnit\":\"source\",\"views\":[\"section\"],\"commands\":[{\"type\":\"hatch\",\"points\":[[0,0],[20,0],[20,10],[0,10]],\"pattern\":\"ANSI31\"}]}";
            s.Response = i => Tuple.Create(200, Reply(i == 1 ? bad : repaired));
            var model = Model(s); model.ModelId = "auto";
            await new OpenAiCompatibleClient().CompleteAsync(model, History(), "", ImagePath);
            Check(s.Requests.Count == 2 && s.Requests[1].Contains("sourceUnit") && s.Requests[1].Contains("hatch")
                && s.Requests[1].Contains("image_url") && !HasTemperature(s.Requests[1]),
                "auto vision repair receives geometry and source metadata errors together with the original image");
        }
        using (var s = new Server())
        {
            s.Response = i => i == 1 ? Tuple.Create(400, unsupported) : Tuple.Create(200, text("OK"));
            var model = Model(s); model.ModelId = "parameter-router";
            var client = new OpenAiCompatibleClient();
            await client.CompleteAsync(model, History(), "", null);
            Check(s.Requests.Count == 2 && HasTemperature(s.Requests[0]) && !HasTemperature(s.Requests[1]), "explicit temperature rejection retries once without the unsupported field");
            var first = (Dictionary<string, object>)Json.DeserializeObject(s.Requests[0]);
            var second = (Dictionary<string, object>)Json.DeserializeObject(s.Requests[1]); first.Remove("temperature");
            Check(Json.Serialize(first) == Json.Serialize(second), "parameter compatibility preserves model, prompts, messages and stream setting");
            await client.CompleteAsync(model, History(), "", null);
            Check(s.Requests.Count == 3 && !HasTemperature(s.Requests[2]), "learned model capability avoids the next failed request");
            model.ModelId = "another-model";
            await client.CompleteAsync(model, History(), "", null);
            Check(HasTemperature(s.Requests[3]), "temperature compatibility is scoped to endpoint and model");
        }
        using (var s = new Server())
        {
            s.Response = i => Tuple.Create(400, "{\"error\":{\"message\":\"temperature must be a number\"}}");
            try { await new OpenAiCompatibleClient().CompleteAsync(Model(s), History(), "", null); Check(false, "invalid temperature value is not an unsupported capability"); }
            catch (InvalidOperationException) { Check(s.Requests.Count == 1, "invalid temperature value is not an unsupported capability"); }
        }
        using (var s = new Server())
        {
            s.Response = i => Tuple.Create(200, StreamReply("AUTO_STREAM_OK"));
            var model = Model(s); model.ModelId = "auto"; var delta = new StringBuilder();
            var answer = await new OpenAiCompatibleClient().CompleteAsync(model, History(), "", null, value => delta.Append(value));
            Check(answer == "AUTO_STREAM_OK" && delta.ToString() == answer && s.Requests.Count == 1 && !HasTemperature(s.Requests[0]), "auto streaming omits temperature without nonstream fallback");
        }
        using (var s = new Server())
        {
            s.Response = i => i == 1 ? Tuple.Create(400, unsupported) : Tuple.Create(200, StreamReply("STREAM_OK"));
            var model = Model(s); model.ModelId = "stream-router";
            var answer = await new OpenAiCompatibleClient().CompleteAsync(model, History(), "", null, value => { });
            Check(answer == "STREAM_OK" && s.Requests.Count == 2 && !HasTemperature(s.Requests[1]) && s.Requests[1].Contains("\"stream\":true"),
                "streaming compatibility retries the same streaming request once");
        }
    }

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

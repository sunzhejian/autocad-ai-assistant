using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(CadIntegrationTests))]
public sealed class CadIntegrationTests
{
    private static readonly List<object> Results = new List<object>();
    private static IDisposable WaitingGuard;
    private static string LastReceipt;
    private static string Run(string plan)
    {
        var assembly = Assembly.LoadFrom(Environment.GetEnvironmentVariable("CADAI_PLUGIN_DLL"));
        var type = assembly.GetType("AutoCAD.AI.Assistant.Services.CadDrawingExecutor", true);
        var instance = Activator.CreateInstance(type, true);
        var args = new object[] { plan, null };
        var result = type.GetMethod("Execute").Invoke(instance, args);
        LastReceipt = Convert.ToString(type.GetProperty("LastReceiptPath").GetValue(instance, null));
        Results.Add(new { result = result != null, message = Convert.ToString(args[1]), receipt_path = LastReceipt });
        return result == null ? null : Convert.ToString(args[1]);
    }
    private static int Count()
    {
        var db = AcApplication.DocumentManager.MdiActiveDocument.Database;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var space = (BlockTableRecord)tr.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            int count = 0; foreach (ObjectId id in space) count++;
            return count;
        }
    }
    private static void Check(bool condition, string name)
    {
        Results.Add(new { test = name, passed = condition });
        AcApplication.DocumentManager.MdiActiveDocument.Editor.WriteMessage("\n" + (condition ? "PASS " : "FAIL ") + name + "\n");
        Save();
        if (!condition) throw new System.Exception("FAIL " + name);
    }
    private static void Save()
    {
        File.WriteAllText(Environment.GetEnvironmentVariable("CADAI_TEST_REPORT"), new JavaScriptSerializer().Serialize(Results));
    }
    [CommandMethod("AIVERIFY")]
    public void Verify()
    {
        try { VerifyCore(); }
        catch (System.Exception ex) { Results.Add(new { error = ex.Message }); Save(); }
    }
    private void VerifyCore()
    {
        var startup = Assembly.LoadFrom(Environment.GetEnvironmentVariable("CADAI_PLUGIN_DLL"))
            .GetType("AutoCAD.AI.Assistant.Services.CadStartupUi", true);
        Check(!(bool)startup.GetProperty("IsDesktopHost", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null, null)
            && !(bool)startup.GetField("_pending", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null),
            "headless host skips startup UI and does not retain an idle subscription");
        var oldLinetypeScale = AcApplication.DocumentManager.MdiActiveDocument.Database.Ltscale;
        var imageLines = "{\"sourceUnit\":\"cm\",\"coordinateUnit\":\"source\",\"views\":[\"section\"],\"commands\":[{\"type\":\"setvar\",\"name\":\"DIMTXT\",\"value\":35},{\"type\":\"line\",\"points\":[[0,0],[1000,1100]],\"layer\":\"中心线\"}]}";
        Check(Run(imageLines) != null, "image centerline executes with explicit text height");
        using (var tr = AcApplication.DocumentManager.MdiActiveDocument.Database.TransactionManager.StartTransaction())
        {
            var table = (BlockTable)tr.GetObject(AcApplication.DocumentManager.MdiActiveDocument.Database.BlockTableId, OpenMode.ForRead);
            var space = (BlockTableRecord)tr.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            Line last = null; foreach (ObjectId id in space) { var line = tr.GetObject(id, OpenMode.ForRead) as Line; if (line != null) last = line; }
            Check(last != null && Math.Abs(last.LinetypeScale * oldLinetypeScale - 100) < 1e-8
                && AcApplication.DocumentManager.MdiActiveDocument.Database.Ltscale == oldLinetypeScale,
                "new centerline scale is visible without changing existing drawing linetype scale");
        }
        var before = Count();
        AcApplication.SetSystemVariable("LWDISPLAY", 0);
        var cm = "{\"sourceUnit\":\"cm\",\"commands\":[{\"type\":\"standard\"},{\"type\":\"rect\",\"p1\":[0,0],\"p2\":[6000,1200]},{\"type\":\"dimlinear\",\"p1\":[0,0],\"p2\":[6000,0],\"position\":[0,-200],\"value\":600,\"confidence\":\"inferred\"}]}";
        Check(Run(cm) != null && Count() == before + 2, "cm geometry created atomically");
        Check(!AcApplication.DocumentManager.MdiActiveDocument.Database.Dimtih
            && !AcApplication.DocumentManager.MdiActiveDocument.Database.Dimtoh,
            "standard dimensions keep vertical text aligned with vertical dimension lines");
        Check(Convert.ToInt32(AcApplication.GetSystemVariable("LWDISPLAY")) == 0, "drawing preserves the user's lightweight lineweight display");
        var receipt = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(LastReceipt));
        Check(Convert.ToBoolean(receipt["native_checks_passed"]) && !Convert.ToBoolean(receipt["source_accuracy_verified"])
            && Convert.ToInt32(receipt["dimensions"]) == 1, "receipt records native checks without claiming source accuracy");
        var db = AcApplication.DocumentManager.MdiActiveDocument.Database;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var space = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            bool found = false;
            foreach (ObjectId id in space)
            {
                var dim = tr.GetObject(id, OpenMode.ForRead) as Dimension;
                if (dim == null) continue;
                found = true;
                var linear = dim as RotatedDimension;
                Results.Add(new { native_measurement = dim.Measurement, unit_factor = dim.Dimlfac });
                Check(linear != null && Math.Abs(linear.XLine2Point.DistanceTo(linear.XLine1Point) - 6000) < 1e-6
                    && Math.Abs(dim.Dimlfac - .1) < 1e-9, "cm dimension displays source units");
                Check(dim.DimensionText.Contains("估") && dim.ColorIndex == 30, "estimated dimension marked visibly");
            }
            Check(found, "native dimension exists");
        }
        var invalid = "{\"commands\":[{\"type\":\"line\",\"points\":[[0,0],[100,100]]},{\"type\":\"hatch\",\"points\":[[0,0],[100,0],[0,100]],\"pattern\":\"NO_SUCH_PATTERN_XYZ\"}]}";
        var countBeforeFailure = Count();
        Check(Run(invalid) == null && Count() == countBeforeFailure, "native failure rolls back earlier entities");
        var hole = "{\"sourceUnit\":\"cm\",\"coordinateUnit\":\"source\",\"commands\":[{\"type\":\"hatch\",\"pattern\":\"ANSI31\",\"points\":[[0,0],[1000,0],[1000,1100],[0,1100]],\"holes\":[[[150,150],[850,150],[850,950],[150,950]]]}]}";
        Check(Run(hole) != null && Count() == countBeforeFailure + 1, "source unit hollow section created");
        receipt = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(LastReceipt));
        Check(Convert.ToInt32(receipt["hatches"]) == 1, "receipt includes native hatch area and holes");
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var space = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in space)
            {
                var hatch = tr.GetObject(id, OpenMode.ForRead) as Hatch;
                if (hatch != null) Check(hatch.NumberOfLoops == 2 && Math.Abs(hatch.Area - 54000000) < 1, "hatch preserves hole after cm conversion");
            }
        }
        var materialBefore=Count();
        var materials="{\"commands\":[{\"type\":\"hatch\",\"material\":\"gravel\",\"spacing\":50,\"points\":[[20000,0],[21000,0],[21000,200],[20000,200]]},{\"type\":\"hatch\",\"material\":\"gravel\",\"spacing\":50,\"points\":[[20000,500],[25000,500],[25000,700],[20000,700]]}]}";
        Check(Run(materials)!=null && Count()==materialBefore+2,"material hatch native execution");
        using(var tr=db.TransactionManager.StartTransaction())
        {
            var bt=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForRead);
            var space=(BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace],OpenMode.ForRead);
            var scales=new List<double>();
            foreach(ObjectId id in space)
            {
                var hatch=tr.GetObject(id,OpenMode.ForRead) as Hatch;
                if(hatch!=null && hatch.PatternName=="GRAVEL")scales.Add(hatch.PatternScale);
            }
            Check(scales.Count==2 && Math.Abs(scales[0]-scales[1])<1e-8,"texture size independent of long boundary");
        }
        var denseBefore=Count();
        Check(Run("{\"commands\":[{\"type\":\"circle\",\"center\":[0,0],\"radius\":5},{\"type\":\"hatch\",\"material\":\"sand\",\"spacing\":0.1,\"points\":[[0,0],[1000,0],[1000,1000],[0,1000]]}]}")==null
            && Count()==denseBefore,"excessive density rolls back native batch");
        Run("{\"commands\":[{\"type\":\"setvar\",\"name\":\"DIMSCALE\",\"value\":1},{\"type\":\"hatch\",\"material\":\"gravel\",\"points\":[[30000,0],[30010,0],[30010,10],[30000,10]]},{\"type\":\"setvar\",\"name\":\"DIMSCALE\",\"value\":0.5},{\"type\":\"hatch\",\"material\":\"gravel\",\"points\":[[30020,0],[30030,0],[30030,10],[30020,10]]}]}");
        using(var tr=db.TransactionManager.StartTransaction())
        {
            var bt=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForRead);
            var space=(BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace],OpenMode.ForRead);
            var scales=new List<double>();
            foreach(ObjectId id in space){var h=tr.GetObject(id,OpenMode.ForRead) as Hatch;if(h!=null&&h.PatternName=="GRAVEL")scales.Add(h.PatternScale);}
            Check(scales.Count==4&&Math.Abs(scales[3]/scales[2]-.5)<1e-8,"material density follows enlarged paper scale");
        }
        countBeforeFailure = Count();
        var roundDims = "{\"sourceUnit\":\"cm\",\"coordinateUnit\":\"source\",\"commands\":[{\"type\":\"circle\",\"center\":[100,100],\"radius\":10},{\"type\":\"dimradius\",\"center\":[100,100],\"point\":[110,100],\"value\":10},{\"type\":\"dimdiameter\",\"center\":[100,100],\"point\":[110,100],\"value\":20}]}";
        Check(Run(roundDims) != null && Count() == countBeforeFailure + 3, "radius and diameter source unit native readback");
        var nested = "{\"commands\":[{\"type\":\"block\",\"name\":\"receipt_test\",\"commands\":[{\"type\":\"dimaligned\",\"p1\":[0,0],\"p2\":[30,40],\"position\":[-5,10],\"value\":50}]},{\"type\":\"insert\",\"name\":\"receipt_test\",\"position\":[0,0]}]}";
        Check(Run(nested) != null && File.ReadAllText(LastReceipt).Contains("\"command\":\"1.1\""), "nested block receipt preserves command identity");
        VerifyNewFeatures();
        VerifyRevisionGuard();
        VerifyPreviewSettings();
        VerifyRecorderLifecycle();
        countBeforeFailure = Count();
        var commands = new List<string>();
        for (var i = 0; i < 400; i++) commands.Add("{\"type\":\"circle\",\"center\":[" + (i * 20) + ",2000],\"radius\":5}");
        var timer = Stopwatch.StartNew();
        Check(Run("{\"commands\":[" + string.Join(",", commands.ToArray()) + "]}") != null && Count() == countBeforeFailure + 400, "400 command native batch");
        Results.Add(new { test = "400 command execution", milliseconds = timer.ElapsedMilliseconds });
        Save();
    }

    private static void VerifyNewFeatures()
    {
        var db = AcApplication.DocumentManager.MdiActiveDocument.Database;
        var commands = new object[] {
            new { type="donut", center=new[]{5000,0}, innerDiameter=6, outerDiameter=10, layer="FEATURE_TEST" },
            new { type="donut", center=new[]{5050,0}, innerDiameter=0, outerDiameter=10, layer="FEATURE_TEST" },
            new { type="solid", points=new[]{new[]{5100,0},new[]{5104,0},new[]{5100,3}}, layer="FEATURE_TEST" },
            new { type="solid", points=new[]{new[]{5150,0},new[]{5160,0},new[]{5160,5},new[]{5150,5}}, layer="FEATURE_TEST" },
            new { type="polyline", points=new[]{new[]{5200,0},new[]{5210,0}}, bulges=new[]{1,0}, layer="FEATURE_TEST" },
            new { type="dimangular", center=new[]{5300,0}, p1=new[]{5310,0}, p2=new[]{5300,10}, position=new[]{5305,5}, value=90, layer="FEATURE_TEST" },
            new { type="leader", points=new[]{new[]{5400,0},new[]{5410,10}}, position=new[]{5420,10}, height=.35, arrowSize=.2, content="引线测试", layer="FEATURE_TEST" },
            new { type="mtext", position=new[]{5450,0}, height=.35, content="说明\n复核", layer="FEATURE_TEST" }
        };
        var before = Count();
        Check(Run(new JavaScriptSerializer().Serialize(new { sourceUnit="cm", coordinateUnit="source", commands=commands })) != null && Count() == before + 8,
            "five new drawing capabilities execute atomically with source centimeter coordinates");
        var hollow=false; var disk=false; var curve=false; var triangle=false; var quad=false; var angle=false; var leaderOk=false; var mtextOk=false;
        using(var tr=db.TransactionManager.StartTransaction())
        {
            var bt=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForRead);
            var space=(BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace],OpenMode.ForRead);
            foreach(ObjectId id in space)
            {
                var e=(Entity)tr.GetObject(id,OpenMode.ForRead); if(e.Layer!="FEATURE_TEST")continue;
                var p=e as Polyline;
                if(p!=null && p.Closed)
                {
                    var middleRadius=p.GetPoint2dAt(0).GetDistanceTo(p.GetPoint2dAt(1))/2;
                    var width=p.GetStartWidthAt(0);
                    var outer=middleRadius+width/2; var inner=middleRadius-width/2;
                    if(Math.Abs(outer-50)<1e-8 && Math.Abs(inner-30)<1e-8)hollow=p.GetBulgeAt(0)==1 && p.GetBulgeAt(1)==1;
                    if(Math.Abs(outer-50)<1e-8 && Math.Abs(inner)<1e-8)disk=true;
                }
                if(p!=null && !p.Closed)curve=p.NumberOfVertices==2 && Math.Abs(p.Length-50*Math.PI)<1e-8 && Math.Abs(p.GetArcSegmentAt(0).Radius-50)<1e-8;
                var solid=e as Solid;
                if(solid!=null)
                {
                    var box=solid.GeometricExtents;
                    if(Math.Abs(box.MaxPoint.X-box.MinPoint.X-40)<1e-8 && Math.Abs(box.MaxPoint.Y-box.MinPoint.Y-30)<1e-8)triangle=true;
                    if(Math.Abs(box.MaxPoint.X-box.MinPoint.X-100)<1e-8 && Math.Abs(box.MaxPoint.Y-box.MinPoint.Y-50)<1e-8)quad=true;
                }
                var dim=e as Point3AngularDimension;
                if(dim!=null)angle=Math.Abs(dim.Measurement-Math.PI/2)<1e-8 && Math.Abs(dim.Dimlfac-1)<1e-8 && String.IsNullOrEmpty(dim.DimensionText);
                var ml=e as MLeader;
                if(ml!=null)
                {
                    var leaders=ml.GetLeaderIndexes(); var lines=ml.GetLeaderLineIndexes(Convert.ToInt32(leaders[0]));
                    using(var text=ml.MText)leaderOk=ml.GetFirstVertex(Convert.ToInt32(lines[0])).DistanceTo(new Autodesk.AutoCAD.Geometry.Point3d(54000,0,0))<1e-8
                        && text.Text.Contains("引线测试") && Math.Abs(ml.TextHeight-3.5)<1e-8 && Math.Abs(ml.ArrowSize-2)<1e-8;
                    leaderOk &= ml.VerticesCount(Convert.ToInt32(lines[0]))>=3 && ml.GetVertex(Convert.ToInt32(lines[0]),1)
                        .DistanceTo(new Autodesk.AutoCAD.Geometry.Point3d(54100,100,0))<1e-8;
                }
                var mtext=e as MText;
                if(mtext!=null)mtextOk=Math.Abs(mtext.TextHeight-3.5)<1e-8 && mtext.Contents.Contains("\\P");
            }
        }
        Check(hollow,"native donut retains correct inner and outer radii");
        Check(disk,"zero inner diameter renders a filled disk");
        Check(curve,"bulge polyline is a true radius 50 arc with exact semicircle length");
        Check(triangle && quad,"native solids preserve triangle and quadrilateral bounds");
        Check(angle,"native angular dimension reads 90 degrees without centimeter length scaling or text override");
        Check(leaderOk,"native MLeader preserves arrow tip bend text height arrow size and Chinese content");
        Check(mtextOk,"native MText retains requested character height and paragraph breaks");
        var receipt=File.ReadAllText(LastReceipt);
        Check(receipt.Contains("\"measurement_unit\":\"deg\"") && receipt.Contains("\"expected\":90"),"angular receipt explicitly records degrees and checks its expected value");
    }

    private static void VerifyPreviewSettings()
    {
        var assembly = Assembly.LoadFrom(Environment.GetEnvironmentVariable("CADAI_PLUGIN_DLL"));
        var type = assembly.GetType("AutoCAD.AI.Assistant.Services.CadPreviewSettings", true);
        var names = new[] { "SELECTIONPREVIEW", "COMMANDPREVIEW", "HPQUICKPREVIEW", "ROLLOVERTIPS" };
        var saved = new int[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            AcApplication.SetSystemVariable(names[i], i == 0 ? 3 : 1);
            saved[i] = Convert.ToInt32(AcApplication.GetSystemVariable(names[i]));
        }
        var autosave = Convert.ToInt32(AcApplication.GetSystemVariable("SAVETIME")); var before = Count();
        type.GetMethod("Apply").Invoke(null, null);
        var reduced = true; foreach (var name in names) reduced &= Convert.ToInt32(AcApplication.GetSystemVariable(name)) == 0;
        Check(reduced && Count() == before && Convert.ToInt32(AcApplication.GetSystemVariable("SAVETIME")) == autosave,
            "preview performance mode changes only whitelisted display options");
        type.GetMethod("Apply").Invoke(null, null); type.GetMethod("Restore").Invoke(null, null);
        var restored = true;
        for (var i = 0; i < names.Length; i++) restored &= Convert.ToInt32(AcApplication.GetSystemVariable(names[i])) == saved[i];
        Check(restored, "repeated performance mode preserves the original preview backup");
        var file = Path.Combine(Environment.GetEnvironmentVariable("CADAI_DATA_DIR"), "preview-settings.json");
        File.WriteAllText(file, "{\"schema\":1,\"active\":true,\"values\":{\"SECURELOAD\":0}}");
        var rejected = false;
        try { type.GetMethod("Restore").Invoke(null, null); } catch (TargetInvocationException) { rejected = true; }
        Check(rejected, "malformed preview backup cannot modify unrelated system variables");
    }

    private static void VerifyRecorderLifecycle()
    {
        var assembly = Assembly.LoadFrom(Environment.GetEnvironmentVariable("CADAI_PLUGIN_DLL"));
        var catalog = assembly.GetType("AutoCAD.AI.Assistant.Services.ShortcutCatalog", true);
        var recorder = assembly.GetType("AutoCAD.AI.Assistant.Services.CadCommandRecorder", true);
        catalog.GetMethod("SetUsageTracking").Invoke(null, new object[] { false });
        recorder.GetMethod("Attach").Invoke(null, null);
        Check(!Convert.ToBoolean(recorder.GetProperty("IsAttached").GetValue(null, null)), "disabled usage tracking installs no document listener");
        catalog.GetMethod("SetUsageTracking").Invoke(null, new object[] { true });
        recorder.GetMethod("Attach").Invoke(null, null); recorder.GetMethod("Attach").Invoke(null, null);
        Check(Convert.ToBoolean(recorder.GetProperty("IsAttached").GetValue(null, null)), "usage recorder attaches idempotently when explicitly enabled");
        recorder.GetMethod("Detach").Invoke(null, null); recorder.GetMethod("Detach").Invoke(null, null);
        Check(!Convert.ToBoolean(recorder.GetProperty("IsAttached").GetValue(null, null)), "usage recorder detaches idempotently");
        catalog.GetMethod("SetUsageTracking").Invoke(null, new object[] { false });
    }

    private static void VerifyRevisionGuard()
    {
        var assembly = Assembly.LoadFrom(Environment.GetEnvironmentVariable("CADAI_PLUGIN_DLL"));
        var type = assembly.GetType("AutoCAD.AI.Assistant.Services.CadRevisionGuard", true);
        var document = AcApplication.DocumentManager.MdiActiveDocument;
        using (var guard = (IDisposable)Activator.CreateInstance(type, new object[] { document }))
        {
            type.GetMethod("Verify").Invoke(guard, null);
            Check(true, "unchanged drawing passes revision guard");
            Run("{\"commands\":[{\"type\":\"circle\",\"center\":[0,0],\"radius\":5}]}");
            var rejected = false;
            try { type.GetMethod("Verify").Invoke(guard, null); }
            catch (TargetInvocationException ex) { rejected = ex.InnerException is InvalidOperationException; }
            Check(rejected, "drawing edit invalidates waiting AI result");
        }
        var oldUnits = document.Database.Insunits;
        using (var guard = (IDisposable)Activator.CreateInstance(type, new object[] { document }))
        {
            document.Database.Insunits = UnitsValue.Meters;
            var rejected = false;
            try { type.GetMethod("Verify").Invoke(guard, null); } catch (TargetInvocationException) { rejected = true; }
            Check(rejected, "drawing unit change invalidates waiting AI result");
        }
        document.Database.Insunits = oldUnits;
    }

    [CommandMethod("AIGUARDCAPTURE")]
    public void CaptureGuard()
    {
        var type = Assembly.LoadFrom(Environment.GetEnvironmentVariable("CADAI_PLUGIN_DLL")).GetType("AutoCAD.AI.Assistant.Services.CadRevisionGuard", true);
        WaitingGuard = (IDisposable)Activator.CreateInstance(type, new object[] { AcApplication.DocumentManager.MdiActiveDocument });
    }
    [CommandMethod("AIGUARDMOVE")]
    public void MoveExisting()
    {
        var db = AcApplication.DocumentManager.MdiActiveDocument.Database;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var space = (BlockTableRecord)tr.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in space)
            {
                var circle = tr.GetObject(id, OpenMode.ForWrite) as Circle;
                if (circle != null) { circle.Center = circle.Center + new Autodesk.AutoCAD.Geometry.Vector3d(1, 0, 0); break; }
            }
            tr.Commit();
        }
    }
    [CommandMethod("AIGUARDVERIFY")]
    public void VerifyWaiting()
    {
        var rejected = false;
        try { WaitingGuard.GetType().GetMethod("Verify").Invoke(WaitingGuard, null); }
        catch (TargetInvocationException ex) { rejected = ex.InnerException is InvalidOperationException; }
        finally { WaitingGuard.Dispose(); WaitingGuard = null; }
        Check(rejected, "existing object edit across commands invalidates waiting AI result");
    }

    [CommandMethod("AIGUARDPAN")]
    public void PanOnly()
    {
        var editor = AcApplication.DocumentManager.MdiActiveDocument.Editor;
        using (var view = editor.GetCurrentView())
        {
            view.CenterPoint = new Autodesk.AutoCAD.Geometry.Point2d(view.CenterPoint.X + 20, view.CenterPoint.Y + 30);
            view.Width *= 1.1; view.Height *= 1.1; editor.SetCurrentView(view);
        }
    }
    [CommandMethod("AIGUARDSTABLE")]
    public void VerifyViewOnly()
    {
        var accepted = false;
        try { WaitingGuard.GetType().GetMethod("Verify").Invoke(WaitingGuard, null); accepted = true; }
        finally { WaitingGuard.Dispose(); WaitingGuard = null; }
        Check(accepted, "view-only pan and zoom does not invalidate waiting AI result");
    }

    [CommandMethod("AIEMPTY")]
    public void AssertEmpty() { Check(Count() == 0, "single U removes entire AIPLAN batch"); }

    [CommandMethod("AICOUNT")]
    public void ReportCount() { Results.Add(new { test = "entity count", count = Count() }); Save(); }

    [CommandMethod("AIREPRO")]
    public void Reproduce()
    {
        try
        {
            var plan = File.ReadAllText(Environment.GetEnvironmentVariable("CADAI_REPRO_PLAN"));
            Check(Run(plan) != null, "source plan executed");
            var db = AcApplication.DocumentManager.MdiActiveDocument.Database;
            var dimensions = new List<object>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var space = (BlockTableRecord)tr.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    var dim = tr.GetObject(id, OpenMode.ForRead) as Dimension;
                    if (dim != null) dimensions.Add(new { type = dim.GetType().Name, measurement = dim.Measurement,
                        unit_factor = dim.Dimlfac, text = dim.DimensionText, color = dim.ColorIndex });
                }
            }
            var target = Environment.GetEnvironmentVariable("CADAI_REPRO_OUTPUT");
            db.SaveAs(target + ".dwg", DwgVersion.Current);
            db.DxfOut(target + ".dxf", 16, DwgVersion.AC1027);
            Results.Add(new { test = "reproduction", entities = Count(), dimensions = dimensions });
            Save();
        }
        catch (System.Exception ex) { Results.Add(new { error = ex.Message }); Save(); }
    }
}

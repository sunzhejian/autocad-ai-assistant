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
    private static string Run(string plan)
    {
        var assembly = Assembly.LoadFrom(Environment.GetEnvironmentVariable("CADAI_PLUGIN_DLL"));
        var type = assembly.GetType("AutoCAD.AI.Assistant.Services.CadDrawingExecutor", true);
        var instance = Activator.CreateInstance(type, true);
        var args = new object[] { plan, null };
        var result = type.GetMethod("Execute").Invoke(instance, args);
        Results.Add(new { result = result != null, message = Convert.ToString(args[1]) });
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
        var before = Count();
        var cm = "{\"sourceUnit\":\"cm\",\"commands\":[{\"type\":\"standard\"},{\"type\":\"rect\",\"p1\":[0,0],\"p2\":[6000,1200]},{\"type\":\"dimlinear\",\"p1\":[0,0],\"p2\":[6000,0],\"position\":[0,-200],\"value\":600,\"confidence\":\"inferred\"}]}";
        Check(Run(cm) != null && Count() == before + 2, "cm geometry created atomically");
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
        countBeforeFailure = Count();
        var commands = new List<string>();
        for (var i = 0; i < 400; i++) commands.Add("{\"type\":\"circle\",\"center\":[" + (i * 20) + ",2000],\"radius\":5}");
        var timer = Stopwatch.StartNew();
        Check(Run("{\"commands\":[" + string.Join(",", commands.ToArray()) + "]}") != null && Count() == countBeforeFailure + 400, "400 command native batch");
        Results.Add(new { test = "400 command execution", milliseconds = timer.ElapsedMilliseconds });
        Save();
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

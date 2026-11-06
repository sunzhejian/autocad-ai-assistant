using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(LargeDrawingTests))]
public sealed class LargeDrawingTests
{
    private static readonly List<object> Results = new List<object>();
    private static void Save() { File.WriteAllText(Environment.GetEnvironmentVariable("CADAI_LARGE_REPORT"), new JavaScriptSerializer().Serialize(Results)); }
    private static void Check(bool passed, string test) { Results.Add(new { test = test, passed = passed }); Save(); if (!passed) throw new InvalidOperationException(test); }
    private static void View(double center, double twist)
    {
        var editor = AcApplication.DocumentManager.MdiActiveDocument.Editor;
        using (var view = editor.GetCurrentView())
        {
            view.Target = Point3d.Origin; view.ViewDirection = Vector3d.ZAxis; view.ViewTwist = twist;
            view.Width = 200; view.Height = 100; view.CenterPoint = new Point2d(center, center); editor.SetCurrentView(view);
        }
    }
    private static string Execute(Type type, string plan)
    {
        var args = new object[] { plan, null }; var instance = Activator.CreateInstance(type, true);
        var result = type.GetMethod("Execute").Invoke(instance, args);
        if (result == null) throw new InvalidOperationException(Convert.ToString(args[1])); return Convert.ToString(args[1]);
    }
    [CommandMethod("AILARGEVERIFY")]
    public void Run()
    {
        try
        {
            var document = AcApplication.DocumentManager.MdiActiveDocument; var db = document.Database;
            var assembly = Assembly.LoadFrom(Environment.GetEnvironmentVariable("CADAI_PLUGIN_DLL"));
            var executor = assembly.GetType("AutoCAD.AI.Assistant.Services.CadDrawingExecutor", true);
            var contextType = assembly.GetType("AutoCAD.AI.Assistant.Services.CadDrawingContext");
            var baseline = contextType == null;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var model = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                for (var i = 0; i < 100000; i++)
                {
                    var x = 1000000.0 + i * 2; var y = 1000000.0 + (i % 20) * 5;
                    var line = new Line(new Point3d(x, y, 0), new Point3d(x + 1, y + 1, 0));
                    model.AppendEntity(line); tr.AddNewlyCreatedDBObject(line, true);
                }
                tr.Commit();
            }
            document.Editor.Regen();
            var times = new List<long>(); string context = null;
            for (var i = 0; i < 3; i++)
            {
                var timer = Stopwatch.StartNew();
                context = baseline ? Convert.ToString(executor.GetMethod("BuildContext").Invoke(null, null))
                    : Convert.ToString(contextType.GetMethod("Read").Invoke(null, new object[] { document, true }));
                times.Add(timer.ElapsedMilliseconds);
            }
            Results.Add(new { version = assembly.GetName().Version.ToString(), existing_entities = 100000, context_ms = times, context = context }); Save();
            if (!baseline)
            {
                Check(context.Contains("≥") && context.Contains("类型抽样="), "large drawing context explicitly labels its bounded sample");
                Check(Convert.ToString(executor.GetMethod("BuildContext").Invoke(null, null)).Contains("未扫描"), "busy CAD command skips modeless context scan");
            }
            View(0, 0);
            double expectedWidth, expectedHeight;
            using (var initial = document.Editor.GetCurrentView()) { expectedWidth = initial.Width; expectedHeight = initial.Height; }
            var drawing = Stopwatch.StartNew(); Execute(executor, "{\"commands\":[{\"type\":\"circle\",\"center\":[0,0],\"radius\":5}]}");
            using (var view = document.Editor.GetCurrentView())
            {
                Results.Add(new { stage = "visible_new_geometry", milliseconds = drawing.ElapsedMilliseconds, view_width = view.Width, view_height = view.Height }); Save();
                if (!baseline) Check(Math.Abs(view.Height - expectedHeight) < .01 && Math.Abs(view.Width - expectedWidth) < .01, "visible new geometry preserves user's view in a 100k drawing");
            }
            View(2000000, Math.PI / 6);
            drawing.Restart(); Execute(executor, "{\"commands\":[{\"type\":\"circle\",\"center\":[1000,1000],\"radius\":10}]}");
            using (var view = document.Editor.GetCurrentView())
            {
                Results.Add(new { stage = "offscreen_new_geometry", milliseconds = drawing.ElapsedMilliseconds, view_width = view.Width, view_height = view.Height, twist = view.ViewTwist }); Save();
                if (!baseline) Check(view.Height < 100 && Math.Abs(view.ViewTwist - Math.PI / 6) < .001, "offscreen geometry fits its own bounds and preserves rotated view");
            }
            using (var view = document.Editor.GetCurrentView())
            {
                var expectedX = 1000 * Math.Cos(Math.PI / 6) - 1000 * Math.Sin(Math.PI / 6);
                var expectedY = 1000 * Math.Sin(Math.PI / 6) + 1000 * Math.Cos(Math.PI / 6);
                if (!baseline) Check(Math.Abs(expectedX - view.CenterPoint.X) < .01 && Math.Abs(expectedY - view.CenterPoint.Y) < .01,
                    "new geometry is centered correctly in display coordinates");
            }
            Save();
        }
        catch (System.Exception ex) { Results.Add(new { error = ex.ToString() }); Save(); }
    }
}

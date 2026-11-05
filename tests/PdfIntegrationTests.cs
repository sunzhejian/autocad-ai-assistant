using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(PdfIntegrationTests))]
public sealed class PdfIntegrationTests
{
    private static readonly List<object> Results = new List<object>();
    private static void Check(bool value, string test)
    {
        Results.Add(new { test = test, passed = value }); Save();
        if (!value) throw new InvalidOperationException(test);
    }
    private static void Save() { File.WriteAllText(Environment.GetEnvironmentVariable("CADAI_PDF_REPORT"), new JavaScriptSerializer().Serialize(Results)); }
    private static List<ObjectId> ModelIds()
    {
        var db = AcApplication.DocumentManager.MdiActiveDocument.Database;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var space = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            var result = new List<ObjectId>(); foreach (ObjectId id in space) result.Add(id); return result;
        }
    }
    private static string Import(string metadata, double scale)
    {
        var assembly = Assembly.LoadFrom(Environment.GetEnvironmentVariable("CADAI_PLUGIN_DLL"));
        var sourceType = assembly.GetType("AutoCAD.AI.Assistant.Services.PdfPageSource");
        var source = new JavaScriptSerializer().Deserialize(File.ReadAllText(metadata), sourceType);
        var importer = assembly.GetType("AutoCAD.AI.Assistant.Services.PdfDrawingImporter");
        return Convert.ToString(importer.GetMethod("Execute").Invoke(null, new object[] { AcApplication.DocumentManager.MdiActiveDocument, source, scale }));
    }
    [CommandMethod("AIPDFTEST")]
    public void Small()
    {
        try
        {
            var db = AcApplication.DocumentManager.MdiActiveDocument.Database;
            db.Insunits = UnitsValue.Centimeters;
            AcApplication.SetSystemVariable("FILEDIA", 1);
            AcApplication.SetSystemVariable("PDFIMPORTMODE", 2);
            AcApplication.SetSystemVariable("MEASUREMENT", 0);
            var before = ModelIds().Count;
            var message = Import(Environment.GetEnvironmentVariable("CADAI_SMALL_PDF"), 2);
            Results.Add(new { message = message });
            var ids = ModelIds();
            Check(ids.Count == before + 1, "PDF import creates one model block");
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var block = (BlockReference)tr.GetObject(ids[ids.Count - 1], OpenMode.ForRead);
                var ext = block.GeometricExtents; var width = ext.MaxPoint.X - ext.MinPoint.X;
                Results.Add(new { width_cm = width }); Save();
                Check(Math.Abs(width - 5.08) < .01, "PDF paper scale converts mm to drawing cm");
                var record = (BlockTableRecord)tr.GetObject(block.BlockTableRecord, OpenMode.ForRead);
                var fills = 0; foreach (ObjectId id in record) if (tr.GetObject(id, OpenMode.ForRead) is Hatch) fills++;
                Check(fills > 0, "PDF solid fills retained as native hatches");
            }
            Check(Convert.ToInt32(AcApplication.GetSystemVariable("FILEDIA")) == 1
                && Convert.ToInt32(AcApplication.GetSystemVariable("PDFIMPORTMODE")) == 2
                && Convert.ToInt32(AcApplication.GetSystemVariable("MEASUREMENT")) == 0, "PDF import restores UI and profile variables");
        }
        catch (System.Exception ex) { Results.Add(new { error = ex.ToString() }); Save(); }
    }
    [CommandMethod("AIPDFUNDO")]
    public void Undo() { Check(ModelIds().Count == 0, "one U removes entire PDF import"); }
    [CommandMethod("AIPDFMIXED")]
    public void Mixed()
    {
        try
        {
            var old = Convert.ToString(AcApplication.GetSystemVariable("PDFIMPORTIMAGEPATH"));
            Import(Environment.GetEnvironmentVariable("CADAI_MIXED_PDF"), 1);
            var db = AcApplication.DocumentManager.MdiActiveDocument.Database;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var block = (BlockReference)tr.GetObject(ModelIds()[0], OpenMode.ForRead);
                var record = (BlockTableRecord)tr.GetObject(block.BlockTableRecord, OpenMode.ForRead);
                var images = 0;
                foreach (ObjectId id in record)
                {
                    var image = tr.GetObject(id, OpenMode.ForRead) as RasterImage;
                    if (image == null) continue;
                    var definition = (RasterImageDef)tr.GetObject(image.ImageDefId, OpenMode.ForRead);
                    Check(File.Exists(definition.SourceFileName) && definition.SourceFileName.Contains("sources"), "mixed PDF keeps raster dependency in source cache");
                    images++;
                }
                Check(images > 0 && Convert.ToString(AcApplication.GetSystemVariable("PDFIMPORTIMAGEPATH")) == old, "mixed PDF raster retained and image path restored");
            }
        }
        catch (System.Exception ex) { Results.Add(new { error = ex.ToString() }); Save(); }
    }
    [CommandMethod("AIPDFREAL")]
    public void Real()
    {
        try
        {
            var db = AcApplication.DocumentManager.MdiActiveDocument.Database; db.Insunits = UnitsValue.Millimeters;
            var message = Import(Environment.GetEnvironmentVariable("CADAI_REAL_PDF"), 1);
            Results.Add(new { message = message });
            Check(ModelIds().Count == 1, "contest PDF imports into a single block");
            var output = Environment.GetEnvironmentVariable("CADAI_PDF_OUTPUT");
            db.SaveAs(output + ".dwg", DwgVersion.Current); db.DxfOut(output + ".dxf", 16, DwgVersion.AC1027);
            Save();
        }
        catch (System.Exception ex) { Results.Add(new { error = ex.ToString() }); Save(); }
    }
}

using System;
using System.IO;
using System.Globalization;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(NativeDrawingExport))]
public sealed class NativeDrawingExport
{
    private static void Set(string key, double value) { Environment.SetEnvironmentVariable(key, value.ToString("R", CultureInfo.InvariantCulture)); }
    [CommandMethod("AIEXPORTCOPY")]
    public void Export()
    {
        var db = AcApplication.DocumentManager.MdiActiveDocument.Database;
        var target = Environment.GetEnvironmentVariable("CADAI_REPRO_OUTPUT");
        try
        {
            db.UpdateExt(true);
            var min = db.Extmin; var max = db.Extmax;
            var width = max.X - min.X; var height = max.Y - min.Y;
            if (width <= 0 || height <= 0) throw new InvalidOperationException("Drawing has no finite 2D bounds.");
            var ratio = Math.Max(width / 380, height / 257);
            Set("CADAI_PLOT_RATIO", ratio);
            Set("CADAI_PLOT_PAPER_WIDTH", 420); Set("CADAI_PLOT_PAPER_HEIGHT", 297);
            Set("CADAI_PLOT_ORIGIN_X", min.X - (420 * ratio - width) / 2);
            Set("CADAI_PLOT_ORIGIN_Y", min.Y - (297 * ratio - height) / 2);
            db.DxfOut(target + ".dxf", 16, DwgVersion.AC1027);
            new NativePdfPlot().Plot();
            if (!File.Exists(target + ".pdf")) throw new InvalidOperationException("Native PDF was not produced.");
            File.WriteAllText(target + ".export-ok.txt", "Exported the existing drawing copy without redrawing or editing its entities.");
        }
        catch (System.Exception ex) { File.WriteAllText(target + ".export-error.txt", ex.ToString()); }
    }
}

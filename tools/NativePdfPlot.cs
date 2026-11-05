using System;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication=Autodesk.AutoCAD.ApplicationServices.Application;

[assembly:CommandClass(typeof(NativePdfPlot))]
public sealed class NativePdfPlot
{
    private static double Setting(string name){return double.Parse(Environment.GetEnvironmentVariable(name),System.Globalization.CultureInfo.InvariantCulture);}
    [CommandMethod("AIPDF")]
    public void Plot()
    {
        var target=Environment.GetEnvironmentVariable("CADAI_REPRO_OUTPUT");
        var doc=AcApplication.DocumentManager.MdiActiveDocument;
        var previous=AcApplication.GetSystemVariable("BACKGROUNDPLOT");
        try
        {
            AcApplication.SetSystemVariable("BACKGROUNDPLOT",0);
            using(var tr=doc.Database.TransactionManager.StartTransaction())
            {
                var layoutId=LayoutManager.Current.GetLayoutId(LayoutManager.Current.CurrentLayout);
                var layout=(Layout)tr.GetObject(layoutId,OpenMode.ForRead);
                using(var settings=new PlotSettings(layout.ModelType))
                using(var info=new PlotInfo())
                using(var validator=new PlotInfoValidator())
                {
                    settings.CopyFrom(layout);
                    var ratio=Setting("CADAI_PLOT_RATIO");
                    var width=Setting("CADAI_PLOT_PAPER_WIDTH");var height=Setting("CADAI_PLOT_PAPER_HEIGHT");
                    var originX=Setting("CADAI_PLOT_ORIGIN_X");var originY=Setting("CADAI_PLOT_ORIGIN_Y");
                    var psv=PlotSettingsValidator.Current;
                    psv.SetPlotConfigurationName(settings,"DWG To PDF.pc3",null);
                    psv.RefreshLists(settings);
                    string media=null;
                    foreach(string name in psv.GetCanonicalMediaNameList(settings))
                        if(name.Contains(width.ToString("0.00",System.Globalization.CultureInfo.InvariantCulture))&&name.Contains(height.ToString("0.00",System.Globalization.CultureInfo.InvariantCulture)))
                        { media=name; if(name.ToLowerInvariant().Contains("full_bleed"))break; }
                    if(media==null)throw new InvalidOperationException("A3 media unavailable.");
                    psv.SetPlotConfigurationName(settings,"DWG To PDF.pc3",media);
                    psv.SetPlotPaperUnits(settings,PlotPaperUnit.Millimeters);
                    psv.SetPlotRotation(settings,PlotRotation.Degrees090);
                    psv.SetPlotWindowArea(settings,new Extents2d(originX,originY,originX+width*ratio,originY+height*ratio));
                    psv.SetPlotType(settings,Autodesk.AutoCAD.DatabaseServices.PlotType.Window);
                    psv.SetUseStandardScale(settings,false);
                    psv.SetCustomPrintScale(settings,new CustomScale(1,ratio));
                    psv.SetPlotCentered(settings,true);
                    settings.PrintLineweights=true;
                    info.Layout=layoutId;info.OverrideSettings=settings;
                    validator.MediaMatchingPolicy=MatchingPolicy.MatchEnabled;
                    validator.Validate(info);
                    using(var engine=PlotFactory.CreatePublishEngine())
                    using(var page=new PlotPageInfo())
                    {
                        engine.BeginPlot(null,null);
                        engine.BeginDocument(info,doc.Name,null,1,true,target+".pdf");
                        engine.BeginPage(page,info,true,null);
                        engine.BeginGenerateGraphics(null);
                        engine.EndGenerateGraphics(null);
                        engine.EndPage(null);
                        engine.EndDocument(null);
                        engine.EndPlot(null);
                    }
                    File.WriteAllText(target+".plot-media.txt",media);
                }
            }
        }
        catch(System.Exception ex){File.WriteAllText(target+".plot-error.txt",ex.ToString());doc.Editor.WriteMessage("\nPDF export failed: "+ex.Message);}
        finally{AcApplication.SetSystemVariable("BACKGROUNDPLOT",previous);}
    }
}

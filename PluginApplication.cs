using System;
using System.Drawing;
using System.Windows.Forms.Integration;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using AutoCAD.AI.Assistant.Views;

[assembly: CommandClass(typeof(AutoCAD.AI.Assistant.PluginApplication))]

namespace AutoCAD.AI.Assistant
{
    public sealed class PluginApplication
    {
        [CommandMethod("AIDRAW", CommandFlags.Modal)]
        public void ExecuteDrawingPlan()
        {
            AutoCAD.AI.Assistant.Services.CadExecutionQueue.ExecutePending(
                Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument);
        }

        [CommandMethod("AIPLAN", CommandFlags.Modal)]
        public void ExecutePlanFile()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            var prompt = new Autodesk.AutoCAD.EditorInput.PromptStringOptions("\n计划 JSON 文件路径: ") { AllowSpaces = true };
            var result = doc.Editor.GetString(prompt);
            if (result.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK) return;
            string message;
            new AutoCAD.AI.Assistant.Services.CadDrawingExecutor().Execute(System.IO.File.ReadAllText(result.StringResult.Trim('"')), out message);
            doc.Editor.WriteMessage("\n" + message + "\n");
        }

        private static PaletteSet _palette;
        private static AiPanel _panel;

        [CommandMethod("AI", CommandFlags.Session)]
        public void ShowAiPanel()
        {
            AutoCAD.AI.Assistant.Services.CadCommandRecorder.Attach();

            if (_palette == null)
            {
                _panel = new AiPanel();
                var host = new ElementHost
                {
                    Child = _panel,
                    Dock = System.Windows.Forms.DockStyle.Fill
                };

                _palette = new PaletteSet("AI", new Guid("AF50251F-E562-4D21-933C-963738B5C775"))
                {
                    Size = new Size(450, 720),
                    MinimumSize = new Size(360, 480)
                };
                _palette.Add("AI", host);
            }

            _palette.Visible = true;
            _palette.Activate(0);
            if (_panel != null)
            {
                _panel.Dispatcher.BeginInvoke(new Action(_panel.FocusInput), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }
    }
}

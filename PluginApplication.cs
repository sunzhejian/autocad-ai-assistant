using System;
using System.Drawing;
using System.Windows.Forms.Integration;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using AutoCAD.AI.Assistant.Views;
using AutoCAD.AI.Assistant.Services;

[assembly: CommandClass(typeof(AutoCAD.AI.Assistant.PluginApplication))]
[assembly: ExtensionApplication(typeof(AutoCAD.AI.Assistant.PluginApplication))]

namespace AutoCAD.AI.Assistant
{
    public sealed class PluginApplication : IExtensionApplication
    {
        public void Initialize()
        {
            ShortcutCatalog.UsageTrackingChanged += UpdateRecording;
            CadStartupUi.Initialize();
        }
        public void Terminate()
        {
            ShortcutCatalog.UsageTrackingChanged -= UpdateRecording;
            CadStartupUi.Terminate();
            CadCommandRecorder.Detach();
            try { ShortcutCatalog.FlushAsync().Wait(500); DiagnosticsLog.FlushAsync().Wait(500); } catch { }
        }
        private static bool _visible;
        [CommandMethod("AIUI", CommandFlags.Session)]
        public void RestoreInterface() { CadStartupUi.Apply(); }
        [CommandMethod("AIUIOFF", CommandFlags.Session)]
        public void DisableStartupInterface() { CadStartupUi.Disable(); }
        [CommandMethod("AIUIRESTORE", CommandFlags.Session)]
        public void RestoreStartupPreferences() { CadStartupUi.Restore(); }
        private static void UpdateRecording()
        {
            // The visibility flag also avoids loading palette UI wrappers in the
            // headless host. No palette API is needed for a counter notification.
            if (_visible && ShortcutCatalog.UsageTrackingEnabled) CadCommandRecorder.Attach();
            else CadCommandRecorder.Detach();
        }
        [CommandMethod("AIPERF", CommandFlags.Modal)]
        public void ReducePreviews() { PreviewCommand(false); }
        [CommandMethod("AIPERFRESTORE", CommandFlags.Modal)]
        public void RestorePreviews() { PreviewCommand(true); }
        private static void PreviewCommand(bool restore)
        {
            var document = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            try { document.Editor.WriteMessage("\n" + (restore ? CadPreviewSettings.Restore() : CadPreviewSettings.Apply()) + "\n"); }
            catch (System.Exception ex) { document.Editor.WriteMessage("\n预览设置未完成：" + ex.Message + "\n"); }
        }
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
            if (_palette == null)
            {
                _panel = new AiPanel();
                var host = new ElementHost
                {
                    Child = _panel,
                    Dock = System.Windows.Forms.DockStyle.Fill
                };

                _palette = new PaletteSet("AI " + typeof(PluginApplication).Assembly.GetName().Version.ToString(3), new Guid("AF50251F-E562-4D21-933C-963738B5C775"))
                {
                    Size = new Size(450, 720),
                    MinimumSize = new Size(360, 480)
                };
                _palette.Add("AI", host);
                _palette.StateChanged += (sender, e) =>
                {
                    if (e.NewState == StateEventIndex.Hide) _visible = false;
                    else if (e.NewState == StateEventIndex.Show) _visible = true;
                    UpdateRecording();
                };
            }

            _palette.Visible = true;
            _visible = true; UpdateRecording();
            _palette.Activate(0);
            if (_panel != null)
            {
                _panel.Dispatcher.BeginInvoke(new Action(_panel.FocusInput), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }
    }
}

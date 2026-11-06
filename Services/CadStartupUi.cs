using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoCAD.AI.Assistant.Services
{
    // Startup only: never poll the editor during normal drawing or instantiate AI UI.
    internal static class CadStartupUi
    {
        private static int _attempts;
        private static bool _pending;
        private static string DisabledPath { get { return Path.Combine(DiagnosticsLog.Folder, "startup-ui.disabled"); } }
        internal static bool IsDesktopHost
        { get { return string.Equals(Process.GetCurrentProcess().ProcessName, "acad", StringComparison.OrdinalIgnoreCase); } }

        public static void Initialize()
        {
            // Keep AdWindows/PaletteSet wrappers out of accoreconsole entirely.
            if (!IsDesktopHost || File.Exists(DisabledPath) || _pending) return;
            _attempts = 0; _pending = true;
            AcApplication.Idle += OnIdle;
        }
        public static void Terminate()
        {
            if (!_pending) return;
            AcApplication.Idle -= OnIdle; _pending = false;
        }
        private static void OnIdle(object sender, EventArgs args)
        {
            if (++_attempts > 20) { Terminate(); return; }
            try
            {
                var doc = AcApplication.DocumentManager.MdiActiveDocument;
                if (doc != null && !doc.Editor.IsQuiescent) return;
                Terminate(); // Add(template) can pump messages; detach before it can re-enter.
                if (doc == null) doc = CreateMetricDrawing();
                if (doc == null) return;
                doc.SendStringToExecute("AIUI ", true, false, false);
            }
            catch (System.Exception ex)
            {
                Terminate(); DiagnosticsLog.Write("startup_ui: " + ex.Message);
            }
        }
        private static Document CreateMetricDrawing()
        {
            var root = Convert.ToString(AcApplication.GetSystemVariable("LOCALROOTPREFIX"));
            var template = Path.Combine(root, "Template", "acadiso.dwt");
            if (!File.Exists(template))
            {
                try { template = HostApplicationServices.Current.FindFile("acadiso.dwt", null, FindFileHint.Default); }
                catch { return null; }
            }
            return AcApplication.DocumentManager.Add(template);
        }
        public static void Apply()
        {
            if (!IsDesktopHost) return;
            try
            {
                if (File.Exists(DisabledPath)) File.Delete(DisabledPath);
                var doc = AcApplication.DocumentManager.MdiActiveDocument ?? CreateMetricDrawing();
                if (doc == null) return;
                var backup = Path.Combine(DiagnosticsLog.Folder, "startup-ui-original.json");
                if (!File.Exists(backup))
                {
                    var values = new Dictionary<string, object>();
                    foreach (var name in new[] { "STARTUP", "STARTMODE", "FILEDIA", "CMDDIA", "MENUBAR", "STATUSBAR" })
                        values[name] = AcApplication.GetSystemVariable(name);
                    Directory.CreateDirectory(DiagnosticsLog.Folder);
                    File.WriteAllText(backup, new JavaScriptSerializer().Serialize(values));
                }
                // Registry-backed application preferences; never change an existing DWG's units.
                Set("STARTUP", 0); Set("STARTMODE", 0);
                Set("FILEDIA", 1); Set("CMDDIA", 1); Set("MENUBAR", 1); Set("STATUSBAR", 1);
                // Ribbon creation must run after RIBBON has finished, in a separate command.
                doc.SendStringToExecute("_.CLEANSCREENOFF _.RIBBON _.COMMANDLINE _.FILETAB AIUITAB ", true, false, false);
            }
            catch (System.Exception ex) { DiagnosticsLog.Write("startup_ui: " + ex.Message); }
        }
        public static void Disable()
        {
            if (!IsDesktopHost) return;
            Terminate(); Directory.CreateDirectory(DiagnosticsLog.Folder);
            File.WriteAllText(DisabledPath, "Automatic startup interface disabled. Run AIUI to enable again.");
            var doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc != null) doc.Editor.WriteMessage("\n已关闭插件启动界面整理；当前界面保留。输入 AIUI 可重新启用。\n");
        }
        private static void Set(string name, int value)
        {
            AcApplication.SetSystemVariable(name, Convert.ChangeType(value, AcApplication.GetSystemVariable(name).GetType()));
        }
    }
}

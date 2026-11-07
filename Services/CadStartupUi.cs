using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
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
        private static string BackupPath { get { return Path.Combine(DiagnosticsLog.Folder, "startup-ui-original.json"); } }
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
                var doc = AcApplication.DocumentManager.MdiActiveDocument ?? CreateMetricDrawing();
                if (doc == null) return;
                var previous = Capture();
                StartupSettingsBackup.Ensure(BackupPath, previous);
                try
                {
                    // Registry-backed preferences; never change an existing DWG's units.
                    Set("STARTUP", 0); Set("STARTMODE", 0);
                    Set("FILEDIA", 1); Set("CMDDIA", 1); Set("MENUBAR", 1); Set("STATUSBAR", 1);
                    if (File.Exists(DisabledPath)) File.Delete(DisabledPath);
                }
                catch { RollBack(previous); throw; }
                // Ribbon creation must run after RIBBON has finished, in a separate command.
                doc.SendStringToExecute("_.CLEANSCREENOFF _.RIBBON _.COMMANDLINE _.FILETAB AIUITAB ", true, false, false);
            }
            catch (System.Exception ex)
            {
                DiagnosticsLog.Write("startup_ui: " + ex.Message);
                var doc = AcApplication.DocumentManager.MdiActiveDocument;
                if (doc != null) doc.Editor.WriteMessage("\n界面设置未完成：" + ex.Message + "\n");
            }
        }
        public static void Disable()
        {
            if (!IsDesktopHost) return;
            Terminate();
            AtomicFile.Write(DisabledPath, "Automatic startup interface disabled. Run AIUI to enable again.");
            var doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc != null) doc.Editor.WriteMessage("\n已关闭插件启动界面整理；当前界面保留。输入 AIUI 可重新启用。\n");
        }
        public static void Restore()
        {
            if (!IsDesktopHost) return;
            var doc = AcApplication.DocumentManager.MdiActiveDocument;
            try
            {
                // Fully validate before changing any CAD preference.
                var original = StartupSettingsBackup.Read(BackupPath);
                var previous = Capture();
                try
                {
                    foreach (var pair in original) Set(pair.Key, pair.Value);
                    Disable();
                }
                catch { RollBack(previous); throw; }
                if (doc != null) doc.Editor.WriteMessage("\n已恢复六项原始启动偏好并关闭启动整理；当前图纸和功能区保留。\n");
            }
            catch (System.Exception ex)
            { if (doc != null) doc.Editor.WriteMessage("\n启动偏好未恢复：" + ex.Message + "\n"); }
        }
        private static Dictionary<string, int> Capture()
        {
            var values = new Dictionary<string, int>();
            foreach (var name in StartupSettingsBackup.Names) values[name] = Convert.ToInt32(AcApplication.GetSystemVariable(name));
            return values;
        }
        private static void RollBack(Dictionary<string, int> values)
        { foreach (var pair in values) try { Set(pair.Key, pair.Value); } catch { } }
        private static void Set(string name, int value)
        {
            AcApplication.SetSystemVariable(name, Convert.ChangeType(value, AcApplication.GetSystemVariable(name).GetType()));
        }
    }
}

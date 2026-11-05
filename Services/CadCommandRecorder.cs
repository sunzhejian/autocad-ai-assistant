using System;
using Autodesk.AutoCAD.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoCAD.AI.Assistant.Services
{
    /// <summary>监听 AutoCAD 命令开始事件，把用户实际执行的命令计入指令集统计。</summary>
    internal static class CadCommandRecorder
    {
        private static bool _attached;
        private static Document _current;
        public static bool IsAttached { get { return _attached; } }

        public static void Attach()
        {
            if (!ShortcutCatalog.UsageTrackingEnabled) { Detach(); return; }
            if (_attached) return;
            _attached = true;
            ShortcutCatalog.GetSorted();

            var manager = AcApplication.DocumentManager;
            manager.DocumentActivated += OnDocumentActivated;
            manager.DocumentToBeDestroyed += OnDocumentClosing;
            if (manager.MdiActiveDocument != null) Hook(manager.MdiActiveDocument);
        }

        public static void Detach()
        {
            if (!_attached) return;
            _attached = false;
            try
            {
                AcApplication.DocumentManager.DocumentActivated -= OnDocumentActivated;
                AcApplication.DocumentManager.DocumentToBeDestroyed -= OnDocumentClosing;
            }
            catch { }
            Unhook();
            ShortcutCatalog.Flush();
        }
        private static void Unhook()
        {
            if (_current == null) return;
            try { _current.CommandWillStart -= OnCommandWillStart; } catch { }
            _current = null;
        }
        private static void OnDocumentClosing(object sender, DocumentCollectionEventArgs e)
        { if (e.Document == _current) Unhook(); }

        private static void OnDocumentActivated(object sender, DocumentCollectionEventArgs e)
        {
            if (e != null && e.Document != null) Hook(e.Document);
        }

        private static void Hook(Document document)
        {
            if (document == null || _current == document) return;
            Unhook();
            _current = document;
            document.CommandWillStart += OnCommandWillStart;
        }

        private static void OnCommandWillStart(object sender, CommandEventArgs e)
        {
            try
            {
                if (e != null) ShortcutCatalog.RecordCommand(e.GlobalCommandName);
            }
            catch
            {
            }
        }
    }
}

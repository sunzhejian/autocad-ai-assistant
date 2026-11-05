using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace AutoCAD.AI.Assistant.Services
{
    /// <summary>A short-lived observer; it never writes to the drawing.</summary>
    internal sealed class CadRevisionGuard : IDisposable
    {
        private readonly Document _document;
        private readonly Database _database;
        private readonly ObjectId _space;
        private readonly bool _tileMode;
        private readonly Handle _handseed;
        private readonly UnitsValue _units;
        private bool _changed;
        private bool _closed;
        public CadRevisionGuard(Document document)
        {
            _document = document; _database = document.Database;
            _space = _database.CurrentSpaceId; _tileMode = _database.TileMode;
            _handseed = _database.Handseed;
            _units = _database.Insunits;
            _database.ObjectAppended += Changed;
            _database.ObjectModified += Changed;
            _database.ObjectErased += Erased;
            Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.DocumentToBeDestroyed += Closing;
        }
        private void Changed(object sender, ObjectEventArgs e) { _changed = true; }
        private void Erased(object sender, ObjectErasedEventArgs e) { _changed = true; }
        private void Closing(object sender, DocumentCollectionEventArgs e) { if (e.Document == _document) _closed = true; }
        public void Verify()
        {
            if (_closed) throw new InvalidOperationException("原图形已关闭，结果未执行。");
            if (Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument != _document)
                throw new InvalidOperationException("活动图形已切换，请回到原图后重新发送。");
            if (_changed || _database.Handseed != _handseed || _database.Insunits != _units || _database.CurrentSpaceId != _space || _database.TileMode != _tileMode)
                throw new InvalidOperationException("等待期间图形已修改，已停止自动落图；计划保留在本地，请核对后用 AIPLAN 执行。");
        }
        public void Dispose()
        {
            try
            {
                if (!_closed) { _database.ObjectAppended -= Changed; _database.ObjectModified -= Changed; _database.ObjectErased -= Erased; }
            }
            catch (Exception ex) { DiagnosticsLog.Error("释放图形变更观察器", ex); }
            Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.DocumentToBeDestroyed -= Closing;
        }
    }
}

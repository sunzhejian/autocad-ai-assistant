using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using System.Web.Script.Serialization;
using AutoCAD.AI.Assistant.Models;

namespace AutoCAD.AI.Assistant.Services
{
    internal sealed class ModelSettingsStore
    {
        private readonly string _filePath;
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();
        public string LastWarning { get; private set; }
        public ModelSettingsStore() : this(Path.Combine(DiagnosticsLog.Folder, "models.json")) { }
        internal ModelSettingsStore(string filePath) { _filePath = Path.GetFullPath(filePath); }

        private List<ModelConfiguration> Read(string path)
        {
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException();
            var models = _serializer.Deserialize<List<ModelConfiguration>>(File.ReadAllText(path, Encoding.UTF8));
            Validate(models); return models;
        }
        private static void Validate(List<ModelConfiguration> models)
        {
            if (models == null || models.Count > 100) throw new InvalidDataException("模型配置列表无效。");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var model in models)
            {
                Uri uri;
                if (model == null || String.IsNullOrWhiteSpace(model.Id) || !ids.Add(model.Id)
                    || String.IsNullOrWhiteSpace(model.ModelId) || !ModelConfiguration.IsValidReasoningEffort(model.ReasoningEffort) || !Uri.TryCreate(model.RequestUrl, UriKind.Absolute, out uri)
                    || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                    throw new InvalidDataException("模型配置条目无效。");
            }
        }

        public List<ModelConfiguration> Load()
        {
            LastWarning = null;
            if (!File.Exists(_filePath) && !File.Exists(_filePath + ".bak")) return new List<ModelConfiguration>();
            try { return Read(_filePath); }
            catch (Exception)
            {
                try
                {
                    var models = Read(_filePath + ".bak");
                    LastWarning = "模型配置主文件无法读取，已加载上一次有效备份。";
                    DiagnosticsLog.Write(LastWarning); return models;
                }
                catch (Exception)
                { throw new InvalidDataException("模型配置和备份均无法读取；文件已保留，请检查本地 models.json 与 models.json.bak。"); }
            }
        }

        public void Save(IEnumerable<ModelConfiguration> models)
        {
            var snapshot = models.ToList(); Validate(snapshot);
            var json = _serializer.Serialize(snapshot);
            if (Encoding.UTF8.GetByteCount(json) > 1024 * 1024) throw new InvalidDataException("模型配置超过 1MB，请缩短系统提示词。");
            bool healthy = false;
            if (File.Exists(_filePath))
            {
                try { Read(_filePath); healthy = true; }
                catch (Exception)
                {
                    // Preserve both a damaged primary and the last healthy backup.
                    File.Copy(_filePath, _filePath + ".corrupt-" + Guid.NewGuid().ToString("N"));
                }
            }
            AtomicFile.Write(_filePath, json, healthy ? _filePath + ".bak" : null);
        }
    }
}

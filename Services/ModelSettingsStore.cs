using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using AutoCAD.AI.Assistant.Models;

namespace AutoCAD.AI.Assistant.Services
{
    internal sealed class ModelSettingsStore
    {
        private readonly string _filePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AutoCAD.AI.Assistant",
            "models.json");
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();

        public List<ModelConfiguration> Load()
        {
            if (!File.Exists(_filePath)) return new List<ModelConfiguration>();
            var json = File.ReadAllText(_filePath, Encoding.UTF8);
            return _serializer.Deserialize<List<ModelConfiguration>>(json) ?? new List<ModelConfiguration>();
        }

        public void Save(IEnumerable<ModelConfiguration> models)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath));
            var json = _serializer.Serialize(models);
            File.WriteAllText(_filePath, json, new UTF8Encoding(false));
        }
    }
}

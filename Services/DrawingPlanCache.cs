using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AutoCAD.AI.Assistant.Models;

namespace AutoCAD.AI.Assistant.Services
{
    /// <summary>Content-addressed cache of executed plans, not an assertion of source-image accuracy.</summary>
    internal sealed class DrawingPlanCache
    {
        private readonly string _folder;
        public DrawingPlanCache(string folder = null) { _folder = folder ?? Path.Combine(DiagnosticsLog.Folder, "executed-plans"); }

        public static string Key(string image, string prompt, ModelConfiguration model, bool details)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(image))
            {
                var imageHash = Convert.ToBase64String(sha.ComputeHash(stream));
                var identity = imageHash + "\n" + prompt + "\n" + model.RequestUrl + "\n" + model.UseFullUrl + "\n"
                    + model.ModelId + "\n" + model.SystemPrompt + "\n" + details + "\n" + OpenAiCompatibleClient.DrawingProtocol;
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-", "").ToLowerInvariant();
            }
        }

        public string TryRead(string key)
        {
            var file = Path.Combine(_folder, key + ".json");
            try
            {
                if (!File.Exists(file) || DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(7)) return null;
                var plan = File.ReadAllText(file, Encoding.UTF8);
                return CadPlanValidator.Validate(plan) == null && CadPlanQuality.ImageContractError(plan) == null ? plan : null;
            }
            catch { return null; }
        }

        public void SaveExecuted(string key, string plan)
        {
            if (string.IsNullOrEmpty(key) || CadPlanValidator.Validate(plan) != null || CadPlanQuality.ImageContractError(plan) != null) return;
            string temp = null;
            try
            {
                Directory.CreateDirectory(_folder);
                var file = Path.Combine(_folder, key + ".json");
                temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, plan, new UTF8Encoding(false));
                if (File.Exists(file)) File.Replace(temp, file, null); else File.Move(temp, file);
            }
            catch (Exception ex) { DiagnosticsLog.Error("保存已执行计划缓存", ex); }
            finally { if (temp != null && File.Exists(temp)) File.Delete(temp); }
        }
    }
}

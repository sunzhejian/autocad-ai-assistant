using System;

namespace AutoCAD.AI.Assistant.Models
{
    public sealed class ModelConfiguration
    {
        public ModelConfiguration()
        {
            Id = Guid.NewGuid().ToString("N");
        }

        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string ModelId { get; set; }
        public string RequestUrl { get; set; }
        public bool UseFullUrl { get; set; }
        public string ApiKeyProtected { get; set; }
        public string SystemPrompt { get; set; }
        public string ReasoningEffort { get; set; }

        public static string NormalizeReasoningEffort(string value)
        { return String.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant(); }

        public static bool IsValidReasoningEffort(string value)
        {
            switch (NormalizeReasoningEffort(value))
            {
                case null: case "none": case "minimal": case "low": case "medium": case "high": case "xhigh": case "max": return true;
                default: return false;
            }
        }

        public string Title
        {
            get { return string.IsNullOrWhiteSpace(DisplayName) ? ModelId : DisplayName; }
        }
    }
}

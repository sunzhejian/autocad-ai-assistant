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

        public string Title
        {
            get { return string.IsNullOrWhiteSpace(DisplayName) ? ModelId : DisplayName; }
        }
    }
}

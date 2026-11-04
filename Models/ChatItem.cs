using System;

namespace AutoCAD.AI.Assistant.Models
{
    public sealed class ChatItem
    {
        public ChatItem()
        {
            CreatedAt = DateTime.Now;
        }

        public string Role { get; set; }
        public string Content { get; set; }
        public bool IsUser { get; set; }
        public DateTime CreatedAt { get; set; }
        public string Header
        {
            get { return IsUser ? "你" : Role; }
        }
    }
}

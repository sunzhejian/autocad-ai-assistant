using System;
using System.ComponentModel;

namespace AutoCAD.AI.Assistant.Models
{
    public sealed class ChatItem : INotifyPropertyChanged
    {
        private string _role;
        private string _content;

        public ChatItem()
        {
            CreatedAt = DateTime.Now;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public string Role
        {
            get { return _role; }
            set
            {
                _role = value;
                Raise("Role");
                Raise("Header");
            }
        }

        public string Content
        {
            get { return _content; }
            set
            {
                _content = value;
                Raise("Content");
            }
        }

        public string ImagePath { get; set; }
        public bool IsUser { get; set; }
        public DateTime CreatedAt { get; set; }

        public bool HasImage
        {
            get { return !string.IsNullOrEmpty(ImagePath); }
        }

        public string Header
        {
            get { return IsUser ? "你" : Role; }
        }

        private void Raise(string name)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }
}

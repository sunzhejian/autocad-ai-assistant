using System;
using System.Text;

namespace AutoCAD.AI.Assistant.Services
{
    /// <summary>Bounded, thread-safe preview; full model output stays in the client.</summary>
    internal sealed class StreamingPreview
    {
        private const int Limit = 8000;
        private readonly object _gate = new object();
        private readonly StringBuilder _text = new StringBuilder();
        private bool _fenced, _limited, _dirty;
        public int BufferedCharacters { get { lock (_gate) return _text.Length; } }
        public void Append(string delta)
        {
            if (String.IsNullOrEmpty(delta)) return;
            lock (_gate)
            {
                if (_fenced || _limited) return;
                var start = Math.Max(0, _text.Length - 2);
                _text.Append(delta, 0, Math.Min(delta.Length, Limit + 3 - _text.Length));
                for (var i = start; i + 2 < _text.Length; i++)
                    if (_text[i] == '`' && _text[i + 1] == '`' && _text[i + 2] == '`')
                    { _text.Length = i; _fenced = true; break; }
                if (!_fenced && _text.Length > Limit) { _text.Length = Limit; _limited = true; }
                _dirty = true;
            }
        }
        public string Take()
        {
            lock (_gate)
            {
                if (!_dirty) return null;
                _dirty = false;
                var text = _text.ToString();
                return _fenced ? text.TrimEnd() + "\n（正在生成绘图指令…）"
                    : _limited ? text + "\n（预览已限长，完整结果仍会校验）" : text;
            }
        }
    }
}

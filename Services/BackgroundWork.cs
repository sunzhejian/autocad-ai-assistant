using System;
using System.Threading;
using System.Threading.Tasks;

namespace AutoCAD.AI.Assistant.Services
{
    /// <summary>Coalesces small persistence jobs; the caller never runs their I/O.</summary>
    internal sealed class BackgroundWork : IDisposable
    {
        private readonly object _sync = new object();
        private readonly Timer _timer;
        private readonly int _delay;
        private Action _pending;
        private Task _running;
        private bool _disposed;
        public string LastError { get; private set; }
        public BackgroundWork(int delayMilliseconds)
        {
            _delay = delayMilliseconds;
            _timer = new Timer(delegate { RunOne(); }, null, Timeout.Infinite, Timeout.Infinite);
        }
        public void Request(Action action)
        {
            lock (_sync)
            {
                if (_disposed) return;
                var first = _pending == null;
                _pending = action;
                // A fixed batching window also persists during continuous activity.
                if (first && _running == null) _timer.Change(_delay, Timeout.Infinite);
            }
        }
        private void RunOne()
        {
            lock (_sync)
            {
                if (_disposed || _running != null || _pending == null) return;
                StartLocked();
            }
        }
        private void StartLocked()
        {
            var action = _pending; _pending = null;
            _running = Task.Run(delegate
            {
                try { action(); LastError = null; }
                catch (Exception ex) { LastError = ex.GetType().Name + ": " + ex.Message; }
                finally
                {
                    lock (_sync)
                    {
                        _running = null;
                        if (!_disposed && _pending != null) _timer.Change(_delay, Timeout.Infinite);
                    }
                }
            });
        }
        public async Task FlushAsync()
        {
            while (true)
            {
                Task active;
                lock (_sync)
                {
                    if (_disposed) return;
                    _timer.Change(Timeout.Infinite, Timeout.Infinite);
                    if (_running == null)
                    {
                        if (_pending == null) return;
                        StartLocked();
                    }
                    active = _running;
                }
                await active.ConfigureAwait(false);
            }
        }
        public void Dispose()
        {
            lock (_sync) { _disposed = true; _pending = null; _timer.Dispose(); }
        }
    }
}

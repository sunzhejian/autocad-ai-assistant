using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;

namespace AutoCAD.AI.Assistant.Services
{
    /// <summary>Runs each batch as one document command, so U undoes the complete batch.</summary>
    internal static class CadExecutionQueue
    {
        private sealed class Pending
        {
            public string Plan;
            public CancellationToken Cancellation;
            public TaskCompletionSource<string> Completion = new TaskCompletionSource<string>();
            public CancellationTokenSource Deadline;
            public CancellationTokenRegistration Registration;
        }

        private static readonly Dictionary<Document, Pending> Jobs = new Dictionary<Document, Pending>();
        private static readonly object Sync = new object();

        static CadExecutionQueue()
        {
            Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.DocumentToBeDestroyed += delegate(object sender, DocumentCollectionEventArgs e)
            {
                Pending job;
                lock (Sync)
                {
                    if (!Jobs.TryGetValue(e.Document, out job)) return;
                    Jobs.Remove(e.Document);
                }
                job.Completion.TrySetException(new InvalidOperationException("原图形已关闭，计划未执行。"));
            };
        }

        public static Task<string> Enqueue(Document document, string plan, CancellationToken cancellation)
        {
            if (document == null) throw new InvalidOperationException("没有活动图形。");
            var job = new Pending { Plan = plan, Cancellation = cancellation };
            lock (Sync)
            {
                if (Jobs.ContainsKey(document)) throw new InvalidOperationException("此图形已有待执行计划。");
                Jobs.Add(document, job);
            }
            job.Deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            job.Registration = job.Deadline.Token.Register(delegate
            {
                var removed = false;
                lock (Sync) { Pending current; if (Jobs.TryGetValue(document, out current) && current == job) { Jobs.Remove(document); removed = true; } }
                if (removed) job.Completion.TrySetCanceled();
            });
            job.Deadline.CancelAfter(TimeSpan.FromSeconds(45));
            job.Completion.Task.ContinueWith(delegate { job.Registration.Dispose(); job.Deadline.Dispose(); }, TaskScheduler.Default);
            try { document.SendStringToExecute("AIDRAW ", true, false, false); }
            catch { lock (Sync) Jobs.Remove(document); job.Registration.Dispose(); job.Deadline.Dispose(); throw; }
            return job.Completion.Task;
        }

        public static void ExecutePending(Document document)
        {
            Pending job;
            lock (Sync)
            {
                if (document == null || !Jobs.TryGetValue(document, out job)) return;
                Jobs.Remove(document);
            }
            try
            {
                job.Cancellation.ThrowIfCancellationRequested();
                job.Deadline.Token.ThrowIfCancellationRequested();
                string message;
                if (new CadDrawingExecutor().Execute(job.Plan, out message) == null)
                    throw new InvalidOperationException(message);
                job.Completion.TrySetResult(message);
            }
            catch (OperationCanceledException) { job.Completion.TrySetCanceled(); }
            catch (Exception ex) { job.Completion.TrySetException(ex); }
        }
    }
}

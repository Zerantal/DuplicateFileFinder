using System.Collections.Concurrent;

namespace DupFileUtil.Util
{
    internal class StandaloneSynchronizationContext : SynchronizationContext
    {
        private readonly BlockingCollection<KeyValuePair<SendOrPostCallback, object?>> _queue = new();

        private int _operationCount = 0;

        public override void OperationStarted()
        {
            Interlocked.Increment(ref _operationCount);
        }

        public override void OperationCompleted()
        {
            if (Interlocked.Decrement(ref _operationCount) == 0)
                Complete();
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            _queue.Add(new KeyValuePair<SendOrPostCallback, object?>(d, state));
        }

        public void RunOnCurrentThread()
        {
            while (_queue.TryTake(out var workItem, Timeout.Infinite))
            {
                workItem.Key(workItem.Value);
            }
        }

        public void Complete()
        {
            _queue.CompleteAdding();
        }

        // ReSharper disable once UnusedMember.Global
        public static void Start(Func<Task> func)
        {
            var prevCtx = SynchronizationContext.Current;

            try
            {
                var syncCtx = new StandaloneSynchronizationContext();
                SynchronizationContext.SetSynchronizationContext(syncCtx);

                var t = func();
                t.ContinueWith(
                    delegate { syncCtx.Complete(); }, TaskScheduler.Default);

                syncCtx.RunOnCurrentThread();

                t.GetAwaiter().GetResult();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(prevCtx);
            }

        }

        // ReSharper disable once UnusedMember.Global
        public static void Start(Action asyncMethod)
        {
            {
                var prevCtx = SynchronizationContext.Current;
                try
                {
                    var syncCtx = new StandaloneSynchronizationContext();
                    SynchronizationContext.SetSynchronizationContext(syncCtx);

                    syncCtx.OperationStarted();
                    asyncMethod();
                    syncCtx.OperationCompleted();

                    syncCtx.RunOnCurrentThread();

                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(prevCtx);
                }
            }
        }
    }
}

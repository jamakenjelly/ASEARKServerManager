using System;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace ServerManagerTool.Common.Lib
{
    /// <summary>
    /// This class ensures the following:
    /// 1. All work items run in the order posted.
    /// 2. Work items run on a background thread.
    /// 3. Work items do not overlap
    /// 4. If requested, the completion status of a work item is returned via a task.
    /// </summary>
    public class ActionQueue : IAsyncDisposable
    {
        // Item type is Func<Task> (not the old plain Action) so the block itself can
        // properly await each posted work item - see PostAction(Func<Task> action)
        // below for why that matters. A synchronous Action/Func<T> is trivially
        // wrapped as "invoke it, then return an already-completed Task".
        public ActionBlock<Func<Task>> workQueue;

        public ActionQueue(TaskScheduler scheduler = null)
        {
            this.workQueue = new ActionBlock<Func<Task>>(f => f(), new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 1, TaskScheduler = scheduler ?? TaskScheduler.Default });
        }

        public Task<T> PostAction<T>(Func<T> action)
        {
            TaskCompletionSource<T> tcs = new TaskCompletionSource<T>();
            this.workQueue.Post(() =>
                {
                    try
                    {
                        var result = action.Invoke();
                        Task.Run(() => tcs.TrySetResult(result));
                    }
                    catch(Exception ex)
                    {
                        Task.Run(() => tcs.TrySetException(ex));
                    }
                    return Task.CompletedTask;
                });
            return tcs.Task;
        }

        public Task PostAction(Action action)
        {
            return PostAction(() => { action.Invoke(); return true; });
        }

        // Genuinely awaits the delegate's own returned Task before the queue
        // considers this work item finished and moves on to the next one.
        //
        // PostAction<T>(Func<T> action) above looks like it would also accept an
        // async method (e.g. PostAction(SomeAsyncMethod) where SomeAsyncMethod is
        // "async Task SomeAsyncMethod()") by inferring T = Task - but that silently
        // breaks this class's own documented "work items do not overlap" guarantee:
        // action.Invoke() on an async Task-returning delegate returns the instant the
        // method hits its FIRST await, not when the method actually finishes, so the
        // queue immediately considers that item done and starts the next one while
        // the "posted" async work is still running in the background - exactly the
        // overlap this class exists to prevent.
        //
        // This overload is what actually fixes that: because it takes a non-generic
        // Func<Task> (rather than a generic Func<T> that merely happens to accept
        // one), C# overload resolution prefers this one over PostAction<T> for any
        // call passing an async-Task method or lambda - so every existing
        // PostAction(someAsyncMethod) call site across the app picks this up
        // automatically, with no call-site changes needed, and now genuinely
        // serializes its async work instead of only appearing to.
        public Task PostAction(Func<Task> action)
        {
            TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();
            this.workQueue.Post(async () =>
                {
                    try
                    {
                        await action().ConfigureAwait(false);
                        Task.Run(() => tcs.TrySetResult(true));
                    }
                    catch (Exception ex)
                    {
                        Task.Run(() => tcs.TrySetException(ex));
                    }
                });
            return tcs.Task;
        }

        public async ValueTask DisposeAsync()
        {
            await PostAction(() => this.workQueue.Complete());
        }
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ServerManagerTool.Common.Utils
{
    public static class TaskUtils
    {
        public static void DoNotWait(this Task task)
        {
            // Do nothing, let the task continue.  Eliminates compiler warning about non-awaited tasks in an async method.
        }

        public static void DoNotWait(this ValueTask task)
        {
            // Do nothing, let the task continue.  Eliminates compiler warning about non-awaited tasks in an async method.
        }

        public static async Task RunOnUIThreadAsync(Action action)
        {
            var app = Application.Current;
            if (app != null)
            {
                await app.Dispatcher.InvokeAsync(action);
            }
        }

        // Value-returning counterpart - lets a background-thread method marshal
        // just its reads/writes of WPF DependencyObjects (profiles, servers,
        // etc, which throw "the calling thread cannot access this object
        // because a different thread owns it" if touched off the UI thread)
        // onto the UI thread, then keep working with the returned plain result
        // back on the background thread without staying on the UI thread for
        // the rest of the method (e.g. network calls).
        public static async Task<T> RunOnUIThreadAsync<T>(Func<T> func)
        {
            var app = Application.Current;
            if (app == null)
                return default;

            return await app.Dispatcher.InvokeAsync(func);
        }

        public static readonly Task FinishedTask = Task.FromResult(true);

        public static async Task TimeoutAfterAsync(this Task task, int millisecondsDelay)
        {
            using (var timeoutCancellationTokenSource = new CancellationTokenSource())
            {
                var completedTask = await Task.WhenAny(task, Task.Delay(millisecondsDelay, timeoutCancellationTokenSource.Token));
                if (completedTask == task)
                {
                    timeoutCancellationTokenSource.Cancel();
                    await task;  // Very important in order to propagate exceptions
                }
                else
                {
                    throw new TimeoutException("The operation has timed out.");
                }
            }
        }

        public static async Task<TResult> TimeoutAfterAsync<TResult>(this Task<TResult> task, int millisecondsDelay)
        {
            using (var timeoutCancellationTokenSource = new CancellationTokenSource())
            {                
                var completedTask = await Task.WhenAny(task, Task.Delay(millisecondsDelay, timeoutCancellationTokenSource.Token));
                if (completedTask == task)
                {
                    timeoutCancellationTokenSource.Cancel();
                    return await task;  // Very important in order to propagate exceptions
                }
                else
                {
                    throw new TimeoutException("The operation has timed out.");
                }
            }
        }
    }
}

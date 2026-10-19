#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Westwind.WebConnection
{
    /// <summary>
    /// Synchronizes tasks with the FoxPro main thread.
    /// </summary>
    /// <remarks>
    /// When one or more tasks are ready to run, posts a Windows message to the main FoxPro window.
    /// Subclasses the window to receive the posted message and dispatch the tasks.
    /// Unlike FoxPro BINDEVENT, subclassing processes messages even when FoxPro pumps messages from a dispatched task (e.g. from a modal form).
    /// </remarks>
    internal sealed class FoxProSynchronizationContext : SynchronizationContext
    {
        private readonly IntPtr _hwnd;
        private readonly ConcurrentQueue<(SendOrPostCallback handler, object? state)> _postQueue = [];
        private readonly WndProcDelegate _wndProcDelegate;
        private readonly IntPtr _originalWndProc;
        private readonly uint _postMessageId;
        private readonly Timer _repostTimer;
        private int _dispatchPending; // 1 from posting a dispatch message until a dispatch starts.
        private int _repostDelay;

        private const int RepostDelayAfterFailure = 50, RepostDelayFirst = 1000, RepostDelayMax = 60_000; // Milliseconds.

        public FoxProSynchronizationContext(int hwnd)
        {
            _hwnd = (IntPtr)hwnd;
            _wndProcDelegate = WndProc; // Prevents the delegate from being garbage collected.
            _originalWndProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
            _postMessageId = RegisterWindowMessage("FoxProSynchronizationContextDispatch");
            _repostTimer = new Timer(_ => PostDispatchMessage());
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == _postMessageId)
            {
                Dispatch();
                return IntPtr.Zero;
            }

            return CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
        }

        /// <summary>
        /// Queues a callback and, unless a dispatch message is already pending, posts one. Thread safe.
        /// </summary>
        /// <remarks>
        /// Never throws: the callback is queued and runs at the next dispatch either way.
        /// One pending message dispatches every queued callback, so posts don't fill the thread's message queue (10,000 messages) while FoxPro is busy.
        /// </remarks>
        public override void Post(SendOrPostCallback d, object? state)
        {
            _postQueue.Enqueue((d, state));

            if (Interlocked.Exchange(ref _dispatchPending, 1) == 0)
            {
                _repostDelay = RepostDelayFirst;
                PostDispatchMessage();
            }
        }

        /// <summary>
        /// Posts the dispatch message unless a dispatch has started since it was requested,
        /// and arms the timer to post it again if none has started by then: soon if posting failed because the message queue is full,
        /// otherwise after a growing delay in case the message is removed without being dispatched.
        /// </summary>
        private void PostDispatchMessage()
        {
            if (Volatile.Read(ref _dispatchPending) == 0)
                return;

            int delay = RepostDelayAfterFailure;
            if (PostMessage(_hwnd, _postMessageId, IntPtr.Zero, IntPtr.Zero))
            {
                delay = _repostDelay;
                _repostDelay = Math.Min(delay * 2, RepostDelayMax);
            }
            _repostTimer.Change(delay, Timeout.Infinite);
        }

        /// <summary>
        /// Dispatches all queued send or post callbacks in the synchronization context.
        /// </summary>
        private void Dispatch()
        {
            Volatile.Write(ref _dispatchPending, 0); // Before dequeuing, so that a callback queued after the last dequeue posts a new message.
            while (_postQueue.TryDequeue(out var post))
            {
                try
                {
                    post.handler(post.state);
                }
                catch (Exception ex)
                {
                    // Reports the unhandled exception as an unobserved task exception.
                    // An event is raised by TaskScheduler.UnobservedTaskException after the task's finalizer runs.
                    var edi = ExceptionDispatchInfo.Capture(ex);
                    Task.Run(() => edi.Throw());
                }
            }
        }

        /// <summary>
        /// Starts a dispatch operation. Used by external code to dispatch queued callbacks.
        /// </summary>
        public override void OperationStarted() => Dispatch();

        private const int GWLP_WNDPROC = -4;

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", EntryPoint = "SetWindowLongA")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", CharSet = CharSet.Unicode, BestFitMapping = false, ThrowOnUnmappableChar = true)]
        private static extern uint RegisterWindowMessage(string lpString);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}

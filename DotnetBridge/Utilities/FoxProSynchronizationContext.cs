#nullable enable
using System;
using System.Collections.Concurrent;
using System.ComponentModel;
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
    internal sealed class FoxProSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly IntPtr _hwnd;
        private readonly ConcurrentQueue<(SendOrPostCallback handler, object? state)> _postQueue = [];
        private readonly int _ownerThreadId;
        private readonly WndProcDelegate _wndProcDelegate;
        private readonly IntPtr _originalWndProc;
        private readonly uint _postMessageId;
        private bool _disposed;

        public FoxProSynchronizationContext(long hwnd)
        {
            _hwnd = (IntPtr)hwnd;
            _ownerThreadId = Environment.CurrentManagedThreadId;
            _wndProcDelegate = WndProc; // Prevents the delegate from being garbage collected.
            _postMessageId = RegisterWindowMessage("FoxProSynchronizationContextDispatch");

            if (_hwnd != IntPtr.Zero)
            {
                Marshal.SetLastPInvokeError(0);
                _originalWndProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
                int error = Marshal.GetLastPInvokeError();
                if (_originalWndProc == IntPtr.Zero && error != 0)
                    throw new Win32Exception(error, "Failed to subclass the FoxPro window.");
            }
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
        /// Posts a message to indicate that there are posts ready to dispatch. Thread safe.
        /// </summary>
        public override void Post(SendOrPostCallback d, object? state)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _postQueue.Enqueue((d, state));

            if (_hwnd == IntPtr.Zero)
            {
                Dispatch();
                return;
            }

            if (!PostMessage(_hwnd, _postMessageId, IntPtr.Zero, IntPtr.Zero))
                throw new OutOfMemoryException("Failed to post dispatch message.");
        }

        /// <summary>
        /// Dispatches all queued send or post callbacks in the synchronization context.
        /// </summary>
        private void Dispatch()
        {
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

        public void Dispose()
        {
            if (_disposed)
                return;

            if (_hwnd != IntPtr.Zero && _originalWndProc != IntPtr.Zero)
                _ = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _originalWndProc);

            _disposed = true;
        }

        private const int GWLP_WNDPROC = -4;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", CharSet = CharSet.Unicode, BestFitMapping = false, ThrowOnUnmappableChar = true)]
        private static extern uint RegisterWindowMessage(string lpString);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}

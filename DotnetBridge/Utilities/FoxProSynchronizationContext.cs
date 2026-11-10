#nullable enable
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

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
        private readonly FoxProDispatchQueue _dispatchQueue;
        private readonly int _ownerThreadId;
        private readonly WndProcDelegate _wndProcDelegate;
        private readonly IntPtr _originalWndProc;
        private readonly uint _postMessageId;
        private bool _disposed;

        public FoxProSynchronizationContext(int hwnd, Action<string> recovered, Action<string>? exhausted = null)
        {
            _hwnd = (IntPtr)hwnd;
            _ownerThreadId = Environment.CurrentManagedThreadId;
            _wndProcDelegate = WndProc; // Prevents the delegate from being garbage collected.
            _postMessageId = _hwnd == IntPtr.Zero ? 0 : RegisterWindowMessage("FoxProSynchronizationContextDispatch");
            _dispatchQueue = new FoxProDispatchQueue(PostDispatchMessage,
                diagnostic => recovered($"{diagnostic}; DispatchMessageId={_postMessageId}"),
                exhausted: diagnostic => exhausted?.Invoke($"{diagnostic}; DispatchMessageId={_postMessageId}"));

            if (_hwnd != IntPtr.Zero)
            {
                Marshal.SetLastPInvokeError(0);
                _originalWndProc = SetWindowLong(_hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
                int error = Marshal.GetLastPInvokeError();
                if (_originalWndProc == IntPtr.Zero && error != 0)
                    throw new Win32Exception(error, "Failed to subclass the FoxPro window.");
            }
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == _postMessageId)
            {
                _dispatchQueue.Dispatch(consumeMessage: true);
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
            _dispatchQueue.Post(d, state);
            if (_hwnd == IntPtr.Zero)
                _dispatchQueue.Dispatch(consumeMessage: true, enforceOwnerThread: false);
        }

        internal int PendingCount => _dispatchQueue.PendingCount;

        private int PostDispatchMessage()
        {
            if (_hwnd == IntPtr.Zero) return 0;
            if (PostMessage(_hwnd, _postMessageId, IntPtr.Zero, IntPtr.Zero)) return 0;
            int error = Marshal.GetLastPInvokeError();
            return error != 0 ? error : -1;
        }

        /// <summary>
        /// Starts a dispatch operation. Used by external code to dispatch queued callbacks.
        /// </summary>
        public override void OperationStarted() => _dispatchQueue.Dispatch(consumeMessage: false, enforceOwnerThread: _hwnd != IntPtr.Zero);

        public void Dispose()
        {
            if (_disposed)
                return;
            if (_hwnd != IntPtr.Zero && Environment.CurrentManagedThreadId != _ownerThreadId)
                throw new InvalidOperationException("Dispose must run on the FoxPro owning thread.");

            _dispatchQueue.Dispose();

            if (_hwnd != IntPtr.Zero && _originalWndProc != IntPtr.Zero)
                _ = SetWindowLong(_hwnd, GWLP_WNDPROC, _originalWndProc);

            _disposed = true;
        }

        private const int GWLP_WNDPROC = -4;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", EntryPoint = "SetWindowLongA", SetLastError = true)]
        private static extern IntPtr SetWindowLong(IntPtr window, int index, IntPtr value);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", EntryPoint = "CallWindowProcA")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", CharSet = CharSet.Unicode, BestFitMapping = false, ThrowOnUnmappableChar = true)]
        private static extern uint RegisterWindowMessage(string lpString);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}

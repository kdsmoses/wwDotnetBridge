#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Westwind.WebConnection
{
    /// <summary>
    /// Synchronizes tasks with the FoxPro main thread.
    /// </summary>
    /// <remarks>When one or more tasks are ready to run, posts a Windows message to the main FoxPro window.</remarks>
    internal sealed class FoxProSynchronizationContext(int hwnd, wwDotNetBridge bridge) : SynchronizationContext
    {
        private readonly IntPtr _hwnd = (IntPtr)hwnd;
        private readonly ConcurrentQueue<(SendOrPostCallback handler, object? state)> _postQueue = [];

        /// <summary>
        /// Gets the ID of the Windows message posted when a task is ready to run.
        /// </summary>
        public int PostMessageId { get; private set; } = RegisterWindowMessage("FoxProSynchronizationContextDispatch");

        /// <summary>
        /// Posts a message to indicate that there are posts ready to dispatch. Thread safe.
        /// </summary>
        public override void Post(SendOrPostCallback d, object? state)
        {
            _postQueue.Enqueue((d, state));

            if (!PostMessage(_hwnd, PostMessageId, IntPtr.Zero, IntPtr.Zero))
                bridge.LastException = new OutOfMemoryException("Failed to post dispatch message.");
        }

        /// <summary>
        /// Dispatches all queued send or post callbacks in the synchronization context. Called when a Windows message with ID <see cref="PostMessageId"/> is received.
        /// </summary>
        public void Dispatch()
        {
            // FoxPro ignores a PostMessageId message when posted while handling a previous PostMessageId message. Therefore, it is important to run all queued posts.

            while (_postQueue.TryDequeue(out var post))
            {
                try
                {
                    post.handler(post.state);
                }
                catch (Exception ex)
                {
                    bridge.LastException = ex;
                }
            }
        }

        /// <summary>
        /// Starts a dispatch operation. Used by external code to dispatch queued callbacks.
        /// </summary>
        public override void OperationStarted() => Dispatch();

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", CharSet = CharSet.Unicode, BestFitMapping = false, ThrowOnUnmappableChar = true)]
        private static extern int RegisterWindowMessage(string lpString);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}

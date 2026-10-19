using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Westwind.WebConnection;

namespace wwDotnetBridge.Tests
{
    /// <summary>
    /// Tests posting to the FoxPro synchronization context while its window's thread isn't pumping messages, as while FoxPro is busy.
    /// </summary>
    [TestClass]
    public class FoxProSynchronizationContextTests
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

        [TestMethod]
        [Timeout(30000)]
        public void Post_MoreThanMessageQueueHolds_RunsAllInOrder()
        {
            using var window = new WindowThread();
            var context = window.CreateContext();
            const int count = 20_000; // Twice the default per-thread limit of posted messages.
            var ran = new List<int>();
            var allRan = new ManualResetEventSlim();
            for (int i = 0; i < count; i++)
                context.Post(state => { ran.Add((int)state!); if (ran.Count == count) allRan.Set(); }, i);

            window.Pump();
            Assert.IsTrue(allRan.Wait(TestTimeout));
            CollectionAssert.AreEqual(Enumerable.Range(0, count).ToList(), ran);
        }

        [TestMethod]
        [Timeout(30000)]
        public void Post_WhileMessageQueueFull_RunsOnceItDrains()
        {
            using var window = new WindowThread();
            var context = window.CreateContext();
            window.FillMessageQueue();
            var ran = new ManualResetEventSlim();
            context.Post(_ => ran.Set(), null);

            window.Pump();
            Assert.IsTrue(ran.Wait(TestTimeout));
        }

        /// <summary>
        /// A window on a thread of its own, which pumps messages only once told to.
        /// </summary>
        private sealed class WindowThread : IDisposable
        {
            private readonly Thread _thread;
            private readonly ManualResetEventSlim _created = new(), _pump = new();
            private IntPtr _hwnd;
            private int _threadId;

            public WindowThread()
            {
                _thread = new Thread(Run) { IsBackground = true };
                _thread.Start();
                _created.Wait();
            }

            private void Run()
            {
                _threadId = GetCurrentThreadId();
                _hwnd = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                _created.Set();
                _pump.Wait();
                while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                    DispatchMessage(ref msg);
                DestroyWindow(_hwnd);
            }

            /// <summary>
            /// Creates a synchronization context on this window, as wwDotnetBridge.PRG does on FoxPro's main window.
            /// </summary>
            public SynchronizationContext CreateContext()
            {
                Assert.AreNotEqual(IntPtr.Zero, _hwnd);
                var previous = SynchronizationContext.Current;
                new wwDotNetBridge().SetSynchronizationContext(_hwnd.ToInt32());
                var context = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(previous);
                return context;
            }

            public void FillMessageQueue()
            {
                int posted = 0;
                while (PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero))
                    Assert.IsTrue(++posted < 1_000_000);
                Assert.IsTrue(posted > 0);
            }

            public void Pump() => _pump.Set();

            public void Dispose()
            {
                _pump.Set();
                var stop = DateTime.UtcNow + TestTimeout;
                while (!PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero) && DateTime.UtcNow < stop)
                    Thread.Sleep(10);
                _thread.Join(TestTimeout);
            }

            private const uint WM_NULL = 0, WM_QUIT = 0x12;

            [StructLayout(LayoutKind.Sequential)]
            private struct MSG
            {
                public IntPtr hwnd;
                public uint message;
                public IntPtr wParam, lParam;
                public uint time;
                public int ptX, ptY;
            }

            [DllImport("kernel32.dll")]
            private static extern int GetCurrentThreadId();

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

            [DllImport("user32.dll")]
            private static extern bool DestroyWindow(IntPtr hwnd);

            [DllImport("user32.dll")]
            private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint filterMin, uint filterMax);

            [DllImport("user32.dll")]
            private static extern IntPtr DispatchMessage(ref MSG msg);

            [DllImport("user32.dll")]
            private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll")]
            private static extern bool PostThreadMessage(int threadId, uint msg, IntPtr wParam, IntPtr lParam);
        }
    }
}

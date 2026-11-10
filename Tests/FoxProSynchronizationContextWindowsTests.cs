#nullable enable
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Westwind.WebConnection;

namespace wwDotnetBridge.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class FoxProSynchronizationContextWindowsTests
    {
        [TestMethod]
        public void NativeWindowRetainsReceiveFaultDuringQuotaExhaustion()
        {
            if (!OperatingSystem.IsWindows() || IntPtr.Size != 4) Assert.Inconclusive("Requires a 32-bit Windows host.");
            using var listener = new HttpListener();
            using var portReservation = new TcpListener(IPAddress.Loopback, 0);
            portReservation.Start();
            int port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
            portReservation.Stop();
            listener.Prefixes.Add($"http://localhost:{port}/");
            listener.Start();
            var accept = listener.GetContextAsync();
            using var socket = new ClientWebSocket();
            var connect = socket.ConnectAsync(new Uri($"ws://localhost:{port}/"), CancellationToken.None);
            var server = accept.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult().AcceptWebSocketAsync(null).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            using var serverSocket = server.WebSocket;
            connect.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();

            IntPtr window = CreateWindowExW(0, "STATIC", "dispatch-test", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Assert.AreNotEqual(IntPtr.Zero, window);
            var prior = SynchronizationContext.Current;
            var reports = new System.Collections.Generic.List<string>();
            try
            {
                var bridge = new wwDotNetBridge();
                bridge.SetSynchronizationContext(window.ToInt32());
                using var context = (FoxProSynchronizationContext)SynchronizationContext.Current!;
                using var subscription = new EventSubscriber(bridge, new RecoverySink(reports), "On", true, new VfpStub());
                int owner = Environment.CurrentManagedThreadId, resumedOn = 0;
                var receive = ReceiveFault();
                int messages = 0;
                while (PostMessageW(window, 0x8001, IntPtr.Zero, IntPtr.Zero))
                {
                    if (++messages > 20000) Assert.Fail("Could not exhaust the test thread's message queue.");
                }
                Assert.AreEqual(FoxProDispatchQueue.NotEnoughQuota, Marshal.GetLastPInvokeError());
                _ = Task.Run(socket.Abort);
                Assert.IsTrue(SpinWait.SpinUntil(() => context.PendingCount > 0, TimeSpan.FromSeconds(10)));
                Assert.IsFalse(receive.IsCompleted);
                Assert.IsEmpty(reports);
                PumpUntil(() => receive.IsCompleted && reports.Count == 1);
                Assert.AreEqual(owner, resumedOn);
                StringAssert.Contains(reports[0], "WindowsError=1816;");
                receive.GetAwaiter().GetResult();

                // A callback's nested pump must be able to dispatch the next callback.
                bool nestedDelivered = false, nestedCompleted = false;
                context.Post(_ => { PumpUntil(() => nestedDelivered); nestedCompleted = true; }, null);
                context.Post(_ => nestedDelivered = true, null);
                PumpUntil(() => nestedCompleted);
                Assert.IsTrue(nestedDelivered);

                var source = new DiagnosticSource();
                var disposedSubscription = new EventSubscriber(source, new RecoverySink(reports), "On", true, new VfpStub());
                source.Raise();
                disposedSubscription.Dispose();
                context.OperationStarted();
                Assert.HasCount(1, reports);


                async Task ReceiveFault()
                {
                    try { await socket.ReceiveAsync(new Memory<byte>(new byte[16]), CancellationToken.None); }
                    catch (WebSocketException) { resumedOn = Environment.CurrentManagedThreadId; }
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(prior);
                DestroyWindow(window);
            }
        }

        public sealed class VfpStub
        {
            public bool Eval(string expression) => true;
        }

        public sealed class RecoverySink(System.Collections.Generic.List<string> reports)
        {
            public void OnDispatchQueueRecovered(string diagnostic) => reports.Add(diagnostic);
        }

        public sealed class DiagnosticSource
        {
            public event Action<string>? DispatchQueueRecovered;
            public void Raise() => DispatchQueueRecovered?.Invoke("must not be delivered after unsubscribe");
        }

        private static void PumpUntil(Func<bool> finished)
        {
            var elapsed = Stopwatch.StartNew();
            while (!finished())
            {
                if (elapsed.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Native dispatch did not complete.");
                if (PeekMessageW(out var message, IntPtr.Zero, 0, 0, 1))
                {
                    TranslateMessage(ref message);
                    DispatchMessageW(ref message);
                }
                else Thread.Sleep(1);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Message
        {
            public IntPtr Window;
            public uint Id;
            public IntPtr WParam, LParam;
            public uint Time;
            public int X, Y;
            public uint Private;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(uint extendedStyle, string className, string name, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll")]
        private static extern bool PeekMessageW(out Message message, IntPtr window, uint minimum, uint maximum, uint remove);
        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessageW(ref Message message);
    }
}

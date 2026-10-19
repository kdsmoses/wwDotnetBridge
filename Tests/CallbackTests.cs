using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Westwind.WebConnection;
using WindowThread = wwDotnetBridge.Tests.FoxProSynchronizationContextTests.WindowThread;

namespace wwDotnetBridge.Tests
{
    /// <summary>
    /// Tests the callbacks to FoxPro of async methods and posted events, run through a FoxPro synchronization context on a window as in FoxPro.
    /// </summary>
    [TestClass]
    public class CallbackTests
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

        [TestMethod, Timeout(30000)]
        public void InvokeTaskMethodAsync_TaskWithResult_CallsOnCompleted() => AssertCompleted(nameof(Methods.AnswerLater), 42);

        [TestMethod, Timeout(30000)]
        public void InvokeTaskMethodAsync_CompletedTask_CallsOnCompleted() => AssertCompleted(nameof(Methods.Answer), 42);

        [TestMethod, Timeout(30000)]
        public void InvokeTaskMethodAsync_PlainTask_CallsOnCompletedWithNull() => AssertCompleted(nameof(Methods.Delay), null);

        [TestMethod, Timeout(30000)]
        public void InvokeTaskMethodAsync_AsyncTaskMethod_CallsOnCompletedWithNull() => AssertCompleted(nameof(Methods.NothingLater), null);

        [TestMethod, Timeout(30000)]
        public void InvokeTaskMethodAsync_CanceledTask_CallsOnError() => AssertError(nameof(Methods.Canceled), "canceled");

        [TestMethod, Timeout(30000)]
        public void InvokeTaskMethodAsync_FaultedTask_CallsOnErrorWithPlainException() => AssertError(nameof(Methods.FailsLater), "actively refused");

        [TestMethod, Timeout(30000)]
        public void InvokeTaskMethodAsync_NotATask_CallsOnError() => AssertError(nameof(Methods.NotATask), "did not return a Task");

        [TestMethod, Timeout(30000)]
        public void InvokeMethodAsync_Throws_CallsOnErrorWithPlainException()
        {
            var callback = Run((bridge, cb) => bridge.InvokeMethodAsync(cb, new Methods(), nameof(Methods.Throws)));
            Assert.AreEqual("onError", callback.Which);
            StringAssert.Contains(callback.Message, "actively refused");
            Assert.AreEqual(typeof(Exception), callback.Error.GetType()); // A SocketException isn't COM visible, so FoxPro couldn't take it.
        }

        [TestMethod, Timeout(30000)]
        public void PostedEventHandlerThrows_RaisesUnhandledCallbackException()
        {
            using var window = new WindowThread();
            window.CreateContext();
            string reported = null;
            var done = new ManualResetEventSlim();
            UnhandledCallbackExceptionHandler report = (message, details) => { reported = message; done.Set(); };
            wwDotNetBridge.UnhandledCallbackException += report;
            try
            {
                var source = new Loopback();
                using var subscriber = new Subscription(source, new ThrowingHandler());
                source.Raise();

                window.Pump();
                Assert.IsTrue(done.Wait(TestTimeout));
                Assert.AreEqual("Event handler OnNoParams: Handler failed.", reported);
            }
            finally
            {
                wwDotNetBridge.UnhandledCallbackException -= report;
            }
        }

        [TestMethod, Timeout(30000)]
        public void EventPostedBeforeUnsubscribing_IsNotDelivered()
        {
            using var window = new WindowThread();
            var context = window.CreateContext();
            var source = new Loopback();
            var handler = new CountingHandler();
            var subscriber = new Subscription(source, handler);
            source.Raise();
            subscriber.Dispose();
            var dispatched = new ManualResetEventSlim();
            context.Post(_ => dispatched.Set(), null);

            window.Pump();
            Assert.IsTrue(dispatched.Wait(TestTimeout));
            Assert.AreEqual(0, handler.Count);
        }

        [TestMethod]
        public void GetTypeFromName_FindsLoadedTypes()
        {
            var bridge = new wwDotNetBridge();
            Assert.AreEqual(typeof(Methods), bridge.GetTypeFromName(typeof(Methods).FullName));
            Assert.AreEqual(typeof(Methods), bridge.GetTypeFromName(typeof(Methods).FullName));
            Assert.IsNull(bridge.GetTypeFromName("No.Such.Type"));
        }

        private static void AssertCompleted(string method, object expected)
        {
            var callback = Run((bridge, cb) => bridge.InvokeTaskMethodAsync(cb, new Methods(), method));
            Assert.AreEqual("onCompleted", callback.Which, callback.Message);
            Assert.AreEqual(expected, callback.Result);
        }

        private static void AssertError(string method, string messagePart)
        {
            var callback = Run((bridge, cb) => bridge.InvokeTaskMethodAsync(cb, new Methods(), method));
            Assert.AreEqual("onError", callback.Which);
            StringAssert.Contains(callback.Message, messagePart);
            Assert.AreEqual(typeof(Exception), callback.Error.GetType());
        }

        /// <summary>
        /// Starts an async call as FoxPro does, on a thread whose synchronization context is FoxPro's, then pumps until the callback arrives.
        /// </summary>
        private static Callback Run(Action<wwDotNetBridge, Callback> start)
        {
            using var window = new WindowThread();
            var context = window.CreateContext();
            var bridge = new wwDotNetBridge();
            var callback = new Callback();
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                start(bridge, callback);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }

            window.Pump();
            Assert.IsTrue(callback.Called.Wait(TestTimeout), "No callback.");
            return callback;
        }

        public class Methods
        {
            public Task<int> Answer() => Task.FromResult(42);
            public async Task<int> AnswerLater() { await Task.Delay(10).ConfigureAwait(false); return 42; }
            public Task Delay() => Task.Delay(10);
            public async Task NothingLater() => await Task.Delay(10).ConfigureAwait(false);
            public Task Canceled() => Task.FromCanceled(new CancellationToken(true));
            public async Task<int> FailsLater() { await Task.Delay(10).ConfigureAwait(false); throw new SocketException(10061); }
            public int NotATask() => 1;
            public int Throws() => throw new SocketException(10061);
        }

        public class Callback
        {
            public readonly ManualResetEventSlim Called = new();
            public string Which, Message;
            public object Result, Error;

            public void OnCompleted(object result, string method) { Which = "onCompleted"; Result = result; Called.Set(); }
            public void OnError(string message, object error, string method) { Which = "onError"; Message = message; Error = error; Called.Set(); }
        }

        public class ThrowingHandler
        {
            public void OnNoParams() => throw new InvalidOperationException("Handler failed.");
            public void OnTwoParams(string s, int i) { }
        }

        public class CountingHandler
        {
            public int Count;
            public void OnNoParams() => Count++;
            public void OnTwoParams(string s, int i) => Count++;
        }

        /// <summary>
        /// Posted subscription as wwDotnetBridge.PRG's SubscribeToEvents makes, with every handler method present.
        /// </summary>
        private sealed class Subscription(object source, object handler) : IDisposable
        {
            private readonly EventSubscriber _subscriber = new(source, handler, "On", true, new Vfp());

            public void Dispose()
            {
                try
                {
                    _subscriber.Dispose();
                }
                catch (ArgumentException) { } // Marshal.FinalReleaseComObject throws for a handler that isn't a COM object.
            }
        }

        public class Vfp
        {
            public bool Eval(string _) => true;
        }
    }
}

#nullable enable
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Westwind.WebConnection;

namespace wwDotnetBridge.Tests
{
    [TestClass]
    public class FoxProDispatchQueueTests
    {
        [TestMethod]
        public void QuotaRetriesRetainOrderAndReportOnlyAtDispatch()
        {
            var clock = new ManualClock();
            int error = FoxProDispatchQueue.NotEnoughQuota, posts = 0;
            var delivered = new List<int>();
            var reports = new List<string>();
            using var queue = new FoxProDispatchQueue(() => { posts++; return error; }, reports.Add, clock);
            for (int index = 0; index < 3; index++) queue.Post(state => delivered.Add((int)state!), index);
            Assert.AreEqual(1, posts);
            clock.Advance();
            clock.Advance();
            Assert.AreEqual(3, posts);
            Assert.IsEmpty(reports);
            error = 0;
            clock.Advance();
            Assert.IsEmpty(reports);
            Assert.IsFalse(clock.Timer.Enabled);
            queue.Dispatch(true);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, delivered);
            Assert.HasCount(1, reports);
            StringAssert.Contains(reports[0], "FailedAttempts=3;");
            StringAssert.Contains(reports[0], "DurationMs=750;");
            StringAssert.Contains(reports[0], "MaximumBacklog=3;");
            StringAssert.Contains(reports[0], "PostsPerSecond=4.00;");
            queue.Dispatch(true);
            Assert.HasCount(1, reports);
            Assert.AreEqual(1, clock.Timer.EnableCount);
        }

        [TestMethod]
        public void ConcurrentPostsCoalesceAndExecuteOnceOnOwner()
        {
            int signals = 0, owner = Environment.CurrentManagedThreadId;
            var delivered = new HashSet<int>();
            bool duplicate = false, wrongThread = false;
            using var queue = new FoxProDispatchQueue(() => { signals++; return 0; }, _ => Assert.Fail());
            Parallel.For(0, 1000, index => queue.Post(_ => {
                wrongThread |= owner != Environment.CurrentManagedThreadId;
                duplicate |= !delivered.Add(index);
            }, null));
            Assert.AreEqual(1, signals);
            queue.Dispatch(true);
            Assert.HasCount(1000, delivered);
            Assert.IsFalse(duplicate);
            Assert.IsFalse(wrongThread);
        }

        [TestMethod]
        public void NestedDispatchStartsCallbacksInFifoOrder()
        {
            var order = new List<int>();
            int signals = 0;
            using var queue = new FoxProDispatchQueue(() => { signals++; return 0; }, _ => Assert.Fail());
            queue.Post(_ => { order.Add(1); queue.Dispatch(true); order.Add(3); }, null);
            queue.Post(_ => order.Add(2), null);
            queue.Dispatch(true);
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, order);
            Assert.AreEqual(2, signals);
        }

        [TestMethod]
        public void DirectDrainDoesNotConsumeAnOutstandingWindowMessage()
        {
            int signals = 0;
            using var queue = new FoxProDispatchQueue(() => { signals++; return 0; }, _ => Assert.Fail());
            queue.Post(_ => { }, null);
            queue.Dispatch(false);
            queue.Post(_ => { }, null);
            Assert.AreEqual(1, signals);
            queue.Dispatch(true);
            queue.Post(_ => { }, null);
            Assert.AreEqual(2, signals);
        }

        [TestMethod]
        public void NonQuotaFailureRejectsCallbackAndPreservesWindowsError()
        {
            int error = 5, delivered = 0;
            using var queue = new FoxProDispatchQueue(() => error, _ => Assert.Fail());
            var exception = Assert.ThrowsExactly<Win32Exception>(() => queue.Post(_ => delivered++, null));
            Assert.AreEqual(5, exception.NativeErrorCode);
            StringAssert.Contains(exception.Message, "Backlog=1");
            StringAssert.Contains(exception.Message, "WindowsError=5;");
            error = 0;
            queue.Post(_ => delivered += 10, null);
            queue.Dispatch(true);
            Assert.AreEqual(10, delivered);
        }

        [TestMethod]
        public void NonQuotaRetryFailureStopsTimerAndPropagates()
        {
            int error = FoxProDispatchQueue.NotEnoughQuota;
            var clock = new ManualClock();
            using var queue = new FoxProDispatchQueue(() => error, _ => Assert.Fail(), clock);
            queue.Post(_ => { }, null);
            error = 1400;
            Assert.AreEqual(1400, Assert.ThrowsExactly<Win32Exception>(clock.Advance).NativeErrorCode);
            Assert.IsFalse(clock.Timer.Enabled);
        }

        [TestMethod]
        public void DisposalStopsRetryAndIgnoresAlreadyScheduledTimerCallback()
        {
            int signals = 0;
            var clock = new ManualClock();
            var queue = new FoxProDispatchQueue(() => { signals++; return FoxProDispatchQueue.NotEnoughQuota; }, _ => Assert.Fail(), clock);
            queue.Post(_ => Assert.Fail(), null);
            queue.Dispose();
            clock.Timer.Fire();
            queue.Dispatch(true);
            Assert.AreEqual(1, signals);
            Assert.IsFalse(clock.Timer.Enabled);
            Assert.ThrowsExactly<ObjectDisposedException>(() => queue.Post(_ => { }, null));
        }

        [TestMethod]
        public void ReportingPostFailureDoesNotGenerateAnotherEpisode()
        {
            int error = FoxProDispatchQueue.NotEnoughQuota, reports = 0;
            var clock = new ManualClock();
            FoxProDispatchQueue? queue = null;
            using (queue = new FoxProDispatchQueue(() => error, _ => {
                reports++;
                queue!.Post(_ => { }, null);
            }, clock))
            {
                queue.Post(_ => { }, null);
                queue.Dispatch(true);
                clock.Advance();
                queue.Dispatch(true);
                Assert.AreEqual(1, reports);
            }
        }

        [TestMethod]
        public void ThrowingRecoverySubscriberDoesNotInterruptDeliveryOrNextEpisode()
        {
            int reports = 0, delivered = 0;
            var clock = new ManualClock();
            using var queue = new FoxProDispatchQueue(() => FoxProDispatchQueue.NotEnoughQuota, _ => {
                reports++;
                throw new InvalidOperationException("Diagnostic handler failed.");
            }, clock);
            queue.Post(_ => delivered++, null);
            queue.Dispatch(false);
            queue.Post(_ => delivered++, null);
            queue.Dispatch(false);
            Assert.AreEqual(2, delivered);
            Assert.AreEqual(2, reports);
            Assert.IsFalse(clock.Timer.Enabled);
        }

        [TestMethod]
        public void PostingAndDisposalAreSerialized()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            int signals = 0;
            var queue = new FoxProDispatchQueue(() => {
                Interlocked.Increment(ref signals);
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                return FoxProDispatchQueue.NotEnoughQuota;
            }, _ => { });
            var posting = Task.Run(() => queue.Post(_ => { }, null));
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
            var disposal = Task.Run(queue.Dispose);
            release.Set();
            Task.WhenAll(posting, disposal).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Assert.ThrowsExactly<ObjectDisposedException>(() => queue.Post(_ => { }, null));
            Assert.AreEqual(1, signals);
        }

        [TestMethod]
        public void PostingDuringDispatchDoesNotLoseWakeupsOrCallbacks()
        {
            int delivered = 0;
            using var queue = new FoxProDispatchQueue(() => 0, _ => { });
            var producer = Task.Run(() => {
                for (int index = 0; index < 10000; index++) queue.Post(_ => delivered++, null);
            });
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!producer.IsCompleted || queue.PendingCount != 0)
            {
                if (DateTime.UtcNow > deadline) Assert.Fail("Concurrent posting did not drain.");
                queue.Dispatch(true);
                Thread.Yield();
            }
            producer.GetAwaiter().GetResult();
            Assert.AreEqual(10000, delivered);
        }

        [TestMethod]
        public void RecoveryWithoutSubscriberStillDeliversCallbacks()
        {
            int delivered = 0;
            Action<string>? subscriber = null;
            using var queue = new FoxProDispatchQueue(() => FoxProDispatchQueue.NotEnoughQuota, diagnostic => subscriber?.Invoke(diagnostic));
            queue.Post(_ => delivered++, null);
            queue.Dispatch(false);
            Assert.AreEqual(1, delivered);
            Assert.AreEqual(0, queue.PendingCount);
        }

        [TestMethod]
        public void ExhaustionNotificationIsCorrelatedAndRunsOutsideSchedulingLock()
        {
            var clock = new ManualClock();
            var exhausted = new List<string>();
            var recovered = new List<string>();
            bool lockAvailable = false, retryCommitted = false;
            FoxProDispatchQueue? queue = null;
            using (queue = new FoxProDispatchQueue(() => FoxProDispatchQueue.NotEnoughQuota, recovered.Add, clock,
                diagnostic => {
                    exhausted.Add(diagnostic);
                    retryCommitted = clock.Timer.Enabled;
                    lockAvailable = Task.Run(() => queue!.PendingCount).Wait(TimeSpan.FromSeconds(2));
                    throw new InvalidOperationException("Observer failed");
                }))
            {
                queue.Post(_ => { }, null);
                clock.Advance();
                Assert.HasCount(1, exhausted);
                Assert.IsTrue(lockAvailable);
                Assert.IsTrue(retryCommitted);
                queue.Dispatch(false);
                Assert.HasCount(1, recovered);
                string episode = exhausted[0].Split(';')[0];
                StringAssert.StartsWith(recovered[0], episode + ";");
                queue.Post(_ => { }, null);
                queue.Dispatch(false);
                Assert.HasCount(2, exhausted);
                Assert.AreNotEqual(episode, exhausted[1].Split(';')[0]);
            }
        }

        private sealed class ManualClock : TimeProvider
        {
            private long _ticks;
            internal ManualTimer Timer = null!;
            public override long TimestampFrequency => 1000;
            public override long GetTimestamp() => _ticks;
            public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(_ticks);
            public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => Timer = new ManualTimer(() => callback(state));
            internal void Advance() { _ticks += 250; if (Timer.Enabled) Timer.Fire(); }
        }

        private sealed class ManualTimer(Action callback) : ITimer
        {
            internal bool Enabled;
            internal int EnableCount;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Enabled = dueTime != Timeout.InfiniteTimeSpan;
                if (Enabled) EnableCount++;
                return true;
            }
            internal void Fire() => callback();
            public void Dispose() => Enabled = false;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}

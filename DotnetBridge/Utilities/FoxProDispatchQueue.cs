#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Westwind.WebConnection
{
    /// <summary>Retains callbacks while the owning window's message queue is full.</summary>
    internal sealed class FoxProDispatchQueue : IDisposable
    {
        internal const int NotEnoughQuota = 1816;
        private readonly Lock _gate = new();
        private readonly LinkedList<(SendOrPostCallback callback, object? state)> _callbacks = new();
        private readonly Func<int> _postMessage;
        private readonly Action<string> _recovered;
        private readonly Action<string>? _exhausted;
        private readonly Queue<string> _exhaustionNotifications = new();
        private int _exhaustionPending;
        private readonly TimeProvider _clock;
        private readonly ITimer _retryTimer;
        private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
        private readonly string _identity = Guid.NewGuid().ToString("N");
        private bool _disposed, _messagePending, _retrying, _reportingRecovery, _diagnosticOnlyRetry;
        private long _lastDispatch, _episodeStart, _episodePosts, _failedAttempts;
        private DateTimeOffset? _episodeUtc;
        private int _maximumBacklog;
        private string? _episodeId;

        internal FoxProDispatchQueue(Func<int> postMessage, Action<string> recovered, TimeProvider? clock = null, Action<string>? exhausted = null)
        {
            _postMessage = postMessage;
            _recovered = recovered;
            _exhausted = exhausted;
            _clock = clock ?? TimeProvider.System;
            _lastDispatch = _clock.GetTimestamp();
            _retryTimer = _clock.CreateTimer(_ => Retry(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        internal int PendingCount { get { lock (_gate) return _callbacks.Count; } }

        internal void Post(SendOrPostCallback callback, object? state)
        {
            ArgumentNullException.ThrowIfNull(callback);
            try
            {
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    var node = _callbacks.AddLast((callback, state));
                    if (!_reportingRecovery || Environment.CurrentManagedThreadId != _ownerThreadId) _diagnosticOnlyRetry = false;
                    if (_episodeUtc.HasValue) _episodePosts++;
                    _maximumBacklog = Math.Max(_maximumBacklog, _callbacks.Count);
                    try
                    {
                        if (!_retrying) Signal();
                    }
                    catch
                    {
                        _callbacks.Remove(node); // A rejected post must never execute later.
                        throw;
                    }
                }
            }
            finally { NotifyExhaustion(); }
        }

        private void Signal()
        {
            if (_callbacks.Count == 0) { StopRetry(); return; }
            if (_messagePending) return;
            int error = _postMessage();
            if (error == 0)
            {
                _messagePending = true;
                StopRetry();
                return;
            }
            if (error != NotEnoughQuota)
            {
                StopRetry();
                throw new Win32Exception(error, $"Failed to post FoxPro dispatch message. WindowsError={error}; FailedUtc={_clock.GetUtcNow():O}; Context={_identity}; OwnerThread={_ownerThreadId}; Backlog={_callbacks.Count}; FailedAttempts={(_episodeUtc.HasValue ? _failedAttempts : 0) + 1}; EpisodeStartedUtc={_episodeUtc:O}");
            }
            if (!_episodeUtc.HasValue && _reportingRecovery) _diagnosticOnlyRetry = true;
            if (!_episodeUtc.HasValue && !_diagnosticOnlyRetry)
            {
                _episodeId = Guid.NewGuid().ToString("N");
                _episodeUtc = _clock.GetUtcNow();
                _episodeStart = _clock.GetTimestamp();
                _episodePosts = 1;
                _maximumBacklog = _callbacks.Count;
                _failedAttempts = 0;
                if (_exhausted != null)
                {
                    _exhaustionNotifications.Enqueue($"EpisodeId={_episodeId}; Context={_identity}; WindowsError={NotEnoughQuota}; StartedUtc={_episodeUtc:O}");
                    Volatile.Write(ref _exhaustionPending, 1);
                }
            }
            if (_episodeUtc.HasValue) _failedAttempts++;
            if (!_retrying)
            {
                _retrying = true;
                _retryTimer.Change(TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
            }
        }

        private void Retry()
        {
            try
            {
                lock (_gate)
                {
                    if (!_disposed && _retrying) Signal();
                }
            }
            finally { NotifyExhaustion(); }
        }

        private void NotifyExhaustion()
        {
            while (Volatile.Read(ref _exhaustionPending) != 0)
            {
                string diagnostic;
                lock (_gate)
                {
                    if (_disposed || !_exhaustionNotifications.TryDequeue(out diagnostic!))
                    {
                        Volatile.Write(ref _exhaustionPending, 0);
                        return;
                    }
                    if (_exhaustionNotifications.Count == 0) Volatile.Write(ref _exhaustionPending, 0);
                }
                try { _exhausted?.Invoke(diagnostic); }
                catch { } // Instrumentation must not interrupt recovery.
            }
        }

        private void StopRetry()
        {
            if (!_retrying) return;
            _retrying = false;
            _retryTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        /// <summary>
        /// Consumes a wake-up, or drains directly from the owner's dispatching wait.
        /// </summary>
        internal void Dispatch(bool consumeMessage, bool enforceOwnerThread = true)
        {
            if (enforceOwnerThread && Environment.CurrentManagedThreadId != _ownerThreadId)
                throw new InvalidOperationException("FoxPro callbacks must run on the owning thread.");
            lock (_gate)
            {
                if (_disposed) return;
                if (consumeMessage) _messagePending = false;
                QueueRecoveryDiagnostic();
            }
            for (;;)
            {
                (SendOrPostCallback callback, object? state) post;
                lock (_gate)
                {
                    if (_disposed) return;
                    if (_callbacks.First == null) { StopRetry(); return; }
                    post = _callbacks.First.Value;
                    _callbacks.RemoveFirst();
                    _lastDispatch = _clock.GetTimestamp();
                    if (!_retrying) Signal(); // Keep a wake-up available if this callback enters a nested message loop.
                    QueueRecoveryDiagnostic();
                }
                NotifyExhaustion();
                Invoke(() => post.callback(post.state));
            }
        }

        private void QueueRecoveryDiagnostic()
        {
            if (!_episodeUtc.HasValue) return;
            string diagnostic = RecoveryDetails();
            _episodeUtc = null;
            _diagnosticOnlyRetry = true;
            _callbacks.AddLast((state => ReportRecovery((string)state!), diagnostic)); // Queue once behind existing work, ahead of future application posts.
        }

        private void ReportRecovery(string diagnostic)
        {
            lock (_gate) _reportingRecovery = true;
            try { _recovered(diagnostic); }
            finally { lock (_gate) _reportingRecovery = false; }
        }

        private string RecoveryDetails()
        {
            long now = _clock.GetTimestamp();
            double seconds = _clock.GetElapsedTime(_episodeStart, now).TotalSeconds;
            return string.Create(CultureInfo.InvariantCulture, $"EpisodeId={_episodeId}; Context={_identity}; OwnerThread={_ownerThreadId}; WindowsError={NotEnoughQuota}; StartedUtc={_episodeUtc:O}; ResumedUtc={_clock.GetUtcNow():O}; DurationMs={seconds * 1000:F0}; FailedAttempts={_failedAttempts}; PostsPerSecond={(seconds > 0 ? _episodePosts / seconds : 0):F2}; MaximumBacklog={_maximumBacklog}; BacklogAtResume={_callbacks.Count}; SinceLastDispatchMs={_clock.GetElapsedTime(_lastDispatch, now).TotalMilliseconds:F0}");
        }

        private static void Invoke(Action callback)
        {
            try { callback(); }
            catch (Exception exception)
            {
                var captured = ExceptionDispatchInfo.Capture(exception);
                _ = Task.Run(() => captured.Throw()); // Preserves reporting through UnobservedTaskException.
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _retryTimer.Dispose();
                _callbacks.Clear();
                _exhaustionNotifications.Clear();
                Volatile.Write(ref _exhaustionPending, 0);
            }
        }
    }
}

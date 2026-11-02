namespace MiniProToolkit.Core;

/// <summary>
/// Lazily runs operations on one long-lived background thread, without a queue.
/// A timeout abandons only the caller's wait: the operation cannot be cancelled,
/// retains the single-flight slot until it returns, and is never retried.
/// </summary>
public sealed class SingleFlightOperationWorker : IDisposable
{
    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromMinutes(1);
    private readonly object _gate = new();
    private readonly string _name;
    private readonly Action<Thread>? _configureThread;
    private Thread? _thread;
    private Request? _active;
    private Request? _pending;
    private bool _faulted;
    private bool _disposed;

    /// <param name="name">Name assigned to the sole background worker thread.</param>
    /// <param name="configureThread">
    /// Optional configuration before starting the thread (for example, setting MTA
    /// on Windows). A configuration/start failure permanently closes the worker.
    /// The callback must not start the thread itself.
    /// </param>
    public SingleFlightOperationWorker(string name, Action<Thread>? configureThread = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
        _configureThread = configureThread;
    }

    /// <summary>
    /// Returns fallback when busy, timed out, disposed, faulted, or when the operation
    /// throws. Only the worker invokes the operation. Timeout must be positive and
    /// at most one minute; infinite waits are deliberately unsupported.
    /// </summary>
    public TResult Execute<TResult>(Func<TResult> operation, TResult fallback, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (timeout <= TimeSpan.Zero || timeout > MaximumTimeout)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive and at most one minute.");

        Request<TResult> request;
        lock (_gate)
        {
            if (_disposed || _faulted)
                return fallback;
            if (_thread is { IsAlive: false })
            {
                _faulted = true;
                return fallback;
            }
            if (_active is not null)
                return fallback;

            if (_thread is null)
            {
                try
                {
                    _thread = new Thread(Run) { IsBackground = true, Name = _name };
                    _configureThread?.Invoke(_thread);
                    _thread.Start();
                }
                catch
                {
                    _faulted = true;
                    return fallback;
                }
            }

            request = new Request<TResult>(operation, fallback);
            _active = _pending = request;
            Monitor.Pulse(_gate);
        }

        // Each request owns its completion, so a late result cannot reach another
        // caller. In particular, a timeout never clears _active or creates a thread.
        return request.Completion.Task.Wait(timeout)
            ? request.Completion.Task.Result
            : fallback;
    }

    /// <summary>
    /// Permanently rejects new work and completes existing waits with fallback.
    /// Does not join, abort, or cancel an operation; the background thread exits
    /// after that operation returns. Work not yet started is discarded.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _active?.Publish(discard: true);
            if (_pending is not null)
            {
                _pending = null;
                _active = null;
            }
            Monitor.PulseAll(_gate);
        }
    }

    private void Run()
    {
        while (true)
        {
            Request request;
            lock (_gate)
            {
                while (_pending is null && !_disposed)
                    Monitor.Wait(_gate);
                if (_disposed)
                    return;
                request = _pending!;
                _pending = null;
            }

            request.Invoke();

            lock (_gate)
            {
                // Publish under the same lock, after freeing the slot. A caller
                // observing completion can immediately submit its next operation.
                // Dispose wins over an in-flight success if it closed us first.
                _active = null;
                request.Publish(discard: _disposed);
            }
        }
    }

    private abstract class Request
    {
        public abstract void Invoke();
        public abstract void Publish(bool discard);
    }

    private sealed class Request<TResult> : Request
    {
        private readonly Func<TResult> _operation;
        private readonly TResult _fallback;
        private TResult _result;

        public Request(Func<TResult> operation, TResult fallback)
        {
            _operation = operation;
            _fallback = fallback;
            _result = fallback;
        }
        public TaskCompletionSource<TResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Invoke()
        {
            try { _result = _operation(); }
            catch { _result = _fallback; }
        }

        public override void Publish(bool discard) =>
            Completion.TrySetResult(discard ? _fallback : _result);
    }
}

using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class SingleFlightOperationWorkerTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TimeoutBudget = TimeSpan.FromMilliseconds(100);

    [Fact]
    public void LazyWorkerUsesOneNamedBackgroundThreadAndReleasesSlotBeforeCompletion()
    {
        Thread? thread = null;
        var starts = 0;
        using var worker = new SingleFlightOperationWorker("single-flight-fake", configured =>
        {
            thread = configured;
            Interlocked.Increment(ref starts);
        });
        try
        {
            Assert.Null(thread);
            var caller = Environment.CurrentManagedThreadId;
            for (var i = 0; i < 200; i++)
            {
                Assert.Equal(i, worker.Execute(() =>
                {
                    Assert.Same(thread, Thread.CurrentThread);
                    Assert.NotEqual(caller, Environment.CurrentManagedThreadId);
                    Assert.True(Thread.CurrentThread.IsBackground);
                    Assert.Equal("single-flight-fake", Thread.CurrentThread.Name);
                    return i;
                }, -1, Guard));
            }
            Assert.Equal(1, starts);
            // Result storage must not depend on boxing/type tests: null is a result.
            Assert.Null(worker.Execute<string?>(() => null, "fallback", Guard));
            Assert.Equal("next", worker.Execute(() => "next", "fallback", Guard));
        }
        finally
        {
            worker.Dispose();
            Assert.True(thread is null || thread.Join(Guard));
        }
    }

    [Fact]
    public async Task BlockedFakeTimesOutButConcurrentBusyCallsNeverExecuteOrStartThreads()
    {
        using var fake = new BlockedFake();
        var first = Task.Run(() => fake.Worker.Execute(fake.Invoke, "first-fallback", TimeoutBudget));
        Assert.True(fake.Entered.Wait(Guard));

        var busyExecutions = 0;
        var overlapping = Enumerable.Range(0, 24).Select(_ => Task.Run(() =>
            fake.Worker.Execute(() =>
            {
                Interlocked.Increment(ref busyExecutions);
                return "unexpected";
            }, "busy", Guard))).ToArray();
        Assert.All(await Task.WhenAll(overlapping).WaitAsync(Guard), result => Assert.Equal("busy", result));
        Assert.Equal("first-fallback", await first.WaitAsync(Guard));

        // Even after the original wait timed out, retries are rejected, not queued.
        for (var i = 0; i < 24; i++)
            Assert.Equal("busy", fake.Worker.Execute(() =>
            {
                Interlocked.Increment(ref busyExecutions);
                return "unexpected";
            }, "busy", TimeoutBudget));

        Assert.Equal(0, busyExecutions);
        Assert.Equal(1, fake.Invocations);
        Assert.Equal(1, fake.Starts);
        Assert.False(fake.Returned.IsSet);

        fake.Release.Set();
        Assert.True(fake.Returned.Wait(Guard));
        Assert.True(SpinWait.SpinUntil(() =>
            fake.Worker.Execute(() => "recovered", "busy", Guard) == "recovered", Guard));
        Assert.Equal("immediate-next", fake.Worker.Execute(() => "immediate-next", "busy", Guard));
        Assert.Equal(1, fake.Starts);
        Assert.Equal(1, fake.Invocations);
    }

    [Fact]
    public async Task LateResultStaysWithAbandonedRequestAndCannotLeakToNextResultType()
    {
        using var fake = new BlockedFake();
        var abandoned = Task.Run(() => fake.Worker.Execute(fake.Invoke, "timed-out", TimeoutBudget));
        Assert.True(fake.Entered.Wait(Guard));
        Assert.Equal("timed-out", await abandoned.WaitAsync(Guard));

        fake.Release.Set();
        Assert.True(fake.Returned.Wait(Guard));
        Assert.True(SpinWait.SpinUntil(() => fake.Worker.Execute(() => 42, -1, Guard) == 42, Guard));
        Assert.Equal("fresh", fake.Worker.Execute(() => "fresh", "fallback", Guard));
        Assert.Equal("timed-out", await abandoned);
        Assert.Equal(1, fake.Invocations);
        Assert.Equal(1, fake.Starts);
    }

    [Fact]
    public void OperationThrowReturnsFallbackAndSlotIsImmediatelyReusable()
    {
        Thread? thread = null;
        using var worker = new SingleFlightOperationWorker("throw-fake", configured => thread = configured);
        try
        {
            for (var i = 0; i < 100; i++)
            {
                Assert.Equal(-1, worker.Execute<int>(() => throw new InvalidOperationException("fake"), -1, Guard));
                Assert.Equal(i, worker.Execute(() => i, -1, Guard));
            }
        }
        finally
        {
            worker.Dispose();
            Assert.True(thread is null || thread.Join(Guard));
        }
    }

    [Fact]
    public void ThreadConfigurationFailureIsTerminalAndNeverRunsOperationOrRebuilds()
    {
        var starts = 0;
        var operations = 0;
        using var worker = new SingleFlightOperationWorker("start-failure-fake", _ =>
        {
            Interlocked.Increment(ref starts);
            throw new InvalidOperationException("simulated thread startup failure");
        });
        for (var i = 0; i < 20; i++)
            Assert.Equal(-1, worker.Execute(() => Interlocked.Increment(ref operations), -1, Guard));
        Assert.Equal(1, starts);
        Assert.Equal(0, operations);
    }

    [Fact]
    public void DisposeBeforeFirstUseIsTerminalAndDoesNotCreateThread()
    {
        var starts = 0;
        var operations = 0;
        using var worker = new SingleFlightOperationWorker("disposed-fake", _ => starts++);
        worker.Dispose();
        worker.Dispose();
        Assert.Equal(-1, worker.Execute(() => ++operations, -1, Guard));
        Assert.Equal(0, starts);
        Assert.Equal(0, operations);
    }

    [Fact]
    public async Task DisposeDuringFlightReturnsFallbackWithoutCancellingOrRetryingOperation()
    {
        using var fake = new BlockedFake();
        var inFlight = Task.Run(() => fake.Worker.Execute(fake.Invoke, "disposed-fallback", Guard));
        Assert.True(fake.Entered.Wait(Guard));

        // Dispose must return while the fake is still blocked, without a join.
        await Task.Run(fake.Worker.Dispose).WaitAsync(Guard);
        Assert.Equal("disposed-fallback", await inFlight.WaitAsync(Guard));
        Assert.False(fake.Returned.IsSet);
        Assert.True(fake.WorkerThread!.IsAlive);

        var unexpected = 0;
        Assert.Equal("rejected", fake.Worker.Execute(() =>
        {
            Interlocked.Increment(ref unexpected);
            return "unexpected";
        }, "rejected", Guard));
        fake.Release.Set();
        Assert.True(fake.Returned.Wait(Guard));
        Assert.True(fake.WorkerThread.Join(Guard));
        Assert.Equal("disposed-fallback", await inFlight);
        Assert.Equal("still-rejected", fake.Worker.Execute(() => "unexpected", "still-rejected", Guard));
        Assert.Equal(0, unexpected);
        Assert.Equal(1, fake.Starts);
        Assert.Equal(1, fake.Invocations);
    }

    [Fact]
    public async Task DisposeAfterTimeoutKeepsInFlightOperationSingleAndRejectsFutureWork()
    {
        using var fake = new BlockedFake();
        var first = Task.Run(() => fake.Worker.Execute(fake.Invoke, "timeout", TimeoutBudget));
        Assert.True(fake.Entered.Wait(Guard));
        Assert.Equal("timeout", await first.WaitAsync(Guard));
        fake.Worker.Dispose();
        Assert.False(fake.Returned.IsSet);
        Assert.True(fake.WorkerThread!.IsAlive);
        Assert.Equal("closed", fake.Worker.Execute(fake.Invoke, "closed", Guard));
        fake.Release.Set();
        Assert.True(fake.WorkerThread.Join(Guard));
        Assert.Equal(1, fake.Invocations);
        Assert.Equal(1, fake.Starts);
        Assert.Equal("closed", fake.Worker.Execute(fake.Invoke, "closed", Guard));
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(60001)]
    [InlineData(long.MaxValue / TimeSpan.TicksPerMillisecond)]
    public void InvalidTimeoutIsRejectedWithoutStartingOrRunning(long milliseconds)
    {
        var starts = 0;
        var operations = 0;
        using var worker = new SingleFlightOperationWorker("invalid-timeout", _ => starts++);
        Assert.Throws<ArgumentOutOfRangeException>(() => worker.Execute(
            () => ++operations, -1, TimeSpan.FromMilliseconds(milliseconds)));
        Assert.Equal(0, starts);
        Assert.Equal(0, operations);
    }

    [Fact]
    public void NullOperationIsRejectedWithoutStartingThread()
    {
        var starts = 0;
        using var worker = new SingleFlightOperationWorker("null-operation", _ => starts++);
        Assert.Throws<ArgumentNullException>(() => worker.Execute<int>(null!, -1, Guard));
        Assert.Equal(0, starts);
    }

    private sealed class BlockedFake : IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);
        public ManualResetEventSlim Returned { get; } = new(false);
        public SingleFlightOperationWorker Worker { get; }
        public Thread? WorkerThread { get; private set; }
        private int _starts;
        private int _invocations;
        public int Starts => Volatile.Read(ref _starts);
        public int Invocations => Volatile.Read(ref _invocations);

        public BlockedFake()
        {
            Worker = new SingleFlightOperationWorker("blocked-fake", thread =>
            {
                WorkerThread = thread;
                Interlocked.Increment(ref _starts);
            });
        }

        public string Invoke()
        {
            Interlocked.Increment(ref _invocations);
            Entered.Set();
            try
            {
                if (!Release.Wait(Guard))
                    throw new TimeoutException("Test did not release the blocked fake.");
                return "late-success";
            }
            finally
            {
                Returned.Set();
            }
        }

        public void Dispose()
        {
            Worker.Dispose();
            Release.Set();
            // Always unblock and join before disposing events, including on failure.
            Assert.True(WorkerThread is null || WorkerThread.Join(Guard));
            Entered.Dispose();
            Release.Dispose();
            Returned.Dispose();
        }
    }
}

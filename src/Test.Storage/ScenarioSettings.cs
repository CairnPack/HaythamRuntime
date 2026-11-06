using Haytham.Runtime;
using Haytham.Runtime.Tasks;
using Haytham.Runtime.Work;

namespace Test.Storage;

public sealed class ScenarioSettings {
    public bool VetoFirstStop { get; init; } = true;
    public bool ParallelFailFast { get; init; }
    public bool FailOnStarting { get; init; }
    public bool MergeErrors { get; init; }
    public StopDecisionProbe? StopDecision { get; init; }
    public LifecycleProbe? Lifecycle { get; init; }
    public StartingProbe? Starting { get; init; }
    public ContinuousRetryProbe? ContinuousRetry { get; init; }
    public CleanupFailureProbe? CleanupFailure { get; init; }
    public ContinuousStopSignalsProbe? ContinuousStopSignals { get; init; }
    public TaskExitPolicy SnapshotExitPolicy { get; set; } = TaskExitPolicy.Wait;
}

public sealed class CleanupFailureProbe {
    public bool ReleaseExecuted { get; internal set; }
    public Exception ExpectedFailure { get; } = new InvalidOperationException("Test 模块卸载故意失败，仍持有 Storage 资源。");
}

public sealed class ContinuousStopSignalsProbe {
    public List<ContinuousWorkHandle> Handles { get; } = [];
    public TaskCompletionSource[] StopSignals { get; } = [
        new(TaskCreationOptions.RunContinuationsAsynchronously),
        new(TaskCreationOptions.RunContinuationsAsynchronously)
    ];

    internal async ValueTask ExecuteAsync(int index, ContinuousWorkContext context) {
        using var registration = context.Stopping.Register(() => StopSignals[index].TrySetResult());
        context.ReportReady();
        await StopSignals[index].Task;
        // 两项工作相互依赖，只有全部收到停止信号后才允许结束。
        await StopSignals[1 - index].Task;
    }
}

public sealed class StopDecisionProbe {
    public Exception ExpectedFailure { get; } = new InvalidOperationException("停止判定故意失败。");
}

public sealed class LifecycleProbe {
    public List<ModuleId> Loaded { get; } = [];
    public List<ModuleId> Started { get; } = [];
    public List<ModuleId> Stopping { get; } = [];
    public List<ModuleId> Unloaded { get; } = [];
    public Exception ExpectedFailure { get; } = new InvalidOperationException("Test 模块启动故意失败。");
}

public sealed class StartingProbe {
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class ContinuousRetryProbe {
    private int runCount;

    public ContinuousWorkHandle InitialHandle { get; internal set; } = null!;
    public TaskCompletionSource Fail { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<CancellationToken> RetryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource StopRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseStop { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Exception ExpectedFailure { get; } = new InvalidOperationException("持续工作就绪后故意失败。");
    public int RunCount => Volatile.Read(ref runCount);

    internal async ValueTask ExecuteAsync(ContinuousWorkContext context) {
        var run = Interlocked.Increment(ref runCount);
        context.ReportReady();
        if (run == 1) {
            await Fail.Task;
            throw ExpectedFailure;
        }

        using var registration = context.Stopping.Register(() => StopRequested.TrySetResult());
        RetryStarted.SetResult(context.Stopping);
        await StopRequested.Task;
        await ReleaseStop.Task;
        Finished.SetResult();
    }
}

public sealed class ParallelProbe {
    public TaskCompletionSource SlowStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseSlow { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource SlowFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<RuntimeExecutionException> PublishFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource PublisherReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Exception ExpectedFailure { get; } = new InvalidOperationException("快订阅者故意失败。");
}

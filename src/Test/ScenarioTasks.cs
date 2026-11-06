using Haytham.Runtime.Tasks;
using Test.Storage;

namespace Test;

internal enum CancellationBehavior {
    Cancel,
    Fail,
    Complete
}

internal sealed class ScenarioTaskInput {
    private int executionCount;

    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal CancellationBehavior Behavior { get; init; }
    internal Action? OnStart { get; init; }
    internal Exception ExpectedFailure { get; } = new InvalidOperationException("收到取消后故意失败。");
    internal int ExecutionCount => Volatile.Read(ref executionCount);

    internal async ValueTask<int> ExecuteAsync(CancellationToken cancellation) {
        Interlocked.Increment(ref executionCount);
        OnStart?.Invoke();
        Started.SetResult();
        if (Behavior == CancellationBehavior.Cancel) {
            try {
                await Release.Task.WaitAsync(cancellation);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
                CancellationObserved.TrySetResult();
                throw;
            }
        }
        else {
            using var registration = cancellation.Register(() => CancellationObserved.TrySetResult());
            await Release.Task;
            if (Behavior == CancellationBehavior.Fail) {
                throw ExpectedFailure;
            }
        }

        return 42;
    }
}

internal sealed partial class ScenarioWaitTask : RuntimeTask<ScenarioTaskInput, int> {
    public override TaskExitPolicy ExitPolicy => TaskExitPolicy.Wait;

    protected override ValueTask<int> ExecuteAsync(TaskEvent<ScenarioTaskInput, int> e) => e.Input.ExecuteAsync(e.Cancellation);
}

internal sealed partial class ScenarioCancelTask : RuntimeTask<ScenarioTaskInput, int> {
    public override TaskExitPolicy ExitPolicy => TaskExitPolicy.CancelOnExit;

    protected override ValueTask<int> ExecuteAsync(TaskEvent<ScenarioTaskInput, int> e) => e.Input.ExecuteAsync(e.Cancellation);
}

internal sealed partial class ScenarioPolicyTask(ScenarioSettings settings) : RuntimeTask<ScenarioTaskInput, int> {
    public override TaskExitPolicy ExitPolicy => settings.SnapshotExitPolicy;

    protected override ValueTask<int> ExecuteAsync(TaskEvent<ScenarioTaskInput, int> e) => e.Input.ExecuteAsync(e.Cancellation);
}

internal sealed partial class ScenarioReportTask : RuntimeTask<ReportBoundaryProbe, int> {
    public override TaskExitPolicy ExitPolicy => TaskExitPolicy.Wait;

    protected override ValueTask<int> ExecuteAsync(TaskEvent<ReportBoundaryProbe, int> e) {
        e.Input.SavedOutput = e.Output;
        e.Input.Records.Enqueue("调用前");
        e.Output.ReportProgress(new TaskProgress("报告边界"));
        e.Input.Records.Enqueue("调用后");
        return ValueTask.FromResult(42);
    }
}

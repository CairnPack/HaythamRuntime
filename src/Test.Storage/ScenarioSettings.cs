using Haytham.Runtime;

namespace Test.Storage;

public sealed class ScenarioSettings {
    public bool VetoFirstStop { get; init; } = true;
    public bool ParallelFailFast { get; init; }
}

public sealed class ParallelProbe {
    public TaskCompletionSource SlowStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseSlow { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource SlowFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<RuntimeExecutionException> PublishFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource PublisherReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Exception ExpectedFailure { get; } = new InvalidOperationException("快订阅者故意失败。");
}

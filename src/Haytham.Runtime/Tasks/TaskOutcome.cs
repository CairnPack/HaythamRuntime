namespace Haytham.Runtime.Tasks;

public enum TaskExecutionState {
    Submitted,
    Queued,
    Running,
    Completing,
    Succeeded,
    Failed,
    Canceled
}

public abstract record TaskOutcome<TResult> {
    private protected TaskOutcome() { }

    public sealed record Succeeded(TResult Result) : TaskOutcome<TResult>;
    public sealed record Failed(IReadOnlyList<RuntimeFailure> Failures) : TaskOutcome<TResult>;
    public sealed record Canceled : TaskOutcome<TResult>;
}

public sealed record TaskCompletion<TResult>(TaskOutcome<TResult> Business, RuntimeFailure? OutputFailure);

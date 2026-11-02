namespace Haytham.Runtime.Tasks;

public sealed class TaskRun<TResult> {
    private readonly Action requestCancellation;
    private readonly TaskCompletionSource<TaskCompletion<TResult>> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int state = (int)TaskExecutionState.Submitted;

    internal TaskRun(ExecutionId id, Action requestCancellation) {
        Id = id;
        this.requestCancellation = requestCancellation;
    }

    public ExecutionId Id { get; }
    public TaskExecutionState State => (TaskExecutionState)Volatile.Read(ref state);
    public Task<TaskCompletion<TResult>> Completion => completion.Task;

    public void RequestCancellation() => requestCancellation();

    internal void SetState(TaskExecutionState value) => Volatile.Write(ref state, (int)value);
    internal void Complete(TaskCompletion<TResult> result) => completion.SetResult(result);
}

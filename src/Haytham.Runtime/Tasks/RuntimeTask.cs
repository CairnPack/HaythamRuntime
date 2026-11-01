namespace Haytham.Runtime.Tasks;

public abstract class RuntimeTask {
    private protected RuntimeTask() { }

    public virtual string Name => GetType().Name;
    public abstract TaskExitPolicy ExitPolicy { get; }
}

public abstract class RuntimeTask<TInput, TResult> : RuntimeTask {
    protected RuntimeTask() { }

    protected abstract ValueTask<TResult> ExecuteAsync(TaskEvent<TInput, TResult> e);

    internal ValueTask<TResult> ExecuteCoreAsync(TaskEvent<TInput, TResult> e) => ExecuteAsync(e);
}

public enum TaskExitPolicy {
    Wait,
    CancelOnExit
}

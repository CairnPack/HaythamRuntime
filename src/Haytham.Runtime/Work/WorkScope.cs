using Haytham.Runtime.Events;
using Haytham.Runtime.Tasks;

namespace Haytham.Runtime.Work;

public sealed class WorkScope {
    private readonly WorkNode node;

    internal WorkScope(WorkNode node) => this.node = node;

    public WorkHandle Run(string name, Func<WorkContext, ValueTask> operation) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(operation);
        return new WorkHandle(node.RunChild(name, node.Module, async child => {
            await operation(new WorkContext(child));
            return true;
        }));
    }

    public WorkHandle<T> Run<T>(string name, Func<WorkContext, ValueTask<T>> operation) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(operation);
        return new WorkHandle<T>(node.RunChild(name, node.Module, child => operation(new WorkContext(child))));
    }

    public ValueTask PublishAsync<TEvent>(TEvent e) where TEvent : RuntimeEvent {
        ArgumentNullException.ThrowIfNull(e);
        node.Host.Events.CheckPublish(node.Module, e);
        return new ValueTask(node.RunChild(e.GetType().Name, node.Module, async child => {
            await node.Host.Events.DispatchAsync(e, child);
            return true;
        }));
    }

    public ValueTask<TResult> InvokeAsync<TTask, TInput, TResult>(TInput input)
        where TTask : RuntimeTask<TInput, TResult> {
        var registration = node.Host.Events.GetTask<TTask, TInput, TResult>();
        return new ValueTask<TResult>(node.RunChild(registration.Task.Name, registration.Module,
            child => node.Host.Events.ExecuteTaskAsync((RuntimeTask<TInput, TResult>)registration.Task, input, child)));
    }
}

public sealed class WorkContext {
    internal WorkContext(WorkNode node) {
        Cancellation = node.Cancellation;
        Work = new WorkScope(node);
        Output = node.Output;
    }

    public CancellationToken Cancellation { get; }
    public WorkScope Work { get; }
    public TaskOutput? Output { get; }
}

public sealed class WorkHandle {
    internal WorkHandle(Task completion) => Completion = completion;
    public Task Completion { get; }
}

public sealed class WorkHandle<T> {
    internal WorkHandle(Task<T> completion) => Completion = completion;
    public Task<T> Completion { get; }
}

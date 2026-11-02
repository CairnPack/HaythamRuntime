using Haytham.Runtime.Events;
using Haytham.Runtime.Tasks;

namespace Haytham.Runtime.Modules;

public sealed class ModuleWorkPort {
    private readonly RuntimeHost host;
    private readonly ModuleId module;

    internal ModuleWorkPort(RuntimeHost host, ModuleId module) {
        this.host = host;
        this.module = module;
    }

    public ValueTask PublishAsync<TEvent>(TEvent e, CancellationToken cancellation = default)
        where TEvent : RuntimeEvent => host.PublishRootAsync(module, e, cancellation);

    public TaskRun<TResult> Submit<TTask, TInput, TResult>(
        TInput input,
        string? queueKey = null,
        ITaskOutputSink<TResult>? output = null,
        CancellationToken cancellation = default)
        where TTask : RuntimeTask<TInput, TResult> => host.Submit<TTask, TInput, TResult>(input, queueKey, output, cancellation);
}

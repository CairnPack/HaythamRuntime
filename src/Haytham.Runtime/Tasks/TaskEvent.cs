using Haytham.Runtime.Events;
using Haytham.Runtime.Work;

namespace Haytham.Runtime.Tasks;

public sealed class TaskEvent<TInput, TResult> : RuntimeEvent {
    internal TaskEvent(TInput input, WorkNode node) {
        Input = input;
        ExecutionId = node.Id;
        Cancellation = node.Cancellation;
        Work = new WorkScope(node);
        Output = node.Output!;
    }

    public ExecutionId ExecutionId { get; }
    public TInput Input { get; }
    public CancellationToken Cancellation { get; }
    public WorkScope Work { get; }
    public TaskOutput Output { get; }
    internal override bool IsBuiltIn => true;
}

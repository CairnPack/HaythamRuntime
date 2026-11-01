using Haytham.Runtime.Work;

namespace Haytham.Runtime.Events;

public sealed class EventContext {
    internal EventContext(WorkNode node) {
        ExecutionId = node.Id;
        SubscriberModule = node.Module;
        Cancellation = node.Cancellation;
        Work = new WorkScope(node);
    }

    public ExecutionId ExecutionId { get; }
    public ModuleId SubscriberModule { get; }
    public CancellationToken Cancellation { get; }
    public WorkScope Work { get; }
}

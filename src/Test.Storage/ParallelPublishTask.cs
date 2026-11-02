using Haytham.Runtime;
using Haytham.Runtime.Events;
using Haytham.Runtime.Tasks;

namespace Test.Storage;

public sealed class ParallelProbeEvent(ParallelProbe probe) : RuntimeEvent {
    public ParallelProbe Probe { get; } = probe;
}

public sealed partial class ParallelPublishTask : RuntimeTask<ParallelProbe, bool> {
    public override TaskExitPolicy ExitPolicy => TaskExitPolicy.Wait;

    protected override async ValueTask<bool> ExecuteAsync(TaskEvent<ParallelProbe, bool> e) {
        try {
            await e.Work.PublishAsync(new ParallelProbeEvent(e.Input));
        }
        catch (RuntimeExecutionException error) {
            e.Input.PublishFailure.SetResult(error);
        }

        e.Input.PublisherReturned.SetResult();
        return true;
    }
}

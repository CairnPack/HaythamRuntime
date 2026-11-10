using Haytham.Runtime.Modules;
using Haytham.Runtime.Work;

namespace Haytham.Runtime;

public sealed partial class RuntimeHost {
    private readonly List<RuntimeFailure> cleanupFailures = [];

    internal void RegisterCleanup(ModuleId module, Func<ValueTask<CleanupResult>> cleanup) {
        WorkNode node;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Gate) {
            if (State is not (RuntimeState.Running or RuntimeState.PreStopping or RuntimeState.StopRequested
                or RuntimeState.Draining or RuntimeState.Stopping)) {
                throw new RuntimeContractException($"Runtime 当前状态为 {State}，不能登记清理。");
            }

            node = new WorkNode(this, module, CancellationToken.None, null) { Phase = "Cleanup" };
            RegisterRoot(node.Id, new RootExecution(completion.Task, static () => null));
        }

        using (ExecutionContext.SuppressFlow()) {
            _ = Task.Run(async () => {
                await RunCleanupAsync(node, cleanup);
                lock (Gate) {
                    completion.SetResult();
                    RemoveRoot(node.Id);
                }
            });
        }
    }

    private async Task RunCleanupAsync(WorkNode node, Func<ValueTask<CleanupResult>> cleanup) {
        var failures = new List<RuntimeFailure>();
        await node.ExecuteAsync(async _ => {
            try {
                var result = await cleanup();
                if (!result.IsDone) {
                    failures.Add(new RuntimeFailure(RuntimeFailureKind.Cleanup, result.Message!, Module: node.Module));
                }
            }
            catch (Exception error) {
                ObserveFailure(error, RuntimeFailureKind.Cleanup);
                failures.Add(new RuntimeFailure(RuntimeFailureKind.Cleanup, error.Message, error, node.Module));
            }

            return true;
        });

        lock (Gate) {
            cleanupFailures.AddRange(failures);
        }
    }

    internal IReadOnlyList<RuntimeFailure> TakeCleanupFailures() {
        lock (Gate) {
            var failures = cleanupFailures.ToArray();
            cleanupFailures.Clear();
            return failures;
        }
    }
}

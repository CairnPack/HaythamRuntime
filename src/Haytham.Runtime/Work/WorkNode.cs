using Haytham.Runtime.Tasks;

namespace Haytham.Runtime.Work;

internal sealed class WorkNode {
    private readonly object gate = new();
    private readonly List<Task> children = [];
    private readonly CancellationTokenSource cancellation;
    private readonly CancellationToken parentCancellation;
    private bool open = true;
    private bool finished;

    internal WorkNode(RuntimeHost host, ModuleId module, CancellationToken parentCancellation, TaskOutput? output) {
        Host = host;
        Module = module;
        this.parentCancellation = parentCancellation;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(parentCancellation);
        Cancellation = cancellation.Token;
        Output = output;
    }

    internal RuntimeHost Host { get; }
    internal ExecutionId Id { get; } = new(Guid.NewGuid());
    internal ModuleId Module { get; }
    internal CancellationToken Cancellation { get; }
    internal TaskOutput? Output { get; }

    internal Task<T> RunChild<T>(string name, ModuleId module, Func<WorkNode, ValueTask<T>> operation) {
        WorkNode child;
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) {
            if (!open) {
                throw new RuntimeContractException($"工作登记窗口已关闭，不能登记 {name}。");
            }

            child = new WorkNode(Host, module, Cancellation, Output);
            children.Add(completion.Task);
        }

        _ = RunAsync();
        return completion.Task;

        async Task RunAsync() {
            try {
                completion.SetResult(await child.ExecuteAsync(operation));
            }
            catch (OperationCanceledException error) {
                completion.SetCanceled(error.CancellationToken);
            }
            catch (Exception error) {
                completion.SetException(error);
            }
        }
    }

    internal async Task<T> ExecuteAsync<T>(Func<WorkNode, ValueTask<T>> operation) {
        var failures = new List<RuntimeFailure>();
        var canceled = false;
        T result = default!;
        try {
            result = await operation(this);
        }
        catch (OperationCanceledException error) when (IsEffectiveCancellation(error)) {
            canceled = true;
        }
        catch (Exception error) {
            AddFailure(error);
            // 失败后先请求后代取消，随后仍等待真实结束。
            try {
                CancelDescendants();
            }
            catch (Exception cancellationError) {
                failures.Add(new RuntimeFailure(RuntimeFailureKind.Cleanup, "请求后代取消失败。", cancellationError, Module));
            }
        }
        finally {
            lock (gate) {
                open = false;
            }
        }

        await WaitForDescendantsAsync(error => {
            if (error is OperationCanceledException) {
                canceled = true;
            }
            else {
                AddFailure(error);
            }
        });
        lock (gate) {
            finished = true;
            cancellation.Dispose();
        }

        if (failures.Count > 0) {
            throw new RuntimeExecutionException("受托管工作执行失败。",
                failures.Distinct<RuntimeFailure>(ReferenceEqualityComparer.Instance).ToArray());
        }

        if (canceled) {
            throw new OperationCanceledException(Cancellation);
        }

        return result;

        void AddFailure(Exception error) {
            if (error is RuntimeExecutionException execution) {
                failures.AddRange(execution.Failures);
            }
            else {
                failures.Add(new RuntimeFailure(
                    error is RuntimeContractException ? RuntimeFailureKind.Contract : RuntimeFailureKind.Execution,
                    error.Message, error, Module));
            }
        }
    }

    internal async Task WaitForDescendantsAsync(Action<Exception> onFailure) {
        foreach (var child in children) {
            try {
                await child;
            }
            catch (Exception error) {
                onFailure(error);
            }
        }
    }

    internal void CancelDescendants() => cancellation.Cancel();

    private bool IsEffectiveCancellation(OperationCanceledException error) {
        if (!Cancellation.IsCancellationRequested) {
            return false;
        }

        if (error.CancellationToken == Cancellation
            || (parentCancellation.IsCancellationRequested && error.CancellationToken == parentCancellation)) {
            return true;
        }

        return error is TaskCanceledException { Task: { } child } && children.Contains(child);
    }

    internal void RequestCancellation() {
        lock (gate) {
            if (!finished) {
                cancellation.Cancel();
            }
        }
    }
}

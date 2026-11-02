using Haytham.Runtime.Tasks;

namespace Haytham.Runtime.Work;

internal sealed class WorkNode {
    private readonly object gate = new();
    private readonly List<Task> children = [];
    private readonly CancellationTokenSource cancellation;
    private readonly CancellationToken parentCancellation;
    private readonly WorkNode? parent;

    private readonly TaskCompletionSource<RuntimeExecutionException> failure =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool open = true;
    private bool finished;

    internal WorkNode(RuntimeHost host, ModuleId module, CancellationToken parentCancellation, TaskOutput? output,
        WorkNode? parent = null) {
        Host = host;
        Module = module;
        this.parentCancellation = parentCancellation;
        this.parent = parent;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(parentCancellation);
        Cancellation = cancellation.Token;
        Output = output;
    }

    internal RuntimeHost Host { get; }
    internal ExecutionId Id { get; } = new(Guid.NewGuid());
    internal ModuleId Module { get; }
    internal CancellationToken Cancellation { get; }
    internal TaskOutput? Output { get; }

    internal Task<T> RunChild<T>(string name, ModuleId module, Func<WorkNode, ValueTask<T>> operation,
        TaskOutput? output = null) {
        var child = RegisterChild(name, module, operation, output);
        child.Start();
        return child.Completion;
    }

    internal (Task<T> Completion, Task<RuntimeExecutionException> Failure, Action Start) RegisterChild<T>(
        string name, ModuleId module, Func<WorkNode, ValueTask<T>> operation, TaskOutput? output = null) {
        WorkNode child;
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) {
            if (!open) {
                throw new RuntimeContractException($"工作登记窗口已关闭，不能登记 {name}。");
            }

            child = new WorkNode(Host, module, Cancellation, output ?? Output, this);
            children.Add(completion.Task);
        }

        return (completion.Task, child.failure.Task, () => { _ = RunAsync(); });

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
        Exception? operationFailure = null;
        T result = default!;
        Host.CurrentWork.Value = this;
        try {
            result = await operation(this);
        }
        catch (OperationCanceledException error) when (IsEffectiveCancellation(error)) {
            canceled = true;
        }
        catch (Exception error) {
            operationFailure = error;
        }
        finally {
            lock (gate) {
                open = false;
            }
        }

        if (operationFailure is not null) {
            AddFailure(operationFailure);
            CancelAfterFailure();
        }

        // 登记窗口已关闭，按完成顺序收集，避免较早登记的工作阻挡故障收尾。
        await foreach (var child in Task.WhenEach(children)) {
            try {
                await child;
            }
            catch (OperationCanceledException) when (child.IsCanceled) {
                canceled = true;
            }
            catch (Exception error) {
                AddFailure(error);
                CancelAfterFailure();
            }
        }

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
            IReadOnlyList<RuntimeFailure> added;
            if (error is RuntimeExecutionException execution) {
                added = execution.Failures;
            }
            else {
                added = [
                    new RuntimeFailure(
                        error is RuntimeContractException ? RuntimeFailureKind.Contract : RuntimeFailureKind.Execution,
                        error.Message, error, Module)
                ];
            }

            failures.AddRange(added);
            failure.TrySetResult(new RuntimeExecutionException("受托管工作执行失败。", added));
        }

        void CancelAfterFailure() {
            try {
                // 取消请求不能代替等待后代真实结束。
                RequestCancellation();
            }
            catch (Exception cancellationError) {
                failures.Add(new RuntimeFailure(RuntimeFailureKind.Cleanup, "请求后代取消失败。", cancellationError, Module));
            }
        }
    }

    private bool IsEffectiveCancellation(OperationCanceledException error) {
        for (var source = this; source is not null; source = source.parent) {
            if ((source.Cancellation.IsCancellationRequested && error.CancellationToken == source.Cancellation)
                || (source.parentCancellation.IsCancellationRequested &&
                    error.CancellationToken == source.parentCancellation)) {
                return true;
            }
        }

        lock (gate) {
            return error is TaskCanceledException { Task: { IsCanceled: true } child } && children.Contains(child);
        }
    }

    internal void RequestCancellation() {
        lock (gate) {
            if (!finished) {
                cancellation.Cancel();
            }
        }
    }
}

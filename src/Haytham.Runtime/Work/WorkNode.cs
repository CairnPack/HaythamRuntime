using Haytham.Runtime.Tasks;
using Haytham.Runtime.Logging;
using Microsoft.Extensions.Logging;

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
    internal string? TaskType { get; set; }
    internal string? EventType { get; set; }
    internal string? Phase { get; set; }
    private ExecutionId RootId => parent?.RootId ?? Id;

    internal IDisposable? BeginLogScope() {
        var fields = new Dictionary<string, object?> {
            ["RuntimeId"] = Host.RuntimeId, ["ModuleId"] = Module.Value,
            ["ExecutionId"] = Id.Value, ["RootExecutionId"] = RootId.Value
        };
        if (TaskType is not null) fields.Add("TaskType", TaskType);
        if (EventType is not null) fields.Add("EventType", EventType);
        if (Phase is not null) fields.Add("Phase", Phase);
        return Host.Logger.BeginScope(fields);
    }

    internal Task<T> RunChild<T>(string name, ModuleId module, Func<WorkNode, ValueTask<T>> operation,
        TaskOutput? output = null, string? taskType = null, string? eventType = null) {
        var child = RegisterChild(name, module, operation, output, taskType, eventType);
        child.Start();
        return child.Completion;
    }

    internal (Task<T> Completion, Task<RuntimeExecutionException> Failure, Action Start) RegisterChild<T>(
        string name, ModuleId module, Func<WorkNode, ValueTask<T>> operation, TaskOutput? output = null,
        string? taskType = null, string? eventType = null) {
        WorkNode child;
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) {
            if (!open) {
                throw new RuntimeContractException($"工作登记窗口已关闭，不能登记 {name}。");
            }

            child = new WorkNode(Host, module, Cancellation, output ?? Output, this) {
                TaskType = taskType, EventType = eventType, Phase = Phase
            };
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
        using var scope = BeginLogScope();
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

        if (TaskType is not null && parent is not null) {
            RuntimeLog.TaskFinished(Host.Logger, failures.Count > 0 ? LogLevel.Error : LogLevel.Information,
                failures.Count > 0 ? "Failed" : canceled ? "Canceled" : "Succeeded",
                failures.Distinct<RuntimeFailure>(ReferenceEqualityComparer.Instance).Count(), false);
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
                Host.ObserveFailure(error, error is RuntimeContractException ? RuntimeFailureKind.Contract : RuntimeFailureKind.Execution);
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
                if (cancellationError is not RuntimeExecutionException) Host.ObserveFailure(cancellationError, RuntimeFailureKind.Cleanup);
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

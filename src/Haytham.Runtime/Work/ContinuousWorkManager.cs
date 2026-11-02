using Haytham.Runtime.Modules;

namespace Haytham.Runtime.Work;

internal sealed class ContinuousWorkManager(RuntimeHost host) {
    private readonly Dictionary<(ModuleId Module, string Name), Run> runs = [];
    private readonly List<(ModuleId Module, Task Execution)> notifications = [];

    internal ModuleWorkScope OpenScope(ModuleId module) => new(this, module);

    internal ContinuousWorkHandle Start(ModuleId module, string name, Func<ContinuousWorkContext, ValueTask> operation) {
        lock (host.Gate) {
            if (runs.ContainsKey((module, name))) {
                throw new RuntimeContractException($"模块 {module} 已登记持续工作 {name}。");
            }

            var run = new Run(module, name, operation);
            runs.Add((module, name), run);
            run.Execution = Task.Run(() => ExecuteAsync(run));
            return run.Handle;
        }
    }

    internal async Task WaitReadyAsync() {
        await foreach (var ready in Task.WhenEach(runs.Values.Select(static run => run.Handle.Ready))) {
            await ready;
        }
    }

    internal void ConfirmReady() {
        foreach (var run in runs.Values) {
            if (!run.Handle.Ready.IsCompletedSuccessfully) {
                throw new RuntimeContractException("持续工作尚未就绪。");
            }

            if (run.Handle.Completion.IsCompletedSuccessfully && run.Handle.Completion.Result.Kind == ContinuousWorkOutcomeKind.Failed) {
                throw new RuntimeExecutionException("持续工作不再满足启动条件。", run.Handle.Completion.Result.Failures);
            }
        }
    }

    internal async Task StopAllAsync() {
        Run[] current;
        (ModuleId Module, Task Execution)[] pendingNotifications;
        lock (host.Gate) {
            current = runs.Values.ToArray();
            pendingNotifications = notifications.ToArray();
            notifications.Clear();
        }

        var failures = new List<RuntimeFailure>();
        foreach (var run in current.Where(static run => !run.Handle.Completion.IsCompleted)) {
            try {
                run.Handle.RequestStop();
            }
            catch (Exception error) {
                failures.Add(new RuntimeFailure(RuntimeFailureKind.Cleanup, "请求持续工作停止失败。", error, run.Handle.Module));
            }
        }

        foreach (var notification in current.Select(static run => (run.Handle.Module, run.Execution)).Concat(pendingNotifications)) {
            try {
                await notification.Execution;
            }
            catch (Exception error) {
                failures.Add(new RuntimeFailure(RuntimeFailureKind.Execution, "持续工作故障通知失败。", error, notification.Module));
            }
        }

        if (failures.Count > 0) {
            throw new RuntimeExecutionException("持续工作收尾失败。", failures);
        }
    }

    internal ContinuousWorkHandle Retry(ModuleId module, string name) {
        if (!runs.TryGetValue((module, name), out var previous)) {
            throw new RuntimeContractException($"模块 {module} 未登记持续工作 {name}。");
        }

        if (!previous.Handle.Completion.IsCompleted) {
            throw new RuntimeContractException($"模块 {module} 的持续工作 {name} 尚未结束。");
        }

        // 通知晚于 Completion，重试替换记录时仍须保留未结束或失败的通知路径。
        notifications.RemoveAll(static notification => notification.Execution.IsCompletedSuccessfully);
        if (!previous.Execution.IsCompletedSuccessfully) {
            notifications.Add((module, previous.Execution));
        }

        var run = new Run(module, name, previous.Operation);
        runs[(module, name)] = run;
        run.Execution = Task.Run(() => ExecuteAsync(run));
        return run.Handle;
    }

    internal void NotifyFailure(ContinuousWorkFailure failure) => host.NotifyContinuousWorkFailure(failure);

    private async Task ExecuteAsync(Run run) {
        var failures = new List<RuntimeFailure>();
        try {
            var context = new ContinuousWorkContext(run.Stopping.Token, new ModuleWorkPort(host, run.Handle.Module),
                run.ReportReady);
            await run.Operation(context);
        }
        catch (OperationCanceledException error) when (run.Stopping.IsCancellationRequested && error.CancellationToken == run.Stopping.Token) {
        }
        catch (Exception error) {
            failures.Add(new RuntimeFailure(RuntimeFailureKind.Execution, error.Message, error, run.Handle.Module));
        }

        ContinuousWorkOutcome outcome;
        lock (run.Gate) {
            if (!run.Ready.Task.IsCompleted) {
                var failure = new RuntimeFailure(RuntimeFailureKind.Execution, "持续工作在就绪前结束。", Module: run.Handle.Module);
                run.Ready.TrySetException(new RuntimeExecutionException(failure.Message, failures.Count > 0 ? failures : [failure]));
                if (!run.Stopping.IsCancellationRequested && failures.Count == 0) {
                    failures.Add(failure);
                }
            }

            outcome = new ContinuousWorkOutcome(failures.Count > 0 ? ContinuousWorkOutcomeKind.Failed
                : run.Stopping.IsCancellationRequested ? ContinuousWorkOutcomeKind.Stopped : ContinuousWorkOutcomeKind.Completed, failures);
            run.Finished = true;
            run.Stopping.Dispose();
        }

        lock (host.Gate) {
            run.Completion.SetResult(outcome);
        }

        foreach (var failure in failures) {
            NotifyFailure(new ContinuousWorkFailure(run.Handle.Module, run.Handle.Name, failure));
        }
    }

    private sealed class Run {
        internal Run(ModuleId module, string name, Func<ContinuousWorkContext, ValueTask> operation) {
            Operation = operation;
            Handle = new ContinuousWorkHandle(module, name, Ready.Task, Completion.Task, RequestStop);
        }

        internal object Gate { get; } = new();
        internal Func<ContinuousWorkContext, ValueTask> Operation { get; }
        internal ContinuousWorkHandle Handle { get; }
        internal CancellationTokenSource Stopping { get; } = new();
        internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<ContinuousWorkOutcome> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Execution { get; set; } = null!;
        internal bool Finished { get; set; }

        internal void ReportReady() {
            lock (Gate) {
                Ready.TrySetResult();
            }
        }

        private void RequestStop() {
            lock (Gate) {
                if (!Finished) {
                    Stopping.Cancel();
                }
            }
        }
    }
}

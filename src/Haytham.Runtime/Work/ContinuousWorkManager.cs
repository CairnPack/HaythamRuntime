using Haytham.Runtime.Modules;

namespace Haytham.Runtime.Work;

internal sealed class ContinuousWorkManager(RuntimeHost host) {
    private readonly Dictionary<(ModuleId Module, string Name), Run> runs = [];

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

    internal Task WaitReadyAsync() => Task.WhenAll(runs.Values.Select(static run => run.Handle.Ready));

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
        var current = runs.Values.ToArray();
        var failures = new List<RuntimeFailure>();
        foreach (var run in current) {
            try {
                run.Handle.RequestStop();
            }
            catch (Exception error) {
                failures.Add(new RuntimeFailure(RuntimeFailureKind.Cleanup, "请求持续工作停止失败。", error, run.Handle.Module));
            }
        }

        foreach (var run in current) {
            try {
                await run.Execution;
            }
            catch (Exception error) {
                failures.Add(new RuntimeFailure(RuntimeFailureKind.Execution, "持续工作故障通知失败。", error, run.Handle.Module));
            }
        }

        if (failures.Count > 0) {
            throw new RuntimeExecutionException("持续工作收尾失败。", failures);
        }
    }

    internal ContinuousWorkHandle Retry(ModuleId module, string name) =>
        throw new NotImplementedException("持续工作故障收尾和重试尚未实现。");

    internal void NotifyFailure(ContinuousWorkFailure failure) => host.NotifyContinuousWorkFailure(failure);

    private async Task ExecuteAsync(Run run) {
        var failures = new List<RuntimeFailure>();
        try {
            var context = new ContinuousWorkContext(run.Stopping.Token, new ModuleWorkPort(host, run.Handle.Module),
                () => run.Ready.TrySetResult());
            await run.Operation(context);
        }
        catch (OperationCanceledException error) when (run.Stopping.IsCancellationRequested && error.CancellationToken == run.Stopping.Token) {
        }
        catch (Exception error) {
            failures.Add(new RuntimeFailure(RuntimeFailureKind.Execution, error.Message, error, run.Handle.Module));
        }

        if (!run.Ready.Task.IsCompleted) {
            var failure = new RuntimeFailure(RuntimeFailureKind.Execution, "持续工作在就绪前结束。", Module: run.Handle.Module);
            run.Ready.TrySetException(new RuntimeExecutionException(failure.Message, failures.Count > 0 ? failures : [failure]));
            if (!run.Stopping.IsCancellationRequested && failures.Count == 0) {
                failures.Add(failure);
            }
        }

        lock (run.Gate) {
            var outcome = new ContinuousWorkOutcome(failures.Count > 0 ? ContinuousWorkOutcomeKind.Failed
                : run.Stopping.IsCancellationRequested ? ContinuousWorkOutcomeKind.Stopped : ContinuousWorkOutcomeKind.Completed, failures);
            run.Completion.SetResult(outcome);
            run.Stopping.Dispose();
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

        private void RequestStop() {
            lock (Gate) {
                if (!Completion.Task.IsCompleted) {
                    Stopping.Cancel();
                }
            }
        }
    }
}

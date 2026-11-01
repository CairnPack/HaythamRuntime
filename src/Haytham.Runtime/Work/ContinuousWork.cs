using Haytham.Runtime.Modules;

namespace Haytham.Runtime.Work;

public sealed class ModuleWorkScope {
    private readonly ContinuousWorkManager manager;
    private readonly ModuleId module;
    private bool open = true;

    internal ModuleWorkScope(ContinuousWorkManager manager, ModuleId module) {
        this.manager = manager;
        this.module = module;
    }

    public ContinuousWorkHandle Start(string name, Func<ContinuousWorkContext, ValueTask> operation) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(operation);
        if (!open) {
            throw new RuntimeContractException("持续工作初始登记窗口已关闭。");
        }

        return manager.Start(module, name, operation);
    }

    internal void Close() => open = false;
}

public sealed class ContinuousWorkContext {
    private readonly Action reportReady;

    internal ContinuousWorkContext(CancellationToken stopping, ModuleWorkPort rootWork, Action reportReady) {
        Stopping = stopping;
        RootWork = rootWork;
        this.reportReady = reportReady;
    }

    public CancellationToken Stopping { get; }
    public ModuleWorkPort RootWork { get; }

    public void ReportReady() => reportReady();
}

public sealed class ContinuousWorkHandle {
    private readonly Action requestStop;

    internal ContinuousWorkHandle(ModuleId module, string name, Task ready, Task<ContinuousWorkOutcome> completion, Action requestStop) {
        Module = module;
        Name = name;
        Ready = ready;
        Completion = completion;
        this.requestStop = requestStop;
    }

    public ModuleId Module { get; }
    public string Name { get; }
    public Task Ready { get; }
    public Task<ContinuousWorkOutcome> Completion { get; }

    public void RequestStop() => requestStop();
}

public enum ContinuousWorkOutcomeKind {
    Completed,
    Stopped,
    Failed
}

public sealed record ContinuousWorkOutcome(ContinuousWorkOutcomeKind Kind, IReadOnlyList<RuntimeFailure> Failures);
public sealed record ContinuousWorkFailure(ModuleId Module, string WorkName, RuntimeFailure Failure);

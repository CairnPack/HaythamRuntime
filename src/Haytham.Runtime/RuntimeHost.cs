using Haytham.Runtime.Events;
using Haytham.Runtime.Modules;
using Haytham.Runtime.Tasks;
using Haytham.Runtime.Work;

namespace Haytham.Runtime;

public sealed partial class RuntimeHost {
    private static int creationState;
    private readonly RuntimeLifecycle lifecycle;
    private readonly RuntimeTaskScheduler tasks;
    private readonly Dictionary<ExecutionId, RootExecution> roots = [];
    private readonly HashSet<ModuleId> available = [];
    private bool rootAdmission;
    private int state = (int)RuntimeState.Created;

    private RuntimeHost(IReadOnlyList<ModuleDefinition> definitions, RuntimeCapabilities capabilities) {
        lifecycle = new RuntimeLifecycle(this, definitions, capabilities);
        tasks = new RuntimeTaskScheduler(this);
        ContinuousWork = new ContinuousWorkManager(this);
    }

    public RuntimeState State => (RuntimeState)Volatile.Read(ref state);
    public event Action<ContinuousWorkFailure>? ContinuousWorkFailed;

    internal object Gate { get; } = new();
    internal EventDispatcher Events { get; private set; } = null!;
    internal ContinuousWorkManager ContinuousWork { get; }

    public static RuntimeHost Create(IReadOnlyList<ModuleDefinition> definitions, RuntimeCapabilities capabilities) {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (Interlocked.CompareExchange(ref creationState, 1, 0) != 0) {
            throw new RuntimeAlreadyCreatedException("本进程正在创建或已经创建过 RuntimeHost。");
        }

        try {
            var snapshot = definitions.Select(static definition => definition with {
                Authors = definition.Authors.ToArray(),
                Dependencies = definition.Dependencies.ToArray(),
                RequiredCapabilities = definition.RequiredCapabilities.ToArray()
            }).ToArray();
            var capabilitySnapshot = capabilities.Snapshot();
            var ordered = ModuleOrder.Sort(snapshot);
            var missing = ordered.SelectMany(definition => definition.RequiredCapabilities
                .Where(type => !capabilitySnapshot.Contains(type))
                .Select(type => $"模块 {definition.Id} 需要 {type.FullName}。")).ToArray();
            if (missing.Length > 0) {
                throw new RuntimeConfigurationException("能力表缺少构造参数，没有创建任何模块。", missing);
            }

            var host = new RuntimeHost(ordered, capabilitySnapshot);
            Volatile.Write(ref creationState, 2);
            return host;
        }
        catch {
            Volatile.Write(ref creationState, 0);
            throw;
        }
    }

    public Task StartAsync() => lifecycle.StartAsync();
    public Task PreStopAsync() => lifecycle.PreStopAsync();
    public void CancelPreStop() => lifecycle.CancelPreStop();
    public Task<StopOutcome> StopAsync() => lifecycle.StopAsync();
    public void Kill() => lifecycle.Kill();

    public TaskRun<TResult> Submit<TTask, TInput, TResult>(TInput input, string? queueKey = null,
        ITaskOutputSink<TResult>? output = null, CancellationToken cancellation = default)
        where TTask : RuntimeTask<TInput, TResult> {
        if (Events is null) {
            throw new RuntimeContractException("Runtime 尚未启动，不能提交 Task。");
        }

        return tasks.Submit<TTask, TInput, TResult>(input, queueKey, output, cancellation);
    }

    public ContinuousWorkHandle RetryContinuousWork(ModuleId module, string name) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (Gate) {
            if (State != RuntimeState.Running) {
                throw new RuntimeContractException("只有运行中的宿主可以重试持续工作。");
            }

            return ContinuousWork.Retry(module, name);
        }
    }

    internal void InitializeEvents() => Events = new EventDispatcher(this);
    internal void SetState(RuntimeState value) => Volatile.Write(ref state, (int)value);

    internal void SetAdmission(bool open) {
        lock (Gate) {
            rootAdmission = open;
        }
    }

    internal void RequireRootAdmission() {
        if (!rootAdmission) {
            throw new RuntimeContractException($"Runtime 当前状态为 {State}，不接收新的根工作。");
        }
    }

    internal void RegisterRoot(ExecutionId id, RootExecution root) => roots.Add(id, root);
    internal void RemoveRoot(ExecutionId id) => roots.Remove(id);

    internal RootExecution[] SnapshotRoots() {
        lock (Gate) {
            return roots.Values.ToArray();
        }
    }

    internal bool IsModuleAvailable(ModuleId module) {
        lock (Gate) {
            return available.Contains(module);
        }
    }

    internal void SetModuleAvailable(ModuleId module, bool value) {
        lock (Gate) {
            if (value) {
                available.Add(module);
            }
            else {
                available.Remove(module);
            }
        }
    }

    internal ValueTask PublishRootAsync<TEvent>(ModuleId module, TEvent e, CancellationToken cancellation) where TEvent : RuntimeEvent {
        ArgumentNullException.ThrowIfNull(e);
        Events.CheckPublish(module, e);
        WorkNode node;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Gate) {
            RequireRootAdmission();
            node = new WorkNode(this, module, cancellation, null);
            RegisterRoot(node.Id, new RootExecution(completion.Task, static () => null));
        }

        _ = RunAsync();
        return new ValueTask(completion.Task);

        async Task RunAsync() {
            Exception? failure = null;
            try {
                await node.ExecuteAsync(async current => {
                    await Events.DispatchAsync(e, current);
                    return true;
                });
            }
            catch (Exception error) {
                failure = error;
            }

            lock (Gate) {
                if (failure is null) {
                    completion.SetResult();
                }
                else if (failure is OperationCanceledException canceled) {
                    completion.SetCanceled(canceled.CancellationToken);
                }
                else {
                    completion.SetException(failure);
                }

                RemoveRoot(node.Id);
            }
        }
    }

    internal void NotifyContinuousWorkFailure(ContinuousWorkFailure failure) => ContinuousWorkFailed?.Invoke(failure);
}

internal sealed record RootExecution(Task Completion, Func<RuntimeFailure?> GetOutputFailure);

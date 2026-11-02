using System.Collections.Frozen;
using Haytham.Runtime.Modules;
using Haytham.Runtime.Tasks;
using Haytham.Runtime.Work;

namespace Haytham.Runtime.Events;

internal sealed class EventDispatcher(RuntimeHost host, IReadOnlyList<ModuleDefinition> modules) {
    private readonly Dictionary<Type, EventRegistration> builtIns = [];
    private readonly List<EventSubscription> registerHandlers = [];
    private readonly Dictionary<ModuleId, HashSet<ModuleId>> dependencies = [];
    private EventSubscription[] registrationPlan = [];
    private DispatchPlan? plan;
    private bool modulesLocked;

    internal bool IsLocked => Volatile.Read(ref plan) is not null;

    internal void RegisterBuiltIns() {
        Add<RuntimeEvents.OnRegistering>(EventDispatchMode.SerialFailFast);
        Add<RuntimeEvents.OnLoading>(EventDispatchMode.SerialFailFast);
        Add<RuntimeEvents.OnStarting>(EventDispatchMode.SerialFailFast);
        Add<RuntimeEvents.OnStopRequested>(EventDispatchMode.SerialCollect);
        Add<RuntimeEvents.OnStopping>(EventDispatchMode.SerialCollect);
        Add<RuntimeEvents.OnUnloading>(EventDispatchMode.SerialCollect);
        // TaskEvent 族通过 ExecuteTaskAsync 的类型化适配器执行，不开放业务订阅。

        void Add<TEvent>(EventDispatchMode mode) where TEvent : RuntimeEvent =>
            builtIns.Add(typeof(TEvent), new EventRegistration(typeof(TEvent), null, typeof(TEvent).Name, mode));
    }

    internal void AddRegisterHandler(ModuleId module, RuntimeEventHandler<RuntimeEvents.OnRegistering> handler) {
        if (modulesLocked) {
            throw new RuntimeContractException("模块接线已锁定。");
        }

        registerHandlers.Add(EventSubscription.Create(module, handler));
    }

    internal void LockModules() {
        foreach (var module in modules) {
            var inherited = new HashSet<ModuleId>();
            foreach (var dependency in module.Dependencies) {
                var id = new ModuleId(dependency);
                inherited.Add(id);
                inherited.UnionWith(dependencies[id]);
            }

            dependencies.Add(new ModuleId(module.Id), inherited);
        }

        registrationPlan = registerHandlers.ToArray();
        modulesLocked = true;
    }

    internal IReadOnlySet<ModuleId> GetDependencies(ModuleId module) => dependencies[module];

    internal void MergeAndLock(IReadOnlyList<Registry> registries) {
        var problems = new List<string>();
        var mergedEvents = new Dictionary<Type, EventRegistration>(builtIns);
        var mergedTasks = new Dictionary<Type, TaskRegistration>();
        foreach (var registry in registries) {
            foreach (var definition in registry.Events) {
                if (typeof(LifecycleEvent).IsAssignableFrom(definition.Type) || IsTaskEvent(definition.Type)) {
                    problems.Add($"事件 {definition.Type.FullName} 不能覆盖内置事件。");
                }
                else {
                    if (!mergedEvents.TryAdd(definition.Type, definition)) {
                        problems.Add($"事件 {definition.Type.FullName} 重复定义。");
                    }
                }
            }

            foreach (var task in registry.Tasks) {
                if (!mergedTasks.TryAdd(task.Task.GetType(), task)) {
                    problems.Add($"Task {task.Task.GetType().FullName} 重复登记。");
                }
            }
        }

        var mergedSubscriptions = registries.SelectMany(static registry => registry.Subscriptions).ToArray();
        foreach (var subscription in mergedSubscriptions) {
            if (subscription.Type == typeof(RuntimeEvents.OnRegistering) || IsTaskEvent(subscription.Type)) {
                problems.Add($"不能通过 Registry 订阅 {subscription.Type.Name}。");
            }
            else if (!mergedEvents.TryGetValue(subscription.Type, out var definition)) {
                problems.Add($"订阅的事件 {subscription.Type.FullName} 未定义或不可通过 Registry 订阅。");
            }
            else if (definition.Module is { } owner && owner != subscription.Module
                && !dependencies[subscription.Module].Contains(owner)) {
                problems.Add($"模块 {subscription.Module} 订阅事件 {subscription.Type.FullName}，但没有直接或间接依赖定义模块 {owner}。");
            }
        }

        if (problems.Count > 0) {
            throw new RuntimeConfigurationException("Registry 合并失败。", problems);
        }

        Volatile.Write(ref plan, new DispatchPlan(mergedEvents.ToFrozenDictionary(), mergedTasks.ToFrozenDictionary(), mergedSubscriptions));
    }

    internal void CheckPublish(ModuleId module, RuntimeEvent e) {
        var current = Volatile.Read(ref plan);
        if (current is null || !current.Events.TryGetValue(e.GetType(), out var definition) || definition.Module != module) {
            throw new RuntimeContractException($"模块 {module} 不能发布事件 {e.GetType().Name}。");
        }
    }

    internal TaskRegistration GetTask<TTask, TInput, TResult>() where TTask : RuntimeTask<TInput, TResult> {
        var current = Volatile.Read(ref plan);
        if (current is null || !current.Tasks.TryGetValue(typeof(TTask), out var registration)
            || registration.Task is not RuntimeTask<TInput, TResult> || !host.IsModuleAvailable(registration.Module)) {
            throw new RuntimeContractException($"Task {typeof(TTask).Name} 未登记、类型不匹配或所属模块不可执行。");
        }

        return registration;
    }

    internal ValueTask<TResult> ExecuteTaskAsync<TInput, TResult>(RuntimeTask<TInput, TResult> task, TInput input, WorkNode node) =>
        task.ExecuteCoreAsync(new TaskEvent<TInput, TResult>(input, node));

    internal ValueTask DispatchAsync(RuntimeEvent e, WorkNode node, Action? onHandlerReturned = null,
        Action<RuntimeExecutionException>? onFailure = null) {
        var current = Volatile.Read(ref plan);
        var definition = e is RuntimeEvents.OnRegistering ? builtIns[e.GetType()] : current!.Events[e.GetType()];
        var candidates = e is RuntimeEvents.OnRegistering ? registrationPlan : current!.Subscriptions;
        EventSubscription[] participants;
        lock (host.Gate) {
            participants = e is LifecycleEvent lifecycle
                ? candidates.Where(subscription =>
                    subscription.Type == e.GetType() && subscription.Module == lifecycle.TargetModule).ToArray()
                : candidates.Where(subscription =>
                    subscription.Type == e.GetType() && host.IsModuleAvailable(subscription.Module)).ToArray();
        }

        return definition.Mode switch {
            EventDispatchMode.SerialFailFast => DispatchSerialAsync(e, node, participants, false, onHandlerReturned),
            EventDispatchMode.SerialCollect => DispatchSerialAsync(e, node, participants, true, onHandlerReturned),
            EventDispatchMode.ParallelJoin => DispatchParallelAsync(e, node, participants, null),
            EventDispatchMode.ParallelFailFast => DispatchParallelAsync(e, node, participants, onFailure),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    private static async ValueTask DispatchSerialAsync(RuntimeEvent e, WorkNode parent, EventSubscription[] participants,
        bool collect, Action? onHandlerReturned) {
        var failures = new List<RuntimeFailure>();
        var canceled = false;
        for (var i = 0; i < participants.Length; i++) {
            var participant = participants[i];
            try {
                await parent.RunChild(e.GetType().Name, participant.Module, async child => {
                    if (e is RuntimeEvents.OnRegistering registering) {
                        registering.Registry.Open(child);
                    }

                    try {
                        await participant.Invoke(e, new EventContext(child));
                    }
                    finally {
                        // 本模块最后一个处理器返回时关闭窗口，不等待它的后代结束。
                        if (i == participants.Length - 1) {
                            onHandlerReturned?.Invoke();
                        }
                    }

                    return true;
                });
            }
            catch (RuntimeExecutionException error) {
                failures.AddRange(error.Failures);
                if (!collect) {
                    break;
                }
            }
            catch (OperationCanceledException) {
                canceled = true;
                if (!collect) {
                    break;
                }
            }
        }

        if (failures.Count > 0) {
            throw new RuntimeExecutionException($"事件 {e.GetType().Name} 派发失败。", failures);
        }

        if (canceled) {
            throw new OperationCanceledException(parent.Cancellation);
        }
    }

    private static async ValueTask DispatchParallelAsync(RuntimeEvent e, WorkNode parent, EventSubscription[] participants,
        Action<RuntimeExecutionException>? onFailure) {
        var children = participants.Select(participant => parent.RegisterChild(e.GetType().Name, participant.Module,
            async child => {
                await participant.Invoke(e, new EventContext(child));
                return true;
            })).ToArray();

        // 全部登记后才调度，处理器的同步部分也可以并行执行。
        foreach (var child in children) {
            _ = Task.Run(child.Start);
        }

        var joined = JoinAsync();
        if (onFailure is not null && children.Length > 0) {
            var firstFailure = Task.WhenAny(children.Select(static child => child.Failure)).Unwrap();
            await Task.WhenAny(firstFailure, joined);
            if (firstFailure.IsCompletedSuccessfully) {
                onFailure(firstFailure.Result);
            }
        }

        await joined;

        async Task JoinAsync() {
            var failures = new List<RuntimeFailure>();
            var canceled = false;
            await foreach (var completion in Task.WhenEach(children.Select(static child => child.Completion))) {
                try {
                    await completion;
                }
                catch (RuntimeExecutionException error) {
                    failures.AddRange(error.Failures);
                }
                catch (OperationCanceledException) {
                    canceled = true;
                }
            }

            if (failures.Count > 0) {
                throw new RuntimeExecutionException($"事件 {e.GetType().Name} 派发失败。", failures);
            }

            if (canceled) {
                throw new OperationCanceledException(parent.Cancellation);
            }
        }
    }

    private static bool IsTaskEvent(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(TaskEvent<,>);

    private sealed record DispatchPlan(
        FrozenDictionary<Type, EventRegistration> Events,
        FrozenDictionary<Type, TaskRegistration> Tasks,
        EventSubscription[] Subscriptions);

    internal static async ValueTask WaitForPublishAsync(Task completion, Task<RuntimeExecutionException> firstFailure) {
        // 首错只结束发布等待，completion 仍代表完整的派发工作树。
        if (await Task.WhenAny(completion, firstFailure) == firstFailure) {
            throw await firstFailure;
        }

        await completion;
    }
}

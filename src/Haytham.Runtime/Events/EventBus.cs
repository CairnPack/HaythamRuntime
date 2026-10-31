using Haytham.Runtime.Tasks;

namespace Haytham.Runtime.Events;

/// <summary>
/// 事件登记表和执行机制。每个 <see cref="RuntimeHost"/> 有一个，内置事件、模块事件和 TaskEvent 都由它执行。
/// </summary>
/// <remarks>
/// 登记分四个阶段，只能向前推进：
/// <list type="number">
/// <item>引导：Runtime 直接登记内置事件。</item>
/// <item>接线：模块被创建后订阅 <see cref="RegisterEvent"/>。</item>
/// <item>注册：模块名单已锁定，<see cref="RegisterEvent"/> 执行，模块的 Registry 先各自暂存。</item>
/// <item>锁定：Registry 合并完成，此后登记内容不再变化，执行时不需要加锁读取。</item>
/// </list>
/// </remarks>
internal sealed class EventBus {

    private readonly Dictionary<Type, EventRegistration> registrations = new();
    private readonly Dictionary<Type, List<Subscription>> subscriptions = new();
    private readonly Dictionary<Type, TaskRoute> tasks = new();

    public EventBusPhase Phase { get; private set; } = EventBusPhase.Bootstrap;

    public void RegisterBuiltIn<TEvent>() where TEvent : RuntimeEvent {
        RequirePhase(EventBusPhase.Bootstrap);
        registrations.Add(typeof(TEvent), new EventRegistration(typeof(TEvent), Owner: null));
    }

    public void BeginWiring() => Advance(EventBusPhase.Bootstrap, EventBusPhase.Wiring);

    public void LockModules() => Advance(EventBusPhase.Wiring, EventBusPhase.Registering);

    /// <summary>
    /// 接线阶段只能订阅 <see cref="RegisterEvent"/>，其他订阅都通过 Registry 提交，这样第二次锁定前的登记只有一条途径。
    /// </summary>
    public void AddWiringSubscription<TEvent>(string owner, RuntimeEventHandler<TEvent> handler) where TEvent : RuntimeEvent {
        RequirePhase(EventBusPhase.Wiring);
        if (typeof(TEvent) != typeof(RegisterEvent)) {
            throw new InvalidOperationException(
                $"模块 {owner} 在接线时只能订阅 {nameof(RegisterEvent)}，{typeof(TEvent).Name} 请在 RegisterEvent 里通过 Registry 订阅。");
        }

        AddSubscription(Subscription.Create(owner, handler));
    }

    /// <summary>
    /// 检查所有模块提交的 Registry 并合并，然后锁定注册结果。
    /// 有任何冲突时一次列出全部问题，不合并任何一份。
    /// </summary>
    public void MergeAndLock(IReadOnlyList<Registry> registries) {
        RequirePhase(EventBusPhase.Registering);

        var problems = new List<string>();
        var defined = new Dictionary<Type, EventRegistration>(registrations);
        var routes = new Dictionary<Type, TaskRoute>();

        foreach (var registry in registries) {
            foreach (var type in registry.DefinedEvents) {
                if (IsBuiltIn(type)) {
                    problems.Add($"模块 {registry.ModuleId} 不能定义内置事件 {type.Name}。");
                }
                else if (!defined.TryAdd(type, new EventRegistration(type, registry.ModuleId))) {
                    problems.Add($"模块 {registry.ModuleId} 定义的事件 {type.FullName} 已经由 {defined[type].Owner} 定义。");
                }
            }

            foreach (var task in registry.Tasks) {
                if (routes.TryGetValue(task.InputType, out var existing)) {
                    problems.Add($"模块 {registry.ModuleId} 的 Task 输入类型 {task.InputType.FullName} 已经由 {existing.Owner} 登记。");
                    continue;
                }

                routes.Add(task.InputType, task);
                defined.Add(task.Subscription.EventType, new EventRegistration(task.Subscription.EventType, Owner: null));
            }
        }

        foreach (var subscription in registries.SelectMany(static registry => registry.Subscriptions)) {
            var type = subscription.EventType;
            if (type == typeof(RegisterEvent) || typeof(TaskEvent).IsAssignableFrom(type)) {
                problems.Add($"模块 {subscription.Owner} 不能通过 Registry 订阅 {type.Name}。");
            }
            else if (!defined.ContainsKey(type)) {
                problems.Add($"模块 {subscription.Owner} 订阅的事件 {type.FullName} 没有被任何模块定义。");
            }
        }

        if (problems.Count > 0) {
            throw new RuntimeConfigurationException("模块提交的 Registry 有冲突，注册结果没有锁定。", problems);
        }

        foreach (var (type, registration) in defined) {
            registrations.TryAdd(type, registration);
        }

        foreach (var (inputType, route) in routes) {
            tasks.Add(inputType, route);
            AddSubscription(route.Subscription);
        }

        foreach (var subscription in registries.SelectMany(static registry => registry.Subscriptions)) {
            AddSubscription(subscription);
        }

        Phase = EventBusPhase.Locked;
    }

    public bool TryGetTask(Type inputType, out TaskRoute route) => tasks.TryGetValue(inputType, out route!);

    /// <summary>
    /// 执行一次事件。<paramref name="owner"/> 不为空时只执行该模块的订阅者。
    /// 订阅者全部结束后才返回；失败时抛出 <see cref="RuntimeEventException"/>，取消时抛出 <see cref="OperationCanceledException"/>。
    /// </summary>
    public async ValueTask RaiseAsync<TEvent>(TEvent e, string? owner, CancellationToken cancellationToken) where TEvent : RuntimeEvent {
        var type = typeof(TEvent);
        if (!registrations.ContainsKey(type)) {
            throw new InvalidOperationException($"事件 {type.FullName} 没有登记。");
        }

        // 注册阶段只执行 RegisterEvent；其他事件要等注册结果锁定，订阅者名单固定之后才能执行。
        if (Phase != EventBusPhase.Locked && !(Phase == EventBusPhase.Registering && type == typeof(RegisterEvent))) {
            throw new InvalidOperationException($"注册结果锁定之前不能执行事件 {type.Name}。");
        }

        if (!subscriptions.TryGetValue(type, out var all)) {
            return;
        }

        var handlers = owner is null
            ? all
            : all.Where(subscription => string.Equals(subscription.Owner, owner, StringComparison.OrdinalIgnoreCase)).ToList();

        if (e.Execution == EventExecution.Sequential) {
            foreach (var handler in handlers) {
                try {
                    await handler.Invoke(e, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                    throw;
                }
                catch (Exception error) {
                    throw new RuntimeEventException(type, [new EventHandlerFailure(handler.Owner, error)]);
                }
            }

            return;
        }

        var running = handlers.Select(handler => (handler.Owner, Task: Run(handler, e, cancellationToken))).ToList();

        // 等所有订阅者结束再逐个检查结果，不在第一个失败时提前返回，否则其余订阅者会在事件“完成”后继续运行。
        await Task.WhenAll(running.Select(static item => item.Task)).ContinueWith(static _ => { }, TaskScheduler.Default);

        var failures = running
            .Where(static item => item.Task.IsFaulted)
            .Select(static item => new EventHandlerFailure(item.Owner, item.Task.Exception!.InnerException!))
            .ToList();
        if (failures.Count > 0) {
            throw new RuntimeEventException(type, failures);
        }

        if (running.Any(static item => item.Task.IsCanceled)) {
            throw new OperationCanceledException(cancellationToken);
        }

        // 包一层 async，处理器同步抛出的异常也会落到 Task 上，不会打断其他订阅者的启动。
        static async Task Run(Subscription handler, RuntimeEvent e, CancellationToken cancellationToken) =>
            await handler.Invoke(e, cancellationToken);
    }

    private static bool IsBuiltIn(Type type) =>
        type == typeof(RegisterEvent) || typeof(LifecycleEvent).IsAssignableFrom(type) || typeof(TaskEvent).IsAssignableFrom(type);

    private void AddSubscription(Subscription subscription) {
        if (!subscriptions.TryGetValue(subscription.EventType, out var list)) {
            list = [];
            subscriptions.Add(subscription.EventType, list);
        }

        list.Add(subscription);
    }

    private void Advance(EventBusPhase from, EventBusPhase to) {
        RequirePhase(from);
        Phase = to;
    }

    private void RequirePhase(EventBusPhase expected) {
        if (Phase != expected) {
            throw new InvalidOperationException($"事件登记处于 {Phase} 阶段，这个操作只能在 {expected} 阶段进行。");
        }
    }

}

internal enum EventBusPhase {

    Bootstrap,
    Wiring,
    Registering,
    Locked

}

/// <param name="Owner">定义事件的模块 Id；Runtime 内置的事件为 <see langword="null"/>。</param>
internal sealed record EventRegistration(Type EventType, string? Owner);

/// <summary>
/// 一个订阅者及其所属模块。处理器在这里转成统一的形状，执行时不需要反射。
/// </summary>
internal sealed record Subscription(Type EventType, string Owner, Func<RuntimeEvent, CancellationToken, ValueTask> Invoke) {

    public static Subscription Create<TEvent>(string owner, RuntimeEventHandler<TEvent> handler) where TEvent : RuntimeEvent {
        ArgumentNullException.ThrowIfNull(handler);
        return new Subscription(typeof(TEvent), owner, (e, cancellationToken) => handler((TEvent)e, cancellationToken));
    }

}

using System.Runtime.ExceptionServices;
using Haytham.Runtime.Events;
using Haytham.Runtime.Modules;
using Haytham.Runtime.Tasks;

namespace Haytham.Runtime;

/// <summary>
/// 进程里唯一的 Runtime 实例，负责模块的组装、注册、加载、启动、Task 执行、停止和卸载。
/// </summary>
/// <remarks>
/// C# 程序通常调用生成的 <c>RuntimeApplication.Create(capabilities)</c>，它把清单里的模块交给 <see cref="Create"/>。
/// 状态只由这里的生命周期流程推进，模块和调用方都不能直接修改。
/// </remarks>
public sealed class RuntimeHost {

    private static int created;

    private readonly IReadOnlyList<ModuleDefinition> definitions;
    private readonly RuntimeCapabilities capabilities;
    private readonly EventBus events = new();
    private readonly TaskPool tasks = new();
    private readonly List<string> loaded = [];
    private readonly List<string> started = [];
    private int lifecycleBusy;

    private RuntimeHost(IReadOnlyList<ModuleDefinition> definitions, RuntimeCapabilities capabilities) {
        this.definitions = definitions;
        this.capabilities = capabilities;
    }

    public RuntimeState State { get; private set; } = RuntimeState.Created;

    /// <summary>
    /// 创建本进程的 RuntimeHost。只能调用一次，第二次调用（包括并发调用）抛出 <see cref="InvalidOperationException"/>。
    /// 这里只保存模块列表，模块在 <see cref="StartAsync"/> 中才创建。
    /// </summary>
    public static RuntimeHost Create(IReadOnlyList<ModuleDefinition> definitions, RuntimeCapabilities capabilities) {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (Interlocked.Exchange(ref created, 1) == 1) {
            throw new InvalidOperationException("本进程已经创建过 RuntimeHost，一个进程只能有一个。");
        }

        return new RuntimeHost(definitions, capabilities);
    }

    /// <summary>
    /// 走完从组装模块到运行的全部步骤，返回时 Runtime 已处于 <see cref="RuntimeState.Running"/>。
    /// </summary>
    /// <remarks>
    /// 任何一步失败时，先停止并卸载已经加载的模块，再抛出原始错误，此时状态为 <see cref="RuntimeState.Stopped"/>；
    /// 如果清理也失败，状态为 <see cref="RuntimeState.Hanging"/>，抛出 <see cref="RuntimeCleanupException"/>。
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken = default) {
        BeginLifecycleCall(RuntimeState.Created, nameof(StartAsync));
        try {
            State = RuntimeState.Starting;
            try {
                await StartCoreAsync(cancellationToken);
            }
            catch (Exception error) {
                var cleanupErrors = await ShutdownAsync();
                if (cleanupErrors.Count > 0) {
                    State = RuntimeState.Hanging;
                    throw new RuntimeCleanupException(error, cleanupErrors);
                }

                State = RuntimeState.Stopped;
                throw;
            }

            tasks.Open();
            State = RuntimeState.Running;
        }
        finally {
            EndLifecycleCall();
        }
    }

    /// <summary>
    /// 提交一次 Task：按 <typeparamref name="TInput"/> 找到登记的 Task，在新的信道里执行，等本次的内部工作全部结束后返回结果。
    /// </summary>
    /// <exception cref="InvalidOperationException">没有对应的 Task，或者 Runtime 现在不接收新的 Task。</exception>
    /// <exception cref="RuntimeEventException">Task 的处理器失败。</exception>
    /// <exception cref="AggregateException">处理器结束后，它在信道里开始的工作有失败。</exception>
    public async Task<TResult> RunTaskAsync<TInput, TResult>(TInput input, CancellationToken cancellationToken = default) {
        if (!events.TryGetTask(typeof(TInput), out var route) || route.Subscription.EventType != typeof(TaskEvent<TInput, TResult>)) {
            throw new InvalidOperationException($"没有输入为 {typeof(TInput).Name}、结果为 {typeof(TResult).Name} 的 Task。");
        }

        if (!tasks.TryEnter()) {
            throw new InvalidOperationException($"Runtime 当前状态为 {State}，不接收新的 Task。");
        }

        try {
            var run = new TaskEvent<TInput, TResult>(input, cancellationToken);
            Exception? handlerError = null;
            try {
                await events.RaiseAsync(run, owner: null, cancellationToken);
            }
            catch (Exception error) {
                handlerError = error;
            }

            // 处理器失败时也要等信道里的工作结束，Task 结束意味着本次的工作都已停下。
            var workErrors = await run.DrainAsync();
            if (handlerError is not null) {
                if (workErrors.Count == 0) {
                    ExceptionDispatchInfo.Throw(handlerError);
                }

                throw new AggregateException([handlerError, ..workErrors]);
            }

            if (workErrors.Count > 0) {
                throw new AggregateException("Task 信道里的工作失败。", workErrors);
            }

            return run.GetResult();
        }
        finally {
            tasks.Exit();
        }
    }

    /// <summary>
    /// 请求停止。先暂停接收新的 Task，再派发 <see cref="RuntimeEvents.OnStopRequested"/>：
    /// 有模块否决时恢复接收并返回 <see cref="StopOutcome.Vetoed"/>；
    /// 没有否决时等已接纳的 Task 结束，然后按依赖反序停止、卸载模块，返回 <see cref="StopOutcome.Stopped"/>。
    /// </summary>
    /// <exception cref="RuntimeCleanupException">停止或卸载中有模块失败，状态为 <see cref="RuntimeState.Hanging"/>。</exception>
    public async Task<StopOutcome> RequestStopAsync() {
        BeginLifecycleCall(RuntimeState.Running, nameof(RequestStopAsync));
        try {
            tasks.Close();
            var request = new RuntimeEvents.OnStopRequested();
            try {
                await events.RaiseAsync(request, owner: null, CancellationToken.None);
            }
            catch {
                // 判定本身失败时不推进停止，恢复接收 Task 后把错误交给调用方。
                tasks.Open();
                throw;
            }

            if (request.IsCanceled) {
                tasks.Open();
                return StopOutcome.Vetoed;
            }

            await tasks.WaitIdleAsync();

            State = RuntimeState.Stopping;
            var cleanupErrors = await ShutdownAsync();
            if (cleanupErrors.Count > 0) {
                State = RuntimeState.Hanging;
                throw new RuntimeCleanupException(originalError: null, cleanupErrors);
            }

            State = RuntimeState.Stopped;
            return StopOutcome.Stopped;
        }
        finally {
            EndLifecycleCall();
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken) {
        // 1. 引导：直接登记内置事件。RegisterEvent 不能登记它自己，所以内置事件不经过 Registry。
        events.RegisterBuiltIn<RegisterEvent>();
        events.RegisterBuiltIn<RuntimeEvents.OnLoad>();
        events.RegisterBuiltIn<RuntimeEvents.OnStart>();
        events.RegisterBuiltIn<RuntimeEvents.OnStopRequested>();
        events.RegisterBuiltIn<RuntimeEvents.OnStopping>();
        events.RegisterBuiltIn<RuntimeEvents.OnUnload>();

        // 2. 检查模块列表、按依赖排序，确认能力齐全后再创建模块，缺项时一个模块都不创建。
        var ordered = ModuleOrder.Sort(definitions);
        var missing = ordered
            .SelectMany(definition => definition.RequiredCapabilities
                .Where(type => !capabilities.Contains(type))
                .Select(type => $"模块 {definition.Id} 需要 {type.FullName}。"))
            .ToList();
        if (missing.Count > 0) {
            throw new RuntimeConfigurationException("能力表缺少模块需要的对象，没有创建任何模块。", missing);
        }

        // 3. 创建模块并接上 RegisterEvent 订阅，然后第一次锁定：模块名单从此固定。
        events.BeginWiring();
        foreach (var definition in ordered) {
            var module = definition.Create(capabilities);
            module.Subscribe(new EventSubscriber(definition.Id, events));
        }

        events.LockModules();

        // 4. 按依赖顺序逐个模块执行 RegisterEvent，处理器结束即提交 Registry；全部提交后检查合并，第二次锁定。
        var registries = new List<Registry>(ordered.Count);
        foreach (var definition in ordered) {
            var registry = new Registry(definition.Id, events);
            await events.RaiseAsync(new RegisterEvent(registry), definition.Id, cancellationToken);
            registry.Submit();
            registries.Add(registry);
        }

        events.MergeAndLock(registries);

        // 5. 加载：依赖都加载成功后才加载依赖方，并记下实际加载成功的顺序，卸载时按它反序进行。
        foreach (var definition in ordered) {
            await events.RaiseAsync(new RuntimeEvents.OnLoad(definition.Id), definition.Id, cancellationToken);
            loaded.Add(definition.Id);
        }

        // 6. 启动：所有模块加载后再按同样的顺序启动。
        foreach (var id in loaded) {
            await events.RaiseAsync(new RuntimeEvents.OnStart(id), id, cancellationToken);
            started.Add(id);
        }
    }

    /// <summary>
    /// 按反序停止已启动的模块，再按反序卸载已加载的模块。某一步失败时记下错误，继续处理其余模块。
    /// </summary>
    private async Task<IReadOnlyList<Exception>> ShutdownAsync() {
        var errors = new List<Exception>();
        for (var i = started.Count - 1; i >= 0; i--) {
            await Collect(new RuntimeEvents.OnStopping(started[i]), started[i]);
        }

        started.Clear();

        for (var i = loaded.Count - 1; i >= 0; i--) {
            await Collect(new RuntimeEvents.OnUnload(loaded[i]), loaded[i]);
        }

        loaded.Clear();
        return errors;

        async Task Collect<TEvent>(TEvent e, string moduleId) where TEvent : RuntimeEvent {
            try {
                await events.RaiseAsync(e, moduleId, CancellationToken.None);
            }
            catch (Exception error) {
                errors.Add(error);
            }
        }
    }

    /// <summary>
    /// 同一时间只允许一个生命周期操作推进状态。这里不持锁等待模块的处理器，第二个调用直接失败。
    /// </summary>
    private void BeginLifecycleCall(RuntimeState expected, string operation) {
        if (Interlocked.Exchange(ref lifecycleBusy, 1) == 1) {
            throw new InvalidOperationException($"另一个生命周期操作正在进行，不能同时调用 {operation}。");
        }

        if (State != expected) {
            lifecycleBusy = 0;
            throw new InvalidOperationException($"{operation} 只能在 {expected} 状态下调用，当前状态为 {State}。");
        }
    }

    private void EndLifecycleCall() => Volatile.Write(ref lifecycleBusy, 0);

}

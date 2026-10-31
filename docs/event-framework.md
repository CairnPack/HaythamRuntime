# Event 框架骨架

更新日期：2026-11-01。本篇记录 Runtime 第一版可运行骨架里的 Event、RegisterEvent、Registry 和 TaskEvent 是怎样写的，以及哪些地方还是暂定做法。需求以[生命周期草稿](haytham-runtime-lifecycle.md)为准，模块清单以[模块清单与 NativeAOT](module-entry-aot.md)为准。

## 1. 一个模块怎样接入

下面是 `src/Test.Storage/StorageModule.cs` 的简化版：

```csharp
public sealed class StorageChangedEvent(string key) : RuntimeEvent {
    public string Key { get; } = key;
    public override EventExecution Execution => EventExecution.Parallel;
}

[RuntimeModule]
internal sealed class StorageModule(TextWriter log) : IRuntimeModule {

    private EventSource<StorageChangedEvent>? changed;

    // 第一步：Runtime 创建模块后调用，这里只能订阅 RegisterEvent。
    public void Subscribe(EventSubscriber events) => events.Subscribe<RegisterEvent>(OnRegister);

    // 第二步：RegisterEvent 执行时，把定义、订阅和 Task 登记到 Registry。
    private ValueTask OnRegister(RegisterEvent e, CancellationToken cancellationToken) {
        changed = e.Registry.DefineEvent<StorageChangedEvent>();
        e.Registry.Subscribe<RuntimeEvents.OnLoad>(OnLoad);
        e.Registry.AddTask<SaveValue, bool>(OnSave);
        return ValueTask.CompletedTask;
    }
}
```

模块作者要做的事只有两步：

1. 实现 `IRuntimeModule.Subscribe`，订阅 `RegisterEvent`。
2. 在 `RegisterEvent` 的处理器里通过 `Registry` 定义自己的事件、订阅生命周期事件或其他模块的事件、登记 Task。

模块没有订阅某个生命周期事件时，Runtime 不会替它调用任何默认的初始化或清理方法。

## 2. 基元类型

这些类型都在 `src/Haytham.Runtime/Events/` 和 `src/Haytham.Runtime/Tasks/` 里：

| 类型 | 作用 |
|---|---|
| `RuntimeEvent` | 基元 Event。所有事件都从它继承。一个事件对象代表一次执行，派发时新建，所以两次派发的数据互不相通。 |
| `EventExecution` | 订阅者的执行方式。`Sequential` 按订阅顺序逐个执行，第一个失败就停止；`Parallel` 同时开始，全部结束后一起报告失败。事件类型通过重写 `Execution` 选择，默认是 `Sequential`。 |
| `ICancelableEvent`、`CancelableEvent` | 可以被订阅者否决的事件。否决只记录结果，所有订阅者照常执行完，再由派发方决定下一步。模块定义可否决事件时继承 `CancelableEvent`。 |
| `LifecycleEvent`、`CancelableLifecycleEvent` | Runtime 自己的生命周期事件的基类。构造函数只对 Runtime 程序集开放，所以模块不能派生它们。 |
| `RuntimeEvents` | 生命周期事件的命名容器：`OnLoad`、`OnStart`、`OnStopRequested`、`OnStopping`、`OnUnload`。它只放类型，不保存订阅。 |
| `RegisterEvent` | 注册事件，带着本模块的 `Registry`。 |
| `TaskEvent`、`TaskEvent<TInput, TResult>` | 一次 Task 执行的信道，带输入、结果、取消令牌，以及 `Run` 方法。 |
| `RuntimeEventHandler<TEvent>` | 订阅者的形状：`(TEvent e, CancellationToken cancellationToken) => ValueTask`。返回的 `ValueTask` 结束之前，事件不算完成。 |
| `EventSource<TEvent>` | 模块派发自己定义的事件的入口，由 `Registry.DefineEvent` 返回，只有定义事件的模块持有。 |
| `RuntimeEventException` | 订阅者失败时抛出，列出失败的订阅者所属的模块和错误。 |

执行机制在内部类 `EventBus` 里，每个 `RuntimeHost` 有一个，不是进程级的静态表。订阅者转成统一的委托保存，执行时不用反射，符合 NativeAOT 的要求。

## 3. 两次锁定

`EventBus` 的登记分四个阶段，只能向前推进：

1. **引导**：Runtime 直接登记内置事件（`RegisterEvent` 和五个生命周期事件）。`RegisterEvent` 不能登记它自己，所以内置事件不经过 Registry。
2. **接线**：Runtime 按依赖顺序创建模块，调用 `Subscribe`。这时只能订阅 `RegisterEvent`，订阅其他事件会立刻报错，这样第二次锁定之前的登记只有 Registry 一条途径。接线结束就是第一次锁定，模块名单从此固定。
3. **注册**：Runtime 按依赖顺序逐个模块派发 `RegisterEvent`，每个模块拿到自己的 `Registry`。处理器结束就算提交，之后再调用这份 Registry 会报错。
4. **锁定**：所有模块提交后，Runtime 一次检查全部 Registry，有问题时一次列出全部问题并停止启动，任何一份都不合并。检查的内容是：
   - 模块不能定义内置事件（`RegisterEvent`、生命周期事件、`TaskEvent`）。
   - 同一个事件类型不能被两个模块定义。
   - 同一种 Task 输入类型只能有一个 Task。
   - 不能通过 Registry 订阅 `RegisterEvent` 或 `TaskEvent`；Task 的处理器只能通过 `AddTask` 登记，这样增加订阅者不会让同一次 Task 执行两遍业务。
   - 订阅的事件必须是内置事件或某个模块定义的事件。

   检查通过后合并并锁定，这是第二次锁定。此后订阅者名单不再变化，所以执行事件时不需要加锁读取，也不存在派发途中退订的问题。

## 4. 生命周期主线

`RuntimeHost` 的公开入口：

```csharp
var runtime = RuntimeApplication.Create(capabilities);   // 生成的代码，内部调用 RuntimeHost.Create
await runtime.StartAsync();
var result = await runtime.RunTaskAsync<InstallRequest, InstallResult>(request);
var outcome = await runtime.RequestStopAsync();          // Vetoed 或 Stopped
```

`StartAsync` 依次执行：登记内置事件 → 检查模块列表并按依赖排序 → 检查能力表是否齐全 → 创建模块并接线 → 第一次锁定 → 逐个模块派发 `RegisterEvent` → 检查合并并第二次锁定 → 按依赖顺序派发 `OnLoad` → 按同样顺序派发 `OnStart` → 开始接收 Task，进入 `Running`。

任何一步失败时，Runtime 按反序给已启动的模块派发 `OnStopping`，再按实际加载成功的反序派发 `OnUnload`，然后抛出原始错误，状态为 `Stopped`。清理也失败时状态为 `Hanging`，抛出 `RuntimeCleanupException`，里面同时保留原始错误和清理错误。

`RequestStopAsync` 依次执行：暂停接收新的 Task → 派发 `OnStopRequested`。有订阅者调用 `Cancel()` 时恢复接收并返回 `Vetoed`。没有否决时等已接纳的 Task 全部结束 → 进入 `Stopping` → 反序派发 `OnStopping` → 反序派发 `OnUnload` → `Stopped`。清理失败时继续处理其余模块，最后进入 `Hanging` 并抛出 `RuntimeCleanupException`。

`StartAsync` 和 `RequestStopAsync` 同一时间只能有一个在执行，第二个调用直接报错，不排队等待。

## 5. Task 怎样执行

调用方提交 `TInput` 时，Runtime 按输入类型找到登记的 Task，为这次执行新建一个 `TaskEvent<TInput, TResult>`，通过同一个 `EventBus` 执行它。处理器调用 `SetResult` 设置结果，也可以用 `Run` 在本次信道里开始短程工作后直接返回。Runtime 会等信道里的工作全部结束，才把结果或错误交给调用方，所以 Task 结束时本次的工作都已经停下。

处理器失败时，Runtime 同样等信道里的工作结束再报告错误。处理器结束时没有设置结果也算失败。任务池只统计已接纳、尚未结束的 Task，用于停止时等待它们结束，不为每个 Task 分配线程。

## 6. 暂定做法

下面这些是本轮为了让骨架能运行而选的做法，还没有和用户确认：

- `OnStopRequested` 的处理器抛出异常时，Runtime 不推进停止，恢复接收 Task，把错误交给调用方。
- `RuntimeHost.Create` 第二次调用时抛出 `InvalidOperationException`。
- 生命周期事件在阶段内按依赖顺序逐个模块串行执行，没有做独立模块并行。
- Task 按输入类型匹配，一种输入类型只对应一个 Task。
- `RegisterEvent` 按依赖顺序逐个模块派发，每个模块拿到自己的 Registry。

## 7. 还没有做的部分

- 持续工作的托管（教程第 8 章）：模块现在只能在 `OnStopping` 里自己结束持续工作，Runtime 还不跟踪它们。
- `Kill` 旁路：强制终止策略的接口还没定。
- 日志接入：消费者提供日志的方式还没定，`RuntimeHost` 现在不写日志。
- Task 的进度通知、父子任务和并发限制。
- 模块之间互相调用服务的方式。
- NativeAOT 发布验证。

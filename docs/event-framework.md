# Event 框架规格

更新日期：2026-11-03。本篇是 Runtime 事件框架的实现规格，取代旧版“Event 框架骨架”。设计过程和每项决定的来由见[架构评审](event-architecture-review.md)；需求以[生命周期草稿](haytham-runtime-lifecycle.md)为准，模块清单以[模块清单与 NativeAOT](module-entry-aot.md)为准。

正文由 Astra 把第一到第十九轮的结论和用户的决定合并而成。Claude 在交给实现之前做了几处简化，已经直接改进正文，改动列在下面，方便对照评审文件：

- 错误类型：`RuntimeFailure` 去掉 `Code`、`Execution`、`Stage`、`WorkName` 字段，`RuntimeFailureKind` 去掉 `Business`；不新增 `RuntimeBusinessException`，`RuntimeContractException` 不带错误码。Create 预检和 Registry 合并检查继续用现有的 `RuntimeConfigurationException` 一次列出全部问题。理由是讨论里没有出现过错误码表和区分业务错误的场景。
- 排队键：去掉 `TaskQueueKey(Domain, Value)` 和 `TaskSubmissionOptions`，`Submit` 直接接收 `string? queueKey`。理由是键的含义由接入层决定，一个字符串就够，HL 需要区分种类时自己加前缀。
- 取消结果：去掉 `TaskCancellation(Reason)`，`TaskOutcome.Canceled` 不带数据。理由是 `RequestCancellation()` 不接收原因，没有地方填写它。
- 停止的重复调用：去掉 `StopOutcomeKind.Rejected`。并发、重复或状态不对时调用 `StopAsync`，和其他生命周期方法一样抛 `RuntimeContractException`，对应用户“直接报错”的决定。`StopOutcome` 只描述真正执行过的停止。
- 模块清单：`ModuleDefinition`、`RuntimeCapabilities` 和生成器的现有输出不变，第 6 节不再列出它们的草图；`ModuleDefinition.Id` 仍是字符串，Runtime 内部转成 `ModuleId`。
- C2 生成规则：除了 `Submit(RuntimeHost, …)` 和 `InvokeAsync(WorkScope, …)`，生成器再生成一个 `Submit(ModuleWorkPort, …)`，让持续工作提交根 Task 时也能用短写法。
- 不可参与派发的模块：第 3 节第 8 条写明具体条件。

代码块是**接口声明稿，不是可直接编译的实现文件**。同一类型分节列出的成员应合并；没有公开构造函数的上下文、作用域和 handle 由 Runtime 创建。此前未完整确定的类型形状、冲突处理及暂定规则集中列在第 8 节。

## 1. 基元与统一错误类型

```csharp
public abstract class RuntimeEvent
{
    protected RuntimeEvent();
}

public delegate ValueTask RuntimeEventHandler<TEvent>(
    TEvent e,
    EventContext context)
    where TEvent : RuntimeEvent;

public sealed record EventDefinition<TEvent>(
    string Name,
    EventDispatchMode Mode)
    where TEvent : RuntimeEvent;

public enum EventDispatchMode
{
    SerialFailFast,
    SerialCollect,
    ParallelJoin,
    ParallelFailFast
}

public sealed class EventContext
{
    public ExecutionId ExecutionId { get; }
    public ModuleId SubscriberModule { get; }
    public CancellationToken Cancellation { get; }
    public WorkScope Work { get; }
}

public readonly record struct ExecutionId(Guid Value);

public readonly struct ModuleId : IEquatable<ModuleId>
{
    public ModuleId(string value);

    public string Value { get; }

    public bool Equals(ModuleId other);
    public override bool Equals(object? obj);
    public override int GetHashCode();
    public override string ToString();

    public static bool operator ==(ModuleId left, ModuleId right);
    public static bool operator !=(ModuleId left, ModuleId right);
}

public enum RuntimeFailureKind
{
    Execution,
    Contract,
    Cleanup,
    Output
}

public sealed record RuntimeFailure(
    RuntimeFailureKind Kind,
    string Message,
    Exception? Exception = null,
    ModuleId? Module = null);

public sealed class RuntimeContractException : InvalidOperationException
{
    public RuntimeContractException(
        string message,
        Exception? innerException = null);
}

public sealed class RuntimeExecutionException : Exception
{
    public RuntimeExecutionException(
        string message,
        IReadOnlyList<RuntimeFailure> failures);

    public IReadOnlyList<RuntimeFailure> Failures { get; }
}

public sealed class RuntimeAlreadyCreatedException
    : InvalidOperationException
{
    public RuntimeAlreadyCreatedException(string message);
}
```

1. **三项职责分开。**
   - `RuntimeEvent` 是受托管事件的基元。
   - 具体事件对象保存一次派发的数据。
   - 宿主内部派发器负责参与者选择、执行、等待、取消关联和错误收集。
   - `EventDefinition<TEvent>` 保存不可变执行契约，不保存本次执行状态。

2. **模块事件继承 `RuntimeEvent`。**业务由订阅者完成；事件子类不能覆盖内核派发算法。

3. **执行身份属于上下文和执行记录。**普通事件对象不保存 `ExecutionId`、派发状态或“已使用”标记。每次执行由宿主分配独立身份。

4. **每次派发使用新事件对象是调用契约。**Runtime 不维护对象使用历史，不专门检测重复或并发派发同一个对象。

5. **类型身份采用准确的 CLR 类型。**不按类名匹配，不自动沿继承树广播；订阅 `RuntimeEvent` 不等于订阅全部事件。`typeof`、`GetType()` 和类型身份比较可以使用，不使用反射扫描或反射调用。

6. **`ModuleId` 忽略大小写比较和计算哈希。**显示保留原字符串，来源仍为生成清单中的包 Id。空值、空白值不合法。

7. **错误分类。**
   - 订阅者、Task 或工作执行时抛出的异常：`Execution`。业务错误就是普通异常，Runtime 不区分业务错误和程序缺陷。
   - 未接纳的调用、非法阶段、无效登记等：`Contract`。
   - 资源释放失败：`Cleanup`。
   - 输出接收或收尾失败：`Output`，属于内部基础设施故障。
   - 协作取消单独表达，不转换成普通失败。

8. **错误传播载体。**
   - 根 Task 使用结果类型返回已接纳执行的结果。
   - 普通事件和必要子工作失败使用 `RuntimeExecutionException`。
   - 调用契约拒绝使用 `RuntimeContractException`，包括 `StopAsync` 的并发、重复和状态不对。
   - Create 预检和 Registry 合并检查的问题使用现有的 `RuntimeConfigurationException`，一次列出全部问题。
   - 重复创建只使用 `RuntimeAlreadyCreatedException`，由消息区分正在创建、已经创建等情况。
   - 有效取消使用 BCL `OperationCanceledException`，不另造取消异常。

9. 原始异常和后续错误必须保留。汇总不得把同一故障沿父子传播重复计数，不承诺并行故障的固定顺序。

10. `RuntimeFailure` 是进程内诊断对象。HL 对外输出必须映射为安全的错误协议，不能直接序列化异常、完整请求、凭据或敏感命令行。

## 2. 工作树

```csharp
public sealed class WorkScope
{
    public WorkHandle Run(
        string name,
        Func<WorkContext, ValueTask> operation);

    public WorkHandle<T> Run<T>(
        string name,
        Func<WorkContext, ValueTask<T>> operation);

    public ValueTask PublishAsync<TEvent>(TEvent e)
        where TEvent : RuntimeEvent;

    public ValueTask<TResult> InvokeAsync<TTask, TInput, TResult>(
        TInput input)
        where TTask : RuntimeTask<TInput, TResult>;
}

public sealed class WorkContext
{
    public CancellationToken Cancellation { get; }
    public WorkScope Work { get; }

    public TaskOutput? Output { get; }
}

public sealed class WorkHandle
{
    public Task Completion { get; }
}

public sealed class WorkHandle<T>
{
    public Task<T> Completion { get; }
}
```

1. **先登记，后执行。**`Run`、内部事件派发及 `InvokeAsync` 必须先建立父子关系，再执行用户代码。同步抛异常也属于已登记工作。

2. **必要工作不能脱离归属。**
   - 处理器直接等待的工作包含在处理器执行中。
   - 经 `WorkScope` 登记的工作即使调用者忘记等待 handle，父执行仍须等待。
   - 自行启动且未登记的后台工作违反契约；Runtime 不承诺发现它。

3. **委托返回不等于执行完成。**当前委托已经返回，但必要后代或清理尚未结束时，当前执行仍未完成。

4. **登记窗口按工作划分。**
   - 运行中的处理器或子工作可以登记自己的必要后代。
   - 该处理器或工作委托返回后，它自己的登记权限关闭。
   - 尚在运行的已登记子工作仍可登记自己的后代。
   - 权限检查与子工作登记必须协调，不能刚判定完成又接纳新孩子。
   - 保存上下文引用不能延长窗口。

5. **普通短程步骤使用 `Run`。**它不创建新的根 Task，也不要求每个步骤都变成 Task 类。

6. **跨模块业务调用使用 `InvokeAsync`。**
   - 查找已登记 Task 类，调用其实例行为。
   - 子 Task 有独立输入、结果和执行记录，但属于当前根执行树。
   - 不经过根准入门禁，不进入根 FIFO，不重新取得父根任务已持有的键。
   - 子调用不接受新的根提交选项。
   - 不检查调用链递归，也不在每次调用时验证模块依赖方向。
   - 调用时仍检查定义存在、类型契约匹配、提供模块可执行及作用域有效。

7. **执行身份随实际业务归属切换。**进入另一个模块的 Task 后，内部事件发布权限按该 Task 的所属模块判断，根执行关联保持不变。

8. **失败收尾。**
   - 一个工作执行失败时，向它尚未结束的必要后代请求取消，并等待真实结束。
   - 不能用取消请求代替等待。
   - 父执行收集后代失败及清理错误。
   - 事件派发器对订阅者错误的收集时机服从第 3 节，不提前破坏 `SerialCollect` 或 `ParallelJoin` 的执行语义。

9. **观察异常不等于恢复成功。**必要子工作失败已经进入执行树结果，父处理器捕获等待异常不会自动清除该失败。可恢复操作应在该项工作内部完成恢复，再成功返回。

10. **取消判定。**
    - 父取消向必要后代传播，子工作不能改写父取消源。
    - 只有与本次有效取消来源关联的 `OperationCanceledException` 才表示取消。
    - 无关令牌或没有有效取消请求的取消异常按故障处理。
    - 收到请求后仍正常完成，可以判成功。
    - 存在真实失败或清理失败时，失败优先于取消。

11. **执行结束条件。**主工作、全部必要后代及本次必要清理都已结束，才可形成最终成功、失败或取消结果。

12. `WorkContext.Output` 在 Task 执行树内指向该根 Task 的报告出口；非 Task 的生命周期或根事件工作中为 `null`。不新增独立遥测通道。

13. 第一版没有根或子工作并发上限、执行名额、名额等待队列及后台加权调度。共享资源同步仍由业务设施负责。

## 3. 四种派发方式与发布权限

本节使用以下既定声明，不增加公开派发器：

```csharp
public enum EventDispatchMode
{
    SerialFailFast,
    SerialCollect,
    ParallelJoin,
    ParallelFailFast
}

public sealed class WorkScope
{
    public ValueTask PublishAsync<TEvent>(TEvent e)
        where TEvent : RuntimeEvent;
}
```

1. **参与名单按单次派发确定。**订阅表在第二次锁定后不可修改。串行顺序为冻结的模块计划顺序，同一模块内按顺序登记的订阅顺序。

2. **`SerialFailFast`。**
   - 依次执行订阅者。
   - 当前订阅者及其必要工作结束后再执行下一项。
   - 首次失败后不启动后续订阅者。
   - 已开始的工作仍须真实结束，错误返回派发方。

3. **`SerialCollect`。**
   - 依次执行全部符合执行条件的订阅者。
   - 某项失败不阻止后续项。
   - 全部必要工作结束后汇总返回错误。
   - 清理阶段仍由生命周期驱动器判断每项清理是否安全，不得机械释放仍在使用的资源。

4. **`ParallelJoin`。**
   - 先登记参与者，再并行执行。
   - 等待全部订阅者及必要后代结束。
   - 汇总所有错误后返回。
   - 并行载荷必须不可变或隔离；Runtime 不保证任意共享对象线程安全。

5. **`ParallelFailFast`。**
   - 先建立全部参与者的追踪，再启动执行。
   - 第一次观察到失败时，`PublishAsync` 提前向派发方返回错误。
   - 此模式本身不取消其他订阅者。
   - 没有失败时，等待全部成功后才成功返回。
   - 后续错误继续进入同一执行记录，最终汇入所属根执行结果。
   - 派发方的方法可以提前返回，但其所属受托管执行必须继续等待剩余订阅者。
   - 如果首错向上传播使父工作失败，父工作可以按工作树故障收尾规则取消其后代。
   - 首错不是“事件已经结束”的证明，不能据此释放其他订阅者仍在使用的资源。

6. **不可发布其他模块定义的事件。**
   - 模块只能通过自己的受控上下文或 `ModuleWorkPort` 发布本模块定义的普通事件。
   - 发布其他模块事件应拒绝。
   - 跨模块取得业务结果使用对方 Task。
   - 其他模块可以按注册规则订阅该事件，发布权限与订阅权限不同。

7. **内置事件受保护。**
   - 生命周期事件及 TaskEvent 的具体类型由 Runtime 封闭构造。
   - Registry 不能覆盖其定义或执行策略。
   - 普通发布入口不能派发生命周期事件或 TaskEvent。
   - TaskEvent 的唯一执行入口来自 Task 类，不允许普通 `Subscribe<TaskEvent<…>>` 增加业务执行者。
   - `RuntimeEvents` 只是类型容器，没有静态可变订阅。

8. **不可用模块的普通订阅者不参与新派发。**
   - “不可用”只指两种生命周期状态：订阅者所在模块的 `OnLoading` 还没有成功完成，或者这个模块已经开始 `OnUnloading`。例如 A 在自己的 `OnLoading` 里发布事件时，依赖 A、还没加载的 B 的订阅者不参与。
   - 不删除注册绑定，不返回跳过名单。
   - 无参与者时正常完成。
   - 已开始的订阅者仍须追踪到结束。
   - 持续工作单项失败不会自动使模块不可用，因此不会触发这种筛选。
   - 框架定向生命周期派发不受普通业务筛选阻挡。

9. 派发成功表示本次实际参与者及必要工作成功结束，不保证每个已登记订阅者均参与。

10. 不提供 `MinimumHandlers`、`bindingId`、`order`、`StopPropagation()`、运行期业务订阅退订或 `EventDispatchResult`。

## 4. 接线与 Registry

```csharp
public interface IRuntimeModule
{
    void Connect(ModuleWiring wiring);
}

public sealed class ModuleWiring
{
    public ModuleWorkPort RootWork { get; }

    public void SubscribeRegister(
        RuntimeEventHandler<RuntimeEvents.OnRegistering> handler);
}

public sealed class ModuleWorkPort
{
    public ValueTask PublishAsync<TEvent>(
        TEvent e,
        CancellationToken cancellation = default)
        where TEvent : RuntimeEvent;

    public TaskRun<TResult> Submit<TTask, TInput, TResult>(
        TInput input,
        string? queueKey = null,
        ITaskOutputSink<TResult>? output = null,
        CancellationToken cancellation = default)
        where TTask : RuntimeTask<TInput, TResult>;
}

public sealed class Registry
{
    internal Registry();

    public void RegisterTask(RuntimeTask task);

    public void RegisterEvent<TEvent>(
        EventDefinition<TEvent> definition)
        where TEvent : RuntimeEvent;

    public void Subscribe<TEvent>(
        RuntimeEventHandler<TEvent> handler)
        where TEvent : RuntimeEvent;
}
```

`RuntimeEvents.OnRegistering` 的完整声明见第 6 节。

1. `Connect` 只做接线，接上一个 `OnRegistering` 订阅者；不开展业务初始化、不打开资源、不启动循环。

2. 没有内容需要登记的模块也可接上一个空注册处理器。业务生命周期处理器不强制存在。

3. `ModuleWiring` 的接线窗口由 Runtime 控制。`RootWork` 可长期保存，但不因此延长接线窗口。

4. **根入口与内部入口分开。**
   - `ModuleWorkPort` 创建新根 Task 或根事件执行。
   - 根准入关闭时拒绝，包括持续工作产生的新根工作。
   - `WorkScope` 创建当前执行的必要后代，已接纳执行可在根准入关闭后继续完成。
   - 不提供通用 bypass 参数，不根据线程猜测根调用还是子调用。

5. 根事件也纳入宿主在途追踪。`ParallelFailFast` 提前返回错误后，实际根事件仍保留登记，直到参与工作全部结束。

6. **第一次锁定**固定模块实例集合、模块归属及注册事件接线。此时尚允许各模块登记 Task、事件定义和订阅。

7. Runtime 为每个模块的 `OnRegistering` 创建独立 Registry，归属由框架确定，模块不能自行填写或冒充归属。

8. Registry 仅由注册处理器本身顺序修改：
   - 可以异步等待后继续登记。
   - 不允许子工作或其他并发调用修改。
   - 不提供线程安全保证，也不承诺识别所有违规并发访问。
   - 注册处理器返回时关闭窗口；以后登记明确报错。
   - 如仍有必要子工作，继续等待；它们不能继续登记。

9. **隐式提交。**
   - 处理器及其必要工作全部成功，接受该模块的候选清单。
   - 没有登记任何内容是合法空清单。
   - 任一注册执行失败，整批候选清单不发布。
   - 不提供 `CreateRegistry`、`Seal`、`Submit` 或多份候选清单选择。

10. **合并检查清单。**
    - 同一个实际 Task 类型重复登记。
    - 同一个事件 CLR 类型重复定义。
    - 覆盖内置事件、冒用内置保留身份或非法内置订阅。
    - 订阅引用未定义的普通事件。
    - 静态事件类型契约不匹配。
    - 跨模块事件订阅缺少相应模块依赖。
    - Registry 窗口已关闭，或提交对象不属于本宿主／本模块的有效注册执行。
    - 生成清单的重复模块 Id、缺失依赖或依赖环仍须在创建预检中拒绝。

11. Task 的行为随实例一起登记，不再检查“缺少执行绑定／多个执行绑定”，不再允许跨模块替别人实现 Task。

12. 同一模块可以对同一事件登记多个订阅者。没有订阅标识去重，也不按委托相等性去重。

13. **第二次锁定**原子发布全部 Task 定义、事件定义、订阅及派发计划。锁定后不可更改，但可以不断创建新的执行和持续工作运行记录。

14. 两次锁定是关闭修改窗口和发布不可变结果，不是持有线程锁跨越用户代码或 `await`。

## 5. Task、排队与输出

```csharp
public abstract class RuntimeTask
{
    private protected RuntimeTask();

    public virtual string Name { get; }
    public abstract TaskExitPolicy ExitPolicy { get; }
}

public abstract class RuntimeTask<TInput, TResult> : RuntimeTask
{
    protected RuntimeTask();

    protected abstract ValueTask<TResult> ExecuteAsync(
        TaskEvent<TInput, TResult> e);
}

public enum TaskExitPolicy
{
    Wait,
    CancelOnExit
}

public sealed class TaskEvent<TInput, TResult> : RuntimeEvent
{
    internal TaskEvent();

    public ExecutionId ExecutionId { get; }
    public TInput Input { get; }
    public CancellationToken Cancellation { get; }
    public WorkScope Work { get; }
    public TaskOutput Output { get; }
}

public enum TaskExecutionState
{
    Submitted,
    Queued,
    Running,
    Completing,
    Succeeded,
    Failed,
    Canceled
}

public sealed record TaskProgress(
    string Phase,
    long? Done = null,
    long? Total = null,
    long? Bytes = null,
    long? TotalBytes = null,
    string? Item = null);

public sealed record TaskLogEntry(string Message);

public sealed class TaskOutput
{
    public void ReportProgress(TaskProgress progress);
    public void ReportLog(TaskLogEntry entry);
}

public interface ITaskOutputSink<TResult>
{
    void ReportState(TaskExecutionState state);
    void ReportProgress(TaskProgress progress);
    void ReportLog(TaskLogEntry entry);

    ValueTask CompleteAsync(TaskOutcome<TResult> outcome);
}

public abstract record TaskOutcome<TResult>
{
    private protected TaskOutcome();

    public sealed record Succeeded(TResult Result)
        : TaskOutcome<TResult>;

    public sealed record Failed(
        IReadOnlyList<RuntimeFailure> Failures)
        : TaskOutcome<TResult>;

    public sealed record Canceled : TaskOutcome<TResult>;
}

public sealed record TaskCompletion<TResult>(
    TaskOutcome<TResult> Business,
    RuntimeFailure? OutputFailure);

public sealed class TaskRun<TResult>
{
    public ExecutionId Id { get; }
    public TaskExecutionState State { get; }
    public Task<TaskCompletion<TResult>> Completion { get; }

    public void RequestCancellation();
}

public sealed partial class RuntimeHost
{
    public TaskRun<TResult> Submit<TTask, TInput, TResult>(
        TInput input,
        string? queueKey = null,
        ITaskOutputSink<TResult>? output = null,
        CancellationToken cancellation = default)
        where TTask : RuntimeTask<TInput, TResult>;
}

public sealed class WorkScope
{
    public ValueTask<TResult> InvokeAsync<TTask, TInput, TResult>(
        TInput input)
        where TTask : RuntimeTask<TInput, TResult>;
}
```

### Task 身份与行为

1. Task 写成类，行为写在 `ExecuteAsync` 内，只能由登记它的模块实现。Runtime 不提供定义与实现分离、替换实现或服务定位器机制。

2. 登记身份使用 `task.GetType()`，调用身份使用 `typeof(TTask)`，按准确类型配对。变量声明为 `RuntimeTask` 不影响登记身份。

3. `Name` 默认使用实际类的简单名称，需要时覆写：
   - 仅用于展示和诊断。
   - 不要求全局唯一。
   - 不作为 Task 身份、HL op 名或本地化键。

4. `ExitPolicy` 由具体类声明，登记时固定。默认业务选择应是 `Wait`，但基类签名要求作者明确覆写。

5. Task 实例登记一次，可以执行多次；不能把某次输入、取消、结果或工作树保存在实例共享字段中。共享资源同步由业务负责。

6. TaskEvent 是 Runtime 内置事件族。实例每次执行新建，唯一业务适配器直接调用该 Task 的 `ExecuteAsync`；继续复用统一派发和工作树机制。

### C2 生成规则

7. 具体 Task 使用 `partial`，继承 `RuntimeTask<TInput,TResult>`，不带自身类型参数。

8. 生成器在具体类中生成：
   - 强类型静态 `Submit(RuntimeHost, TInput, …)`，转调公开的三个类型参数 `RuntimeHost.Submit<TTask,TInput,TResult>`。
   - 强类型静态 `Submit(ModuleWorkPort, TInput, …)`，转调 `ModuleWorkPort.Submit<TTask,TInput,TResult>`。
   - 强类型静态 `InvokeAsync(WorkScope, TInput)`，转调对应 `WorkScope.InvokeAsync`。
   - 不保存静态实例或可变状态。

9. 调用形如 `SaveValueTask.Submit(host, request)`、`SaveValueTask.InvokeAsync(context.Work, request)`。生成器按符号识别类型，不靠运行时反射；诊断边界另行实现。

### 接纳、排队和取消

10. 提交先检查根准入，再查找定义、检查类型，并原子完成执行登记。接纳失败抛契约错误，不产生 TaskRun 或输出事件。

11. 提交不在调用线程执行业务，包括 `ExecuteAsync` 第一个 `await` 之前的同步代码。

12. 无键任务接纳后进入执行；有键任务按该键的接纳顺序运行。不同 Task 类使用相同键时也共享串行顺序。

13. 键的业务含义由接入层决定。Runtime 不认识实例或路径；键空间和规范化由调用方保证。

14. 排队任务已经属于在途任务：
    - 可取消。
    - 纳入预退出政策和正式 Stop 排空。
    - 进入 Queued 状态，由输出适配器映射为界面排队进度。
    - 排队取消可直接离队，不等待前项；不执行主处理器，仍完成取消结果输出。

15. 必要子 Task 不进入根队列，不重新申请父根任务的键；不提供子调用根提交选项。

16. 第一版不实现根／子并发上限、相关配置入口、后台权重、线程优先级或容量拒绝。不存在 `TaskExecutionOptions`、`TaskExecutionClass` 或 Runtime 执行名额。将来调度扩展不影响当前按键业务串行规则。

17. 一次提交就是一次执行，不自动去重、重试或重放。Task 不在执行中等待用户回答；需要用户选择时，返回报告或业务错误，由接入层询问后重新提交。

### 业务结果、输出与唯一 Completion

18. `ExecuteAsync` 返回值只是候选成功结果。必要子工作或清理失败后不能返回成功。

19. 最终业务结果：
    - 全部必要业务及清理成功：`Succeeded`。
    - 存在业务、内部或清理错误：`Failed`。
    - 有效取消导致工作停止且无失败：`Canceled`。

20. 输出与业务结果分开表达，但只保留一个 `Completion`：
    - 业务成功、输出失败：保留成功值，同时填写 `OutputFailure`。
    - 业务失败、输出失败：两类信息都保留。
    - 输出成功时 `OutputFailure` 为 `null`。
    - 等待 Completion 正常返回不等于没有错误，调用方检查两个字段。

21. 正常收尾顺序：

    1. 主执行、必要后代及本次清理实际结束。
    2. 封闭业务报告入口，等待已进入的同步报告调用返回。
    3. 确定业务结果，进入 Completing。
    4. 输出适配器停止并汇合延迟报告活动。
    5. flush 进度、log 及必要丢弃标记。
    6. 将唯一最终结果交给本任务输出序列。
    7. 完成该任务并释放同键执行权，允许下一项运行。

22. `CompleteAsync` 成功表示最终结果已进入接入层输出序列，不表示已发送、收到或 ACK。没有输出接收端时，输出收尾直接成功。

23. 输出收尾失败时，记录内部输出错误，形成 `TaskCompletion`，随后照常释放同键执行权。不暂停该键、不终止后续批次、不停止该键接纳，也不自动重试业务。

24. 输出失败必须表示本次输出尝试确已结束，不能遗留未追踪的定时器继续报告。Runtime 不通过同一个失败输出路径递归发送“输出失败事件”。

25. `TaskRun.State` 最终描述业务结果，输出故障由 `Completion.OutputFailure` 表达。`Completing` 表示结果尚在输出收尾中。

### 报告和接入层边界

26. `TaskOutput` 绑定执行身份；业务不填写 HL job id。必要子工作的报告关联到根 Task，进度汇总算法由业务负责。

27. 报告同步交给接收端，快速返回，不在 Runtime 内偷偷使用未追踪的线程池回调。接收端负责协调并发报告。

28. HC／Gebinde 可继续使用各自的 `IProgress<T>`，由同步适配器转换；不要求共享业务进度类型，不使用 `System.Progress<T>` 把报告无序延迟到完成结果之后。

29. HL 负责节流、合并、有界缓冲、log 打包、传输、重连及界面协议。默认数值、pump 和 ACK 实现不进入 Runtime 契约。

30. 输出接收端没有业务执行权。增加 UI、CLI 或日志消费者不会增加 Task 主执行次数。

31. 活动任务列表、HL job id、最新进度及 hot restart 重建记录由 HL 提交入口和输出适配器维护。Runtime 不增加 `GetSnapshot`、`WatchFaultsAsync` 或通用输出历史。

## 6. 生命周期、停止和资源收尾

```csharp
public abstract class LifecycleEvent : RuntimeEvent
{
    private protected LifecycleEvent();

    public ModuleId TargetModule { get; }
}

public abstract class CancelableLifecycleEvent : LifecycleEvent
{
    private protected CancelableLifecycleEvent();

    public bool IsCanceled { get; }
    public void Cancel();
}

public static class RuntimeEvents
{
    public sealed class OnRegistering : LifecycleEvent
    {
        internal OnRegistering();
        public Registry Registry { get; }
    }

    public sealed class OnLoading : LifecycleEvent
    {
        internal OnLoading();
    }

    public sealed class OnStarting : LifecycleEvent
    {
        internal OnStarting();
        public ModuleWorkScope ContinuousWork { get; }
    }

    public sealed class OnStopRequested : CancelableLifecycleEvent
    {
        internal OnStopRequested();
    }

    public sealed class OnStopping : LifecycleEvent
    {
        internal OnStopping();
    }

    public sealed class OnUnloading : LifecycleEvent
    {
        internal OnUnloading();
    }
}

public enum RuntimeState
{
    Created,
    Starting,
    Running,
    PreStopping,
    StopRequested,
    Draining,
    Stopping,
    Unloading,
    Stopped,
    Hanging
}

public enum StopOutcomeKind
{
    Stopped,
    Vetoed,
    DecisionFailed,
    CleanupFailed
}

public sealed record StopOutcome(
    StopOutcomeKind Kind,
    IReadOnlyList<RuntimeFailure> Failures);

public sealed partial class RuntimeHost
{
    private RuntimeHost();

    public RuntimeState State { get; }

    public static RuntimeHost Create(
        IReadOnlyList<ModuleDefinition> definitions,
        RuntimeCapabilities capabilities);

    public Task StartAsync();
    public Task PreStopAsync();
    public void CancelPreStop();
    public Task<StopOutcome> StopAsync();

    public void Kill();
}

public interface IRuntimeTerminationStrategy
{
    void Terminate();
}
```

`ModuleDefinition` 和 `RuntimeCapabilities` 沿用现有代码，不改形状。`ModuleDefinition.Id` 和 `Dependencies` 仍是字符串，Runtime 内部转成 `ModuleId`。

### 宿主与创建

1. 一个进程只创建一个 RuntimeHost。停止、启动失败或 Hanging 都不释放创建名额，不支持停止后重新启动同一宿主。

2. 进程入口仅保存受控创建状态。模块、订阅、执行、资源等可变状态由宿主实例持有。

3. `Create`：
   - 取得用于本次创建的清单和能力映射快照。
   - 检查重复 Id、缺失依赖、依赖环并排序。
   - 检查所有构造参数所需能力，一次报告全部缺项。
   - 不调用模块工厂，不创建模块，不执行接线，不登记事件，不开展 I/O 或业务初始化。
   - 成功返回 Created 宿主。

4. 创建权检查必须原子协调。并发或重复 Create 使用同一种 `RuntimeAlreadyCreatedException`。纯预检失败、尚未创建宿主时，可以修正输入后再次创建；一旦唯一宿主已创建，不得再创建第二个。

5. 模块清单和构造工厂来自已定案生成协议。能力按类型获取，调用方拥有能力对象的释放权；Runtime 不释放借用对象。

6. Runtime 本身不引用 HL、HC、Gebinde 业务，也不接管控制台退出或进程退出信号。部署不得在同一进程加载多个各自携带独立 Runtime 的 NativeAOT 映像并声称共享同一单例。

### StartAsync

7. Start 仅从 Created 开始，主生命周期只有一个推进者。执行步骤：

    1. 进入 Starting。
    2. 建立最小事件执行能力。
    3. 直接登记内置生命周期事件及 TaskEvent 机制，避免注册自循环。
    4. 按生成工厂创建模块。
    5. 对每个模块调用 `Connect`。
    6. 完成第一次锁定。
    7. 定向派发各模块的 `OnRegistering`，收集并等待隐式提交。
    8. 全量校验合并，完成第二次锁定。
    9. 按依赖计划执行所有模块的 OnLoading。
    10. 记录实际成功加载顺序。
    11. 全部加载结束后，按依赖计划执行 OnStarting。
    12. 等待必要启动工作及持续工作 Ready。
    13. 确认启动条件仍成立，进入 Running，开放根准入。

8. 第一版阶段内使用拓扑串行。没有绑定某生命周期事件的模块不执行虚构回调；它仍参与阶段和依赖状态记录。

9. 生命周期派发按“事件类型＋订阅所属模块”选择参与者。加载 A 时只执行 A 的 OnLoading，不反复广播全部模块。

10. 构造、注入、接线和定义登记不得自动初始化业务；业务初始化只发生在实际注册的对应事件中。

11. **启动失败回滚。**
    - 不进入 Running，不开放普通根准入。
    - 等已经开始的必要工作真正结束。
    - 当前加载失败模块处理自己部分取得的资源。
    - 若已有持续工作启动，先请求它们结束并等待。
    - 对实际成功加载模块反序执行必要卸载。
    - 保留原始启动错误及所有清理错误。
    - 回收成功则进入 Stopped；清理未完成或失败则 Hanging。
    - `StartAsync` 通过 `RuntimeExecutionException` 报告失败。
    - 失败后不能再调用 Start 重来。

### PreStopAsync 与撤销

12. `PreStopAsync` 是可撤销的退出准备，不执行 Stop 判定、不等待所有任务结束：
    - 从 Running 进入 PreStopping。
    - 原子关闭新根 Task 和根事件准入。
    - 对已接纳的 `CancelOnExit` 根 Task 请求取消，包括排队项。
    - `Wait` 根 Task 不主动取消。
    - 已接纳执行的必要后代继续受追踪。

13. 退出策略只应用于根执行。Wait 安装调用的子 Task 即使定义为 CancelOnExit，也不能因此被单独取消。

14. `CancelPreStop` 仅从尚未正式提交停止的 PreStopping 恢复 Running 和有效准入。已经发出的取消请求不撤回，取消任务不重启。

15. 取消请求发出失败时明确报告错误，保留真实预退出状态；不得假称所有目标均已收到取消，不自动进入卸载。

16. 不在预退出或 Task 处理器中等待用户回答。用户交互由 HL 控制。

### StopAsync

17. 正式 Stop 可以从 Running 或 PreStopping 开始。直接从 Running 调用时不隐式应用 CancelOnExit，仍自然排空已有 Task。

18. 正式停止步骤：

    1. 关闭根准入，进入 StopRequested。
    2. 定向、串行执行完整 OnStopRequested 判定名单，等待必要工作。
    3. 有处理器错误：返回 DecisionFailed，暂按用户 4.1 的 A 恢复到停止前的状态：从 Running 发起的回到 Running 并重新开放准入；从 PreStopping 发起的回到 PreStopping，准入保持关闭，因为否决只撤销这次正式停止，预退出仍然有效。
    4. 无错误但有否决：返回 Vetoed，按上一步同样的规则恢复。
    5. 无错误、无否决：进入 Draining，保持根准入关闭。
    6. 取得本次排空开始时尚未完成的根 Task 和根事件执行，等待真实结束；Task 等唯一 Completion。
    7. 进入不可逆 Stopping，向全部持续工作请求结束，再等待；执行各模块 OnStopping 的必要收尾。
    8. 所有仍会使用模块资源的工作结束后进入 Unloading。
    9. 按实际成功加载顺序反序执行 OnUnloading，只释放自有资源。
    10. 形成 StopOutcome 和最终状态。

19. `Cancel()` 只标记本次判定否决：
    - 单向、幂等。
    - 不直接开门禁。
    - 不取消 Task、不停止传播、不终止其他判定处理器。
    - 每次停止使用新的事件实例，各模块判定由宿主汇总。
    - 判定事件不得进行不可逆资源释放。

20. **OnStopRequested 的业务用途仍待细化。**不能将“安装必须完成”实现为永久否决，应使用 Wait Task。当前暂按“完整判定后任一否决即否决；异常优先于否决”执行，不把这一暂定策略当成已完成的业务定案。

21. **排空错误汇总。**
    - 普通 Task 业务失败回给原调用方，不因此阻断 Stop。
    - 本次排空集合中的 Task 输出故障加入 StopOutcome.Failures。
    - 排空开始前已经完成的 Task 不追溯，不建立宿主全历史错误表。
    - 获取集合与完成移出登记必须协调，避免竞态漏项。
    - 根事件尚未结束的必要工作也必须等完。

22. **输出与状态。**
    - 输出失败不会自动导致 Hanging。
    - 业务、持续工作及资源都完成时，状态可以是 Stopped，Failures 非空。
    - Stopped 不证明所有结果成功入队，更不证明界面收到。
    - HL 检查输出错误，并负责其传输排空及接收约定。

23. **持续工作停止。**先向全部工作发信号，再等待，不能先等某个无限循环结束才通知其他循环。不在停止信号之前将持续工作当普通短程 Task 排空。

24. **清理错误。**
    - OnStopping 报错但工作结束、资源全部成功释放：Stopped，错误进入 Failures。
    - 卸载或资源清理失败：继续仍安全的清理，最终 Hanging，结果为 CleanupFailed。
    - 不能释放仍被工作或依赖使用的资源。
    - Hanging 保持普通业务准入关闭，保留实际未完成义务及错误。
    - 仍在等待的工作不等于已失败结束；没有默认超时自动转为成功或自动 Kill。

25. Stop 返回结果，不通过抛执行异常报告正常停止流程中的失败。

### 重复调用与 Kill

26. 生命周期不合并、不排队重复调用：
    - 并发 Start、重复 Start、重复 PreStop、无预退出时 CancelPreStop、并发 Stop、Stopped 后再次 Stop 均明确拒绝。
    - 所有入口（包括 StopAsync）都以 `RuntimeContractException` 拒绝。
    - Stop 正式提交后，不能再通过 CancelPreStop 撤销。
    - 不增加“模块正在等待自己的 Stop”的调用链检测。

27. 调用者可限制自己的等待时间，但不会取消宿主收尾，不改变资源实际状态。

28. Kill 是明确选择的旁路：
    - 运行、预退出、Stop 等待或 Hanging 时可调用。
    - 不等待 Stop 操作，不经过否决，不等待任务或卸载。
    - 调用方通过能力表提供 `IRuntimeTerminationStrategy`。
    - 策略缺失、抛错或意外返回时，报告终止未完成，不声称安全停止。
    - 不自动升级为 Kill，不保证强杀任意托管线程。
    - 正常停止不等于结束游戏进程。

## 7. 持续工作

```csharp
public sealed class ModuleWorkScope
{
    public ContinuousWorkHandle Start(
        string name,
        Func<ContinuousWorkContext, ValueTask> operation);
}

public sealed class ContinuousWorkContext
{
    public CancellationToken Stopping { get; }
    public ModuleWorkPort RootWork { get; }

    public void ReportReady();
}

public sealed class ContinuousWorkHandle
{
    public ModuleId Module { get; }
    public string Name { get; }

    public Task Ready { get; }
    public Task<ContinuousWorkOutcome> Completion { get; }

    public void RequestStop();
}

public enum ContinuousWorkOutcomeKind
{
    Completed,
    Stopped,
    Failed
}

public sealed record ContinuousWorkOutcome(
    ContinuousWorkOutcomeKind Kind,
    IReadOnlyList<RuntimeFailure> Failures);

public sealed record ContinuousWorkFailure(
    ModuleId Module,
    string WorkName,
    RuntimeFailure Failure);

public sealed partial class RuntimeHost
{
    public event Action<ContinuousWorkFailure>? ContinuousWorkFailed;

    public ContinuousWorkHandle RetryContinuousWork(
        ModuleId module,
        string name);
}
```

1. 持续工作在 OnStarting 中经 `e.ContinuousWork.Start` 登记，属于模块生命周期作用域，不属于启动事件的短程必要子工作。

2. 同一模块内名字唯一、登记后不变，按序数且区分大小写比较。身份就是 `(ModuleId, Name)`；不提供 `ContinuousWorkId` 或公开 Attempt 编号。

3. `Start` 先登记，再调度执行委托；同步抛错也有明确归属。OnStarting 返回后关闭该次初始登记窗口。

4. **Ready 与 Completion 分开。**
   - ReportReady 表示本次运行已满足启动条件。
   - Ready 成功不表示循环已结束。
   - 启动阶段等待 Ready，不等待整个循环。
   - 就绪前失败、取消或结束，不能伪装成 Ready 成功。
   - 就绪后 Completion 记录本次真正结束的结果。
   - 每次重试创建新的上下文、取消源和两个完成对象；旧 handle 永久保留旧次结果。

5. 持续工作可以通过 RootWork 发布本模块事件或提交根 Task。它们受普通根准入限制；不能因为来自后台循环就绕过预退出或 Stop 门禁。

6. **故障仅停止该项工作。**
   - 不执行模块 OnStopping 或 OnUnloading。
   - 不改变整个模块 Task 准入。
   - 不暂停依赖模块。
   - 不跳过该模块事件订阅者。
   - 不自动重试。

7. 工作委托负责自己的业务状态和资源：
   - 出错时通过自身异常处理将服务标记为不可用。
   - 必要时对其他工作调用 RequestStop。
   - 完成自身清理后继续传播错误，不得只更新状态然后正常返回掩盖失败。
   - 没有 `onFailure` 参数或模块故障钩子。

8. Runtime 在该次执行和收尾真正结束后，记录结果、完成 Completion，并通过 `ContinuousWorkFailed` 通知 HL／CLI。正常响应停止请求不是意外故障通知。

9. 回调不是业务执行入口，不得用同步等待阻塞宿主收尾。回调自身异常不能覆盖原工作故障，具体错误边界见第 8 节。

10. **相关工作恢复由业务负责。**
    - 推荐 B 依赖 A 时，A 不可用期间 B 异步等待服务可用信号，而不是停止 B。
    - A 重试初始化成功后，由 A 的执行委托恢复服务状态并通知等待者。
    - B 的等待响应自己的 Stopping。
    - 服务调用仍须处理运行中再次失效。
    - Runtime 不增加依赖重启图、就绪通知链或自动恢复相关工作的机制。

11. RequestStop 仅请求终止这次 handle 指向的运行，不自动恢复。若模块确实停止了另一项工作，要重新运行它时必须明确请求该项重试。

12. **RetryContinuousWork。**
    - 按模块和名字查找既有登记。
    - 前次运行及收尾必须已经结束。
    - 宿主必须处于 Running；预退出、正式停止或 Stopped 时拒绝。
    - 当前仍在运行、停止中或已有新的重试运行时，重复请求明确拒绝。
    - 同步完成检查和新运行登记，返回新 handle；不在调用线程执行工作业务。
    - 重用原登记委托，不重新构造模块，不重新注册，不重跑 OnStarting／OnStopping。
    - 运行条件由本次委托检查，失败通过 Ready／Completion 报告，不增加前置条件系统。
    - 不重放此前业务 Task，不自动重启其他工作。

13. 同一名字的“新运行”由内部运行记录区分，无须公开次数。对旧 handle 调用 RequestStop 不得影响新运行。

14. 宿主停止时，对所有尚未结束的当前运行请求停止并等待；已经失败结束的工作不被重新启动。资源释放仍等待实际使用者结束。

15. 第一版不区分必要／非必要持续工作。所有已登记运行都必须追踪到结束；这不意味着其失败自动使整个模块失效。

## 8. 合并中发现的未定形状、冲突与暂定做法

本节不增加新的功能机制，只明确实现者不能从早期草图直接推导的细节。

1. **错误类型此前只有名字或局部草图。**  
   第 1 节的 `RuntimeFailureKind`、`RuntimeFailure` 字段及契约／执行异常，是为完整规格补齐的暂定形状，Claude 已按开头的说明简化。原始异常仍可使用 BCL 异常；不要求为每种错误新增异常类。

2. **TaskOutcome、日志项和状态枚举此前未完整定义。**  
   暂定采用第 5 节的三个业务结果分支；日志项只保存一条消息，不提前增加日志等级、结构化属性系统。Task 最终 State 表示业务结果，输出失败独立存在于 TaskCompletion；唯一 Completion 仍在输出收尾结束后完成。

3. **ReportState／ReportProgress／ReportLog 抛异常的细节未单独定案。**  
   暂定同步调用异常沿当前调用路径显式暴露，不能静默吞掉。若因此中断业务执行，业务结果记录该内部故障；最终输出收尾再次失败时另记 OutputFailure。不得因为记录了输出错误就自动重跑业务。

4. **WorkContext 在非 Task 工作中的输出属性此前不自洽。**  
   暂定为可空 `TaskOutput?`；Task 树内存在，普通根事件和生命周期工作中不存在。不引入无归属输出通道。

5. **“Stop 不抛异常”与“重复生命周期调用直接报错”需要统一。**  
   StopAsync 对重复、并发及非法状态抛 `RuntimeContractException`，和其他生命周期方法一致；不会共享正在执行的 Stop。“不抛异常”只针对真正执行过的停止流程，它的失败放在 `StopOutcome` 里。

6. **宿主状态细分及启动回滚终态此前没有最终完整枚举。**  
   暂定采用第 6 节：启动失败且回收完毕为 Stopped，清理失败为 Hanging；不再允许 Start。Created 状态下尚未 Start 的取消创建／释放入口没有讨论，第一版不额外增加，StopAsync 暂拒绝从 Created 调用。

7. **OnStopRequested 仍明确待细化。**  
   当前使用完整串行判定、异常返回 DecisionFailed 并恢复运行的暂定方案。用户交互使用预退出，不让任务或判定处理器等待用户。不能将这一节暂定行为扩展为新的否决政策。

8. **Kill 的语义已确认，接口名字此前未定。**  
   `Kill()` 和通过能力表提供的 `IRuntimeTerminationStrategy.Terminate()` 是本规格的最小暂定声明。策略返回代表未完成终止，使用 `RuntimeExecutionException` 报告，不引入虚假的 Killed 成功状态。

9. **ModuleDefinition 的构造需求元数据。**  
   现有代码已经有生成的 `RequiredCapabilities` 类型列表，Create 用它检查全部缺项，不调用模块工厂。

10. **持续工作正常自行返回的分类此前未完整确定。**  
    暂定：
    - 有效停止请求后的正常结束为 Stopped。
    - 就绪后没有故障的自行结束为 Completed。
    - 该业务本应永久运行却提前退出时，由工作委托将其报告为故障，Runtime 不猜测业务循环是否本应继续。
    - 就绪前退出使 Ready 失败，不能成功推进启动。

11. **ContinuousWorkFailed 订阅者自身异常没有独立故障通道。**  
    暂定消费者回调必须快速返回、不得抛异常；违规作为内部故障沿通知执行路径显式暴露，不覆盖已确定的持续工作结果，不递归调用同一通知，也不为此新增故障流、重启器或回调重试机制。

12. **清理责任有明确语义，但没有批准通用资源登记 API。**  
    本规格不新增资源袋或任意清理回调注册接口。处理器内部的部分初始化清理由处理器自己保证；模块已加载资源由 OnUnloading 释放。Runtime 组织等待、阶段和错误汇总，不能声称能自动发现业务代码未报告的资源。

13. **模块不可用时的普通事件筛选保留，但持续工作故障不再触发模块停用。**  
    该筛选用于生命周期中实际不可执行的模块；不引入新的模块健康状态传播或暂停接口。生命周期期间的内部事件参与者必须符合当前阶段的就绪条件，不靠订阅顺序猜测。

14. **已经明确删除或暂缓的机制不得恢复。**
    - TaskKey、DefineTask／BindTask、CRTP 自身类型参数、运行期弱类型调用入口。
    - Registry Seal／Submit、并行子工作登记、原子 Registry 写入协议。
    - MinimumHandlers、bindingId、order、事件对象重复派发检测、跳过名单。
    - Task 递归检测、执行期依赖方向检查、等待自身 Stop 的专门检测。
    - 根／子并发上限及其配置、后台加权调度。
    - OutputCompletion、输出失败暂停键或停止批次、宿主长期累计 Task 输出故障。
    - Runtime 快照、故障异步流、缺口检测和重放。
    - 持续工作故障导致模块停止、依赖方暂停、模块重启、ContinuousWorkId、Attempt、onFailure。

本次仅完成最终规格整理；没有读取现有代码、访问当前目录以外的路径、修改文件或编写实现代码。

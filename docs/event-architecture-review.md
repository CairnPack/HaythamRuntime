# Event 架构设计评审（Astra 方案）

更新日期：2026-11-02。本篇是交给用户审查的材料，审查通过前不改代码，也不派 Sol 实现。

## 怎么得到这份方案

- 设计者是 Codex 的 gpt-6-astra，只读运行。它只能看到复制到临时目录的 5 份需求文档：生命周期草稿、开发约束、模块清单与 NativeAOT、手写教程总纲、用户早期笔记。它没有看到任何代码，也没有看到描述现有实现的 `event-framework.md`。
- 讨论共三轮：
  - 第一轮：Astra 给出九项设计。
  - 第二轮：Claude 问了两个问题，一是模块在处理器以外怎样发起工作、模块之间怎样互相调用，二是 Registry 为什么要显式提交。
  - 第三轮：Claude 问了一个命名冲突问题，Astra 确认方案定稿。
  - 第四到第十一轮是第二阶段：审查过度工程化和防御性设计，再补入 HL 的需求重审，说明见第 1 节的“第二阶段说明”。
  - 第十二到第十六轮是第三阶段：按用户在 4.9 节的决定修订 Task、并行事件和持续工作故障的设计，说明见第 1 节的“第三阶段说明”。
- 第 1 节是 Astra 各轮回答的原文，没有删改。后一轮的修订取代前一轮的对应部分，例如第一轮里的 `Event`、`EventHandler<TEvent>`、`RegistryDraft`／`Seal`／`Submit` 都已经被后面的轮次改掉，阅读时以后面的为准。
- 第 2 到第 5 节是 Claude 写的：需要用户留意的分歧、代答情况、需要用户回答的问题，以及和分支 `runtime/event-skeleton` 上现有实现的差异。

## 1. Astra 的方案原文

### 第一轮：九项设计

本次只做架构设计：只依据当前目录的 5 份文档，没有读取现有代码、访问目录外路径或修改文件。已确认规则保持不变；以下未定案部分是本次设计建议。C# 均为声明草图，不提供实现。

整体建议是：**定义描述契约，事件实例承载本次数据，宿主内部的统一执行器负责执行与收尾。** Task、生命周期和持续工作共享执行基础，但各自保留不同的完成条件。

#### 1. 基元 Event、派发器与事件对象

**设计决定与理由**

将三个概念分开：

| 概念 | 职责 | 不承担的职责 |
|---|---|---|
| 基元 `Event` | 规定“这是一次受托管事件执行”，提供只读执行身份 | 不保存订阅表，不允许模块覆盖派发算法 |
| 具体事件对象 | 保存本次输入，以及契约允许修改的业务数据 | 不跨执行复用，不自行改变宿主阶段 |
| 宿主内的派发器 | 选择参与者、创建执行作用域、调用处理器、等待必要工作、汇总结果 | 不实现下载、安装等业务 |

另外，`EventDefinition<TEvent>` 是不可变的事件定义，保存执行策略、发布权限和参与者数量约束。它与每次创建的事件对象不同。

所谓 Event 的“载入点”，明确为三个东西：

1. **谁可以触发**：宿主驱动器，或定义该事件的模块。
2. **在哪里触发**：生命周期阶段，或某个受托管工作作用域。
3. **触发后等待什么**：处理器、必要子工作及本次清理义务。

模块通过继承 `Event` 定义载荷，通过 Registry 声明执行契约。模块不继承派发器，也不重写 `ExecuteAsync` 一类内核方法。

```csharp
public abstract class Event
{
    protected Event();
    public ExecutionId ExecutionId { get; }
}

public delegate ValueTask EventHandler<TEvent>(
    TEvent e,
    EventContext context)
    where TEvent : Event;

public sealed class EventContext
{
    public ModuleId SubscriberModule { get; }
    public CancellationToken Cancellation { get; }
    public WorkScope Work { get; }

    public ValueTask PublishAsync<TEvent>(TEvent e)
        where TEvent : Event;
}

internal sealed class EventDispatcher
{
    internal ValueTask<DispatchOutcome> DispatchAsync<TEvent>(
        TEvent e,
        DispatchPlan<TEvent> plan,
        ExecutionScope scope)
        where TEvent : Event;
}
```

`EventContext` 由框架创建，并绑定当前处理器和有效作用域；公开构造函数不存在。事件对象首次派发时绑定执行身份，重复或并发派发同一个对象均拒绝。

**内置事件保护采用三层约束：**

- `RegisterEvent`、`TaskEvent` 及具体生命周期事件封闭继承，构造入口仅对 Runtime 内部开放。
- 事件身份按准确的 CLR 类型匹配，不按类名或继承关系自动匹配；订阅 `Event` 不等于订阅全部内置事件。
- 内置定义由宿主直接登记，保留其定义身份与发布权限；Registry 只能提交允许的订阅，不能重新定义或替换内置事件。公开发布路径再次检查权限。

模块即使声明一个同名类，也只是另一个类型；即使拿到内置事件对象，也不能经公开发布入口推进生命周期。这是正常 API 使用边界，不是针对同进程恶意代码的安全沙箱。

**未选方案：**不让 Event 子类重写完整执行算法，否则模块可以绕过必要工作等待、错误汇总和宿主阶段控制。

#### 2. RegisterEvent 接线、Registry 与两次锁定

**设计决定与理由**

给 `IRuntimeModule` 只增加一个接线成员：

```csharp
public interface IRuntimeModule
{
    void Connect(ModuleWiring wiring);
}

public sealed class ModuleWiring
{
    public void SubscribeRegister(
        EventHandler<RegisterEvent> handler);
}
```

`Connect` 只登记一个 RegisterEvent 处理器，不加载资源、不开展业务。它是框架协议接线，不是强制业务初始化回调。没有定义需要提交的模块，可以提交空 Registry。

Runtime 创建模块后调用 `Connect`，然后锁定模块名单。随后通过同一个 Event 派发器，对各模块分别派发 RegisterEvent。

```csharp
public sealed class RegisterEvent : Event
{
    internal RegisterEvent();

    public RegistryDraft CreateRegistry();
    public void Submit(Registry registry);
}

public sealed class RegistryDraft
{
    public void DefineTask<TInput, TResult>(
        TaskDefinition<TInput, TResult> definition);

    public void BindTask<TInput, TResult>(
        TaskKey<TInput, TResult> key,
        TaskHandler<TInput, TResult> handler);

    public void DefineEvent<TEvent>(
        EventDefinition<TEvent> definition)
        where TEvent : Event;

    public void BindEvent<TEvent>(
        string bindingId,
        EventHandler<TEvent> handler,
        int order = 0)
        where TEvent : Event;

    public Registry Seal();
}
```

`RegistryDraft` 只是注册清单的局部编辑器，与早期 RuntimeBuilder／AddModule 方案无关。`Seal` 产生不可变提交件；`Submit` 只接受本次注册窗口创建、归属本模块的清单。

三类内容明确区分：

| 内容 | 表达什么 |
|---|---|
| Task 定义 | 触发身份、输入类型、返回类型及执行约束 |
| Event 定义 | 事件类型、发布权限、串并行及等待／错误契约 |
| 绑定 | 哪个模块的哪个处理器承担哪个定义的执行 |

Task 定义与唯一主执行绑定合起来，完整表达“触发、行为、返回”。模块不能自行填写绑定所属模块，归属由注册上下文盖章。

每个 RegisterEvent 必须**恰好提交一次**；提交后处理器及其必要工作仍须完成。处理器后来失败，即使已经提交，也不接受该模块的注册成果。

合并采用事务式流程：收集所有提交件 → 全量校验 → 原子发布不可变注册快照。任何失败都丢弃整批候选结果，不产生半注册的可运行宿主。

合并检查至少包括：

- Task 身份重复、同一身份对应不同输入／输出类型。
- 同一事件类型被重复定义，或诊断名称冒用保留的内置命名空间。
- 内置定义覆盖、未经允许的内置绑定。
- Task 缺少主执行绑定，或出现多个主执行绑定。
- 绑定引用不存在的定义，或静态类型契约不匹配。
- 同一模块内绑定标识重复；跨模块定义占用不属于自己的身份。
- 事件策略与绑定不相容，例如要求唯一处理器却绑定多个。
- 跨模块绑定缺少相应已生成的依赖关系；不从订阅顺序偷偷推导新依赖。
- 注册窗口失效、重复提交、提交件来自其他宿主或模块。

两次锁定的对象不同：

| 锁定 | 锁住什么 | 之后仍允许什么 |
|---|---|---|
| 第一次 | 模块实例集合、生成清单对应关系、RegisterEvent 接线名单 | 模块在 RegisterEvent 中提交 Task／Event 定义及绑定 |
| 第二次 | Task／Event 定义、业务绑定、执行策略和派发计划 | 创建新的执行实例、更新执行状态、登记受托管工作 |

这里的“锁定”指关闭修改窗口、发布不可变快照，不是持有线程锁跨越处理器的 `await`。

第一版不开放运行期修改业务绑定。进度观察的订阅／退订使用单独的观察通道，不改变第二次锁定的执行计划。

**未选方案：**不把所有业务注册直接放进 `Connect`，否则 RegisterEvent 退化为空壳，也会混淆两次锁定的边界。

#### 3. Task 的触发契约、配对与结果

**设计决定与理由**

采用显式的类型化 Task 标识，不使用任意谓词抢占输入，也不按订阅先后选择执行者。

```csharp
public readonly record struct TaskKey<TInput, TResult>(
    string ModuleId,
    string Name);

public sealed record TaskDefinition<TInput, TResult>(
    TaskKey<TInput, TResult> Key,
    TaskExecutionOptions Options);

public delegate ValueTask<TResult> TaskHandler<TInput, TResult>(
    TaskEvent<TInput, TResult> e,
    EventContext context);

public sealed class TaskEvent<TInput, TResult> : Event
{
    internal TaskEvent();
    public TInput Input { get; }
}

public sealed class RuntimeHost
{
    public TaskRun<TResult> Submit<TInput, TResult>(
        TaskKey<TInput, TResult> key,
        TInput input,
        CancellationToken cancellation = default);
}

public sealed class TaskRun<TResult>
{
    public ExecutionId Id { get; }
    public TaskExecutionState State { get; }
    public Task<TaskOutcome<TResult>> Completion { get; }
    public void RequestCancellation();
}
```

触发契约由以下部分构成：

- `ModuleId + Name`：明确操作身份。
- `TInput`：输入结构。
- `TResult`：唯一结果结构。
- `TaskExecutionOptions`：执行并发约束等基础契约。
- 唯一 `TaskHandler<TInput,TResult>`：实际业务责任人。

模块 Id 按文档要求忽略大小写；本地 Task 名建议使用序数、区分大小写比较。相同业务可拥有多个不同 Task 名，不靠输入类型猜测意图。

外部 JSON、命令行、Dart 请求先由应用后端或 Native 边界转换成准确的 key 和输入，再进入 Runtime。序列化和按字符串选择泛型类型不进入 Runtime 内核。

每次提交按以下顺序处理：

1. 查找定义并验证输入／输出配对。
2. 检查模块可用性与准入。
3. 将准入检查与根执行登记作为一个原子操作。
4. 创建独立 TaskEvent 信道，交给统一派发器执行唯一主绑定。
5. 等待整个必要工作集合完成，再交付结果。

`TaskEvent<TInput,TResult>` 是 Runtime 拥有的内置事件族；泛型参数仅表达静态输入输出绑定。`BindTask` 生成或构造静态泛型适配器，不需要运行时反射生成调用器。

结果分成两层：

- **未接纳**：未知 Task、宿主未运行、准入关闭、模块不可用等，`Submit` 抛出明确的契约拒绝异常，不创建执行记录。
- **已接纳**：`Completion` 返回 `Succeeded(result)`、`Failed(failure)` 或 `Cancelled(info)`，不以 `default(TResult)` 充当失败。

已知业务失败与内部故障都属于失败结果，但保留不同类别、原始错误及关联身份。处理器的返回值只是暂存候选结果；必要子工作失败后不能再把这个候选值当作成功返回。

一次提交就是一次执行。第一版不自动去重、不自动重试。

**未选方案：**不以“第一个能处理输入的订阅者”为执行者，因为它无法稳定检测冲突，也无法保证返回类型和唯一业务责任。

#### 4. TaskEvent 信道与内部短程工作

**设计决定与理由**

信道是宿主持有的**一次执行记录与作用域**，不必实现为消息队列。

每个信道至少包含：

- 宿主、Task、执行、主模块身份。
- 输入、唯一主绑定及本次事件执行关系。
- 取消来源与原因。
- 主处理器、必要子工作和内部事件的父子归属。
- 候选返回值、错误集合、进度与最终结果。
- 本次资源清理义务、完成状态和完成等待对象。

内部短程工作有两种合规写法：

1. 在处理器内直接 `await`，其生命周期自然包含在处理器中。
2. 经 `WorkScope` 登记后并行开展，由信道统一等待。

```csharp
public sealed class WorkScope
{
    public WorkHandle Run(
        string name,
        Func<WorkContext, ValueTask> operation);

    public WorkHandle<T> Run<T>(
        string name,
        Func<WorkContext, ValueTask<T>> operation);
}

public sealed class WorkContext
{
    public CancellationToken Cancellation { get; }
    public WorkScope Work { get; }

    public ValueTask PublishAsync<TEvent>(TEvent e)
        where TEvent : Event;
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

`Run` 必须先登记、再调用操作；同步抛异常也进入追踪结果。调用者即使忘记等待 handle，父信道仍等待它。处理器自己启动未登记的后台工作属于违反契约，框架不能凭空发现它。

**关闭与登记采用结构化父子规则：**

- 每个运行中的处理器／子工作拥有自己的登记权限。
- 处理器返回后，其权限关闭，不能保存 `context` 稍后继续登记。
- 已登记且尚未结束的子工作，仍能登记自己的必要后代。
- “检查权限并增加子工作计数”必须原子完成。
- 主处理器结束、全部后代结束、清理结束后，信道才能封闭并产生终态。

这样不会在“刚判定完成”的瞬间又进入一个孩子，也不需要在父处理器返回时禁止合法子工作继续展开。

终态规则：

| 终态 | 必须满足的条件 |
|---|---|
| 成功 | 主处理器正常返回；全部必要工作成功；本次清理成功 |
| 失败 | 全部必要工作已实际结束，且存在未恢复的业务、执行或清理错误 |
| 已取消 | 全部必要工作已实际结束；确实因有效取消而停止；没有更高优先级的失败 |

失败优先于取消。收到取消请求但所有工作仍正常完成，可以判成功；不能仅凭令牌置位改成已取消。

已登记必要工作即使异常被父处理器观察到，也不能自动从失败集合消失。可恢复的重试或替代逻辑应封装在该工作内部，在它最终返回前完成恢复。

主处理器失败时，建议向仍存活的必要子工作发出故障收尾取消请求，然后继续等待真实结束；最终仍是失败，不降级成用户取消。正常 Stop 不触发这种取消。

发布终态、移出在途集合、封闭信道应作为一次完成提交；调用方随后观察到不可变结果。宿主不无限保留已完成信道，调用方保留的 handle 只保留结果和必要摘要。

调度名额只限制根 Task；内部子工作不能重新竞争父任务占据的同一根名额，避免父等子死锁。

**未选方案：**不把内部短程工作重新提交成外部根 Task，否则它会失去父子归属，并在 Stop 关闭准入后被错误拦截。

#### 5. 事件执行方式、等待与错误语义

**设计决定与理由**

事件定义者声明策略，Registry 合并时校验，派发器执行。生命周期事件与 TaskEvent 的策略由 Runtime 固定，模块不能在绑定时改写。

```csharp
public enum EventDispatchMode
{
    SerialFailFast,
    SerialCollect,
    ParallelJoin
}

public sealed record EventDefinition<TEvent>(
    string Name,
    EventDispatchMode Mode,
    int MinimumHandlers = 0)
    where TEvent : Event;
```

第一版只保留有明确用途的三种模式：

| 模式 | 等待语义 | 错误语义 | 适用场景 |
|---|---|---|---|
| `SerialFailFast` | 依次执行；当前处理器及其必要工作完成后才执行下一项 | 首个失败后跳过尚未开始的处理器，等待已开始工作结束后返回错误 | 共同参数修改、顺序业务步骤 |
| `SerialCollect` | 依次执行完整参与名单，并等待必要工作 | 保留全部错误；不会因为一个异常就漏掉后续应执行项 | Stop 判定、注册收集、清理 |
| `ParallelJoin` | 启动独立处理器，等待全部已启动处理器和必要工作 | 汇总全部错误；一个失败不等于其他处理器已经结束 | 独立初始化、必要观察处理 |

清理使用 `SerialCollect` 时，生命周期驱动器还要检查每项清理是否仍安全；“尽力执行”不等于无条件释放依赖资源。

串行事件以显式 `order` 决定顺序，同值按冻结的计划顺序处理。依赖顺序由模块加载图决定，优先级不能替代依赖。

并行事件要求输入不可变，或给处理器隔离副本。框架不声称多个订阅者共同修改任意对象是安全的。

所有必要处理器都必须等待。遥测、UI 进度等非必要观察走有界的观察出口，其故障单独报告；不能因为一个 UI 观察者变慢，就把业务主执行重复调用或永久挂起。

三种语义严格分离：

- `OnStopRequested.Cancel()`：否决本次停止。
- `CancellationToken`：请求某项工作协作结束。
- 停止传播：第一版不提供通用 `StopPropagation()`；串行失败后的跳过由事件契约决定。

`OperationCanceledException` 只有能够关联本次有效取消来源时才判取消。无关令牌、未请求取消时主动抛出的取消异常，按失败处理。

正常 Stop 不取消已接纳的短程任务。停止等待的超时、调用者放弃等待，也不能宣布工作结束或释放其资源。

**未选方案：**不把所有事件统一成并行广播，因为顺序修改、Stop 判定和清理具有不同的等待与失败要求。

#### 6. 生命周期命名与单模块派发

**设计决定与理由**

统一使用“此刻执行这一阶段”的名称：

| 名称 | 含义 |
|---|---|
| `RuntimeEvents.OnLoading` | 执行模块已注册的加载工作 |
| `RuntimeEvents.OnStarting` | 完成启动准备，启动持续工作并等待就绪 |
| `RuntimeEvents.OnStopRequested` | 判断是否允许停止，可以否决 |
| `RuntimeEvents.OnStopping` | 已不可逆地进入停止，结束持续工作 |
| `RuntimeEvents.OnUnloading` | 释放本模块拥有的资源 |

不再混用 `OnStart` 与 `OnStarting`；“已启动／已卸载”是阶段完成后产生的观察状态，不承担初始化或清理责任。

```csharp
public abstract class LifecycleEvent : Event
{
    private protected LifecycleEvent();
    public ModuleId TargetModule { get; }
}

public abstract class CancelableLifecycleEvent : LifecycleEvent
{
    private protected CancelableLifecycleEvent();

    public bool IsCancelled { get; }
    public void Cancel();
}

public static class RuntimeEvents
{
    public sealed class OnLoading : LifecycleEvent;
    public sealed class OnStarting : LifecycleEvent;
    public sealed class OnStopRequested : CancelableLifecycleEvent;
    public sealed class OnStopping : LifecycleEvent;
    public sealed class OnUnloading : LifecycleEvent;
}
```

以上省略内置类型的内部构造声明。`RuntimeEvents` 仅作类型容器，不保存订阅、实例、状态或静态总线。

宿主内部使用定向派发入口：

```csharp
internal ValueTask<DispatchOutcome> DispatchLifecycleAsync<TEvent>(
    ModuleId target,
    LifecycleOperationId operation)
    where TEvent : LifecycleEvent;
```

参与者按 **事件类型 + 绑定所属模块** 过滤。当前正在加载 A，只执行 A 绑定的 `OnLoading`；不会先遍历 A、B，再各自广播一次全部处理器。

同一次 Stop 判定拥有一个操作 Id，各模块分别收到新事件对象。某模块内部共享该次否决状态，宿主再汇总全部模块的否决和错误。`Cancel()` 是单向、幂等的标记，只在有效判定期间接受调用。

第一版阶段内采用拓扑串行：

1. 全部模块加载完成。
2. 再依赖顺序启动。
3. 所有启动条件满足后开放外部业务。
4. 卸载严格按实际成功加载记录反序执行。

没有绑定某阶段处理器的模块不执行假定存在的初始化方法，但仍参与依赖与阶段状态记录。

加载部分失败时，当前模块清理其局部已取得资源；宿主回收此前成功加载模块。启动失败时，先结束已经启动的持续工作，再反序回收已加载模块。保留原始失败和清理失败，不进入 Running。

`OnUnloading` 是释放义务的执行位置，阶段完成通知不得再次释放相同资源。

**未选方案：**不在 `IRuntimeModule` 上增加一整套强制 `Load/Start/Stop/Unload` 方法，因为文档要求业务初始化只由实际注册的对应事件触发。

#### 7. OnStopRequested 处理器抛异常

**设计决定与理由**

建议采用：**判定失败，停止请求失败；不把异常算作允许，也不伪装成正常否决。**

执行完整判定名单，等待全部判定必要工作，最后按以下优先级汇总：

1. 存在处理器异常：`DecisionFailed`。
2. 无异常但至少一次 `Cancel()`：`Vetoed`。
3. 无异常、无否决：进入排空，然后执行不可逆停止。

```csharp
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

public sealed class RuntimeHost
{
    public Task<StopOutcome> StopAsync();
}
```

这里推荐的产品行为是：**DecisionFailed 结束这次停止尝试，恢复停止判定前仍然有效的业务准入，并向调用方返回全部错误。** 若同期发生了独立的模块／宿主故障，不得因恢复 Stop 门禁而覆盖故障门禁。

这要求明确约束 `OnStopRequested`：

- 只检查停止条件、表达否决。
- 不释放资源，不终止持续循环，不提交不可逆停止动作。
- 处理器不能等待“只有停止完成才会完成”的操作。
- 所有检查工作结束后，才能恢复门禁。

恢复准入不是忽略异常：此次 `StopAsync` 明确失败，诊断保留，调用方决定何时重新请求。

并发外部 Stop 共享同一个进行中的操作；完成后可以再次尝试。被当前停止流程等待的 Task 或生命周期处理器，不能同步等待自己的 `StopAsync`，应明确拒绝这种重入，避免自等待。

若判定处理器一直不返回，仍处于停止判定中；不能仅因等待超时就恢复门禁并让旧判定继续游离执行。Kill 保留独立入口，且只能由调用方明确选择。

进入 `OnStopping` 后的异常完全不同：不可恢复 Running，应保留错误并继续安全收尾；清理失败进入已确认的 Hanging。

**未选方案：**不把判定异常当作“同意停止”，因为没有完成的判定不能授权后续不可逆资源释放。

#### 8. 重复、并发及停止后调用 RuntimeHost.Create

**设计决定与理由**

推荐严格的 **Create-once**，不隐式返回旧实例。

```csharp
public sealed class RuntimeHost
{
    private RuntimeHost();

    public static RuntimeHost Create(
        IReadOnlyList<ModuleDefinition> definitions,
        RuntimeCapabilities capabilities);

    public Task StartAsync();
}

public sealed class RuntimeAlreadyCreatedException
    : InvalidOperationException;

public sealed class RuntimeCreationInProgressException
    : InvalidOperationException;
```

`Create` 的边界建议定为：

- 检查并固定输入快照。
- 确认进程创建权。
- 建立最小 Event 能力、登记内置事件。
- 创建模块并接上 RegisterEvent。
- 完成第一次锁定，返回尚未运行的宿主。

`StartAsync` 执行异步注册、第二次锁定、加载与启动。它返回成功才代表业务准入开放。

进程入口只保存最小创建状态；模块、注册表、订阅、工作和资源仍保存在唯一宿主上。创建权判断必须是原子的，不能采用“先检查为空，再构造”的竞态写法。

| 情况 | 建议行为 |
|---|---|
| 首次合法调用 | 创建并返回唯一实例 |
| 并发调用，已有创建正在进行 | 明确返回创建进行中错误，不再调用模块构造器 |
| 已创建后再次调用，无论参数是否相同 | 抛重复创建错误 |
| 宿主已经 Stopped 或 Hanging | 仍拒绝创建，不释放创建名额 |
| 创建前的纯输入预检失败 | 没有创建宿主，可以修正输入后重试 |
| 已取得创建权并进入宿主构造后失败 | 记录创建失败，保留已使用状态，不创建第二个实例 |
| `StartAsync` 失败 | 保留同一宿主及故障结果，按失败路径收尾；不允许靠 Create 重来 |

纯预检不能调用模块构造器或其他用户回调。能力缺失仍按已确认规则，在创建任何模块之前一次报告完整缺项。

对同一实例，建议一次完整生命周期，不支持停止后重启。并发启动共享正在执行的启动操作；运行中重复 Start 返回既有成功结果；停止中或已停止时 Start 明确拒绝。

另需限定部署前提：文档已经说明一个进程只加载一个包含 Runtime 的 NativeAOT 原生库。单份库内的静态创建闸门不能跨两个独立 NativeAOT 映像保证唯一性，不能用此机制宣称支持同时加载 hc 与 full。

**未选方案：**不采用 `GetOrCreate`，因为后续调用携带不同清单或 capabilities 时，静默返回旧实例会掩盖配置错误。

#### 9. 持续工作的登记、归属、故障与停止

**设计决定与理由**

持续工作由模块在 `OnStarting` 中登记并启动，归属于**该模块的生命周期作用域**，不归属于启动事件的短程作用域，也不挂在某次业务 Task 下。

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
    public void ReportReady();
}

public sealed class ContinuousWorkHandle
{
    public Task Ready { get; }
    public Task<ContinuousWorkOutcome> Completion { get; }
}

public sealed class RuntimeEvents.OnStarting
{
    public ModuleWorkScope ContinuousWork { get; }
}
```

最后一段只是补充 `OnStarting` 的成员草图，不表示重复声明类型。

持续工作具有三个独立观察点：

- **Ready**：启动条件满足，可以对外服务。
- **Failure**：运行中出现故障，应立即可见。
- **Completion**：循环及其必要收尾确实结束。

`Start` 先建立宿主登记，再调用委托；同步抛错也有归属。启动事件退出后关闭本次登记窗口，Runtime 等待已登记工作的 Ready，不等待整个循环结束。

就绪前抛错、取消或提前退出均使启动失败。`ReportReady` 不能覆盖已经发生的失败；工作在 Ready 后、整体启动完成前退出，也不能让宿主带着失效的必要工作进入 Running。

运行中错误至少经三条路径可见：

```csharp
public sealed class RuntimeHost
{
    public RuntimeSnapshot GetSnapshot();

    public IAsyncEnumerable<RuntimeFault> WatchFaultsAsync(
        CancellationToken cancellation = default);
}
```

- 该工作的 `Completion` 返回真实结果。
- 宿主快照记录工作及所属模块的失效状态。
- 故障观察流通知调用方；日志只作补充。

观察流应有界，丢失通知时能检测缺口并重新读取快照；不能靠无限保留历史保证可观察。

**建议的第一版故障范围：**持续工作默认属于模块的必要工作。意外失败使所属模块及其依赖方不可用于新 Task；无关模块可以继续服务，宿主明确表现为降级状态。已有工作仍受追踪，不因标记模块失效而提前释放资源。不自动重启、不自动重试、不自行退出进程。

停止时区分“停止产生新根工作”和“终止循环”：

1. 进入 Stop 判定时关闭根工作准入，持续工作产生的新根 Task 同样受此门禁约束。
2. 已接纳 Task 的必要后代仍可继续，持续循环仍可服务这些已接纳工作。
3. 若 Stop 被否决，恢复准入，循环继续工作。
4. 若获准，先等待已接纳短程 Task 及必要后代自然结束。
5. 进入不可逆 `OnStopping` 后，先向所有持续工作发出结束信号，再等待它们真正结束。
6. 只有相关工作结束，才能反序卸载其资源。

“先全部发信号，再等待”可以避免先等 A 退出、A 却依赖尚未收到信号的 B 的死锁。需要业务协议停止的模块仍可绑定 `OnStopping`；框架发出协作信号不代替模块的业务收尾。

如果循环不响应停止，保留真实未完成状态及资源。超时不能伪造 Completion，Kill 仍是调用方明确选择的旁路。

**未选方案：**不把无限循环登记为启动事件或普通 Task 的必要子工作，否则启动或 Stop 排空会在发出结束信号前永久等待它。

#### 需要用户决定的问题

以下是产品取舍，不能仅靠执行机制替用户定案；上文已给出推荐默认值。

| 问题 | 倾向的选项 | 理由 |
|---|---|---|
| Stop 判定异常后是否恢复业务准入？ | 本次停止返回 `DecisionFailed`，恢复此前仍有效的准入 | 判定阶段禁止不可逆动作，失败不应悄悄等同于停止获准；也避免应用被一次检查异常永久锁住 |
| 持续工作故障影响全局还是局部？ | 默认使所属模块及依赖方失效，无关模块继续服务 | 能明确暴露故障，同时避免一个独立模块故障使全部能力失效；若产品只接受全有或全无，应改为全局关闭新准入 |
| 是否需要“非必要”的持续工作？ | 第一版全部按必要工作处理 | 没有能力级依赖模型时，声称某循环失败不影响服务很容易失真；确有遥测等用途后再增加显式分类 |
| 停止后的实例是否允许重新启动？ | 不允许；一个进程一次完整生命周期 | 省去重新注册、资源再获取和旧句柄失效的额外协议，同时保持进程唯一约束 |
| 所有资源已释放，但 `OnStopping` 仍有错误，状态如何表示？ | `Stopped`，同时返回非成功的停止结果 | 区分“物理上已结束”和“整个操作无错误”；只要卸载失败或仍有义务未完成，仍按已确认规则进入 Hanging |
| 是否需要默认停止超时？ | 不设自动终止期限；允许调用方限制等待并查询未完成工作 | 不知道实际任务时长，任意默认值会误判；等待超时不取消宿主收尾，也不自动升级 Kill |
| Kill 使用哪种终止策略？ | 调用方显式提供；缺失或失败时明确报告终止未完成 | Runtime 无法可靠强杀任意托管线程，也不能擅自退出消费者进程 |

构造前预检失败可重试、实际创建失败后不再创建第二个实例，是上文 Create-once 建议的一部分；若希望更强的恢复能力，需要另行设计**同一实例**的恢复协议，不能放开第二次实例创建。

#### 模块作者视角的完整示例

为遵守“不写实现代码”，下面使用**成员签名 + 接线清单 + 完整调用时序**展示一个模块需要写什么，不提供方法体。

例子是 `Acme.Install`：构造时借用下载器和配置；加载时打开自己拥有的安装索引；执行一次安装 Task；维护一个持续监测循环；停止后关闭索引。

```csharp
[RuntimeModule]
internal sealed class InstallModule : IRuntimeModule
{
    public InstallModule(
        IDownloader downloader,
        InstallOptions options);

    public void Connect(ModuleWiring wiring);

    private ValueTask Register(
        RegisterEvent e, EventContext context);

    private ValueTask Load(
        RuntimeEvents.OnLoading e, EventContext context);

    private ValueTask Start(
        RuntimeEvents.OnStarting e, EventContext context);

    private ValueTask<InstallResult> Install(
        TaskEvent<InstallRequest, InstallResult> e,
        EventContext context);

    private ValueTask CheckStop(
        RuntimeEvents.OnStopRequested e, EventContext context);

    private ValueTask Stop(
        RuntimeEvents.OnStopping e, EventContext context);

    private ValueTask Unload(
        RuntimeEvents.OnUnloading e, EventContext context);

    private ValueTask Monitor(
        ContinuousWorkContext context);

    private ValueTask PrepareInstall(
        BeforeInstall e, EventContext context);
}

public sealed record InstallRequest(string Source, string Target);
public sealed record InstallResult(string InstalledPath);

public sealed class BeforeInstall : Event
{
    public BeforeInstall(InstallRequest request);
    public InstallRequest Request { get; }
}
```

模块作者填写的接线与注册清单如下：

| 位置 | 作者声明的内容 |
|---|---|
| 项目元数据 | `PackageId = Acme.Install`，版本、作者、说明沿用已定案生成规则 |
| 构造函数 | 保存借用的 `IDownloader` 和配置；不开索引、不启动循环 |
| `Connect` | 经 `SubscribeRegister` 接上 `Register` |
| `Register` | 创建局部 RegistryDraft，完成以下定义与绑定，再 Seal／Submit 一次 |
| Task 定义 | `TaskKey<InstallRequest, InstallResult>("Acme.Install", "Install")` |
| Task 绑定 | 将上述 key 绑定到唯一主处理器 `Install` |
| Event 定义 | `BeforeInstall`，采用 `SerialFailFast` |
| Event 绑定 | 将 `BeforeInstall` 绑定到 `PrepareInstall` |
| 生命周期绑定 | 分别绑定 `Load`、`Start`、`CheckStop`、`Stop`、`Unload` |

从开始到结束的时序是：

1. **接入。**消费者调用生成的 `RuntimeApplication.Create(capabilities)`。生成工厂按类型取得构造参数；Runtime 创建模块、调用 `Connect`，完成第一次锁定。

2. **注册。**消费者调用 `StartAsync`。Runtime 定向派发 RegisterEvent，模块提交上面的清单。所有注册处理器及必要工作结束后，Runtime 校验并完成第二次锁定。

3. **加载。**Runtime 定向派发 `OnLoading`。`Load` 打开模块自有索引；若打开到一半失败，清理局部资源并报告失败。成功后 Runtime 记录模块实际加载次序。

4. **启动。**`Start` 通过 `e.ContinuousWork.Start` 登记 `Monitor`。监测器完成准备后调用 `ReportReady`。Runtime 等待其就绪后进入 Running，监测循环继续运行。

5. **执行一次 Task。**调用方提交 Install key 与 `InstallRequest`，得到 `TaskRun<InstallResult>`。Runtime 为这一次执行创建独立 TaskEvent 信道，只调用一次 `Install`。

6. **执行内部工作。**`Install` 经 `context.PublishAsync` 发布新的 `BeforeInstall` 对象并等待；随后经 `context.Work.Run` 登记必要下载步骤。索引提交等待下载完成，主处理器返回 `InstallResult`。Runtime 在全部必要工作及本次清理结束后，才完成 `TaskRun.Completion`。

7. **请求停止。**调用方调用 `StopAsync`，新根工作准入关闭。`CheckStop` 检查产品约束；确需否决时调用 `e.Cancel()`，本次 Stop 返回 Vetoed，准入恢复。它不关闭索引、不终止监测器。

8. **完成停止。**下一次 Stop 无否决，Runtime 等已接纳安装及其内部工作自然完成。随后进入 `OnStopping`，向监测器发结束信号；`Stop` 完成必要业务收尾，Runtime 等监测器实际退出。

9. **卸载。**Runtime 按实际加载反序派发 `OnUnloading`。`Unload` 释放模块自己的索引，不释放借用的下载器。所有义务成功完成才进入 Stopped；清理失败保留错误并进入 Hanging。

第一轮骨架就应把这九步全部留在真实执行路径中：先用简单业务走通注册、执行、等待、否决、停止与卸载，再补复杂调度和诊断；必要工作追踪、基本失败传播与资源归属从首次接入时就存在。

再次确认：本次仅做架构设计，只依据当前目录的 5 份文档；没有读取现有代码、访问目录外路径或修改任何文件，也没有推翻已确认规则。

### 第二轮：根工作入口、跨模块调用与 Registry 提交方式

以下只回答这两个问题，并修订相关签名；不读取代码或目录外内容，不修改文件，不写实现。你补充的四项推断作为本轮设计前提继续沿用，不再列为待决问题。

#### 问题 1：模块在处理器之外发起工作，以及跨模块调用

第一版确实缺少模块持有的根工作入口，也没有把跨模块调用接入工作归属。建议补齐为：**模块级入口创建根工作，执行作用域内的入口创建子工作。两者显式区分，不根据调用线程或环境上下文猜测。**

##### 持续工作使用模块级根工作入口

在 `Connect` 时，由 Runtime 向模块提供一个 `ModuleWorkPort`。模块可以保存它；持续工作上下文也提供同一个入口。

它不是 RuntimeHost，也不是服务定位器，只允许：

- 发布本模块定义的事件，创建一次短程根事件执行。
- 提交已注册的 Task，创建一次根 Task 执行。

这个入口绑定宿主和发起模块，模块不能指定另一个身份，也不能携带“绕过准入”参数。它只能在宿主允许普通业务执行时使用；构造、注册、加载期间不能通过它提前运行业务。

**持续循环发布的普通事件也必须受根工作门禁和排空管理。**否则虽然关闭了 Task 提交，循环仍能不断发布带业务处理器的事件，Stop 就没有明确边界。

例如监测循环检测到新版本：

1. 经 `context.RootWork.PublishAsync(...)` 发布本模块的 `VersionDetected`。
2. Runtime 原子检查准入并登记这次短程根事件执行。
3. 等待其处理器及必要后代结束，完成根执行登记。
4. 循环继续运行。

如果循环需要发起一次安装，则使用 `RootWork.Submit(...)`，获得原有的 `TaskRun<TResult>`。它与外部提交一样受准入限制。

Stop 判定期间，这两种新根工作都拒绝接纳；否决后重新允许。循环仍然存在，不能把预期的“准入关闭”当作循环自身故障。源事件被拒绝后是丢弃、暂存还是稍后重新检测，属于该业务源的处理逻辑，Runtime 不偷偷建立待恢复的无限队列。

根工作入口不能用于不可逆停止后的清理通知。必要停止操作仍由 `OnStopping` 的受托管作用域完成；状态观察使用 Runtime 的观察出口。

##### 跨模块业务能力，第一版通过类型化 Task 调用

建议第一版**不再增加一套模块服务注册表**。模块对外提供的、有输入和返回值的受托管业务能力，用已注册的 Task 表达；Event 用于挂载、协作及通知。

例如：

- Storage 提供 `ReadEntry`、`CommitInstall` 等具有业务意义的 Task。
- Install 在安装执行中调用这些 Task。
- Storage 自己拥有存储资源，资源实例不经能力表转交给 Install。
- Storage 内部普通方法继续是普通方法，不要求把每个底层方法都变成 Task。

Task key 与输入／输出类型由提供方的公开契约定义。模块引用该契约，再由 Runtime 检查已生成的模块依赖及提供方就绪状态；不允许借一次调用绕开依赖关系。

**有返回值的调用使用 Task；不让调用方通过向对方事件对象写回结果来模拟 RPC。**事件仍适合“安装前修改方案”“版本检测完成”等扩展点，其发布权遵守第一版规则。

##### 执行中调用对方 Task，属于必要子工作

在 `EventContext.Work` 或 `WorkContext.Work` 上增加 `InvokeAsync`。它复用已注册 Task 的定义、唯一执行绑定和统一 Event 派发机制，但采用当前工作作用域的子调用入口。

| 发起入口 | 归属 | Stop 关闭根准入后 |
|---|---|---|
| `ModuleWorkPort.Submit` | 新根 Task | 拒绝 |
| `ModuleWorkPort.PublishAsync` | 新根事件执行 | 拒绝 |
| `WorkScope.InvokeAsync` | 当前执行的必要子 Task | 有效父作用域内仍允许 |
| `WorkScope.PublishAsync` | 当前执行的必要子事件 | 有效父作用域内仍允许 |

因此，Install 已被接纳后，即使 Stop 已关闭根准入，它仍能调用完成本次安装所必需的 Storage Task。

子调用具备以下规则：

- **先登记父子关系，再执行。**即使调用者没有等待返回对象，父作用域也不会漏掉它。
- 每次子 Task 有自己的 TaskEvent 对象、输入、结果和执行记录，不与父 Task 混用数据。
- 如将这组状态称为子信道，它由父信道拥有；仍属于同一次根执行的归属树，不产生新的外部根登记。
- 父任务必须等待子调用及其后代真实结束。
- 父取消向子调用传播；子调用不能直接改写父取消源。
- 必要子调用失败进入父执行的失败汇总，不因调用者观察或捕获异常而自动抹掉。
- 子调用不能重新竞争父任务占据的根调度名额。
- 调用对方处理器时，执行身份切换到提供方模块；后续发布权限及依赖检查按实际执行模块判断，同时保留原始根执行与调用链。

普通内部短程步骤继续使用 `WorkScope.Run`，不需要为每一步创建子 Task 或子信道。

第一版还应拒绝同一调用链上的 Task 递归重入，并要求跨模块调用遵循无环的依赖方向。这样能尽早阻止明显的 A 等 B、B 又等 A 的调用死锁。

**未选做法：**暂不提供 `GetService<T>()` 或直接暴露其他模块的资源对象，否则还要新增提供者配对、就绪、失效、借用和释放协议，而且容易绕开执行追踪。

#### 问题 2：Registry 是否需要显式 Seal／Submit

**两种方式都可以。在当前约束下，我建议改成你提出的隐式提交。**第一版的显式流程不是必须的，其收益不足以抵消额外协议。

显式 `CreateRegistry／Seal／Submit` 主要提供这些能力：

| 显式操作 | 实际收益 |
|---|---|
| `CreateRegistry` | 可以建立多个候选清单，最后选择一个 |
| `Seal` | 在处理器结束前冻结清单，避免辅助代码继续修改 |
| `Submit` | 明确表达“选择这一份”，能区分忘记提交与主动提交空清单 |

但这些收益**不是安全合并所必需的**：

- 每个模块的 Registry 可以由 Runtime 直接创建并绑定归属，不需要作者创建它来证明身份。
- 处理器及必要工作结束时，由 Runtime 原子关闭写入，也能禁止晚到登记。
- 处理器失败时整份丢弃，也能避免接受部分注册。
- 所有模块完成后才全量校验、原子发布，也能避免半注册宿主。
- 即使显式 Submit，仍然必须等待处理器及必要工作结束，因为 Submit 之后仍可能抛异常。

换言之，**显式 Submit 不能代替执行完成，Seal 也不能代替注册窗口的生命周期管理。**它们主要支持更复杂的清单编辑模式；目前没有这个需求。

建议修订为：

1. Runtime 为每个模块的 RegisterEvent 创建一份可登记的 Registry。
2. 处理器通过 `e.Registry` 登记定义和绑定。
3. 经当前作用域登记的必要工作，也可以参与登记。
4. 处理器返回但必要工作未结束时，注册窗口仍有效。
5. 整个注册执行结束时，Runtime 原子关闭窗口并生成内部不可变快照。
6. 任一处理器或必要工作失败，该模块提交失败；整批候选注册结果不发布。
7. 全部注册成功后，执行全量冲突检查和第二次锁定。

这里应区分：

- **本模块窗口关闭**：该模块不能再修改自己的 Registry。
- **第二次锁定**：全部模块的清单成功合并，正式注册结果生效。

并行必要工作允许登记，但每次增加定义或绑定必须是原子操作；处理器顺序不能由并发登记的到达先后决定，仍使用显式排序契约。

正常结束而没有添加任何内容，就是合法空 Registry。少了“忘记 Submit”的检查，但也删除了“重复 Submit”“提交错误清单”“Seal 后又想补一项”等人为协议错误。遗漏必要 Task 的问题应由定义与绑定完整性检查发现，而不是依赖一个提交动作。

保存 Registry 引用不会延长窗口，关闭后任何登记都明确报错。框架仍不能自动识别所有未登记的后台代码；显式方案同样不能解决这一点。

**未选做法：**不同时保留隐式提交与可选 Submit，否则会出现两个提交时刻，增加异常与晚到登记的解释成本。

#### 受影响部分的修订签名草图

以下只表达新增或变化的 API；原有定义类型、结果类型和执行规则继续沿用。

##### 模块持有的根工作入口

```csharp
public sealed class ModuleWiring
{
    // 可由模块保存；不延长接线窗口，也不授予生命周期推进权。
    public ModuleWorkPort RootWork { get; }

    public void SubscribeRegister(
        EventHandler<RegisterEvent> handler);
}

public sealed class ModuleWorkPort
{
    // 创建受根准入约束的短程事件执行，只允许发布本模块定义的事件。
    public ValueTask PublishAsync<TEvent>(
        TEvent e,
        CancellationToken cancellation = default)
        where TEvent : Event;

    public TaskRun<TResult> Submit<TInput, TResult>(
        TaskKey<TInput, TResult> key,
        TInput input,
        CancellationToken cancellation = default);
}

public sealed class ContinuousWorkContext
{
    public CancellationToken Stopping { get; }
    public ModuleWorkPort RootWork { get; }

    public void ReportReady();
}
```

##### 统一的子工作入口

将原来分散在 `EventContext`、`WorkContext` 上的发布方法收进 `WorkScope`，使归属在调用位置直接可见。

```csharp
public sealed class EventContext
{
    public ModuleId SubscriberModule { get; }
    public CancellationToken Cancellation { get; }
    public WorkScope Work { get; }
}

public sealed class WorkContext
{
    public CancellationToken Cancellation { get; }
    public WorkScope Work { get; }
}

public sealed class WorkScope
{
    public ValueTask PublishAsync<TEvent>(TEvent e)
        where TEvent : Event;

    // 调用已注册 Task，登记为当前作用域的必要子调用。
    public ValueTask<TResult> InvokeAsync<TInput, TResult>(
        TaskKey<TInput, TResult> key,
        TInput input);

    public WorkHandle Run(
        string name,
        Func<WorkContext, ValueTask> operation);

    public WorkHandle<T> Run<T>(
        string name,
        Func<WorkContext, ValueTask<T>> operation);
}
```

`InvokeAsync` 成功返回类型化结果；失败或取消通过对应异常到达等待者，同时保留必要子调用的追踪结果。它不调用公开根提交入口。

##### 隐式提交的 Registry

```csharp
public sealed class RegisterEvent : Event
{
    internal RegisterEvent();

    public Registry Registry { get; }
}

public sealed class Registry
{
    // 只能由 Runtime 创建，并绑定模块与注册执行窗口。
    internal Registry();

    public void DefineTask<TInput, TResult>(
        TaskDefinition<TInput, TResult> definition);

    public void BindTask<TInput, TResult>(
        TaskKey<TInput, TResult> key,
        TaskHandler<TInput, TResult> handler);

    public void DefineEvent<TEvent>(
        EventDefinition<TEvent> definition)
        where TEvent : Event;

    public void BindEvent<TEvent>(
        string bindingId,
        EventHandler<TEvent> handler,
        int order = 0)
        where TEvent : Event;
}
```

删除公开的 `RegistryDraft`、`CreateRegistry`、`Seal` 和 `Submit`。窗口关闭、快照生成与事务式合并由 Runtime 内部完成。

本轮仅修订设计与签名，没有读取现有代码、访问目录外路径、修改文件或编写实现。

### 第三轮：命名与定稿确认

1. **两个名字都改：**
   - `Event` → **`RuntimeEvent`**：明确它是 Runtime 托管执行的事件基元，避免公共类型名过于通用。
   - `EventHandler<TEvent>` → **`RuntimeEventHandler<TEvent>`**：直接消除与 `System.EventHandler<TEventArgs>` 的 CS0104 冲突，模块作者不需要别名或完整限定名。

   相应的泛型约束统一改为 `where TEvent : RuntimeEvent`。具体事件名 `RegisterEvent`、`TaskEvent<TInput, TResult>` 及已确认的 `CancelableLifecycleEvent`、`RuntimeEvents.OnStopRequested` 保持不变。

2. **已定稿。**第一版加第二轮修订，再加本轮命名调整，就是我的最终设计方案，没有其他主动修改项。“定稿”指这份设计提案完成，不把先前的建议或推断改称为用户已确认规则。

本轮未读取代码或目录外内容，未修改文件，未编写实现。

### 第二阶段说明（Claude 写）

第四轮起是第二阶段，这一阶段做了两件事：

- 审查前三轮方案里的过度工程化和防御性设计。Claude 挑出 8 处需求文档没有要求、第一版也走不到的机制，每轮问两处（第四到第七轮），请 Astra 给出对应的需求段落或业务场景，答不出来的就删掉。
- 用户指出 Runtime 的需求建立在 Haytham Launcher（下称 HL）之上，又说明 HL 原生库现有的任务／事件框架是 Runtime 的前身，会跟着做一次小重构。所以第八轮起，Claude 把 HL 的四份需求文件（产品术语、目标架构、C 接口的生命周期与任务章节、10 月 26 日的调度讨论纪要，都不含代码）放进 Astra 的目录，请它按 HL 的业务需求重审，HL 现有的实现方式不算约束。
  - 第八轮的提问里，Claude 把用户的话推断成“以满足 HL 的现有任务模型为准”，说得过紧，Astra 因此照搬了几处 HL 现有做法。第九轮更正后，这些结论已经改掉，阅读时以第九轮为准。

和前三轮一样，后一轮的修订取代前一轮的对应部分。第二阶段结束后各部分的最终状态，见第 5 节更新后的差异表。

### 第四轮：MinimumHandlers、bindingId、order 与事件对象的 ExecutionId

#### 第 1 处：`MinimumHandlers`、`bindingId` 和 `order`

**这三个参数都去掉。**文档要求明确执行责任和必要的顺序，但不足以支持这三个通用配置项。

##### `MinimumHandlers`：去掉

没有找到普通事件必须至少有若干订阅者的要求，也没有第一版必经的业务场景。

最接近的是：

- `runtime-authoring-roadmap.md` → 第 6 章「亲手完成」第 3 项：  
  > 每个请求有明确执行责任，不能任取第一个订阅者返回值，也不能把多个进度观察者当成多个业务执行者。

这是 **Task 必须有唯一主执行绑定**的依据，不是普通 Event 配置最少订阅人数的依据。

修订规则：

- 普通事件允许零个处理器，此时正常完成。
- Task 仍要求恰好一个主执行绑定，由 Task 注册校验直接保证。
- RegisterEvent 的接线要求由模块接入协议保证，不通过通用 `MinimumHandlers` 表达。

##### `bindingId`：去掉

没有具体场景证明，同一模块给同一事件绑定两个处理器就应当报错。例如一个模块分别绑定“检查安装目录”和“补充安装参数”，完全可以合法执行。

而且字符串标识只能发现**标识重复**：同一处理器换两个标识仍能重复绑定，因此它不能可靠证明业务接线正确。

修订规则：

- 每次 `BindEvent` 都添加一个处理器。
- 允许同一模块对同一事件绑定多个处理器。
- 删除“绑定标识重复”检查，也不新增委托相等性或方法身份去重。
- Task 的唯一主执行者检查继续保留，与普通事件绑定数量分开。

##### `order`：去掉，保留串行事件的明确顺序

文档确实要求部分事件有顺序：

- `haytham-runtime-lifecycle.md` → 「第三类：挂载点与事件执行生命周期」用途表，“修改共同参数或依次加工方案”：  
  > 显式串行，确定顺序和修改权限。

但这只要求**顺序明确**，不要求额外的数字优先级。第一版的具体场景可以用现有顺序表达：

- 跨模块处理器：依赖提供方先于依赖方，由模块加载计划确定。
- 同一模块的多个处理器：例如先检查目录、再补充参数，由顺序登记表达。

目前没有一个已确定场景，必须让数字 `order` 覆盖上述顺序。因此删除它。

修订后的规则：

- 串行事件按冻结的模块计划顺序执行；同一模块内按绑定登记顺序执行。
- 同一模块内确实有先后要求的绑定，必须顺序登记；并发登记不承诺相对顺序。
- 并行事件不承诺处理器之间的执行先后。
- 生命周期模块推进仍服从依赖图，卸载仍按实际成功加载顺序反序进行，不受普通事件绑定顺序改变。

受影响签名：

```csharp
public sealed record EventDefinition<TEvent>(
    string Name,
    EventDispatchMode Mode)
    where TEvent : RuntimeEvent;

public sealed class Registry
{
    public void BindEvent<TEvent>(
        RuntimeEventHandler<TEvent> handler)
        where TEvent : RuntimeEvent;
}
```

#### 第 2 处：事件对象的 `ExecutionId` 与重复派发检查

**移除事件对象上的 `ExecutionId`，也移除“重复或并发派发同一个对象必须由 Runtime 检测并拒绝”的机制。保留每次执行的身份，但放在执行记录与上下文中。**

文档要求的是执行隔离和可追踪：

- `runtime-authoring-roadmap.md` → 第 3 章「验收」：  
  > 同一 Runtime 的两次 Event 派发执行数据不混用。
- 同文档 → 第 7 章「亲手完成」第 1 项：  
  > 每次 Task 执行建立独立 TaskEvent 信道，记录实例、模块、本次执行身份、输入、取消状态和结果。
- `development.md` → 「日志与机密」：  
  > 日志应能关联 Runtime、模块、阶段和任务。

这些支持**每次执行有独立记录和关联身份**，但没有要求把身份写入事件对象，也没有要求维护对象使用历史来检测重复派发。

修订规则：

- 调用方每次派发模块事件时创建新的事件对象；生命周期事件和 TaskEvent 由 Runtime 每次创建。
- 每次派发都建立独立执行记录、上下文及必要工作追踪。
- 事件对象只承载本次业务数据；不保存执行身份、派发状态或“已使用”标记。
- Runtime 不为检测重复派发而保存事件对象历史。
- 复用同一对象违反调用契约，但第一版不提供专门的运行时检测。
- Stop 判定的操作身份由生命周期驱动器及执行记录保存，不需要借助事件对象上的 `ExecutionId`。

`ExecutionId` 仍有具体用途：关联处理器错误、必要子工作与本次派发。它可以直接放在 `EventContext`；框架自己的日志也可直接读取执行记录。

受影响签名：

```csharp
public abstract class RuntimeEvent
{
    protected RuntimeEvent();
}

public sealed class EventContext
{
    public ExecutionId ExecutionId { get; }
    public ModuleId SubscriberModule { get; }
    public CancellationToken Cancellation { get; }
    public WorkScope Work { get; }
}
```

本轮只审这两处，未读取代码或目录外内容，未修改文件，未编写实现。


### 第五轮：调度名额、递归重入与执行期依赖方向检查

#### 第 3 处：只限制根 Task 的调度名额

**第一版不设执行名额，删除“子调用不能竞争父任务占据的根名额”这条规则。**

文档依据是：

- `haytham-runtime-lifecycle.md` →「第四类：单次业务任务生命周期」：  
  > 任务池不意味着每个 Task 都有独立线程，其并发与调度规则待定。
- `runtime-authoring-roadmap.md` → 第 7 章「亲手完成」第 7 项：  
  > 先不引入复杂线程调度器。若加入并发上限，必须避免父任务占着唯一名额等待无法启动的子任务。

第二句明确是**加入并发上限后的条件要求**，不能反过来成为第一版引入名额的理由。目前也没有已确定的业务负载要求第一版限流。

修订规则：

- 根 Task 通过准入检查并完成在途登记后，直接进入统一执行流程。
- 子 Task 完成父子登记后，同样直接进入执行流程。
- 不设置 Runtime 执行名额，不建立等待名额的调度队列，也不为每个 Task 分配专用线程。
- “任务池”只负责执行身份、归属、状态、取消、结果及在途追踪。
- `TaskExecutionOptions` 中的并发上限、调度名额配置删除；若它没有其他已保留的职责，则删除整个类型及定义中的 `Options` 参数。
- 业务对共享文件、存储事务等资源的同步仍由业务负责。

因此，**由父任务占满 Runtime 名额而造成的父等子死锁随之消失**。这不代表业务锁、循环等待等其他死锁也消失。

受影响签名：

```csharp
public sealed record TaskDefinition<TInput, TResult>(
    TaskKey<TInput, TResult> Key);
```

#### 第 4 处：递归重入检查与执行期依赖方向检查

##### 执行期依赖方向检查：去掉

先纠正上一版的一处混淆：**注册时检查跨模块绑定，不等于检查了处理器内部的跨模块调用。**

例如，Install 在自己的 Task 处理器里调用 Storage Task；这个调用并不必然在 Registry 中形成“Install 绑定 Storage 定义”的记录。因此，执行期检查理论上能发现注册阶段未见过的调用，例如调用者手工构造 key，调用一个未声明依赖的模块。“看得到 key”本身也不是严格的运行时权限保证。

但这个例子只是可以构造出的越界用法。当前文档没有要求 Task 调用成为模块访问控制边界，也没有第一版业务场景要求 Runtime 检测这种越界。

真正要求保留的是：

- `haytham-runtime-lifecycle.md` →「第二类：模块生命周期」：  
  > B 依赖 A 的能力，B 必须等待 A 达到所需状态；不能靠订阅顺序猜测就绪。

它支持**提供方就绪检查**，不直接要求在每次调用时重新验证依赖方向。

修订规则：

- 模块依赖图继续用于加载、就绪协调及卸载顺序。
- 既有的跨模块绑定注册检查保留。
- Task 调用时检查 key 是否已注册、输入输出契约是否匹配、提供方是否可用，以及当前作用域是否有效。
- 删除每次调用时“调用者必须位于提供方依赖方向”的检查。
- 模块按已声明依赖使用其他模块的能力，作为模块作者契约；第一版不增加通用调用权限校验。

这不是认定两项检查完全重复，而是删除当前没有明确需求支撑的执行期防御。

##### 同一调用链上的 Task 递归重入检查：去掉

文档要求考虑父子工作死锁：

- `haytham-runtime-lifecycle.md` →「第四类：单次业务任务生命周期」：  
  > 后续设计需要防止父任务占满执行名额后等待无法启动的子任务，以及生命周期回调等待依赖该回调完成才能就绪的能力等死锁。

这里指出的是具体的框架协调问题，没有要求 Runtime 识别所有模块递归调用。不能据此扩大成禁止同一 Task 在调用链上再次出现。

而且，**递归调用不必然死锁**。同一个处理目录的 Task，可以对子目录递归调用自己，在输入逐步缩小后正常结束；无条件禁止会排除这种合法行为。错误递归可能无限展开，也可能因业务锁或其他等待形成死锁，不统一表现为父等子永久等待。

修订规则：

- 不扫描调用链判断 Task key 是否重复，也不因重复而拒绝调用。
- 每次递归调用仍是独立、受追踪的必要子执行，保留父子归属、取消传播和完成等待。
- 模块作者负责递归终止条件及业务等待关系。
- 未结束的工作继续表现为未完成；Runtime 不因此伪造失败终态、释放资源或自动 Kill。
- 删除的是通用递归检测，不撤销已经明确的框架自等待限制，例如被 Stop 排空的处理器不能等待自己的 Stop 完成。

`WorkScope.InvokeAsync` 的签名不变，只删除上述两项执行期检查。

本轮只审这两处，未读取代码或目录外内容，未修改文件，未编写实现。


### 第六轮：故障观察与进度观察通道

#### 第 5 处：快照、故障观察流与缺口检测

**删除 `GetSnapshot`、`WatchFaultsAsync`，以及有界故障流、缺口检测和快照补偿机制。第一版保留工作的 `Completion`，加一个宿主级故障回调。**

文档要求的是：

- `development.md` →「错误与清理」：  
  > 请求须有明确完成结果；持续工作失败也必须可观察，不能只结束后台线程而假装模块仍然正常。
- `runtime-authoring-roadmap.md` → 第 8 章「亲手完成」第 1 项：  
  > 给持续工作指定所属模块，暴露启动就绪、运行失败和最终完成三个可观察结果。
- 同章第 7 项：  
  > 持续工作意外失败须有观察通道，并定义受影响能力是否仍可调用。

这些要求没有指定异步流、快照或断流恢复协议。

##### 第一版具体需要哪条路径

两条保留路径服务不同的持有者：

| 路径 | 谁使用 | 作用 |
|---|---|---|
| `ContinuousWorkHandle.Completion` | Runtime、持有 handle 的模块 | 等待该工作真正结束，取得其结果；停止收尾需要它 |
| 宿主级故障回调 | `Program.cs` 等消费者入口 | 无须拿到各模块内部的工作 handle，也能立即知道运行故障 |

具体场景：Install 模块的监测循环启动成功，随后因存储访问异常退出。`Program.cs` 没有监测器 handle，单独保留 `Completion` 不能直接通知它；宿主回调将模块、工作身份及错误交给入口，入口可以打印错误并决定是否请求 Stop。

回调在 `StartAsync` 前接上即可，不需要 UI 或远程监控。只保留快照也能通过轮询发现失败，但会要求消费者自行编写轮询，且仍不能代替 Runtime 等待工作实际结束的完成对象。

**有界流加缺口检测是为慢消费者、断开后恢复等场景准备的；当前没有这样的场景，应删除。**

修订签名：

```csharp
public sealed class RuntimeHost
{
    public event Action<ContinuousWorkFailure>? ContinuousWorkFailed;
}

public sealed record ContinuousWorkFailure(
    ModuleId Module,
    string WorkName,
    RuntimeFailure Failure);

public sealed class ContinuousWorkHandle
{
    public Task Ready { get; }
    public Task<ContinuousWorkOutcome> Completion { get; }
}
```

修订规则：

- 故障发生后，Runtime 先记录故障并更新受影响模块的可用状态，再调用已接上的回调。
- 回调是直接通知，不建立后台观察队列，不提供历史重放。
- 回调只负责快速接收、报告故障，不同步等待宿主停止，避免阻塞收尾。
- 回调抛错记录为独立的报告错误，不能覆盖原始工作故障或阻止必要收尾。
- `Completion` 保留最终真实结果；未订阅回调不影响 Runtime 自身追踪故障和等待工作结束。

这里的 C# `event` 是消费者通知入口，不是可推进生命周期的 `RuntimeEvent`。

#### 第 6 处：进度、遥测的独立观察通道

**从第一版删除这个通道及其订阅／退订协议；等第一个具体进度功能出现后再设计。**

不过需要纠正一点：**文档确实提到了进度需求，遥测则没有同等明确的依据。**

- `haytham-runtime-lifecycle.md` →「第四类：单次业务任务生命周期」：  
  > Runtime 提供任务身份、状态和结果、父子归属、取消与等待、调度及并发限制、进度传递和资源作用域。
- `runtime-authoring-roadmap.md` → 第 6 章「验收」：  
  > 新增进度观察者不会使下载执行两次；失败不会被悄悄当成成功。
- `haytham-runtime-lifecycle.md` →「第三类：挂载点与事件执行生命周期」用途表，“进度与状态通知”：  
  > 可供多个观察者订阅；缓冲、节流、顺序与慢订阅者规则按事件用途细化。

这些段落支持未来需要进度能力，**不支持现在就固定为独立有界通道**。尤其最后一句要求按具体用途细化；目前没有订阅者，就无法据此选择丢弃、合并、阻塞或重放规则。

修订规则：

- 第一版不提供进度／遥测专用观察通道。
- 删除该通道的容量、溢出、节流、动态订阅和退订规则。
- 第二次锁定后，普通 Event 的定义与业务绑定仍不可修改；第一版不增加运行期订阅入口。
- 普通 Event 仍按已声明的执行契约等待处理器，不能将其悄悄改成不等待的观察通道。
- 保留“观察者不得导致 Task 主业务重复执行”的架构约束，留待具体进度功能落实。
- 第 5 处的持续工作故障回调直接满足当前必需的故障可见性，不扩展为通用观察系统。

没有需要新增的签名；删除此前提出的专用观察通道即可。

本轮只审这两处，未读取代码或目录外内容，未修改文件，未编写实现。


### 第七轮：并行登记、Stop 自等待与修订清单

本轮仅依据已读的当前目录文档和前述方案审查，不读取代码或目录外内容，不修改文件，不写实现。

#### 第 7 处：并行子工作登记 Registry

**删除并行子工作登记能力，以及为此设置的原子登记要求。**

文档要求的是：

- `runtime-authoring-roadmap.md` → 第 4 章「亲手完成」第 4 项：  
  > 等待两类注册所需工作完成，校验身份与冲突，再锁定 Task／Event 注册结果。
- 同章「验收」：  
  > 注册处理器未完成时不能提前锁表。

这要求等待注册执行结束，但没有要求多个子工作并行修改同一份 Registry。第一版登记静态定义和绑定，也没有需要并行登记的具体场景。

不过，**“禁止子工作登记”和“检测到子工作登记就立即报错”是两项不同的机制**。只凭 Registry 对象或当前线程，不能可靠区分处理器的异步延续与子工作；要保证后者报错，还得增加调用身份追踪。这又是一项当前没有必要的防误用机制。

因此采用更简单的契约，不增加这项检测：

- 只有 RegisterEvent 处理器本身可以顺序登记。
- 不允许从子工作或其他并发调用中修改 Registry；不提供线程安全保证，也不承诺对所有违规调用立即报错。
- 处理器可以异步等待，但恢复后仍由它顺序登记，不要求固定线程。
- 处理器返回时，立即关闭本模块登记窗口；此后登记明确报错。
- 如本次注册执行还有必要工作，统一执行器仍等待其结束；但这些工作不能继续登记。
- 整个注册执行成功才接受该模块清单；处理器或必要工作失败，清单丢弃。
- 全部模块成功后再校验合并、完成第二次锁定。

各模块拥有独立 Registry，不需要为了可能的模块间并行执行，让单份 Registry 支持并发修改。关闭窗口的检查保留，但不再设计并发写入协议。

#### 第 8 处：等待自身 Stop 的运行时检查

**删除这项专门的识别与拒绝检查。**

这里确实有对应的文档建议：

- `runtime-authoring-roadmap.md` → 第 9 章「亲手完成」第 8 项：  
  > 定义并发 Stop、停止过程中再次 Start、处理器内部等待 Stop 的行为。建议并发外部 Stop 共享操作；正在被排空的作用域不允许同步等待自己的 Stop 完成，可拒绝或提供不等待的请求方式。

但这是需要结合公开入口细化的建议，不是要求无条件加入调用链检测。

当前方案中：

- 模块构造参数不提供 RuntimeHost。
- `ModuleWorkPort` 只允许发布根事件和提交根 Task。
- `EventContext`、`WorkContext`、`ContinuousWorkContext` 均不提供 Stop。
- 正常模块调用路径没有可以等待的 `StopAsync`。

消费者当然可以自行把宿主包装进外部回调，间接制造自等待；但当前没有业务场景需要这种接线，不能为此提前引入执行上下文识别。

修订规则：

- 删除“识别当前调用是否来自被排空工作，并明确拒绝”的机制。
- `StopAsync` 仍由消费者入口调用；模块执行入口不增加停止能力。
- 并发外部 Stop 共享正在进行的停止操作，继续保留。
- 消费者不得建立“Stop 等待某工作，该工作又等待 Stop”的循环等待；第一版不承诺自动诊断。
- 此类错误造成未完成时，仍不能伪造停止成功、提前释放资源或自动升级 Kill。

#### 四轮审查后的修订清单

| 项目 | 结论与理由 |
|---|---|
| `EventDefinition.MinimumHandlers` | **删除**：没有普通事件必须达到某个订阅人数的具体需求。 |
| Task 唯一主执行绑定 | **保留**：请求必须有明确且唯一的业务执行责任。 |
| `BindEvent.bindingId` 及重复标识检查 | **删除**：多个处理器可以合法存在，字符串标识也不能证明业务绑定没有重复。 |
| `BindEvent.order` | **删除**：第一版通过模块计划顺序和模块内顺序登记表达串行顺序。 |
| 串行事件的明确执行顺序 | **保留**：共同参数修改和顺序加工需要确定先后。 |
| 事件对象上的 `ExecutionId` | **删除**：业务载荷不需要承担执行身份管理。 |
| 上下文及执行记录中的执行身份 | **保留**：错误关联、父子归属和诊断需要它。 |
| 同一事件对象重复／并发派发检测 | **删除**：每次新建事件对象作为使用契约，不维护对象使用历史。 |
| 根 Task 调度名额、等待名额的队列 | **删除**：第一版没有并发限额需求，接纳并登记后直接执行。 |
| `TaskExecutionOptions` | **删除**：当前没有独立于已删除调度配置的必要职责。 |
| 子调用不得竞争父任务根名额 | **删除**：名额机制删除后，这条规则失去对象。 |
| 在途追踪、父子归属、取消与完成等待 | **保留**：它们决定工作是否真正结束，不依赖调度名额。 |
| 执行期跨模块依赖方向检查 | **删除**：当前不需要把 Task 调用做成模块访问权限边界。 |
| 已有注册依赖检查、调用时提供方可用性检查 | **保留**：注册关系应有效，失效或未就绪能力不能被使用。 |
| 同一调用链上的 Task 递归重入检测 | **删除**：没有通用检测要求，而且递归调用可以合法结束。 |
| `GetSnapshot`、`WatchFaultsAsync` | **删除**：第一版无需同时提供多套故障观察方式。 |
| 故障流容量、缺口检测、快照补偿 | **删除**：没有慢消费者或断流恢复场景。 |
| 持续工作的 Ready／Completion | **保留**：启动就绪与实际结束不同，宿主需要分别等待。 |
| `ContinuousWorkFailed` 宿主回调 | **保留**：消费者没有模块内部工作 handle，也必须能及时看到运行故障。 |
| 进度／遥测专用有界通道及动态订阅退订 | **删除**：进度是后续需求，但第一版没有具体订阅场景来决定通道语义。 |
| 第二次锁定后业务定义和绑定不可修改 | **保留**：没有专用观察通道，不意味着开放运行期业务注册。 |
| Registry 的并行子工作登记及原子写入协议 | **删除**：静态定义顺序登记即可，没有并行修改需求。 |
| Registry 窗口关闭、失败丢弃和全量合并 | **保留**：防止晚到登记和半注册实例进入运行。 |
| 等待自身 Stop 的专门识别与拒绝 | **删除**：正常模块 API 不提供 Stop，没有第一版必经调用路径。 |
| 并发外部 Stop 合并、真实排空与收尾 | **保留**：防止重复推进和提前释放资源，属于正常生命周期要求。 |

#### 受影响部分的最终签名

以下仅列受上述删改影响的声明；其余已保留签名不变。代码块为签名草图，不含实现。

##### 事件基元、定义、处理器与上下文

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

public sealed class EventContext
{
    public ExecutionId ExecutionId { get; }
    public ModuleId SubscriberModule { get; }
    public CancellationToken Cancellation { get; }
    public WorkScope Work { get; }
}
```

事件对象不保存执行身份或已派发标记。`EventDispatchMode` 仍保留此前确定的串行失败即停、串行汇总、并行等待三种模式。

##### Task 定义与子调用

```csharp
public sealed record TaskDefinition<TInput, TResult>(
    TaskKey<TInput, TResult> Key);

public sealed class WorkScope
{
    public ValueTask<TResult> InvokeAsync<TInput, TResult>(
        TaskKey<TInput, TResult> key,
        TInput input);

    public ValueTask PublishAsync<TEvent>(TEvent e)
        where TEvent : RuntimeEvent;

    public WorkHandle Run(
        string name,
        Func<WorkContext, ValueTask> operation);

    public WorkHandle<T> Run<T>(
        string name,
        Func<WorkContext, ValueTask<T>> operation);
}
```

`InvokeAsync` 保留必要子执行归属；不检查递归 key、不检查调用依赖方向、不竞争 Runtime 执行名额。

##### 接线与隐式提交的 Registry

```csharp
public interface IRuntimeModule
{
    void Connect(ModuleWiring wiring);
}

public sealed class ModuleWiring
{
    public ModuleWorkPort RootWork { get; }

    public void SubscribeRegister(
        RuntimeEventHandler<RegisterEvent> handler);
}

public sealed class RegisterEvent : RuntimeEvent
{
    internal RegisterEvent();

    public Registry Registry { get; }
}

public sealed class Registry
{
    internal Registry();

    public void DefineTask<TInput, TResult>(
        TaskDefinition<TInput, TResult> definition);

    public void BindTask<TInput, TResult>(
        TaskKey<TInput, TResult> key,
        TaskHandler<TInput, TResult> handler);

    public void DefineEvent<TEvent>(
        EventDefinition<TEvent> definition)
        where TEvent : RuntimeEvent;

    public void BindEvent<TEvent>(
        RuntimeEventHandler<TEvent> handler)
        where TEvent : RuntimeEvent;
}
```

Registry 仅供注册处理器顺序修改；处理器返回即关闭登记窗口，整个注册执行成功后隐式接受清单。没有公开 `Seal`／`Submit`，也没有并发登记保证。

##### 持续工作完成与消费者故障通知

```csharp
public sealed class ContinuousWorkHandle
{
    public Task Ready { get; }
    public Task<ContinuousWorkOutcome> Completion { get; }
}

public sealed record ContinuousWorkFailure(
    ModuleId Module,
    string WorkName,
    RuntimeFailure Failure);

public sealed class RuntimeHost
{
    public event Action<ContinuousWorkFailure>? ContinuousWorkFailed;

    public Task<StopOutcome> StopAsync();
}
```

没有 `GetSnapshot`、`WatchFaultsAsync` 或进度观察通道。模块上下文和模块工作入口均不增加 `StopAsync`。

以上为四轮审查后的修订结果。本轮未读取现有代码、访问目录外路径、修改文件或编写实现。


### 第八轮：按 HL 需求重审调度与进度（用户更正前）

这次需要修正之前的结论：**第一版应恢复按键排队和 Task 进度／log 出口，但不因此恢复尚未批准的全局并发名额，也不把 HL 的事件 pump 搬进 Runtime。**

以下只讨论这两处；所有签名均为设计草图。

#### 第 3 处：根 Task 调度与执行选项

##### 文档依据与职责划分

`hl-c-interface-lifecycle-jobs.md` §6.5「同一实例的任务按实例排队」明确要求：

> 同一个键的任务按登记顺序取得执行权，登记顺序就是 `hl_start` 的调用顺序。

> 上一个任务的完成事件放进事件队列以后，下一个任务才开始运行。

同节还要求排队期间报告 `queued`，排队取消直接离队。§6.6 则明确：

> 注册时可以给“从输入取排队键”的函数……运行时不认识“实例”，由模块决定用什么当键。

据此建议采用以下复用边界。**这是本轮的迁入设计，不是声称现有文档已经把原生库职责划给 Runtime。**

| 规则 | Runtime | HL 原生库 |
|---|---|---|
| 按键 FIFO | 管理队列、执行权、排队取消及在途归属 | 按 op 和输入决定键 |
| 登记顺序 | 提交时同步完成接纳和排队登记 | 在 `hl_start` 返回前完成提交 |
| 排队进度 | 报告进入 Queued 状态 | 转换成 `phase:"queued"` |
| 同键下一项启动条件 | 等前项完成必要工作及约定的输出收尾 | flush 并将完成事件入队，随后确认输出收尾完成 |
| 后台执行 | 传递执行等级，使用接入的执行设施 | 将 `background:true` 映射为后台等级，并提供实际线程执行策略 |
| C 接口和界面协议 | 不认识 | job id、JSON、错误码、pump、ACK 均留在这里 |

HL 不再保留另一套重复的 FIFO；它负责排队政策和协议，Runtime 承担受托管排队机制。CLI 可以直接复用该机制。

##### 恢复哪些参数

**恢复具体的提交选项，不恢复原来含义宽泛的 `TaskExecutionOptions`。**

排队键取决于本次输入，后台标记也来自本次调用，适合放在提交参数中。HL 的 op 注册继续保存取键函数，提交时计算键；Task 定义仍表达业务触发和唯一执行绑定。

```csharp
public readonly record struct TaskQueueKey(
    string Domain,
    string Value);

public enum TaskExecutionClass
{
    Normal,
    Background
}

public sealed record TaskSubmissionOptions(
    TaskQueueKey? QueueKey = null,
    TaskExecutionClass ExecutionClass = TaskExecutionClass.Normal);
```

例如，安装、修复同一个实例都使用同一个实例键，不能因为 Task 名不同就分到不同队列。`Domain` 用于区分实例、仓库等不同键空间；路径或实例身份如何规范化由 HL 决定。

规则如下：

- 排队中的根 Task 已经被接纳，必须纳入取消、Stop 排空和状态追踪。
- 排队取消不等待取得该键的执行权；完成其取消收尾后产生取消结果。
- 同键 FIFO 约束实际业务执行顺序，允许文档规定的两项例外：排队进度提前出现、排队取消提前完成。
- 不占用 UI 线程执行任务处理器，包括处理器第一个 `await` 之前的同步部分。`Submit` 只做接纳、登记和调度。
- 后台等级不改变同键 FIFO，也不自行增加“前台任务插队”的政策。

##### 全局并发上限仍不恢复

`hl-task-scheduling-discussion.md` 明确标注：

> 讨论中的调度改造设想（未批准）

有界并发、队列满拒绝、多个键轮流分配执行位都属于这项设想。现有按键串行要求并不等于全局有固定名额；`downloadConcurrency` 也不能直接解释成 Runtime 根 Task 并发数。

因此本轮：

- 恢复按键队列。
- 不确定全局并发上限、队列容量或公平轮转算法。
- 不恢复全局名额配置。

§6.5 已规定后台任务降低线程优先级，因此这个行为不能删掉。但它应由 HL 接入的实际执行设施实现，不能把 `Background` 简单解释为 `Task.Run`，或在共享线程池线程上随意修改优先级。也不能据此声称任意 `await` 后的延续都自动拥有同样的线程优先级。

##### 子调用规则恢复为更准确的版本

**恢复“子调用不能重新等待父执行已经占用的根调度资源”，当前具体指同键执行权。**

具体场景：实例 A 的安装 Task 已持有 A 的执行权，安装内部调用必要子 Task。如果子调用重新进入 A 的根队列，就会排在父任务后面，而父任务正在等待它。

因此：

- `WorkScope.InvokeAsync` 仍是当前根执行的必要子调用。
- 子调用继承根执行归属，不再次进入根 FIFO，也不重新申请父任务持有的键。
- 内部调用不接受新的根提交选项。
- 这不授予跨资源互斥保证；内部访问其他资源仍须遵守业务同步规则。
- 以后若批准全局执行名额，再把同一原则应用到名额，当前不提前实现。

#### 第 6 处：Task 进度、log 与完成事件

##### 恢复报告出口，节流和队列留在 HL

`hl-c-interface-lifecycle-jobs.md` §6.5「进度」已经明确划分：

> Gebinde 与 Haytham Core 各自定义自己的进度 record，公开 API 收 BCL 的 `IProgress<T>`，不共用类型；节流是组装层的事。

因此 Runtime 第一版需要的是：

1. 与当前执行绑定的同步进度／log 报告入口。
2. 根 Task 与必要子工作的关联。
3. 一个在业务及子工作结束后执行的输出收尾接口。

**Runtime 不提供第二套 100 ms 节流器或有界事件队列。**以下规则全部由 HL 原生库实现：

- 每任务 100 ms 进度节流，`phase` 变化立即交给事件通道。
- 按 id 合并尚未发送的最新进度。
- log 每 100 ms 或 256 行打包。
- log 字节上限及 `dropped` 标记。
- 完成事件不因积压被丢弃或合并。
- pump 批量发送、两批在途、ACK 和 port 重连。

这里“立即”不等于 UI 已经收到；后续仍经过文档规定的事件队列与 pump。

HC／Gebinde 保留各自的 `IProgress<T>`。接入模块或转接层用同步适配器，把业务进度转换为 Runtime 的报告数据；不要求两个库引用同一个进度类型，也不使用 `System.Progress<T>` 异步投递。

##### Runtime 提供的报告入口

可以使用一个小的通用进度记录，承载 HL 当前确实需要的数据；JSON 字段及本地化处理仍由 HL 映射。

```csharp
public sealed record TaskProgress(
    string Phase,
    long? Done = null,
    long? Total = null,
    long? Bytes = null,
    long? TotalBytes = null,
    string? Item = null);

public sealed class TaskOutput
{
    public void ReportProgress(TaskProgress progress);
    public void ReportLog(TaskLogEntry entry);
}

public sealed class WorkContext
{
    public CancellationToken Cancellation { get; }
    public WorkScope Work { get; }
    public TaskOutput Output { get; }
}

public sealed class TaskEvent<TInput, TResult> : RuntimeEvent
{
    internal TaskEvent();

    public TInput Input { get; }
    public TaskOutput Output { get; }
}
```

`TaskOutput` 绑定当前执行，调用者不自行填写 job id。必要子工作的报告沿根执行关联到同一个 HL job；Runtime 不自动把多个子任务的百分比相加，整体进度如何计算仍由业务决定。

`ReportProgress`／`ReportLog` 同步交给输出接收端，允许接收端缓存、合并，但不会把报告偷偷投递为一个未追踪的线程池回调。并发子工作报告时，由每个根任务的输出接收端协调写入。

##### 必须增加输出收尾边界

仅仅让 HL 在 `TaskRun.Completion` 后订阅一个回调还不够：如果 Runtime 已释放键，下一项可能在 HL flush 和完成事件入队前就开始。

所以恢复一个**每次根提交绑定的输出接收端**，并将其收尾完成纳入同键调度的释放条件：

```csharp
public interface ITaskOutputSink<TResult>
{
    void ReportState(TaskExecutionState state);
    void ReportProgress(TaskProgress progress);
    void ReportLog(TaskLogEntry entry);

    ValueTask CompleteAsync(TaskOutcome<TResult> outcome);
}

public sealed class RuntimeHost
{
    public TaskRun<TResult> Submit<TInput, TResult>(
        TaskKey<TInput, TResult> key,
        TInput input,
        TaskSubmissionOptions options,
        ITaskOutputSink<TResult>? output = null,
        CancellationToken cancellation = default);
}
```

HL 在提交前创建接收端并关联 job id；接收端从接纳开始就已接好，不能事后订阅而漏掉早期进度。未接纳的提交不会产生输出事件。`ModuleWorkPort` 的根提交沿用相同的调度选项。

正常完成时序明确为：

```text
主处理器、必要子工作及本次清理实际结束
→ 确定业务结果，关闭业务报告入口
→ HL 的 CompleteAsync：
     结束节流定时活动
     flush 剩余进度和 log，补必要的 dropped 标记
     将唯一完成事件入队
→ 输出收尾返回
→ Runtime 释放同键执行权，允许下一项启动
```

HL 的最终入队操作必须与自己的报告／定时器协调，保证完成事件之后不会再出现该任务的报告。**这里等待的是入队，不是 pump 发送，更不是 Dart ACK。**

`hl-task-scheduling-discussion.md` 对“送出／收到”的描述与现行文档存在措辞差异；本轮以 §6.5 明确的“完成事件放进事件队列以后”为准。

如果 flush 或终态入队抛异常，属于输出收尾的基础设施故障：保留原业务结果并显式报告，不能声称已成功入队，也不能按正常路径直接放行同键下一项。不会为此自动重试业务或承诺故障条件下事件必达。

##### 为什么不会重复执行业务

输出接收端只接收报告和结果，没有业务执行权：

- Task 仍只有一个主执行绑定。
- 添加进度或 log 展示只改变输出的消费，不新增 TaskEvent 业务订阅。
- 不因 UI 重连、报告重送或新增观察者重新调用主处理器。
- 输出通道故障不转化为业务重试。

因此恢复的是 **Task 的输出接口与完成边界**，不是运行期任意增删业务订阅的通用事件总线。

##### 其他需要下一轮重审的项目

- **活动任务快照／查询删除项**：§6.4、§6.8 的 `hl_list` 和 hot restart 明确需要在途任务及最新进度查询，但不因此要求恢复通用故障流和缺口重放。
- **预退出与 `Wait`／`CancelOnExit`**：HL 可撤销的预退出会取消指定任务，不能直接等同于 Runtime 的 Stop 否决或“正常 Stop 不取消已有任务”。
- **停止收尾与输出通道生命周期**：§6.4 要求安全退出先处理完完成事件，再 shutdown；Runtime 停止、HL 任务终态入队和 pump 关闭需要明确衔接。

本轮只读取了当前目录新增的四份 HL 文档，未读取现有代码、访问目录外路径或修改文件，也未编写实现。


### 第九轮：分开 HL 的业务需求和现有实现后，再审调度与进度

上一轮我把部分 HL 现有协议当成了 Runtime 必须保持的契约，约束得过紧。按这次澄清，**保留业务语义，重新选择调度和输出机制**；不再以“现有文档如此实现”作为保留理由。

#### 第 3 处：Task 调度

##### 哪些结论需要改

| 上一轮结论 | 更新后的决定 |
|---|---|
| 并发上限未批准，所以第一版不提供 | 未批准只代表不能称为既定要求。本轮建议采用**可配置的根 Task 并发上限**。 |
| 后台任务降低线程优先级 | 改为**调度上的后台等级**，不绑定操作系统线程优先级。 |
| 同键下一项必须等前项完成事件入队 | 改为等待前项业务、必要子工作及资源清理完成；**不等待界面输出入队**。 |
| 使用 FIFO 保持同实例顺序 | 保留，但理由是调用顺序可预测、避免后提交的操作越过前项，不是沿袭现有队列实现。 |
| 具体实例键由 HL 决定 | 保留：Runtime 不认识实例，键由接入层根据业务输入计算。 |

##### 更合适的调度规则

**采用按键串行，加可配置的根执行并发上限。**

具体场景是：用户同时安装多个实例，又运行清理扫描。只做按键串行会允许所有不同实例立即执行；根并发上限可以控制同时展开的业务操作数量，而不要求每项操作独占线程。

这个上限只表示“同时执行多少个根业务操作”，**不等于下载连接数、线程数或子工作并发数**。下载器、存储等设施仍管理自己的资源限制。

调度规则：

1. 提交时同步完成准入与登记，返回后该任务已经可追踪、可取消。
2. 同键按接纳顺序运行。只有键队首且该键空闲的任务，才有资格取得根执行名额。
3. 等待键时不占根执行名额，避免大量同键任务占满名额却无法工作。
4. 排队取消直接移除，不执行主处理器，正常产生取消结果。
5. 键和根执行名额一直保留到本次业务、必要子工作及资源清理实际结束。
6. 随后释放调度资源；界面输出收尾单独追踪，不继续占据业务执行名额。
7. 根任务处理器不在提交者线程上执行，避免同步代码阻塞 Flutter 的 FFI 调用。

**后台等级表示在争用执行名额时较低的调度优先级。**不抢占已经运行的任务，也不改变同键 FIFO。正常／后台候选采用加权选择，并保证持续有正常任务时后台仍能获得执行机会；权重不是公共业务契约，不在此固定数值。

这比降低线程优先级更符合异步工作：一次 Task 可能经过多个线程和 I/O 等待，操作系统线程优先级并不能准确表达整个业务操作的优先级。若业务需要限制后台下载速度，应在下载资源层处理，不能声称根调度等级已经做到。

##### 子调用规则恢复

**必要子调用不能重新竞争父任务占据的根名额或同键执行权。**

否则“安装占据一个名额，调用 Storage 子任务并等待”就可能因名额耗尽而停住。

子调用继续属于当前根执行树，不进入根调度队列。这个规则只解决 Runtime 自己引入的调度等待；不代替业务资源锁，也不意味着所有子工作可以无限并发。

##### 更新后的签名

调度上限是宿主级配置；排队键和后台等级是本次提交的属性。`TaskDefinition` 不恢复笼统的执行选项。

```csharp
public sealed record RuntimeSchedulingOptions(
    int MaxConcurrentRootTasks);

public readonly record struct TaskQueueKey(
    string Domain,
    string Value);

public enum TaskExecutionClass
{
    Normal,
    Background
}

public sealed record TaskSubmissionOptions(
    TaskQueueKey? QueueKey = null,
    TaskExecutionClass ExecutionClass = TaskExecutionClass.Normal);

public sealed record TaskDefinition<TInput, TResult>(
    TaskKey<TInput, TResult> Key);
```

`RuntimeSchedulingOptions` 在组装时配置，可沿用现有 `RuntimeCapabilities` 入口提供，不改变已定案的模块创建协议。具体上限由 HL／CLI 根据负载选择，不把某个默认数值写成 Runtime 业务契约。

同实例任务使用相同键；不同操作名不能导致同实例被分到不同队列。跨进程互斥和底层资源一致性仍由业务设施负责。

#### 第 6 处：进度、log 与完成结果

##### 哪些结论需要改

| 上一轮结论 | 更新后的决定 |
|---|---|
| 100 ms、256 行、4 MB 等规则必须保持 | 不固定这些数值；保留可配置节流、合并和有限缓冲。 |
| 必须使用现有 pump、批量和 ACK 机制 | Runtime 不依赖这些机制；HL 可以保留或重构其传输实现。 |
| 输出收尾成功后才能释放同键执行权 | 删除这项耦合；业务完成和输出完成分别表示。 |
| 输出失败会阻止同键下一项正常推进 | 改为显式报告输出故障，但不把已完成业务继续当作占用资源的任务。 |
| 同步报告、等待子工作后封闭输出、完成结果最后出现 | 保留，这些直接保障业务要求。 |

##### 保留同步报告，输出缓冲由接入层负责

Runtime 负责：

- 报告属于哪次执行。
- 必要子工作报告归属于哪个根任务。
- 子工作全部结束后才封闭报告入口。
- 正常收尾时只交付一次最终结果。
- 追踪输出收尾的成功或失败。

HL 输出适配器负责：

- 把业务进度映射到界面协议。
- 节流、合并、log 打包及缓冲上限。
- 最后一次 flush 与完成结果的有序入队。
- 传输、重连及界面消费协议。

CLI 可以提供另一种输出适配器，不必继承 Flutter 的传输和 ACK 机制。

仍推荐以下策略，但不固定参数：

- 高频进度合并为最新状态。
- 阶段变化及时交给输出队列，不受普通进度节流间隔阻挡。
- log 批量输出，缓冲有上限；发生丢弃时明确报告数量。
- 完成结果不被普通进度覆盖，也不因普通缓冲积压而静默丢弃。
- 不承诺传输故障时必达；发送失败不能触发业务重新执行。

HC／Gebinde 的公开 API 仍可以各自使用 `IProgress<T>`，由同步适配器转换。保留同步报告的理由是防止未追踪的异步报告越过最终结果，不是为了保持现有类名或实现。

##### 把业务完成与输出完成分开

更新后的时序是：

```text
主处理器、必要子工作及资源清理结束
→ 封闭报告入口，等待已经进入的报告调用返回
→ 固定业务结果，释放键和根执行名额
→ 输出适配器停止并汇合其定时活动
→ flush 缓冲的进度和 log
→ 将唯一完成结果放入该任务的有序输出序列
→ 输出收尾完成
```

这样，同实例的下一项可能已经开始，而前项完成结果还在输出收尾中。**这不破坏同实例业务串行，也不破坏每个任务自己的完成结果最后出现。**跨任务界面事件交错不再被当作业务互斥条件。

如果界面需要展示操作先后，可以依据任务接纳顺序和状态，不必通过“阻塞后一个业务，等前一个通知入队”来保证。

两个完成对象分别表达：

- `Completion`：业务及必要清理已经结束，结果确定。
- `OutputCompletion`：最后报告已 flush，最终结果已交给接入层输出序列；不表示界面已经收到。

输出失败使 `OutputCompletion` 明确失败，保留原始业务结果，不把成功安装改称安装失败，也不自动重试安装。输出适配器释放前，接入层必须处理其尚未结束的输出收尾。

##### 更新后的签名

```csharp
public sealed record TaskProgress(
    string Phase,
    long? Done = null,
    long? Total = null,
    long? Bytes = null,
    long? TotalBytes = null,
    string? Item = null);

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

    // flush 并提交最终输出；不等待界面接收或 ACK。
    ValueTask CompleteAsync(TaskOutcome<TResult> outcome);
}

public sealed class TaskRun<TResult>
{
    public ExecutionId Id { get; }
    public TaskExecutionState State { get; }

    public Task<TaskOutcome<TResult>> Completion { get; }
    public Task OutputCompletion { get; }

    public void RequestCancellation();
}

public sealed class RuntimeHost
{
    public TaskRun<TResult> Submit<TInput, TResult>(
        TaskKey<TInput, TResult> key,
        TInput input,
        TaskSubmissionOptions options,
        ITaskOutputSink<TResult>? output = null,
        CancellationToken cancellation = default);
}
```

`TaskEvent` 和 `WorkContext` 继续提供绑定本次执行的 `TaskOutput`；`ModuleWorkPort` 的根提交采用相同选项。没有输出接收端时，输出完成不产生额外等待。

报告方法同步接收并快速返回，不执行网络发送或等待界面。接收端可以使用内部缓冲，但必须在 `CompleteAsync` 中汇合自己的延迟活动，确保最终结果之后不再产生本任务输出。

**输出接收端始终不具有业务执行权。**一个 Task 只有一个主执行绑定；增加界面、日志或 CLI 输出消费者，不增加业务处理器，也不重新派发 TaskEvent。

本轮仅重新评估设计并给出签名草图，未读取现有代码或目录外内容，未修改文件，未编写实现。


### 第十轮：预退出、退出策略与 Stop 排空

这两项需要补充明确的阶段边界：**预退出是可撤销的准备操作，与正式 Stop 分开；正式 Stop 成功必须同时完成业务排空和任务输出收尾。**这能保留已确认的“正常 Stop 不自动取消已有 Task”规则。

以下仅修订设计和签名，不读取代码、不访问目录外路径、不修改文件。

#### 1. 预退出、退出策略与 Stop 判定

##### 将预退出作为独立操作

`hl-c-interface-lifecycle-jobs.md` §6.4 要求预退出可撤销，§6.5 要求 `CancelOnExit` 任务在预退出时收到取消请求，而撤销后不重新启动。

这与 `OnStopRequested.Cancel()` 不同：

- **撤销预退出**：调用方改变退出意图，主动恢复接纳工作。
- **否决 Stop**：模块处理器判断当前不能停止。
- **任务取消**：已经发送的协作请求，不因前两者恢复准入而撤回。

因此不把用户等待、确认及撤销塞进 `OnStopRequested`。增加可选的 `PrepareStopAsync`／`CancelStopPreparation`，正式停止仍使用 `StopAsync`。

预退出期间，模块和持续工作仍运行，只关闭新根工作准入。已经接纳的工作及其必要后代保留执行权。

##### 退出策略声明在 Task 定义上，仅作用于根执行

模块知道哪种操作可以中断，因此由 Task 定义声明：

```csharp
public enum TaskExitPolicy
{
    Wait,
    CancelOnExit
}

public sealed record TaskDefinition<TInput, TResult>(
    TaskKey<TInput, TResult> Key,
    TaskExitPolicy ExitPolicy = TaskExitPolicy.Wait);
```

- `Wait`：预退出不主动取消，等待实际完成。
- `CancelOnExit`：预退出向已接纳的该类根 Task 请求取消，包括排队任务。

`Wait` 不是“禁止调用方取消”，只是退出准备阶段不主动取消它；业务自身的取消安全性仍由业务契约负责。

**策略只应用于根执行。**例如一个 `Wait` 安装任务调用了定义为 `CancelOnExit` 的扫描 Task 作为必要子工作，预退出不能单独取消这个孩子。它属于安装执行树，跟随根执行的取消决定。否则可能把本应完成的安装拆断。

HL 的 op 映射使用对应 Task 的策略，不再维护一份容易与之冲突的重复声明。若两个操作需要不同退出语义，可以注册不同的 Task 定义并复用内部业务方法。

##### 修改后的完整流程

```text
运行
  → 可选：PrepareStopAsync
      → 原子关闭新根工作准入
      → 对已接纳的 CancelOnExit 根执行请求取消
      → Wait 根执行继续，所有必要后代继续受追踪
      → 保持预退出，任务可继续完成、结果可继续输出

      ├─ CancelStopPreparation
      │   → 恢复此前有效的准入
      │   → 已请求取消的任务不复活、不重启
      │
      └─ StopAsync：正式提交停止
          → OnStopRequested
              ├─ 否决：结束本次退出准备，恢复有效准入
              ├─ 判定异常：返回 DecisionFailed，按既定建议恢复有效准入
              └─ 通过：
                  → 保持准入关闭
                  → 等业务及必要子工作实际结束
                  → 等全部任务输出收尾
                  → OnStopping，结束持续工作并等待
                  → 反序卸载
```

`PrepareStopAsync` **只等待准备动作和取消请求的发出，不等待任务结束，也不等待界面回答**。用户思考、等待任务或撤销时，HL 留在这个阶段。

`StopAsync` 是正式提交边界。调用后不再通过 `CancelStopPreparation` 撤销；是否继续由 `OnStopRequested` 判定。通过判定后的排空不开放新的撤销协议。

直接调用 `StopAsync`、不先预退出时，仍沿用原流程：

> 关闭准入 → Stop 判定 → 已接纳 Task 自然完成 → 输出收尾 → 不可逆停止 → 卸载。

它**不会隐式应用 `CancelOnExit`**。因此预退出取消是调用方明确请求的附加行为，不推翻已确认的正常停止规则。

其他边界：

- 重复准备共享正在进行的准备操作；已经准备好时不重复发送取消。
- 撤销只移除预退出造成的门禁，不覆盖其他故障造成的不可用状态。
- 取消请求发出时出错，明确报告准备失败；不能假装全部目标已经收到取消，也不能自动开始卸载。
- Stop 被否决后，先前已取消的任务仍保持取消。这和用户撤销预退出一样，不保证恢复到完全相同的任务集合。

受影响签名：

```csharp
public sealed class RuntimeHost
{
    public Task PrepareStopAsync();

    // 仅撤销尚未正式提交的退出准备。
    public void CancelStopPreparation();

    public Task<StopOutcome> StopAsync();
}
```

#### 2. Stop 排空与输出通道衔接

##### 正式 Stop 必须等待所有 OutputCompletion

**只等业务 `Completion` 不够。**那只能证明业务和必要清理结束，不能证明节流器已 flush，或最终结果已交给接入层。

Stop 应分两步等待：

1. **业务排空**：所有已接纳根执行及必要后代真正结束。
2. **任务输出排空**：所有相关 `OutputCompletion` 完成，并检查是否成功。

这也包括：

- 排队期间被取消的任务。
- 业务失败但仍须报告结果的任务。
- 业务早已结束、已经不在业务在途集合中，但输出尚未完成的任务。

因此 Runtime 必须独立追踪待完成的输出收尾，不能只遍历“当前正在执行的 Task”。输出登记在任务接纳时建立，直到输出收尾结束才解除；发生的输出故障保留到停止结果中。

这不改变上一轮已分开的调度语义：业务完成后是否释放同键执行权，与整个宿主 Stop 最终要等待哪些义务，是两件事。

##### Stopped 与成功结果应提供明确保证

建议把以下条件作为成功停止的必要条件：

- 所有业务执行已结束。
- 所有任务的 `OutputCompletion` 成功。
- 持续工作已结束。
- 必要卸载和资源清理成功。

所以：

> **`StopAsync` 成功返回 `Stopped`，保证全部已接纳 Task 的最终结果已经交给接入层的输出序列。**

它不保证结果已传输到 Flutter，更不保证界面已经处理。

如果某个输出收尾失败：

- 不改变已经确定的业务结果。
- 等待并检查其他输出收尾，继续仍安全的停止和清理。
- Stop 返回输出收尾错误，不报告成功 Stopped。
- 最终按未完整完成收尾处理为 Hanging，并区分“输出义务失败”与“资源仍未释放”。

如果输出收尾一直不结束，Stop 就没有完成；调用方可以放弃等待，但不能据此视为收尾完成或自动 Kill。

##### 接入层关闭传输的时机

必须区分两个确认点：

| 确认点 | 谁负责 | 能证明什么 |
|---|---|---|
| Runtime 停止成功 | Runtime | 最终结果已经交给接入层输出序列 |
| 接入层传输排空／接收确认 | HL | 结果已经达到产品规定的送达条件 |

安全退出应当是：

```text
Runtime Stop 成功
→ 接入层确认不再有任务输出生产者
→ 将自己的输出序列传输完
→ 按 HL 的业务要求确认界面已接收或处理
→ 关闭传输
```

**不能在 Runtime 刚进入 Stopped 时立即丢弃传输队列。**此时可以开始关闭传输的收尾，但如果 HL 要求界面看到最终结果，就必须完成接入层自己的送达等待。

Runtime 不等待 Flutter ACK，否则宿主停止会依赖 UI 是否仍在消费，且可能与 UI 等待 Stop 的流程形成循环。

上述保证针对 Task 输出。游戏 handle 等独立事件源仍须由 HL 按自身归属停止转发和收尾，不能把“Task 输出已结束”理解为游戏进程已经结束。

##### 受影响签名与语义

上一轮的两个完成对象保留：

```csharp
public sealed class TaskRun<TResult>
{
    // 业务、必要子工作与本次资源清理已经结束。
    public Task<TaskOutcome<TResult>> Completion { get; }

    // 输出已 flush，最终结果已交给接入层输出序列。
    public Task OutputCompletion { get; }
}

public interface ITaskOutputSink<TResult>
{
    void ReportState(TaskExecutionState state);
    void ReportProgress(TaskProgress progress);
    void ReportLog(TaskLogEntry entry);

    // 成功返回即确认本任务最终输出已提交；
    // 不等待界面接收或处理。
    ValueTask CompleteAsync(TaskOutcome<TResult> outcome);
}
```

停止结果补充输出收尾失败类别：

```csharp
public enum StopOutcomeKind
{
    Stopped,
    Vetoed,
    DecisionFailed,
    OutputFailed,
    CleanupFailed
}

public sealed record StopOutcome(
    StopOutcomeKind Kind,
    IReadOnlyList<RuntimeFailure> Failures);
```

若输出和资源清理同时失败，`Kind` 使用 `CleanupFailed`，`Failures` 保留两类错误，不能因主类别不同而丢掉输出故障。单个业务 Task 失败、但其失败结果成功输出，不算 Stop 失败。

本轮只处理预退出与输出收尾，不展开在途查询；未读取现有代码、访问目录外路径、修改文件或编写实现。


### 第十一轮：在途任务查询与输出收尾失败时的宿主状态

#### 1. 在途任务查询

**Runtime 第一版不新增列表或快照 API。由 HL 的任务登记表结合输出适配器保存查询数据。**

按现有数据归属：

| 查询数据 | 已经掌握它的一方 |
|---|---|
| HL job id、op 名称 | HL 提交入口 |
| Task 当前状态 | Runtime 的 `TaskRun.State`，输出适配器也收到 `ReportState` |
| 最新进度 | HL 输出适配器收到的 `ReportProgress` |
| 最终结果是否已经进入 HL 输出序列 | HL 输出适配器 |
| HL 是否处于预退出 | HL 的退出协调入口 |

为了回答 `hl_list` 再让 Runtime 保存一份进度及 HL 操作信息，会产生两份状态，还需要额外对齐。

具体规则：

- HL 在提交任务前建立 job 记录，并把输出适配器与该记录关联；提交失败则移除记录，不产生任务事件。
- 输出适配器收到状态和进度时，先更新记录，再执行节流或传输。查询看到的是**最新已报告进度**，不是最后一条已经发送的进度。
- 记录包括排队、执行中的任务；业务已经结束但输出尚未完成的任务，仍保留，并明确标记为输出收尾中。
- 最终结果成功进入输出序列后，任务可从活动列表移除。输出失败单独保留明确诊断，不能冒充成功入队。
- hot restart 只更换界面连接，不重建任务记录或再次提交任务。
- HL 对自己的登记表生成一致的纯内存快照，不从多个异步回调临时拼接查询结果。
- 预退出标记由 HL 唯一的退出协调入口维护；准备、撤销和正式 Stop 的结果都通过该入口更新，不能仅凭“最近调用过哪个方法”猜测状态。

这满足本轮要求的“重新取得仍在运行的任务和最新进度”。它不自动提供已完成任务历史或丢失完成事件的重放。

**Runtime 无需新增签名。**HL 内部可以提供以下查询声明，具体 JSON 由 C 接口层转换：

```csharp
internal sealed class LauncherJobRegistry
{
    internal LauncherJobsSnapshot GetSnapshot();
}
```

`LauncherJobsSnapshot` 包含预退出标记，以及活动 job 的 id、op、业务状态、最新进度和输出收尾状态；它不是 Runtime 公共类型。

#### 2. 输出收尾失败不应使宿主进入 Hanging

**同意删除这项状态影响。上一轮把“停止过程有错误”和“工作或资源尚未安全结束”混在了一起。**

没有具体业务场景要求：资源已经成功释放，仅因为最终结果未能入队，就把宿主标记为 Hanging。HL 需要知道输出失败，才能提示或选择退出方式；`StopOutcome.Failures` 足以表达，无须改变资源生命周期状态。

修订规则：

- Stop 仍等待所有 `OutputCompletion` **结束**，并收集其失败；等待结束不等于要求全部成功。
- 输出收尾失败后，继续安全停止持续工作和卸载资源。
- 业务、持续工作及必要资源清理全部完成后，宿主进入 **Stopped**，即使存在输出错误。
- `StopOutcome.Kind` 为 `Stopped`，同时在 `Failures` 中保留输出错误。
- 输出和资源清理同时失败，仍按资源清理失败进入 Hanging，保留两类错误。
- 输出收尾一直没有结束时，不能套用“已经失败且结束”的规则；Stop 仍未完成。
- 删除 `StopOutcomeKind.OutputFailed`，输出错误通过已有的 `RuntimeFailure` 类别区分。

```csharp
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
```

相应撤回上一轮的保证：

> **Stopped 只证明受托管工作和必要资源收尾已经完成，不再单独证明所有最终结果成功进入输出序列。**

接入层应检查输出错误，或等待并检查其持有的 `OutputCompletion`。所有输出收尾成功后，才能确认最终结果均已交给输出序列；之后是否已经送达界面，仍由 HL 自己判断。`Stopped` 且 `Failures` 非空，是“宿主已停止，但停止过程中有错误”，不能展示成无错误成功。

本轮未读取代码或目录外内容，未修改文件，未编写实现。

### 第三阶段说明（Claude 写）

第十二轮起是第三阶段。用户在 4.9 节答复了第 4 节的问题并给出 21 条决定，其中有几项要请 Astra 按决定修订签名或重新设计：Task 基类与 Registry、只保留一个 `Completion` 后怎样报告输出错误、并行事件“一出错就先返回”、持续工作出错后的处理和重试入口。Claude 把 4.9 节原文放进 Astra 的目录，作为用户已经确认的前提。

每轮 Astra 交回设计后，Claude 照第二阶段的做法检查过度工程化，把说不出业务场景的机制发回去问，第十三、十五、十六轮都是这类追问。

用户随后在侧对话里答复了 4.10（采用 C2）和 4.11（选 B）。第十七轮按这两个答复修订，提示由侧对话写好、Claude 代发；第十八、十九轮是 Claude 对第十七轮的追问。

和前面一样，后一轮的修订取代前一轮的对应部分，例如第十四、十五轮里的 `RetryModuleAsync` 和“故障后停止整个模块”已经被第十七轮取代。第三阶段结束后的最终状态见第 5 节。

### 第十二轮：按用户决定修订 Task 基类、Registry 与 Completion

本轮的提问：Claude 把 4.9 节用户的决定放进 Astra 的目录（`user-decisions-2026-11-02.md`），请它修订 Task 基类、Registry、执行方法的签名，以及只保留一个 `Completion` 后输出收尾失败怎样表示。

已读取 `user-decisions-2026-11-02.md`。以下以其中第 1～5、16、17、20 条为准；只处理 Task，不展开其他待设计项。

#### 1. Task 基类、Registry 与按类调用

##### 基类和执行上下文

保留 **`RuntimeTask<TInput, TResult>`**，它与 `RuntimeEvent` 对应，也避开 BCL 的 `Task` 名称。

Task 对象是登记一次、可执行多次的行为对象。名字、退出策略放在基类属性上；本次输入、取消、子工作和输出放在每次执行新建的 `TaskEvent` 中。

执行方法只接收一个参数：

```csharp
public abstract class RuntimeTask<TInput, TResult> : RuntimeTask
{
    protected RuntimeTask();

    protected abstract ValueTask<TResult> ExecuteAsync(
        TaskEvent<TInput, TResult> e);
}

public abstract class RuntimeTask
{
    private protected RuntimeTask();

    public abstract string Name { get; }
    public abstract TaskExitPolicy ExitPolicy { get; }
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
```

非泛型 `RuntimeTask` 是后面按单个任务类型参数登记和查询的公共基底，不是另一种执行机制。

选择单参数的理由是：Task 已有专用执行对象，再增加 `EventContext` 会把同一次执行的信息拆成两处。普通事件订阅者继续使用 `(e, EventContext)`，无需强行统一两种业务入口。

规则：

- Task 实例可以保存业务依赖，但不得把某次输入、结果或取消状态保存在实例字段中；不同根执行可能同时调用它。
- `Name` 是稳定的展示／诊断名称，不再用于执行配对。
- `ExitPolicy` 是定义级属性，登记时读取并固定；仍只作用于根执行。
- Task 不提供 `SetResult`；`ExecuteAsync` 的返回值就是候选业务结果。
- `Work` 管理必要子工作及跨模块 Task 调用；`Output` 绑定当前根执行的输出。
- TaskEvent 的执行身份属于这个专用执行上下文，不恢复普通 `RuntimeEvent` 上的身份属性或对象复用检查。

##### TaskEvent 保留，但不再开放普通订阅绑定

**保留 TaskEvent。**它仍是统一事件执行机制中的内置执行类型，但业务入口已经由 Task 类的 `ExecuteAsync` 唯一确定。

Runtime 登记 Task 时，建立调用该方法的静态适配器；执行时由统一派发器调用。模块不能通过 `Subscribe<TaskEvent<…>>` 再增加一个业务执行者，输出接收端也不会变成 TaskEvent 订阅者。

因此删除：

- `TaskDefinition`、`TaskKey`、`TaskHandler`。
- `DefineTask`／`BindTask` 两步注册。
- “缺少主执行绑定／存在多个主执行绑定”的检查。
- 跨模块替另一个模块实现 Task 的能力。

##### Registry

```csharp
public sealed class Registry
{
    internal Registry();

    public void RegisterTask<TTask>(TTask task)
        where TTask : RuntimeTask;

    public void RegisterEvent<TEvent>(
        EventDefinition<TEvent> definition)
        where TEvent : RuntimeEvent;

    public void Subscribe<TEvent>(
        RuntimeEventHandler<TEvent> handler)
        where TEvent : RuntimeEvent;
}
```

`e.Registry.RegisterTask(new SaveValueTask())` 会推断 `TTask` 为 `SaveValueTask`。

规则：

- 任务实例、行为和登记所属模块一次性绑定。
- 登记模块实现并拥有该 Task；其他模块只能调用，不能替换执行行为。
- 同一 Task 类重复登记报冲突，不再检查可重复的展示名称。
- 登记必须使用具体 Task 类型；不要先把变量转为 `RuntimeTask` 再登记。
- Registry 继续采用顺序登记、隐式提交；不恢复 `Seal`／`Submit`。

##### 按类配对不需要反射，但存在一个 C# 签名限制

查找实例本身很简单：宿主持有以 `typeof(TTask)` 为身份的登记表。`typeof` 和类型身份比较不需要反射扫描、反射调用或动态生成代码。

登记时，泛型基类提供已经静态编译的执行适配器，它知道 `TInput`、`TResult`，直接调用虚方法 `ExecuteAsync`。执行表保存在宿主实例中，不是全局静态 Task 表。

**需要明确的限制是：C# 不能从 `TTask` 的泛型基类自动推导方法返回类型。**

对于：

```csharp
host.Submit<SaveValueTask>(request)
```

一个通用方法不能直接写出“返回 `TaskRun<SaveValueTask 的 TResult>`”。C# 没有关联类型语法，也不支持显式填写第一个方法类型参数、让剩余类型参数自动推断。

这不影响按类查找，却影响输入和返回值能否在调用点保持编译期强类型。

**严格保留用户指定调用形式时，完整签名可以是：**

```csharp
public sealed class RuntimeHost
{
    public TaskRun Submit<TTask>(
        object? input,
        TaskSubmissionOptions? options = null,
        ITaskOutputSink? output = null,
        CancellationToken cancellation = default)
        where TTask : RuntimeTask;
}

public sealed class WorkScope
{
    public ValueTask<TaskValue> InvokeAsync<TTask>(
        object? input)
        where TTask : RuntimeTask;
}

public sealed class TaskValue
{
    public T Get<T>();
}
```

这里由登记时生成的静态泛型适配器检查输入类型、保存结果并提供 `Get<T>()`。类型错误明确报错，不做反射转换。业务失败和取消仍由子调用传播，`TaskValue` 只包装成功值。

但代价是：调用方传错输入类型、取错结果类型时，错误发生在运行期。

**这一点需要用户决定：**

| 选项 | 影响 |
|---|---|
| 保持 `Submit<SaveValueTask>(request)`／`InvokeAsync<SaveValueTask>(request)` | 使用上面的通用签名，输入输出在适配边界做运行期类型检查 |
| 允许显式填写输入输出类型 | 保留编译期输入、返回值和输出接收端检查，但调用较长 |

第二种形式的完整签名是：

```csharp
public TaskRun<TResult> Submit<TTask, TInput, TResult>(
    TInput input,
    TaskSubmissionOptions? options = null,
    ITaskOutputSink<TResult>? output = null,
    CancellationToken cancellation = default)
    where TTask : RuntimeTask<TInput, TResult>;

public ValueTask<TResult> InvokeAsync<TTask, TInput, TResult>(
    TInput input)
    where TTask : RuntimeTask<TInput, TResult>;
```

我倾向第二种的类型安全，但它会改变用户指定的调用写法，**不能自行替换**。若调用写法必须保持，则采用第一种；不为了隐藏这个语言限制，额外引入一套复杂的生成式调用门面。

#### 2. 单个 Completion 与输出收尾失败

##### Completion 返回“业务结果＋输出故障”

单独返回原来的业务 `TaskOutcome` 已经不足以表达：

> 安装成功，但最终结果无法进入输出队列。

建议新增一个完成汇总，保留两个维度，但只有一个等待对象：

```csharp
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
```

如果选择上节保留单类型参数的调用形式，对应使用非泛型外壳：

```csharp
public sealed record TaskCompletion(
    TaskOutcome Business,
    RuntimeFailure? OutputFailure);

public sealed class TaskRun
{
    public ExecutionId Id { get; }
    public TaskExecutionState State { get; }
    public Task<TaskCompletion> Completion { get; }

    public void RequestCancellation();
}
```

其中非泛型 `TaskOutcome` 的成功分支携带前述 `TaskValue`；失败和取消分支保留各自的信息。

具体结果：

| 实际情况 | Completion 的内容 |
|---|---|
| 业务成功，输出成功 | 成功业务结果，`OutputFailure = null` |
| 业务失败，失败结果输出成功 | 原业务失败，`OutputFailure = null` |
| 业务成功，输出收尾失败 | **成功业务结果＋内部输出故障** |
| 业务失败，输出也失败 | **原业务失败＋内部输出故障** |

`Completion` 正常返回汇总，不代表任务整体无错误。调用方必须检查业务结果与输出故障，不能只看等待是否抛异常。

这是沿用结构化结果模型明确报告内部故障，不是写日志后吞掉异常。`OutputFailure` 必须保存原始异常及执行关联；不会把真实业务成功改写为业务失败，也不会丢失返回值。

输出接收端仍只接收业务结果：

```csharp
public interface ITaskOutputSink<TResult>
{
    void ReportState(TaskExecutionState state);
    void ReportProgress(TaskProgress progress);
    void ReportLog(TaskLogEntry entry);

    ValueTask CompleteAsync(TaskOutcome<TResult> outcome);
}
```

`CompleteAsync` 自己失败时，Runtime 在外层生成 `TaskCompletion`。不再尝试通过同一个失败的输出路径递归发送“输出失败的完成事件”；HL 可从持有的 `TaskRun.Completion` 得到该内部故障。

##### 完成和 Stop 的规则

正常顺序恢复为：

1. 主处理器、必要子工作及本次清理结束。
2. 封闭报告入口，确定业务结果。
3. flush 输出，并将最终结果交给输出序列。
4. 输出收尾成功后，释放同键执行权。
5. 完成唯一的 `Completion`。

输出收尾失败时：

- 该次 `Completion` 仍须结束，返回业务结果和输出故障，不能永久挂起来掩盖错误。
- Runtime 在解除这次执行登记前，将输出故障记录到宿主的停止故障汇总中。
- Stop 等待所有相关 `Completion`，检查其输出故障；已经完成、已移出在途集合的故障也不能漏掉。
- 单纯业务失败且错误成功输出，不因此使 Stop 失败。
- 输出失败加入 `StopOutcome.Failures`；工作和资源都已结束时，宿主仍进入 **Stopped**。
- 不恢复 `OutputCompletion`，也不把输出错误变成 Hanging。

##### 还有一个故障后的排队选择需要用户决定

第 20 条规定的是：**前项完成结果入队后，才能开始同键下一项。**

输出收尾失败时，这个条件没有满足。因此不能一边报告输出失败，一边无说明地放行下一项。

两个可选行为：

- **暂停该键的队列**：后续任务仍等待，由调用方取消或另行处理；缺点是 Stop 会继续等这些任务，需要新增恢复协议。
- **终止该键的当前排队批次**：不执行后续已排队任务，让它们以明确的“前序输出收尾失败，未执行”结果结束，并各自尝试输出收尾；不自动重试。该键停止接纳新任务，避免后续提交绕过故障。

我倾向第二种：故障显式、队列能够结束，也不用新增输出重试／恢复协议。但这会决定故障影响范围，**需用户确认，不能从第 20 条直接推出**。

以上仅修订 Task 部分；未读取现有代码、访问目录外路径、修改文件或编写实现。

### 第十三轮：输出收尾失败后的处理与更短的强类型调用

本轮的提问：Claude 追问两处：输出收尾失败后“终止同键批次、停止接纳”和“Stop 汇总所有历史输出故障”对应什么场景；以及有没有既短又能在编译时检查类型的调用写法。

#### 1. 输出收尾失败：采用报告后继续，不增加队列故障政策

##### 失败场景

在当前 HL 场景中，`CompleteAsync` 只负责进程内的最终输出提交。可能的失败主要是：

- 结果转换或序列化存在程序缺陷。
- 输出适配器被错误地提前关闭。
- 缓冲管理或并发协调存在内部错误。
- 内存不足等运行环境故障。

这些都不是需要业务分支处理的正常情况。Dart port 断开、界面没有 ACK，则属于后续传输状态，不应被混入“最终结果进入进程内输出序列失败”。

所以没有依据为它设计暂停队列、终止批次或停止接纳等专门政策。

##### 采用第三种：报告故障，照常放行同键下一项

修订为：

1. 等业务、必要子工作及本次清理结束。
2. 进行输出收尾。
3. 输出成功，或收尾已明确失败后，形成该次 `TaskCompletion`。
4. 结束这次执行并释放同键执行权，允许下一项运行。

顺序约束明确为：

> **正常情况下，前项完成结果入队后才运行下一项；发生输出内部故障时，先结束前项的输出尝试并明确报告错误，再继续调度。**

这不会造成两个同实例业务操作重叠。前项结果未能送到界面是需要暴露的输出故障，但停止后续业务不能修复它；目前没有业务场景支持进一步扩大故障范围。

前提是“输出失败”确实表示这次输出尝试已经结束，不能一边抛出错误，一边遗留未追踪的定时器或回调继续产生输出。

**删除暂停该键、终止当前批次、禁止该键新提交的方案。**保留上一轮的结果结构：

```csharp
public sealed record TaskCompletion<TResult>(
    TaskOutcome<TResult> Business,
    RuntimeFailure? OutputFailure);
```

业务成功而输出失败时，成功结果和内部错误同时保留，不重试业务。

##### Stop 只汇总本次排空涉及的任务

同意删除宿主长期累计每次输出故障的机制。历史任务的错误已经由各自 `Completion` 交给调用方，当前没有要求 Stop 重复报告它们的场景。

修订规则：

- 正式进入排空时，Runtime 原子取得**尚未完成 `Completion`** 的根任务集合。
- 这些任务包括排队、运行中、输出收尾中的任务；它们的必要子工作由根任务负责等待。
- Stop 等待这个集合，并把其中的输出故障加入 `StopOutcome.Failures`。
- 排空开始前已经完成的任务不追溯、不重新汇总。
- 取得集合与任务完成移出登记必须协调，避免边界竞态漏掉尚未完成的任务。
- 工作和资源成功结束后，宿主进入 Stopped；本次排空的输出错误仍可使 `Failures` 非空。

#### 2. 更短且保持编译期类型的调用形式

**自身类型参数方案可以用，而且比前两个选项更适合这里。推荐由泛型基类提供静态的强类型入口。**

##### 调用方实际写法

Task 声明：

```csharp
public sealed class SaveValueTask
    : RuntimeTask<SaveValueTask, SaveRequest, bool>
```

调用：

```csharp
SaveValueTask.Submit(host, request)
SaveValueTask.InvokeAsync(context.Work, request)
```

第一项返回 `TaskRun<bool>`，第二项返回 `ValueTask<bool>`；`request` 必须能转换为 `SaveRequest`。调用方不需要重复填写输入和结果类型。

这里的方法是从封闭泛型基类继承的静态成员。编译器已经知道该基类中的三个类型参数，不需要从方法约束里反向推断 `TResult`。

##### 签名草图

```csharp
public abstract class RuntimeTask
{
    private protected RuntimeTask();

    public virtual string Name { get; }
    public abstract TaskExitPolicy ExitPolicy { get; }
}

public abstract class RuntimeTask<TSelf, TInput, TResult>
    : RuntimeTask
    where TSelf : RuntimeTask<TSelf, TInput, TResult>
{
    protected RuntimeTask();

    protected abstract ValueTask<TResult> ExecuteAsync(
        TaskEvent<TInput, TResult> e);

    public static TaskRun<TResult> Submit(
        RuntimeHost host,
        TInput input,
        TaskSubmissionOptions? options = null,
        ITaskOutputSink<TResult>? output = null,
        CancellationToken cancellation = default);

    public static ValueTask<TResult> InvokeAsync(
        WorkScope work,
        TInput input);
}

public sealed class Registry
{
    public void RegisterTask(RuntimeTask task);
}
```

静态入口只负责把类型确定的调用交给指定宿主或作用域：

- 不保存静态任务实例、订阅或其他可变状态。
- 不创建任务实例，也不绕过 Registry。
- 根提交仍检查准入并排队；内部调用仍属于当前执行的必要子工作。
- 没有登记对应 Task 时明确拒绝。
- 行为仍由已登记实例的 `ExecuteAsync` 执行，不通过静态方法实现业务。

##### 代价和限制

主要代价是模块作者在继承声明中多写一次自己的类型，调用形式也从 `host.Submit<SaveValueTask>` 改成了 `SaveValueTask.Submit(host, ...)`。

另一个 C# 限制是：自身类型约束**不能完全证明 `TSelf` 就是实际派生类**。有人仍可能错误地继承一个以其他 Task 为自身参数的基类。因此建议具体 Task 使用 `sealed`，登记时检查实际类型与声明的 `TSelf` 一致。这是一项保证注册身份与强类型入口一致的检查，不涉及调用链、程序集扫描或动态代码。

强类型句柄也能解决问题，例如：

```csharp
host.Submit(SaveValueTask.Handle, request)
```

但需要额外的句柄类型和暴露成员。当前没有需要把 Task 入口作为数据传递的场景，所以静态入口更简单。

**推荐把这种形式作为交给用户的首选。**它保持按 Task 类配对和编译期类型检查，但改变了用户原先指定的调用拼写，需要用户确认；不是 C# 原写法已经能够成立。

##### 登记身份使用 `task.GetType()`

**可以。**登记表直接用实际运行时类型作为身份，删除“登记时必须保留具体变量类型”的规则：

```csharp
public void RegisterTask(RuntimeTask task);
```

即使变量先转成 `RuntimeTask`，`GetType()` 仍返回 `SaveValueTask`。

获取类型身份不等于反射扫描或反射调用。执行仍通过泛型基类提供的静态编译适配器和虚方法完成，NativeAOT 不需要按名字创建对象或生成调用代码。

采用自身类型参数方案后，登记时再将实际类型与 `typeof(TSelf)` 核对即可。

##### Name 默认使用类名

**不必每个 Task 都覆写。**建议改为虚属性，默认使用实际类型的简单类名，需要展示或诊断别名时再覆写。

它只用于展示和诊断：

- 不参与登记配对。
- 不要求全局唯一。
- 不作为稳定的 C 接口 op 名或本地化键。
- 类重命名可能改变默认名称；需要稳定名称的 Task 显式覆写。

本轮仅回答上述两处，未读取现有代码或目录外内容，未修改文件，未编写实现。

### 第十四轮：并行事件出错先返回、持续工作故障与重试入口

本轮的提问：Claude 请 Astra 设计 4.9 第 18 条的并行“一出错就先返回”，以及第 10 条持续工作出错后的处理和重试入口。

本轮只设计这两项，不读取代码或目录外内容、不修改文件、不写实现。以下区分可直接确定的执行规则，以及仍需用户确认的重试契约。

#### 1. 并行事件“一出错就先返回”

##### 名称与声明

建议命名为 **`ParallelFailFast`**，与 `ParallelJoin` 并列：

```csharp
public enum EventDispatchMode
{
    SerialFailFast,
    SerialCollect,
    ParallelJoin,
    ParallelFailFast
}

public sealed record EventDefinition<TEvent>(
    string Name,
    EventDispatchMode Mode)
    where TEvent : RuntimeEvent;
```

这里的 FailFast 只表示**尽早向派发方报告失败**，不表示其他订阅者已经停止，也不表示整个事件执行已经结束。

具体规则：

- 派发开始时确定参与名单，并先建立全部参与者的追踪登记。
- 启动各订阅者；一个订阅者同步抛错也不能使其他参与者漏掉登记。
- 第一次观察到失败后，派发等待立即以错误结束。
- 没有失败时，仍须等全部订阅者及必要子工作成功结束，派发才成功。
- 多项同时失败时，不保证哪一项被选为首次返回的错误。

##### 不自动取消其他订阅者

**建议这个模式本身不向其他订阅者请求取消。**用户要求的是提前取得错误，并没有要求失败后取消其他业务。

例如并行执行两个独立检查：第一个失败后，调用方可以提前显示错误，但第二个仍可完成并提供另一个诊断。

不过，必须区分两层：

- `ParallelFailFast` 本身不取消兄弟订阅者。
- 如果首次错误未被处理，导致所属 Task 失败，Task 可以按既有故障收尾规则向尚未结束的必要工作请求取消。
- 调用方主动取消、所属模块故障等取消来源，仍然生效。

即使收到取消请求，也必须等待真正结束。

##### 提前返回不解除工作归属

仍沿用结构化工作归属，不增加脱离执行的后台容器：

```text
根 Task
  └─ 派发方工作
      └─ 本次事件执行
          ├─ 订阅者 A：失败，立即通知派发方
          └─ 订阅者 B：继续运行，仍登记在本次事件执行下
```

因此：

- 派发方的方法可以在 B 结束前收到异常、执行错误处理，甚至返回。
- **派发方所属的受托管执行不能在 B 结束前最终完成。**
- 父工作自身的委托返回，只代表它不再执行代码；它登记的必要后代仍须结束。
- 若属于根 Task，Task 的最终结果、输出收尾和唯一 `Completion` 都等待这些订阅者。
- 若属于根事件执行，由宿主保持该根执行登记直到结束。
- 若属于生命周期阶段，该阶段仍等待本次事件实际结束，不能因为已经收到首个错误就提前卸载资源。

宿主 Stop 通过等待这些根执行排空，间接等待剩余订阅者；不需要再建一个“失败后遗留工作列表”。

##### 后续错误放在哪里

本次事件执行记录保存所有错误：

- 首次失败立即传给派发等待者。
- 后续失败继续加入同一事件的错误集合。
- 最终汇入所属 Task、根事件或生命周期操作的失败结果。
- 首次错误不能因为“提前报告一次、最终汇总一次”而被重复计为两个故障。

派发方捕获首次异常，不会自动把这个必要事件执行改成成功；这与此前“必要子工作失败仍进入父执行结果”的规则一致。

普通派发签名可以保持不变：

```csharp
public sealed class WorkScope
{
    public ValueTask PublishAsync<TEvent>(TEvent e)
        where TEvent : RuntimeEvent;
}
```

该方法在 `ParallelFailFast` 下代表“派发方取得成功或首次失败的时刻”；实际执行结束仍由作用域追踪。

如果调用方在收到首错后立即执行清理，**不能释放剩余订阅者仍在使用的资源**。此类必须先汇总完再清理的调用点，应使用 `ParallelJoin`；不能把提前返回当作安全释放的信号。

#### 2. 持续工作故障、模块停止与重试

##### “只停掉所属模块”不能直接等同于卸载

建议先确定一个边界：

> **停止模块的执行，但暂不卸载模块，也不替换模块实例及已登记对象。**

原因是其他模块可能仍借用它的资源。若自动执行 `OnUnloading` 并释放资源，就可能破坏依赖模块；为了安全卸载又不得不停止依赖模块，扩大成用户没有要求的级联重启。

持续工作在宿主运行期间失败后，建议执行：

1. 记录原始故障，立即将所属模块标记为不可用。
2. 关闭该模块的新业务准入。
3. 标记依赖该模块的模块暂不可用于新业务，但不自动对它们执行停止或卸载。
4. 排队中、尚未开始的受影响 Task 以“模块不可用”失败，不无限等待恢复。
5. 已开始的工作保留追踪；已经借出的资源继续有效。新发生的受影响调用明确失败，不能静默跳过。
6. 等所属模块当前必要业务执行结束，再定向执行它的 `OnStopping`，结束该模块其余持续工作并等待真实结束。
7. 进入“模块已停止、可评估重试”的状态；**不执行 `OnUnloading`**。

第 5～6 步不能保证立即完成。若已有业务依赖失败循环才能继续完成，其模块实现必须让等待者观察到服务故障，不能留下永远不完成的业务等待。框架不凭故障标记假装这些工作结束，也不设置默认超时强行释放资源。

这与宿主正常 Stop 不同：宿主最终卸载时，仍统一执行反序 `OnUnloading`。

##### Task、事件与依赖关系如何处理

注册结果继续保持第二次锁定后的形状：

- 不删除 Task 或事件定义，不解除业务订阅，不重新执行 `OnRegistering`。
- 所属模块不可用时，它的 Task 不能开始执行。
- 普通事件的执行计划若需要不可用模块的订阅者，应明确失败，不能把该订阅者悄悄过滤掉并报告成功。
- 框架定向调用 `OnStopping`、重试阶段的 `OnStarting` 及最终 `OnUnloading` 使用受控生命周期路径，不受普通业务门禁阻拦。
- 依赖模块只是因依赖不可用而暂停新业务准入，不自动收到 `OnStopping`。
- 已运行的依赖模块工作如果因此实际失败，按自身工作契约报告；不能宣称它们一定不受影响。

模块恢复后，只有仍然健康、且全部依赖条件重新满足的模块才恢复准入；不能顺带清除它们自己的故障。

##### 错误仍通过宿主回调交给消费者

保留 `RuntimeHost.ContinuousWorkFailed`，因为运行中的监测循环没有一个仍在等待它的外部业务调用方。HL 原生库和 CLI 入口是这项通知的消费者。

```csharp
public sealed record ContinuousWorkFailure(
    ModuleId Module,
    string WorkName,
    RuntimeFailure Failure);

public sealed class RuntimeHost
{
    public event Action<ContinuousWorkFailure>? ContinuousWorkFailed;
}
```

通知时机是：**原始故障已记录、模块已关闭新业务准入之后**，不必等整个模块停完才让界面知道。

回调中请求重试不能阻塞等待当前故障回调自己返回。重试入口可以异步等待框架已经启动的模块停止操作；并发或重复的重试请求则按用户决定明确报错，不排队执行多次重启。

##### 推荐重试整个模块的运行阶段，不只重跑一个循环

建议接口：

```csharp
public sealed class RuntimeHost
{
    public Task<ModuleRetryOutcome> RetryModuleAsync(
        ModuleId module);
}

public enum ModuleRetryOutcomeKind
{
    Restarted,
    Failed
}

public sealed record ModuleRetryOutcome(
    ModuleRetryOutcomeKind Kind,
    IReadOnlyList<RuntimeFailure> Failures);
```

**推荐范围是重新执行该模块的 `OnStarting`，启动并等待该模块全部必要持续工作就绪。**不重新构造模块、不重新注册、不重复 `OnLoading`。

理由是：既然故障处理已经停止了所属模块的全部持续工作，只重跑最先失败的那一个，会遗漏一起停止的其他工作。完整重做启动阶段更容易保证模块的就绪条件。

适用 HL 场景：清理提醒或版本监测所在模块的后台循环因外部访问失败退出。该模块停止后台活动，但保留已经加载的配置、服务引用和资源；用户请求重试后，重新启动监测活动并等待就绪。这个场景不需要替换整个模块或重新打开所有依赖资源。

重试规则：

- 宿主必须处于允许业务运行的状态；预退出或正式 Stop 期间拒绝新重试。
- 等该模块故障停止真正结束后再启动，绝不新旧循环重叠运行。
- 上一次停止有未完成义务，或模块声明的运行前提已经失效时，不开始重试。
- 重新执行 `OnStarting`，为新一轮持续工作建立新的取消源和工作记录。
- 所有启动条件满足后，恢复模块可用性和符合条件的依赖方准入。
- 重试启动失败，停止本次已启动的工作，返回原始错误及收尾错误，模块继续不可用。
- 不自动重试，也不重放此前失败或取消的业务 Task。

##### 需要用户确认的取舍

上述重试方式要求一个目前尚未确认的契约：

> **模块的 `OnStopping → OnStarting` 必须支持在同一个已加载模块实例上再次执行；OnStopping 停止运行活动，OnUnloading 才负责最终释放加载期资源。**

这不违反“宿主停止后不能重启”，因为宿主没有停止，但它确实增加了模块级重启能力，需要用户确认。

两个选项：

- **推荐：停止整个模块的运行活动，再重跑 `OnStarting`。**适合上述 HL 监测场景，保持模块停止与恢复的范围一致。
- **只停止并重试失败的持续工作。**实现范围更小，但必须把用户的“停掉所属模块”解释为模块不可用、其他持续工作仍可存活，而非整个模块的运行活动都停止。

不建议默认采用“卸载、重新加载整个模块”：目前没有必要场景证明值得承担借用资源失效、依赖方协调和对象重新绑定的复杂度。若模块的加载期状态已经损坏，本次重试应明确失败，而不是承诺能够自动恢复。

本轮仅给出这两项设计，未读取现有代码、访问目录外路径、修改文件或编写实现。

### 第十五轮：故障是否扩散到其他模块、重试的前置检查

本轮的提问：Claude 追问两处：依赖方整体暂停、有不可用订阅者就整次派发失败，这两条对应什么场景；重试前的“未完成义务”“运行前提”检查对应什么场景。

#### 1. 故障扩散到其他模块和事件

##### 删除“整体暂停依赖方”

目前没有具体 HL 场景要求：一个模块的持续工作失败后，所有依赖模块的全部 Task 都必须停止接纳。

例如，某个模块依赖存储模块，但其中一个 Task 只检查输入或计算方案，并不访问存储；仅凭模块依赖关系拒绝它，扩大了故障范围。

修订规则：

- **只关闭故障所属模块的普通业务准入。**
- 其他模块保持原有可用状态，不沿依赖图传播暂停。
- 其他模块真正调用故障模块的 Task 时，调用以“提供模块不可用”失败。
- 经业务接口访问服务时，由服务本身报告不可用或实际故障；Runtime 不声称能够截获所有普通方法调用。
- 依赖图继续用于初始化和卸载顺序，不作为运行期自动传播停用状态的规则。
- 故障模块恢复后，只恢复它自己的准入，不执行依赖模块的“集体恢复”。

##### 删除“存在不可用订阅者就使整次派发失败”

同样没有具体 HL 场景支持这项通用规则。对于通知、独立检查等事件，一个暂不可用的模块不应自动阻止其他模块的订阅者运行。

采用你提出的方案：**不可用模块的订阅者不参与新的普通事件派发，并明确记录跳过情况。**

修订规则：

- 确定派发参与名单时，排除普通业务已停用模块的订阅者。
- 返回的派发结果包含被跳过的模块及原因；“未参与”不能伪装成“已成功执行”。
- 其余订阅者仍按 `SerialFailFast`、`SerialCollect`、`ParallelJoin` 或 `ParallelFailFast` 执行。
- 跳过本身不算订阅者异常，也不触发 `ParallelFailFast`。
- 已经开始运行的订阅者仍被追踪到真实结束，不能通过停用模块将其从等待集合中移除。
- 不删除注册绑定；模块恢复后，后续派发重新包含它。
- 框架定向执行该模块的停止、重试启动和卸载事件，不适用普通事件的跳过规则。

有一个边界应明确：若某业务必须取得某个模块的处理结果才能继续，应使用对该模块 Task 的显式调用，或由派发方检查跳过信息后决定失败。**不为这种可能性恢复通用的最少订阅人数或整次派发自动失败机制。**

派发结果需要承载跳过信息，这是本次修改带来的必要补充：

```csharp
public sealed record SkippedEventSubscriber(
    ModuleId Module,
    string Reason);

public sealed record EventDispatchResult(
    IReadOnlyList<SkippedEventSubscriber> SkippedSubscribers);

public sealed class WorkScope
{
    public ValueTask<EventDispatchResult> PublishAsync<TEvent>(TEvent e)
        where TEvent : RuntimeEvent;
}
```

订阅者错误仍按既定异常语义返回，并在错误信息中附带本次跳过情况。跳过名单在派发参与名单确定时形成，因此不需要等待 `ParallelFailFast` 的剩余订阅者结束才能取得。

#### 2. 重试的前置检查

这里应拆开处理：**保留框架已掌握的“旧工作是否真正结束”，删除没有定义过的“模块运行前提”检查。**

##### 旧工作尚未结束：保留实际等待，不新增业务检查

具体场景是：模块故障后，另一项监测循环正在等待网络请求、文件访问或执行退出收尾，虽然已收到停止请求，但尚未返回。此时重跑 `OnStarting`，可能启动第二个循环，与旧循环同时访问同一份模块状态。

Runtime 已经掌握：

- 该模块的活动执行及必要子工作。
- 持续工作的完成对象。
- 本次 `OnStopping` 的执行完成情况。

因此不需要模块声明新条件，也不需要探测线程。直接复用已有工作追踪：

- 重试先等待本次模块停止操作实际结束。
- 等待期间不执行新的 `OnStarting`。
- 某项工作一直未结束，重试就仍未开始；不设置默认超时，也不假装停止完成。
- 只收到取消请求，不视为工作结束。

但要修正上一轮过宽的措辞：**“停止曾经报错”不等于“仍有工作没有结束”。**已经结束的 `OnStopping` 即使返回错误，也不能仅凭这一点永久禁止重试；保留错误，再由重试启动判断业务状态能否恢复。

##### “模块声明的运行前提”：删除，交给 OnStarting

此前没有设计这项声明，也没有具体 HL 需求支持再增加一套前置检查协议。

例如，监测循环重试时网络仍不可用、目标目录不可访问、内部服务尚未恢复，这些条件只有模块理解。Runtime 不应为它们建立通用条件表。

修订规则：

1. Runtime 确认没有与本次重试冲突的生命周期操作，并等待旧运行活动真正结束。
2. 定向执行该模块的 `OnStarting`。
3. 模块在其中检查实际运行条件并准备资源；条件不满足就明确报错。
4. 若本次已经启动部分持续工作，按既定失败收尾规则停止并等待它们。
5. 返回重试失败，模块继续不可用；不自动再次重试。

`RetryModuleAsync(ModuleId)` 的签名不变，不增加运行前提描述、健康探测或独立预检接口。

本轮只修订上述两处，未读取现有代码或目录外内容，未修改文件，未编写实现。

### 第十六轮：派发结果里的跳过名单

本轮的提问：Claude 追问：`PublishAsync` 返回被跳过的订阅者名单，哪个场景用得到。

**没有具体 HL 场景需要派发方逐次取得跳过名单，应删除这项返回信息。**上一轮为了让跳过“可见”增加了接口，但没有对应的调用方决策需求。

删除 `EventDispatchResult`、`SkippedEventSubscriber`，以及异常中的跳过信息。恢复签名：

```csharp
public sealed class WorkScope
{
    public ValueTask PublishAsync<TEvent>(TEvent e)
        where TEvent : RuntimeEvent;
}
```

修订规则：

- 普通事件派发时，不可用模块的订阅者不参与；恢复后参与后续派发，不修改注册绑定。
- 派发成功只表示**本次实际参与的订阅者及必要工作成功结束**，不保证所有已登记订阅者都参与。
- 没有可参与的订阅者时正常完成。
- 实际参与者出错，按事件既定策略传播和汇总错误；不附带跳过名单。
- 已开始的订阅者继续追踪到真实结束；`ParallelFailFast` 的提前报错与后续等待规则不变。
- 模块故障通过 `ContinuousWorkFailed` 通知消费者；必须取得某个模块业务结果的调用使用其 Task，不靠普通事件推断该模块是否可用。
- 框架定向执行的生命周期事件不受上述普通事件筛选规则影响。

本轮未读取代码或目录外内容，未修改文件，未编写实现。

### 第十七轮：按用户对 4.10、4.11 的答复修订 Task 调用与持续工作重试

本轮的提问：用户在侧对话里决定 4.10 采用 C2（`partial` 类加生成器生成静态 `Submit`／`InvokeAsync`），4.11 选 B（只停止并重试出错的那一项持续工作）。提示由侧对话写好，Claude 代发。请 Astra 检查 C2 有没有问题，并按 B 重新设计持续工作出错后的步骤和重试入口。

#### 1. C2 调用方式

**C2 可行，且解决了先前的类型推导问题。**生成的方法直接写出三个类型参数，输入、返回值和输出接收端都能保持编译期类型检查；运行时不需要反射扫描、反射调用或动态代码生成。

##### 生成器识别与登记身份

生成器应按 Roslyn **类型符号**识别 `RuntimeTask<TInput, TResult>` 的派生类，取得实际的输入、结果类型，不按基类文本名称匹配。这样使用完整限定名、命名空间别名，或经过中间基类继承，都能正确识别。

登记继续使用：

```csharp
task.GetType()
```

生成入口使用对应具体类作为 `TTask`。两者得到相同类型身份，因此以下写法也没有问题：

```csharp
RuntimeTask task = /* SaveValueTask 实例 */;
```

将变量声明为基类不会改变登记身份。登记表仍属于宿主实例，生成的静态方法不保存任务实例或可变状态。

##### 需要处理的具体编译情况

没有方案性障碍，但生成器需要明确处理这些情况：

- **缺少 `partial`**：按用户决定报编译错误。若 Task 嵌套在其他类型中，为生成嵌套声明，包含它的外层类型也需要 `partial`。
- **成员名称冲突**：作者已经声明同签名的 `Submit` 或 `InvokeAsync` 时，应给出明确诊断，不能生成重复成员。
- **具体 Task 再被继承**：基类生成的静态方法固定提交基类 Task。例如登记的是派生类实例，却通过继承来的基类静态入口提交，会查找不同的登记身份。示例中的 `sealed` 已避免这一问题；建议具体可登记 Task 统一使用 `sealed`。
- **泛型或抽象 Task**：不能未经区分就当作可登记的具体 Task 生成入口。抽象类型只作为基类；若第一版不支持泛型具体 Task，生成器明确诊断即可，不必为尚无场景的类型组合扩展生成规则。

生成器应保留输入类型的可空标注，并使用完整限定类型名，避免生成文件依赖作者文件中的 `using`。

##### Runtime 需要公开的底层签名

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

public sealed class Registry
{
    public void RegisterTask(RuntimeTask task);
}

public sealed class RuntimeHost
{
    public TaskRun<TResult> Submit<TTask, TInput, TResult>(
        TInput input,
        TaskSubmissionOptions? options = null,
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

生成入口调用这些公开方法即可。外部调用方不必接触三个类型参数；非泛型结果包装、运行期输入配对方案和自身类型参数方案均不再需要。

#### 2. 持续工作故障与单项重试

##### 故障只影响该项工作

按用户选择的 B，撤回“模块故障停止”的流程。持续工作运行中失败后，Runtime：

1. 记录该次运行的原始故障。
2. 等待该项工作自己的必要收尾实际结束；抛异常不等于它拥有的收尾已经完成。
3. 调用模块为该项工作登记的故障处理回调。
4. 完成该次运行的 `Completion`，保存失败结果。
5. 通过宿主的 `ContinuousWorkFailed` 通知 HL／CLI。

不执行模块的 `OnStopping`、`OnUnloading`，也不改变整个模块的业务准入。

以下情况需要区别：

- **启动就绪前失败**：仍导致对应启动等待失败，不能报告已经就绪。
- **就绪后运行失败**：只结束这一项工作，模块其他工作继续。
- **响应正常停止请求而结束**：不是意外故障，不触发 `ContinuousWorkFailed`。

##### 模块怎样停止相关工作

给持续工作 handle 增加单项停止请求；启动登记时允许提供一个同步故障回调：

```csharp
public sealed class ModuleWorkScope
{
    public ContinuousWorkHandle Start(
        string name,
        Func<ContinuousWorkContext, ValueTask> operation,
        Action<ContinuousWorkFailure>? onFailure = null);
}

public sealed class ContinuousWorkHandle
{
    public ContinuousWorkId Id { get; }
    public long Attempt { get; }

    public Task Ready { get; }
    public Task<ContinuousWorkOutcome> Completion { get; }

    public void RequestStop();
}
```

例如，一个模块拥有“更新元数据”和“根据元数据发送提醒”两个持续工作。前者失败时，模块的故障回调可以：

- 将自己的服务状态标记为不可用。
- 对提醒工作调用 `RequestStop()`。

Runtime 不推导两项工作的关系，也不沿模块依赖图传播停止。

故障回调只做快速状态更新和停止请求，不能同步等待其他工作结束。被请求停止的工作仍由 Runtime 追踪；宿主停止时仍须等待它们真实结束。回调自身抛错应显式记录为附加内部故障，不能覆盖原始错误或阻止宿主故障通知。

##### 新业务和服务可用性

**Runtime 不因一项持续工作失败而自动关闭模块的 Task 准入，也不跳过该模块的事件订阅者。**

模块按实际能力决定业务行为：

- 不依赖失败工作的 Task 可以继续。
- 依赖失败服务的 Task 在执行时明确报告不可用。
- 其他模块直接持有的服务对象，由服务自身检查状态并报告不可用。
- 故障回调可以更新模块自有的可用状态；需要该条件的业务入口读取它。

这不需要新增模块级准入控制 API，也不恢复依赖方暂停、事件派发失败、跳过名单或通用运行前提检查。

##### 重试对象是一项工作，保留身份、创建新运行

区分两个身份：

- `ContinuousWorkId`：已登记的逻辑工作，跨重试保持不变。
- `Attempt`：某一次运行，每次重试增加；每次都有新的取消源、`Ready` 和 `Completion`。

重试返回**新的 handle**，旧 handle 的失败结果保持不变。

```csharp
public sealed class RuntimeHost
{
    public ContinuousWorkHandle RetryContinuousWork(
        ContinuousWorkId id);
}

public sealed record ContinuousWorkFailure(
    ModuleId Module,
    ContinuousWorkId Work,
    string WorkName,
    long Attempt,
    RuntimeFailure Failure);

public sealed class RuntimeHost
{
    public event Action<ContinuousWorkFailure>? ContinuousWorkFailed;
}
```

`RetryContinuousWork` 同步验证并登记新的运行，然后调度执行，不在调用线程开展业务。调用方通过新 handle 的 `Ready` 等待重试是否成功就绪，通过 `Completion` 观察之后的结束结果。

规则：

- 只重用该项工作登记的执行委托和故障回调，不重新执行 `OnStarting`。
- 前一次运行必须已经结束，包括它自己的收尾；仍在停止或已有重试运行时明确拒绝，避免新旧运行重叠。
- 宿主预退出、正式停止或已停止时，不接受重试。
- 不自动重试，不重放业务 Task。
- 可以重试因故障而停止的工作，也可以重新启动模块主动停止的相关工作；每次入口只启动指定的一项。
- 重试 A 不会自动重启先前由模块停掉的 B。若两者需要恢复顺序，由模块或接入层明确请求，Runtime 不建立新的依赖重启机制。
- 运行条件检查交给这次执行委托。条件仍不满足时，本次 `Ready`／`Completion` 明确失败，不新增预检接口。

新的契约仅要求：**登记为可重试的持续工作委托，能够为每次运行创建自身状态并正确收尾。**不再要求模块的 `OnStopping`／`OnStarting` 支持反复执行。

本轮仅处理以上两点，未读取现有代码或目录外内容，未修改文件，未编写实现。

### 第十八轮：被连带停掉的工作怎样恢复、工作编号是否需要

本轮的提问：Claude 追问两处：模块在故障回调里停掉的其他工作，A 重试后没人负责恢复；`ContinuousWorkId` 和 `Attempt` 有什么场景要用。

#### 1. A 恢复后怎样恢复 B

**选择让 B 在 A 不可用时异步等待，不停止 B。**这最简单，也不需要新增就绪回调、重启通知或依赖重启机制。

这个例子中，依赖关系属于模块自身的业务逻辑：

1. A 失败，模块将自己的元数据服务标记为不可用。
2. B 在使用元数据前检查服务状态；不可用时等待模块持有的可用性信号，不忙循环、不启动重试。
3. 用户请求重试 A，Runtime 重新调用原先登记的执行委托。
4. A 完成初始化后，由该委托更新同一个服务对象，并通知等待者服务已经可用，再报告本次工作就绪。
5. B 的等待结束，继续发送提醒。

模块不需要取得 A 的新 handle：**A 每次运行的业务代码本来就由模块提供，恢复服务状态也由这段代码负责。**

补充规则：

- B 的等待必须响应自身的 `Stopping`，否则宿主停止时可能永远等待 A 恢复。
- B 每次实际使用服务时仍须处理服务再次不可用的情况；收到可用信号不代表服务永远不会失败。
- 如果 A 在 B 已经执行某项操作时失败，该操作由服务照实报错，B 再按业务逻辑等待恢复。
- `RequestStop()` 保留给确实要终止另一项工作的情况；它不附带自动恢复语义。本例改为等待，不再连带停止 B。

**无需新增 Runtime 签名。**可用状态和等待信号属于模块内部服务，不提升为 Runtime 的依赖管理功能。

#### 2. 删除独立 ID 和 Attempt 编号

##### `ContinuousWorkId`：删除

当前没有工作重命名、跨宿主迁移或其他需要独立身份的场景。`(ModuleId, Name)` 已能唯一定位登记项，单独 ID 没有额外价值。

规则调整为：

- 同一模块内持续工作名字必须唯一。
- 名字在登记后保持不变，按序数、区分大小写比较。
- 重试定位既有登记，不重新登记或改变名字。
- 不同模块可以使用相同工作名字。

##### `Attempt`：删除

HL 只需显示故障并请求重试，没有消费者需要读取次数来决定行为。

Runtime 仍须区分每次运行，但可以直接使用各次运行记录、取消源及完成对象，不需要公开递增编号：

- 每次重试返回新的 handle。
- 旧 handle 永久表示旧次运行的结果。
- 当前仍在运行或停止中时，重复重试明确拒绝。
- 故障仍通过该次 handle 的 `Completion` 和宿主回调报告，不增加次数判断或自动重试政策。

##### 修订后的签名

```csharp
public sealed class ModuleWorkScope
{
    public ContinuousWorkHandle Start(
        string name,
        Func<ContinuousWorkContext, ValueTask> operation,
        Action<ContinuousWorkFailure>? onFailure = null);
}

public sealed class ContinuousWorkHandle
{
    public ModuleId Module { get; }
    public string Name { get; }

    public Task Ready { get; }
    public Task<ContinuousWorkOutcome> Completion { get; }

    public void RequestStop();
}

public sealed record ContinuousWorkFailure(
    ModuleId Module,
    string WorkName,
    RuntimeFailure Failure);

public sealed class RuntimeHost
{
    public ContinuousWorkHandle RetryContinuousWork(
        ModuleId module,
        string name);

    public event Action<ContinuousWorkFailure>? ContinuousWorkFailed;
}
```

删除 `ContinuousWorkId` 和公开的 `Attempt`，不新增运行历史查询或恢复通知接口。

本轮只回答上述两点，未读取代码或目录外内容，未修改文件，未编写实现。

### 第十九轮：`onFailure` 回调是否和执行委托重复

本轮的提问：Claude 追问：执行委托自己的 `try`／`catch`／`finally` 就能在出错时更新服务状态，`Start` 的 `onFailure` 参数有什么场景必须用。

**没有当前场景必须依赖 `onFailure`；删除这个参数。**模块的执行委托已经能够处理服务状态、关联工作的停止请求及自身清理，额外回调重复了职责。

修订后的签名：

```csharp
public sealed class ModuleWorkScope
{
    public ContinuousWorkHandle Start(
        string name,
        Func<ContinuousWorkContext, ValueTask> operation);
}
```

规则：

- 执行委托负责更新模块服务的可用状态、必要时请求停止其他工作，并完成自身清理。
- 处理故障后仍须向外传播异常，不能只更新状态就正常返回，掩盖失败。
- Runtime 捕获传播出的异常，记录真实结果、完成该次运行的 `Completion`，并通过 `RuntimeHost.ContinuousWorkFailed` 通知消费者。
- 正常响应停止请求不作为意外故障通知。
- 重试仍重新调用同一个登记委托，创建新的运行上下文和完成对象；不增加额外故障钩子。

本轮未读取代码或目录外内容，未修改文件，未编写实现。


## 2. 没有达成一致、或需要用户判断是否被带偏的地方

用户要求：凡是 Claude 和 Astra 意见不同、而现有代码正好采用 Claude 这一方的地方，都列在这里交给用户判断。

这次讨论里没有留下双方坚持不同意见的点。但有两处是 Claude 提出了等同于现有代码的做法之后，Astra 改从了这个做法，所以也列在这里：

1. **Registry 的提交方式。**
   - Astra 第一版：模块调用 `CreateRegistry` 得到草稿，登记完调用 `Seal` 冻结，再调用 `Submit` 提交，每个模块恰好提交一次。理由是可以准备多份候选清单、提前冻结，并且能区分“忘记提交”和“主动提交空清单”。
   - Claude 在第二轮提出另一种做法：RegisterEvent 直接把本模块的 Registry 交给处理器，处理器结束就算提交。这就是现有代码的做法。
   - Astra 第二轮改为这种隐式提交，理由是显式提交不能代替“等处理器和必要工作结束”这件事，它的好处只在需要编辑多份候选清单时才有用，而目前没有这个需求。Astra 还补了一条现有代码没有的规则：处理器返回后，如果它登记的必要工作还没结束，注册窗口仍然开着。
   - 用户已确认采用隐式提交，见 4.9 第 15 条。

2. **基元类型和委托的命名。**
   - Astra 第一版叫 `Event` 和 `EventHandler<TEvent>`。
   - Claude 在第三轮指出：项目开了 implicit usings，`EventHandler<TEvent>` 和 `System.EventHandler<TEventArgs>` 同名，模块作者会遇到 CS0104（引用不明确）。现有代码用的名字是 `RuntimeEvent` 和 `RuntimeEventHandler<TEvent>`。
   - Astra 第三轮改为 `RuntimeEvent` 和 `RuntimeEventHandler<TEvent>`。这一处是确实会出现的编译错误，不只是偏好；但名字最终和现有代码一样，所以一并列出。

3. **第二阶段删掉的机制。**
   第四到第七轮里，Claude 认为下面这些机制需求没有要求，Astra 同意删除。删除以后，这几处都和现有代码的做法一样（现有代码本来就没有这些机制），所以按用户的规则列在这里：
   - `EventDefinition.MinimumHandlers`（某个事件至少要有几个订阅者）。
   - `BindEvent` 的 `bindingId` 和 `order`。串行事件改为按模块计划顺序执行，同一模块内按登记顺序执行，和现有代码一样。
   - 事件对象上的 `ExecutionId`，以及拒绝重复或并发派发同一个事件对象的检查。执行身份改放在 `EventContext` 上。
   - 同一调用链上 Task 递归重入的检查，以及执行期检查跨模块调用必须沿依赖方向。
   - `GetSnapshot`、`WatchFaultsAsync` 和有界故障流、缺口检测。改成 `RuntimeHost` 上一个 `ContinuousWorkFailed` 回调，这一项现有代码没有对应物。
   - RegisterEvent 里并行子工作登记 Registry 和原子登记。改成只有处理器本身能顺序登记，处理器返回就关闭登记窗口。
   - 被停止流程等待的处理器反过来等待 `StopAsync` 时的拒绝检查。
   用户已确认这些删除，见 4.9 第 19 条。

4. **第二阶段删掉后又按 HL 需求恢复的机制。**
   Claude 原先提议删掉根 Task 的调度名额和进度观察通道，Astra 在第五、第六轮同意了。HL 的需求补入后，Astra 在第八、第九轮把它们以新的形式恢复：按键排队、可配置的根并发上限、后台等级、Task 的进度和 log 输出入口。Claude 原先的删除理由（“需求只写了待定”“没有 UI，只有 Program.cs”）是 Claude 给错了前提，所以这里不算分歧；其中是否要并发上限、同键下一项能否提前开始，列在 4.6、4.7 请用户决定。

5. **事件的执行方式写在哪里。**
   - Astra 的方案：登记事件时传一个 `EventDefinition(Name, Mode)`，执行方式写在这里。
   - 用户在 4.9 决定 Task 写成继承基元类的类，执行方法写在类里。Claude 据此提出：事件的执行方式也可以照同样的思路写在事件类里，由事件类覆写一个 `Mode` 属性，登记时只写 `RegisterEvent<StorageChangedEvent>()`。这就是现有代码的做法（现有的 `Execution` 属性），只是现有代码只有串行、并行两种，没有区分出错时的处理方式。
   - 用户已决定采用 Astra 的方案（登记时传 `EventDefinition`），理由是事件和 Task 性质不同，不用强行一致。见 4.9 第 5 条。

6. **模块在 `Create` 里建立，还是在 `StartAsync` 里建立。**
   - Astra 的方案：`Create` 里完成检查模块清单、建立模块、接线和第一次锁定，`StartAsync` 从 `OnRegistering` 开始。
   - 用户担心这样会让 `Create` 和 `StartAsync` 的职责混在一起。
   - Claude 提出折中：`Create` 只做不产生副作用的检查（依赖是否齐全、能力表是否满足、排序），有问题立刻报错；建立模块、接线和后面的步骤都留在 `StartAsync`。这样“建立模块”仍在 `StartAsync`，和现有代码一样，所以按用户的规则列在这里。用户已采用这个折中，见 4.9 第 21 条。

7. **第三阶段删掉的机制。**
   第十三、十五、十六轮里，Claude 认为下面这些机制说不出对应的 HL 场景，Astra 同意删除。现有代码本来就没有这些机制，删除后和现有代码一致，所以按用户的规则列在这里：
   - 输出收尾失败后，暂停同一个排队键的队列，或者终止这一批排队任务并停止接纳该键的新任务。改为在这个任务的 `Completion` 里报告输出错误，然后照常执行下一个任务。
   - Stop 把宿主运行期间所有任务的输出故障都汇总进 `StopOutcome.Failures`。改为只汇总排空开始时还没结束的任务。
   - 登记 Task 时必须用具体类型、不能先转成 `RuntimeTask` 的规则。改为直接用 `task.GetType()` 作为登记身份。
   - 每个 Task 都必须覆写 `Name`。改为默认用类名，需要时再覆写。
   - 持续工作出错后，依赖这个模块的其他模块也暂停接纳新业务。改为只停所属模块，其他模块真正调用它的 Task 时才照实失败。
   - 普通事件只要有一个订阅者属于不可用的模块，整次派发就失败。改为不可用模块的订阅者暂时不参与派发。
   - `PublishAsync` 返回被跳过的订阅者名单，错误信息里也附带跳过情况。改为 `PublishAsync` 仍返回 `ValueTask`，模块不可用这件事只通过 `ContinuousWorkFailed` 回调通知。
   - 重试前检查“模块声明的运行前提”。改为交给模块的 `OnStarting` 自己判断，条件不满足就报错，按重试失败处理（后来 4.11 选 B，重试改为重新执行这一项工作，判断也随之交给这项工作自己的代码）。
   - 持续工作的独立编号 `ContinuousWorkId` 和运行次数 `Attempt`（第十八轮）。改为用“模块 Id 加工作名字”定位一项工作。
   - 登记持续工作时传入的故障回调 `onFailure`（第十九轮）。改为由工作自己的代码在出错时更新服务状态，再把异常照常抛出。

8. **第三阶段 Claude 自己定下的实现细节。**
   第十七轮 Astra 建议：模块作者在 Task 类里自己写了同名的 `Submit` 或 `InvokeAsync` 时，生成器报一条专门的错误。Claude 不打算采用，因为 C# 编译器本来就会对重复成员报错。生成器的其他边界情况（嵌套类的外层也要 `partial`、Task 类要求 `sealed`、第一版不支持泛型 Task 类）属于实现细节，会直接写进交给 Sol 的任务。

## 3. Claude 代用户回答过的问题

第二轮里，Claude 把下面四项当作推断告诉了 Astra，Astra 按它们继续设计。之后用户确认了新规则：政策性的选择在用户在电脑前时直接问用户。这四项都属于政策选择，所以不再算作已经代答，改列到第 4 节等用户确认。这里只记录当时说了什么：

| 问题 | 当时告诉 Astra 的回答 | 估计的通过率 | 依据 |
|---|---|---|---|
| 停止后的实例能否重新启动 | 不能，一个进程一次完整生命周期 | 约 85% | 总纲第 1.2 节的建议表写了“第一版一次启动、一次完整生命周期”；用户偏好按规模选最简单的方案 |
| 是否设默认停止超时 | 不设；调用方可以限制自己的等待，但超时不取消宿主收尾，也不自动升级 Kill | 约 85% | 开发约束写明“取消令牌和超时不证明工作已经终止”，并且 Kill 不自动升级 |
| Kill 的终止策略由谁提供 | 调用方显式提供，缺失或失败时报告终止未完成 | 约 80% | 总纲第 1.2 节对“断电”旁路的说明 |
| 持续工作要不要分“必要／非必要” | 第一版全部按必要工作处理 | 约 80% | 用户偏好先搭完整骨架，确有需要再加分类 |

第二阶段的代答：

| 问题 | 告诉 Astra 的回答 | 估计的通过率 | 依据 | 后来的情况 |
|---|---|---|---|---|
| Runtime 第一版以什么为准（第八轮） | 以满足 HL 的现有任务模型为准 | 约 90% | 用户说“应该先做能满足 HL 专案的需求，然后再做其他拓展” | 说得过紧。用户随后说明 HL 现有实现会跟着重构，择优而定；第九轮已按用户原意更正：HL 的业务需求要满足，HL 现有的实现方式不约束 Runtime |

第九轮里转述给 Astra 的“HL 现有框架是 Runtime 的前身，会跟着小重构，择优重构”是用户原话的意思，不是推断。

第三阶段（第十二到十六轮）没有代用户回答。交给 Astra 的只有 4.9 节用户自己的决定；需要用户取舍的两项列在 4.10、4.11。

## 4. 需要用户回答的问题

前三项按用户的要求只列选项和影响，不替用户选。“现有代码”指分支上已经写好的做法。

### 4.1 OnStopRequested 的处理器抛异常时怎么办

| 选项 | 会发生什么 |
|---|---|
| A. 本次停止失败，恢复接收新的 Task，把全部错误交给调用方（Astra 倾向这一项；现有代码也是这样，只是用抛异常而不是返回结果来报告） | Runtime 继续运行，调用方决定何时再请求停止。判定阶段不允许做不可逆的事，所以恢复运行不会遇到半关闭的状态。坏处是，出错的判定处理器在下一次停止时可能再次出错。 |
| B. 当作否决处理，返回 Vetoed，错误另外报告 | 调用方只需要处理“否决”和“停止”两种结果，写法更简单。坏处是“有人明确否决”和“判定出错”在结果上分不开，调用方需要另外检查错误才能知道发生了什么。 |
| C. 本次停止失败，但不恢复接收新的 Task，等调用方再次请求停止或选择 Kill | 出错之后不会再接新工作，状态更保守。坏处是 Runtime 实际上停在“不接新 Task、也不停止”的中间状态，调用方必须主动处理，否则应用会一直拒绝业务请求。 |

Astra 明确不采用“把异常当作同意停止”，理由是没有完成的判定不能授权后面不可逆的资源释放。

### 4.2 重复调用 RuntimeHost.Create 时怎么办

| 选项 | 会发生什么 |
|---|---|
| A. 第二次调用一律抛同一种异常，宿主停止后也不能再创建（现有代码是这样） | 写法最简单，重复创建一定会被发现。调用方看不出是“另一个线程正在创建”还是“早就创建过了”。 |
| B. 和 A 一样拒绝，但分成两种异常：“创建正在进行”和“已经创建过”（Astra 倾向这一项） | 并发调用时能从异常类型看出原因，排查更直接。代价是公开多两个异常类型。Astra 还建议：创建前的纯输入检查失败时可以修正后重试，一旦开始创建模块，失败后也不再允许创建第二个实例。 |
| C. 返回已有实例（GetOrCreate） | 调用方不用自己保存实例。坏处是第二次调用传入的模块列表或能力表和第一次不同时，会被悄悄忽略，配置错误不会暴露。Astra 不采用这一项。 |

### 4.3 并发或重复调用生命周期方法（StartAsync、停止）时报错还是排队

| 选项 | 会发生什么 |
|---|---|
| A. 另一个生命周期操作正在进行时直接报错，状态不对时也报错（现有代码是这样） | 实现最简单，调用方能立刻知道自己重复调用了。坏处是两个地方同时请求停止时，其中一个会收到错误，要自己再等待结果。 |
| B. 共享正在进行的操作（Astra 倾向这一项）：并发 Start 拿到同一个启动结果；运行中再调 Start 返回已有的成功结果；并发 Stop 共享同一次停止 | 多个调用方都能拿到同一个真实结果，不需要自己协调。坏处是要多维护“正在进行的操作”这份状态。（Astra 原先还要求拒绝“被停止流程等待的处理器反过来等待停止”，第七轮已删除，因为模块 API 拿不到 `StopAsync`。） |
| C. 排队，前一个结束后再执行下一个 | 每次调用都会真正执行一遍。坏处是例如两次停止请求，第二次会在已经停止的实例上再执行，所以多数情况下仍要报错或直接返回，实际效果接近 A 或 B。 |

### 4.4 Astra 自己列出的待定问题（附 Astra 的倾向）

- **持续工作故障影响全局还是局部？** Astra 倾向：所属模块及依赖它的模块不再接收新 Task，无关模块继续服务，宿主表现为降级状态。如果产品只接受“全部可用或全部不可用”，就改为全局关闭新的准入。
- **所有资源已经释放，但 OnStopping 里有错误时，状态怎么表示？** Astra 倾向：状态为 Stopped，同时返回不成功的停止结果。只要卸载失败或还有清理义务没完成，仍按已确认规则进入 Hanging。

### 4.5 第 3 节的四项政策选择

请确认或修改第 3 节表格里的四个回答。Astra 目前的方案是按这四个回答设计的。

### 4.6 第一版要不要设根 Task 的并发上限

Astra 第九轮的方案是：宿主配置一个 `MaxConcurrentRootTasks`，同时执行的根 Task 不超过这个数；在等键的任务不占名额；子调用不和父任务争名额。

| 选项 | 会发生什么 |
|---|---|
| A. 第一版就设（Astra 倾向这一项） | 例如用户同时安装三个实例、又跑清理扫描，同时展开的操作数受控制。上限只限制根操作的数量，不等于下载连接数或线程数。要多做的是一个调度器：执行名额、名额的等待和释放、子调用不重新排队。 |
| B. 第一版只做按键排队，不设全局上限 | 不同实例的任务同时开始，同一实例的任务依次执行。调度只需要按键排队，第一版少一整块。以后要加上限时，按 Astra 已经给出的规则补上，接口不用改，因为上限是宿主配置，不在 Task 的签名里。 |

HL 的调度讨论纪要（2026-10-26）倾向有上限的并发，但标着“未批准”。

### 4.7 同一实例的下一个任务，能不能在前一个任务的完成结果送进输出队列之前开始

Astra 第九轮把“业务完成”和“输出完成”分开：前一个任务的业务和资源清理结束，就释放这个键，下一个任务可以开始；前一个任务的最后一批进度和完成结果在之后才送进 HL 的输出队列。

| 选项 | 会发生什么 |
|---|---|
| A. 可以提前开始（Astra 倾向这一项） | 下一个任务不用等界面输出收尾，业务串行不受影响，每个任务自己的完成结果仍然是它的最后一条输出。界面上可能先看到下一个任务的进度、后看到前一个任务的完成结果，界面要按任务的接纳顺序显示。`TaskRun` 多一个 `OutputCompletion`。 |
| B. 保持 HL 现在的规则：前一个任务的完成结果入队以后，下一个才开始 | 界面看到的事件顺序和业务顺序一致，界面不用额外处理。Runtime 释放键之前要等输出适配器的 `CompleteAsync` 返回，输出适配器出错时同键的后续任务会被卡住，需要另外规定怎么放行。 |

HL 的调度讨论纪要已经点出这条语义差异：如果产品要求界面先看到前一项完成、后一项才启动，就应保留现行规则。

### 4.8 后台任务的加权选择（Claude 的疑问，没有和 Astra 讨论）

Astra 第九轮规定：争用执行名额时，正常任务和后台任务按权重选择，并保证一直有正常任务时后台任务仍能轮到。这只在 4.6 选 A 时才有意义。Claude 认为第一版可能用不到权重和“保证轮到”，只要“后台任务排在正常任务后面”就够了；如果 4.6 选 A，请用户决定要不要先简化成这样，再交给 Sol 实现。

### 4.9 用户已经答复的意见（2026-11-02）

以下是用户在审查第 5 节时给出的决定，不是 Claude 的推断。交给 Sol 实现之前，Claude 会先把它们告诉 Astra，请它按这些决定修订相关签名。

1. **Task 只能由登记它的模块自己实现。**
   不允许 A 模块定义 Task、B 模块来实现。用户的理由是：Task 和 Minecraft 里模组注册方块一样，自己注册，并在方块内部定义它的行为。用户能想到的“别人来实现”的场景只有一个：Gebinde 在不用 CAS 时自己用一个小实现，用 CAS 时换另一种实现；这可以用两个不同的 Task 加一个判断解决，不写这项能力反而更清楚。
   因此，方案里把 Task 拆成 `DefineTask` 和 `BindTask` 两步的做法去掉，合并检查里的“Task 缺少执行绑定或有多个执行绑定”和“跨模块绑定 Task 时检查依赖关系”也随之去掉。

2. **Task 写成类，继承基元 Task 类，行为写在类里。**
   写法如下，基类名 `RuntimeTask<TInput, TResult>` 是 Claude 暂定的，和 `RuntimeEvent` 对应，不叫 `Task` 是为了不和 `System.Threading.Tasks.Task` 重名：

   ```csharp
   sealed class SaveValueTask : RuntimeTask<SaveRequest, bool>
   {
       protected override ValueTask<bool> ExecuteAsync(TaskEvent<SaveRequest, bool> e) { ... }
   }

   e.Registry.RegisterTask(new SaveValueTask());
   ```

   原先写在 `TaskDefinition` 里的名字和退出策略，改为基类上的属性，由子类覆写。基类的具体成员请 Astra 确认。

3. **Registry 的方法名。**
   - 登记 Task：`RegisterTask`。
   - 登记事件：`RegisterEvent`。
   - 订阅事件：`Subscribe`，不叫 `BindEvent`。事件被触发时执行的方法叫“订阅者”。

4. **内置的注册事件改名为 `RuntimeEvents.OnRegister`（第 8 条又统一为 `OnRegistering`）。**
   原因是它原来叫 `RegisterEvent`，和第 3 条的方法名 `RegisterEvent` 重名，在它的处理器里写 `e.Registry.RegisterEvent<...>()` 时分不清哪个是事件、哪个是方法。改名后，它和 `OnLoading`、`OnStarting` 这些生命周期事件放在一起。需求文档里一直用“RegisterEvent”称呼这个事件，实现时要跟着改名。

5. **事件的执行方式在登记时声明。**
   采用 Astra 的方案：事件类只放数据，登记时写 `e.Registry.RegisterEvent(new EventDefinition<StorageChangedEvent>("storage.changed", EventDispatchMode.ParallelJoin))`。用户的理由是事件和 Task 性质不同，不用为了和 Task 的写法一致而把执行方式放进事件类。

6. **并发上限（回答 4.6，已被第 16 条改为暂缓）。**
   要有配置入口，但默认不设上限。配置可以从配置文件读取后注入。根 Task 并发和子任务并发分别用一个配置项设置。
   Claude 的补充：子任务并发上限会重新带来“父等子”的死锁问题。例如上限是 4，四个子任务各自又调用了孙任务并等待，孙任务就永远拿不到名额。所以子任务上限怎样计数（按每个根任务计，还是全局计；正在等待自己后代的子任务算不算占名额），要请 Astra 设计。

7. **HL 原生库要跟着重构。**
   Task 的排队、接收、取消和退出策略都放进 Runtime 以后，HL 原生库里现有的任务调度（每个任务一个线程、按键排队、退出策略声明）要改成调用 Runtime，不再自己维护一套。

8. **生命周期的命名和结果。**
   - 事件名和否决属性的拼写，用户交给 Claude 决定。Claude 定为：生命周期事件统一用进行式，即 `OnRegistering`、`OnLoading`、`OnStarting`、`OnStopRequested`、`OnStopping`、`OnUnloading`（第 4 条的 `OnRegister` 随之改为 `OnRegistering`）；否决属性保留现有的美式拼法 `IsCanceled`，和 .NET 的 `OperationCanceledException`、`Task.IsCanceled` 一致。
   - 模块 Id 用专门的 `ModuleId` 类型，采用方案。
   - 停止结果用 `StopOutcome(Kind, Failures)` 返回，不抛异常，采用方案。

9. **并发或重复调用生命周期方法时直接报错（回答 4.3，选 A）。**
   用户的理由是调用方本来就不应该重复调用这些方法。

10. **持续工作出错时只停掉所属模块（回答 4.4 的第一问）。**
    把错误返回给调用方，并提供一个请求重试的入口。“重试”具体是重新启动这个持续工作还是重新启动整个模块，以及入口的签名，要请 Astra 设计。

11. **预退出的方法名缩短。**
    `PrepareStopAsync` 改为 `PreStopAsync`，`CancelStopPreparation` 改为 `CancelPreStop`，正式停止仍叫 `StopAsync`（不再叫现有的 `RequestStopAsync`）。

12. **`Create` 重复调用时只用一种异常（回答 4.2）。**
    不区分“正在创建”和“已经创建过”，在错误信息里写清楚是哪种情况。

13. **`OnStopping` 出错但资源都已释放时，状态为 Stopped，错误放进 `StopOutcome.Failures`（回答 4.4 的第二问，选 A）。**

14. **TODO：`OnStopRequested` 的否决语义以后再细化。**
    用户指出：像“正在把一大批数据写进磁盘、中途不能停”这种情况，应该做成退出策略为 `Wait` 的任务，进入预退出后让它继续做完，而不是靠 `OnStopRequested` 否决。所以 `OnStopRequested` 什么时候需要否决、和预退出怎样分工，现在设计还早，等做到这一部分时再细化。
    4.1（判定时订阅者抛异常）暂定选 A：本次停止失败，返回 `DecisionFailed` 和错误，Runtime 恢复运行。细化 `OnStopRequested` 时，要回头检查这个答案和第一轮第 7 节的方案还能不能沿用。

15. **确认 Registry 用隐式提交（第 2 节第 1 条）和第二轮的四项代答（4.5）。**
    用户认为显式的 `Seal`／`Submit` 写法啰嗦，用处理器返回这个隐式动作表达“登记完成”是对的。第 3 节表格里的四项回答全部通过。

16. **并发上限整体放进计划，第一版不做（第 6 条改为暂缓）。**
    根任务和子任务的并发上限、配置入口、子任务上限的计数规则，第一版都不做，记录在 [`docs/plans/`](plans/) 里，需要时再拿出来。用户补充：并发上限主要针对用户手动发起的任务（主动并发），后台任务（被动并发）应该和它分开考虑；后台任务的加权选择这个提案有价值，也记进计划。

17. **Task 直接用类配对，不再需要 `TaskKey`。**
    每个 Task 都是一个类，类不会重复，所以调用方写 `host.Submit<SaveValueTask>(request)`，跨模块调用写 `context.Work.InvokeAsync<SaveValueTask>(request)`。

18. **事件执行方式的三种（第 5 节 5.1 的“执行方式由谁决定”）。**
    - 串行：出错就停（后面的订阅者不再执行），或者跳过出错的订阅者、继续执行后面的、最后返回错误，两种都可以，也就是方案里的 `SerialFailFast` 和 `SerialCollect`。
    - 并行：可以选择错误在什么时候返回。一种是全部订阅者结束后汇总返回，也就是方案里的 `ParallelJoin`；另一种是某个订阅者一出错就先返回，方案里没有这一种，要请 Astra 设计。

19. **确认第二阶段删掉的七项机制（第 2 节第 3 条）。**
    用户认为这些都属于过度防御和过度工程化，删除正确。

20. **4.7 选 B：同一实例的下一个任务，要等前一个任务的完成结果进入输出队列后才开始。**
    这样 `TaskRun` 只需要一个 `Completion`，它在输出收尾完成后才结束；去掉单独的 `OutputCompletion`，停止时也只等 `Completion`。输出收尾出错属于内部故障，照实报错。

21. **`Create` 只做没有副作用的检查（第 2 节第 6 条，按 Claude 的折中）。**
    `Create` 检查依赖是否齐全、能力表是否满足并排序，有问题立刻报错；建立模块、接线、第一次锁定和后面的步骤都在 `StartAsync` 里。

### 4.10 启动 Task 的那一行代码怎么写（第十二、十三轮）

**用户已答复（2026-11-02）：采用 C2。**Task 类写成 `partial`，继承 `RuntimeTask<TInput, TResult>`，不带自身类型参数；生成器为每个 Task 类在类里生成带完整类型的静态 `Submit` 和 `InvokeAsync`，内部转调 B 那种写全类型参数的公开方法。Task 类忘了写 `partial` 时，生成器报编译错误提醒加上。这样不需要登记时检查自身类型参数，也不会触发 Rider 对“通过子类名调用基类静态方法”的提示。C2 是 Claude 在 Astra 的选项之外补充的，下面的表格保留原来的三个选项供对照。Astra 在第十七轮确认 C2 可行，Runtime 对外提供的底层签名见那一轮。

```csharp
// 模块作者写的
public sealed partial class SaveValueTask : RuntimeTask<SaveRequest, bool>
{
    protected override ValueTask<bool> ExecuteAsync(TaskEvent<SaveRequest, bool> e) { ... }
}

// 生成器生成的
partial class SaveValueTask
{
    public static TaskRun<bool> Submit(RuntimeHost host, SaveRequest input, ...)
        => host.Submit<SaveValueTask, SaveRequest, bool>(input, ...);

    public static ValueTask<bool> InvokeAsync(WorkScope work, SaveRequest input)
        => work.InvokeAsync<SaveValueTask, SaveRequest, bool>(input);
}

// 调用
var run = SaveValueTask.Submit(host, request);
bool ok = await SaveValueTask.InvokeAsync(context.Work, request);
```

Task 本身的写法已经定了（4.9 第 2 条）。这里要选的是启动 Task 的代码，它出现在两个地方：HL 的 C 接口收到 `hl_start` 后调用 `host.Submit`，以及一个模块在自己的 Task 里调用别的模块的 Task。

难点是 C# 不能只根据 `SaveValueTask` 这个类名推出它的输入类型 `SaveRequest` 和结果类型 `bool`，所以 4.9 第 17 条的写法要么放弃编译时的类型检查，要么把类型写全。

```csharp
// A：4.9 第 17 条原来的写法。输入和结果的类型在运行时才检查。
TaskRun run = host.Submit<SaveValueTask>(request);
bool ok = (await context.Work.InvokeAsync<SaveValueTask>(request)).Get<bool>();

// B：写全三个类型参数。编译时检查类型，每次调用都要重复写输入和结果类型。
TaskRun<bool> run = host.Submit<SaveValueTask, SaveRequest, bool>(request);
bool ok = await context.Work.InvokeAsync<SaveValueTask, SaveRequest, bool>(request);

// C：Task 类在继承时多写一次自己的类名，调用从 Task 类上发起。
sealed class SaveValueTask : RuntimeTask<SaveValueTask, SaveRequest, bool> { ... }

TaskRun<bool> run = SaveValueTask.Submit(host, request);
bool ok = await SaveValueTask.InvokeAsync(context.Work, request);
```

| 选项 | 会发生什么 |
|---|---|
| A. 运行时检查 | 调用最短。传错输入类型或取错结果类型时，程序运行到这一行才报错；`InvokeAsync` 的结果要多写一次 `Get<bool>()` |
| B. 写全类型参数 | 编译时发现类型错误。每个调用处都要写出输入和结果类型 |
| C. 从 Task 类发起（Astra 推荐） | 调用短，编译时发现类型错误。Task 的继承声明多写一次自己的类名；登记时要检查声明里的类名和实际的类一致，防止复制类时忘了改。Claude 记得 Rider 会对“通过子类名调用基类的静态方法”给出提示，这一点没有在 Rider 里验证过 |

### 4.11 持续工作出错后，模块能不能在同一个实例上重新启动（第十四、十五轮）

**用户已答复（2026-11-02）：选 B，并补充一条。**只停止出错的那一项持续工作，重试时重新启动这一项；模块可以在出错时自己决定把同模块里依赖它的其他持续工作也停掉。不要求模块的 `OnStopping`／`OnStarting` 能反复执行。其他模块直接持有这个模块的服务对象时，由服务自己报告不可用，这一点沿用第十五轮的规则。Astra 在第十七到十九轮按 B 重新设计了重试入口，结果见第 5 节 5.5。

4.9 第 10 条要求提供重试入口。Astra 的设计是：持续工作出错后，所属模块执行 `OnStopping` 停止全部运行活动，但不执行 `OnUnloading`，已经加载的资源保留；用户请求重试时调用 `RetryModuleAsync(module)`，Runtime 等旧工作真正结束后，重新执行这个模块的 `OnStarting`。

这要求模块作者保证同一个模块实例可以先执行 `OnStopping`、再执行 `OnStarting`，也就是 `OnStopping` 只停止运行活动，最终释放资源的事放在 `OnUnloading` 里。宿主整体停止后仍然不能重启，这一点不变。

| 选项 | 会发生什么 |
|---|---|
| A. 停止整个模块的运行活动，重试时重新执行 `OnStarting`（Astra 推荐） | 模块停止和恢复的范围一致，重试后模块的所有持续工作都重新启动。模块作者要按上面的要求写 `OnStopping` 和 `OnStarting` |
| B. 只停止并重试出错的那一项持续工作 | Runtime 要做的事少一些。“停掉所属模块”的含义变成“模块不接纳新业务，但其他持续工作继续运行”；重试只重新启动出错的那一项 |

## 5. 方案和现有实现的差异

下面对比方案和分支 `runtime/event-skeleton`（提交 d3fe33a）上的代码。“一致”的项目不需要改；其余各项是按方案实现时要改的地方。表格已经按第二、第三阶段（第四到第十九轮）的结论，以及用户在 4.9、4.10、4.11 的答复更新。

### 5.1 基元类型与派发

| 项目 | 现有实现 | 方案 |
|---|---|---|
| 基元事件名 | `RuntimeEvent` | 一致 |
| 执行方式由谁决定 | 事件类型重写 `Execution` 属性，只有 `Sequential`、`Parallel` 两种 | 共四种：`SerialFailFast`、`SerialCollect`、`ParallelJoin`、`ParallelFailFast`。`ParallelFailFast` 在某个订阅者出错时先把错误交给派发方，不取消其他订阅者；其他订阅者仍由当前执行等到结束，它们后来出的错并入最终结果（4.9 第 18 条，第十四轮）。登记事件时在 `EventDefinition` 里声明（4.9 第 5 条）。生命周期事件和 TaskEvent 的方式由 Runtime 固定 |
| 每次执行的身份 | 没有 | `EventContext.ExecutionId`。事件对象不带执行身份，也不检查同一个对象是否被重复派发 |
| 订阅者签名 | `(TEvent e, CancellationToken cancellationToken)` | `(TEvent e, EventContext context)`。`EventContext` 包含执行身份、订阅者所属模块、取消令牌和 `WorkScope` |
| 串行顺序 | 按订阅登记的先后顺序，也就是模块的依赖顺序 | 一致：按模块计划顺序，同一模块内按登记顺序 |
| 定义时的约束 | 没有 | 只有名字和执行方式两项；普通事件没有订阅者时正常完成 |
| 派发自己定义的事件 | `Registry.DefineEvent` 返回 `EventSource<TEvent>`，在任何地方都可以调用它的 `RaiseAsync` | 去掉 `EventSource`。在处理器或工作里通过 `WorkScope.PublishAsync` 派发，算作当前执行的子工作；在处理器以外（例如持续工作）通过 `ModuleWorkPort.PublishAsync` 派发，算作新的根工作，受准入限制。只有定义事件的模块能派发 |
| 观察者 | 没有 | 不提供通用观察通道。Task 的进度和 log 走 5.3 的 `TaskOutput` 和输出接收端 |

### 5.2 接线、Registry 与两次锁定

| 项目 | 现有实现 | 方案 |
|---|---|---|
| 模块接口 | `IRuntimeModule.Subscribe(EventSubscriber events)`，用通用的 `Subscribe<TEvent>`，但运行时只允许订阅 RegisterEvent | `IRuntimeModule.Connect(ModuleWiring wiring)`，只有订阅 `OnRegistering` 的一个方法，另外提供可以保存下来的 `RootWork` |
| 模块在什么时候创建 | `Create` 只保存模块列表；在 `StartAsync` 里检查列表、创建模块、接线、第一次锁定 | `Create` 只做没有副作用的检查：依赖是否齐全、能力表是否满足，并排序，有问题立刻报错。创建模块、接线、第一次锁定和后面的步骤都在 `StartAsync` 里（4.9 第 21 条）。原来的 RegisterEvent 改名为 `OnRegistering`（4.9 第 4、8 条） |
| Registry 的方法 | `DefineEvent<TEvent>()`、`Subscribe<TEvent>(handler)`、`AddTask<TInput, TResult>(handler)` | `RegisterTask(RuntimeTask task)`、`RegisterEvent(EventDefinition<TEvent>)`、`Subscribe<TEvent>(handler)`（4.9 第 3、5 条）。模块不能订阅 `TaskEvent` |
| 提交时机 | 处理器返回就提交 | 处理器返回就关闭本模块的登记窗口，只有处理器本身能顺序登记。处理器登记的必要工作还在运行时，要等它们结束才接受这份清单，但它们不能再登记 |
| 合并检查 | 不能定义内置事件；同一事件不能被两个模块定义；同一输入类型只能有一个 Task；不能通过 Registry 订阅 RegisterEvent 或 TaskEvent；订阅的事件必须已经定义 | 保留“不能登记内置事件”“同一事件不能被两个模块登记”“订阅的事件必须已经登记”。“同一输入类型只能有一个 Task”改为“同一个 Task 类不能登记两次”，登记身份是 `task.GetType()`（4.9 第 17 条，第十三轮）。新增两项：订阅了别的模块登记的事件时，两个模块之间要有对应的依赖关系；登记不能占用不属于自己的身份。Task 只能由自己的模块实现，所以不需要检查“缺少实现或有多个实现”（4.9 第 1 条） |

### 5.3 Task

| 项目 | 现有实现 | 方案 |
|---|---|---|
| 怎样配对 | 按输入类型，一种输入类型只对应一个 Task | 按 Task 类配对，不再需要 `TaskKey`。同一种输入类型可以对应多个不同的 Task 类（4.9 第 17 条）。调用写成 `SaveValueTask.Submit(host, request)`，这个静态方法由生成器生成（4.10 选 C2） |
| Task 怎样写 | 没有单独的定义，`AddTask` 直接给处理器 | Task 写成类，继承 `RuntimeTask<TInput, TResult>`（名字暂定），在类里覆写执行方法；名字和退出策略 `ExitPolicy`（`Wait` 或 `CancelOnExit`，只对根任务生效）是基类属性。用 `RegisterTask(new SaveValueTask())` 登记，只能由登记它的模块实现（4.9 第 1、2 条）。`Name` 默认用类名，需要时再覆写；`ExitPolicy` 必须覆写。执行方法是 `ExecuteAsync(TaskEvent<TInput, TResult> e)`，输入、执行身份、取消令牌、`WorkScope` 和 `TaskOutput` 都从 `e` 上取（第十二、十三轮）。Task 类要写成 `sealed partial`，漏写 `partial` 时生成器报编译错误（4.10 选 C2） |
| 怎样给出结果 | 调用 `e.SetResult(value)`，结束时没有设置结果就算失败 | 执行方法直接返回 `ValueTask<TResult>` |
| 调用方入口 | `RunTaskAsync<TInput, TResult>(input)` 返回 `Task<TResult>`，失败时抛异常 | 模块作者和 HL 写 `SaveValueTask.Submit(host, input, options, output, cancellation)`，它转调 Runtime 公开的 `host.Submit<TTask, TInput, TResult>(...)`，返回 `TaskRun<TResult>`（包含 `Id`、`State`、`Completion`、`RequestCancellation`）。`options` 是排队键，`output` 是接入层提供的输出接收端。没有被接纳时抛出异常；被接纳以后，`Completion` 不抛异常，而是返回 `TaskCompletion(Business, OutputFailure)`：`Business` 是成功、失败或已取消三种结果之一，`OutputFailure` 是输出收尾的错误，没有出错时为空。`Completion` 要等输出收尾完成才结束，不再有单独的 `OutputCompletion`（4.9 第 20 条，第十二轮） |
| 取消的判定 | 处理器抛出 `OperationCanceledException` 时直接向上抛 | 只有能对应到本次有效的取消来源时才算已取消；失败优先于取消；收到取消请求但工作都正常完成时，仍然算成功 |
| 信道内的短程工作 | `TaskEvent.Run(Func<CancellationToken, Task>)`，只在 TaskEvent 上有 | 所有事件都有 `WorkScope.Run(name, operation)`，可以带返回值、返回 `WorkHandle`；按父子结构登记，处理器返回后就不能再登记，子工作还可以登记自己的后代 |
| 主处理器失败时 | 不取消信道里的工作，只等它们结束 | 向还在运行的必要子工作发出取消请求，再等它们真正结束；结果仍然是失败 |
| 跨模块调用 | 没有 | 写 `SaveValueTask.InvokeAsync(context.Work, input)`，它转调 `WorkScope.InvokeAsync<TTask, TInput, TResult>(input)`，算作当前执行的必要子调用；Stop 关闭准入以后，在已接纳的执行里仍然可以调用。不检查递归调用，也不在执行期检查依赖方向 |
| 调度 | 没有，提交后直接执行 | 同一个排队键的根任务按接纳顺序依次执行，前一个任务的完成结果进入输出队列后，下一个才开始（4.9 第 20 条）；前一个任务输出收尾失败时，在它的 `Completion` 里报告错误，然后照常开始下一个（第十三轮）。排队中的任务可以直接取消。子调用不重新排队。第一版没有并发上限，也没有后台任务的加权选择，这两项记在 `docs/plans/` 里（4.9 第 16 条） |
| 进度和 log | 没有 | 处理器通过 `TaskOutput` 同步报告进度和 log；节流、合并、缓冲上限和传输由接入层的输出接收端负责。业务和子工作结束后封闭报告入口，再调用接收端的 `CompleteAsync` 交出最终结果 |

### 5.4 生命周期

| 项目 | 现有实现 | 方案 |
|---|---|---|
| 事件名 | `RegisterEvent`、`OnLoad`、`OnStart`、`OnStopRequested`、`OnStopping`、`OnUnload` | `OnRegistering`、`OnLoading`、`OnStarting`、`OnStopRequested`、`OnStopping`、`OnUnloading` |
| 目标模块 | 每个事件各自有 `string ModuleId` | 基类 `LifecycleEvent` 有 `ModuleId TargetModule`，模块 Id 用专门的类型 |
| 否决属性的拼写 | `IsCanceled` | 一致（4.9 第 8 条） |
| OnStopRequested 的派发 | 一个事件对象派发给所有模块 | 每个模块收到一个新的事件对象，它们共用同一个操作 Id，由宿主汇总全部否决和错误。否决的语义以后再细化（4.9 第 14 条） |
| 停止的入口与结果 | `RequestStopAsync()` 返回枚举 `StopOutcome`（`Vetoed`、`Stopped`）；清理失败时抛 `RuntimeCleanupException` | `StopAsync()` 返回 `StopOutcome(Kind, Failures)`，`Kind` 有 `Stopped`、`Vetoed`、`DecisionFailed`、`CleanupFailed` 四种。判定时订阅者抛异常返回 `DecisionFailed`，Runtime 恢复运行（4.9 第 14 条，暂定）；`OnStopping` 出错但资源都已释放时仍是 `Stopped`，错误放进 `Failures`（4.9 第 13 条）。排空时只等排空开始时还没结束的任务的 `Completion`，它已经包含输出收尾；这些任务的输出收尾失败记进 `Failures`，宿主仍然进入 Stopped。排空开始前已经结束的任务不再重复汇总（第十三轮） |
| 预退出 | 没有 | `PreStopAsync()` 关闭新任务的接纳，并取消 `CancelOnExit` 的根任务；`CancelPreStop()` 撤销，已取消的任务不恢复。直接调用 `StopAsync()` 不取消任何任务 |
| 启动失败时的回滚 | 反序派发 OnStopping，再反序派发 OnUnload，然后报告原始错误；清理失败时进入 Hanging | 一致 |
| 阶段内的顺序 | 按依赖顺序逐个模块串行 | 一致 |
| 并发和重复调用 | 直接报错 | 一致（4.9 第 9 条） |
| Create 只能调用一次 | 一个 `Interlocked` 标志，所有重复调用都抛同一种异常 | 一致（4.9 第 12 条） |

### 5.5 现有实现完全没有的部分

- 持续工作：在 `OnStarting` 里通过 `e.ContinuousWork.Start(name, operation)` 登记，归属于模块的生命周期；它有 `Ready` 和 `Completion` 两个观察点，处理函数通过 `ContinuousWorkContext` 拿到 `Stopping` 令牌、`ReportReady` 和 `RootWork`。启动时要等它报告就绪；停止时先给所有持续工作发结束信号，再统一等待它们结束。
- 模块的根工作入口 `ModuleWorkPort`：在 `Connect` 时交给模块，可以派发本模块的事件、提交 Task，受准入限制。
- 持续工作出错时（4.9 第 10 条，4.11 选 B，第十七到十九轮）：
  - 只结束出错的这一项工作。Runtime 不执行模块的 `OnStopping`，不关闭模块的新业务接纳，也不跳过模块的事件订阅者。
  - 工作自己的代码在出错时更新模块服务的可用状态，再把异常照常抛出。依赖失败服务的 Task 在执行时照实报告不可用，其他模块持有的服务对象也由服务自己报告。
  - 同模块里依赖它的工作，推荐的写法是在服务不可用时等待，服务恢复后继续，等待时仍要响应停止。模块也可以用 handle 的 `RequestStop()` 直接停掉它，但停掉的工作不会自动恢复。
  - 工作在启动就绪前出错时，启动等待照样失败；响应正常停止请求而结束的，不算出错。
  - 错误通过 `RuntimeHost.ContinuousWorkFailed` 回调交给 HL，内容是模块 Id、工作名字和错误。
  - 重试入口是 `RuntimeHost.RetryContinuousWork(module, name)`，它重新运行这一项工作原来登记的代码，返回新的 handle，通过新 handle 的 `Ready` 和 `Completion` 观察结果。前一次运行还没真正结束、宿主处于预退出或停止时，重试直接报错。不自动重试。
  - 登记写法是 `Start(name, operation)`，同一模块内工作名字不能重复。
- 在途任务查询（HL 的 `hl_list`、hot restart）：Runtime 不提供，由 HL 的任务登记表和输出接收端自己记录状态和最新进度。

### 5.6 不受影响的部分

- `ModuleDefinition`、`RuntimeModuleManifestAttribute`、`RuntimeCapabilities`。
- 生成器现有的模块清单部分不受影响，因为生成的创建代码不调用 `IRuntimeModule` 的成员，把 `Subscribe` 改成 `Connect` 不影响它。生成器要新增一部分：为每个 Task 类生成 `Submit` 和 `InvokeAsync`（4.10 选 C2）。
- 模块列表检查、依赖排序和能力表检查的逻辑本身不变，只是挪到 `Create` 里执行。

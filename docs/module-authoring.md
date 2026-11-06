# 模块开发指南

本篇写给要为 HaythamRuntime 编写模块的程序员，例如 Haytham Launcher 的业务模块作者。读完以后，你应当能从零写出一个模块包：建项目、接入 Runtime、登记事件和 Task、处理生命周期、写持续工作、调用别的模块，并处理好错误和取消。

本篇只讲怎么写模块，不重复设计理由。需要理由或完整规则时，请看 [Event 框架规格](event-framework.md)（下文简称“规格”）和 [模块清单与 NativeAOT](module-entry-aot.md)。如果规格与源码不一致，以 `src/Haytham.Runtime/` 里的实际代码为准。

本篇用到几个词，先在这里各解释一次：

- **模块（module）**：一个 NuGet 包或一个 C# 项目，里面有且只有一个模块主类。
- **Task**：一次可以被提交、排队、取消、报告进度的业务操作，写成一个类。
- **事件（event）**：模块之间用来通知的消息，写成一个类，由定义它的模块发布，由订阅者处理。
- **根执行**：从模块外面（宿主程序或持续工作）提交进来的一次 Task 或一次事件发布。根执行里面再启动的工作都是它的“子工作”，合起来叫“工作树”。
- **持续工作**：模块启动后一直运行的后台循环，例如心跳、连接保活。

## 1. 一个最小模块

下面是一个完整的模块包 `Acme.Notes`：它在内存里保存笔记，提供一个 Task 用来添加笔记，并在添加后发布一个事件。

项目文件 `Acme.Notes.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <!-- 模块的 Id、版本、作者和说明来自这四项，生成器把它们写进模块清单。 -->
        <PackageId>Acme.Notes</PackageId>
        <Version>1.0.0</Version>
        <Authors>Acme</Authors>
        <Description>Keeps a list of notes in memory.</Description>
    </PropertyGroup>

    <ItemGroup>
        <ProjectReference Include="..\Haytham.Runtime\Haytham.Runtime.csproj" />
        <ProjectReference
            Include="..\Haytham.Runtime.Generators\Haytham.Runtime.Generators.csproj"
            OutputItemType="Analyzer"
            ReferenceOutputAssembly="false" />
    </ItemGroup>

</Project>
```

模块代码 `NotesModule.cs`：

```csharp
using Haytham.Runtime.Events;
using Haytham.Runtime.Modules;
using Haytham.Runtime.Tasks;

namespace Acme.Notes;

// Task 的输入。输入和结果类型会出现在公开的 Task 上，所以要写成 public。
public sealed record AddNote(string Text);

// 本模块定义的事件。
public sealed class NoteAddedEvent(string text, int count) : RuntimeEvent {
    public string Text { get; } = text;
    public int Count { get; } = count;
}

[RuntimeModule]
internal sealed class NotesModule(TextWriter log) : IRuntimeModule {
    private readonly object gate = new();
    private List<string>? notes;

    // Connect 只做一件事：接上注册处理器。
    public void Connect(ModuleWiring wiring) => wiring.SubscribeRegister(OnRegistering);

    private ValueTask OnRegistering(RuntimeEvents.OnRegistering e, EventContext context) {
        e.Registry.RegisterEvent(new EventDefinition<NoteAddedEvent>("Note added", EventDispatchMode.ParallelJoin));
        e.Registry.RegisterTask(new AddNoteTask(this));
        e.Registry.Subscribe<RuntimeEvents.OnLoading>(OnLoading);
        e.Registry.Subscribe<RuntimeEvents.OnUnloading>(OnUnloading);
        return ValueTask.CompletedTask;
    }

    private ValueTask OnLoading(RuntimeEvents.OnLoading e, EventContext context) {
        lock (gate) {
            notes = [];
        }

        log.WriteLine($"[{e.TargetModule}] loaded");
        return ValueTask.CompletedTask;
    }

    private ValueTask OnUnloading(RuntimeEvents.OnUnloading e, EventContext context) {
        lock (gate) {
            notes = null;
        }

        log.WriteLine($"[{e.TargetModule}] unloaded");
        return ValueTask.CompletedTask;
    }

    internal int Add(string text) {
        lock (gate) {
            notes!.Add(text);
            return notes.Count;
        }
    }
}

public sealed partial class AddNoteTask : RuntimeTask<AddNote, int> {
    private readonly NotesModule module;

    internal AddNoteTask(NotesModule module) => this.module = module;

    public override TaskExitPolicy ExitPolicy => TaskExitPolicy.Wait;

    protected override async ValueTask<int> ExecuteAsync(TaskEvent<AddNote, int> e) {
        var count = module.Add(e.Input.Text);
        await e.Work.PublishAsync(new NoteAddedEvent(e.Input.Text, count));
        return count;
    }
}
```

承载 Runtime 的应用提交这个 Task，并读取结果：

```csharp
var run = AddNoteTask.Submit(host, new AddNote("hello"));
var completion = await run.Completion;
if (completion.Business is TaskOutcome<int>.Succeeded success) {
    Console.WriteLine($"现在有 {success.Result} 条笔记");
}
```

这段代码里有几处是模块作者的固定写法：

- 模块主类标记 `[RuntimeModule]` 并实现 `IRuntimeModule`。类本身可以是 `internal`，因为生成器在同一个程序集里另外写了一个公开的入口类。
- `Connect` 只接上一个注册处理器。事件、Task 和订阅都在注册处理器里通过 `e.Registry` 登记。
- 模块需要的外部对象（例如 `TextWriter`）写在构造函数参数里，Runtime 从能力表按类型取出。
- Task 是 `sealed partial` 类，生成器会往里补 `Submit` 和 `InvokeAsync` 两类静态方法（第 5 节）。
- 业务状态（这里的 `notes`）在 `OnLoading` 里建立，在 `OnUnloading` 里释放，构造函数里不做这些。

仓库里的 `src/Test.Storage/StorageModule.cs` 和 `src/Test/TestModule.cs` 是可以对照的现成例子。

## 2. 模块包项目怎样配置

### 项目文件

第 1 节的 csproj 已经给出了关键配置，各项的含义如下：

- `PackageId` 就是模块 Id。比较 Id 时不区分大小写，显示时保持你写的样子。没有设置时默认等于程序集名。
- `Version` 就是模块版本，写成合法的 SemVer 2.0 版本号，例如 `1.2.0`。
- `Authors` 和 `Description` 会写进清单，用于显示。
- 两个 `ProjectReference` 分别引用 Runtime 和生成器。生成器要写成 `OutputItemType="Analyzer"` 且 `ReferenceOutputAssembly="false"`，因为它只在编译时运行，不属于运行时依赖。
- `src/` 目录下的项目会通过 `src/Directory.Build.props` 自动导入 `Haytham.Runtime.Generators/build/Haytham.Runtime.Generators.props`，让生成器读到 `PackageId`、`Version` 等属性。放在 `src/` 之外的模块项目需要自己导入这份 props，否则生成器读不到这些属性。

### 依赖怎样表达

要依赖另一个模块，就在 csproj 里引用它的包或项目，用普通的 `ProjectReference` 或 `PackageReference`：

```xml
<ItemGroup>
    <ProjectReference Include="..\Acme.Notes\Acme.Notes.csproj" />
</ItemGroup>
```

模块包构建时，生成器检查你引用的所有程序集，凡是带模块清单的，就把它的 Id 写进你的清单作为依赖。Runtime 因此保证被依赖的模块先加载，后卸载。开发时用项目引用、发布后用包引用，结果相同。

依赖的作用有三处：加载顺序、订阅对方事件的资格（第 4 节）、以及你能在代码里使用对方的公开类型（例如调用它的 Task）。普通工具包没有模块主类，不进入加载顺序。

引用其他模块时，使用普通的引用写法。`PrivateAssets="all"` 和 `ExcludeAssets="compile"` 这两种写法不支持，生成器会读不到依赖。

### 构造函数参数从能力表取

模块主类的构造函数参数，会由生成器逐个改写成 `capabilities.Require<T>()`。宿主程序负责把对象放进能力表：

```csharp
var capabilities = new RuntimeCapabilities()
    .Add<ILoggerFactory>(loggerFactory)
    .Add<TextWriter>(Console.Out);
var host = RuntimeApplication.Create(capabilities);
```

写构造函数时遵守下面几条，它们集中在这里，后面不再重复：

- 模块主类只写一个构造函数。有多个时报 HRT0007。
- 每个参数都是引用类型（类或接口），按类型从能力表取，不要写可空参数、带默认值的参数、`params`、`ref`／`out`／`in`，也不要直接用 `string` 或数字。需要配置值时，包成一个选项类再放进能力表。
- Runtime 在创建任何模块之前，先检查所有模块需要的类型是否都在能力表里。缺了就一次列出全部缺项，抛 `RuntimeConfigurationException`。
- 能力表里的对象由放进去的一方负责释放，模块只是借用，不要在 `OnUnloading` 里去释放它。
- 能力表里一定有 `ILoggerFactory`，因为 Runtime 自己也需要它。模块要写日志时，在构造函数里接收 `ILoggerFactory`，用它创建自己类别的 logger；在事件处理器和 Task 里写的日志会自动带上 Runtime 开的 scope（模块、阶段、执行身份）。
- 构造函数拿不到其他模块提供的服务，因为构造发生在启动的最早阶段，被依赖的模块还没有加载。模块之间的调用走 Task 和事件（第 4、5 节）。
- 构造函数里只保存引用，不做 I/O，也不启动任何东西。

目前生成器还没有检查“可空参数、默认值、`string`”这几类写法，它们不会在构建时报错，但按上面的规则写，以后加上检查时不会受影响。版本号格式同样还没有在构建时检查。

### 宿主项目

承载 Runtime 的项目（不是模块包）在 csproj 里加一个属性，生成器就会收集所有引用的模块，生成 `RuntimeApplication.Create`：

```xml
<PropertyGroup>
    <HaythamRuntimeApplication>true</HaythamRuntimeApplication>
</PropertyGroup>
```

可以对照 `src/Test/Test.csproj`。宿主一个进程里只能创建一个 `RuntimeHost`，再创建会抛 `RuntimeAlreadyCreatedException`。

## 3. 接线和登记

### Connect

`Connect` 在模块实例创建后立即被调用，参数 `ModuleWiring` 有两个成员：

```csharp
public void Connect(ModuleWiring wiring) {
    rootWork = wiring.RootWork;                 // 可以保存下来，以后从模块外部发布事件或提交 Task 时用
    wiring.SubscribeRegister(OnRegistering);    // 只能调用一次
}
```

`Connect` 只做接线。打开文件、连接网络、启动循环这类业务初始化放到生命周期事件里。`SubscribeRegister` 只能在 `Connect` 执行期间调用，并且只能调用一次，否则抛 `RuntimeContractException`。没有任何东西要登记的模块，也可以接一个空的注册处理器。

### OnRegistering 与 Registry

`OnRegistering` 事件的 `e.Registry` 有三个方法：

```csharp
private ValueTask OnRegistering(RuntimeEvents.OnRegistering e, EventContext context) {
    // 1. 定义本模块的事件，并指定派发方式。
    e.Registry.RegisterEvent(new EventDefinition<NoteAddedEvent>("Note added", EventDispatchMode.ParallelJoin));

    // 2. 登记 Task 实例。
    e.Registry.RegisterTask(new AddNoteTask(this));

    // 3. 订阅事件：生命周期事件、本模块的事件，或依赖模块的事件。
    e.Registry.Subscribe<RuntimeEvents.OnLoading>(OnLoading);
    e.Registry.Subscribe<StorageChangedEvent>(OnStorageChanged);
    return ValueTask.CompletedTask;
}
```

- `RegisterEvent<TEvent>`：登记事件类型和它的派发方式。名字只用于显示，不能为空白。
- `RegisterTask`：登记一个 Task 对象。Task 对象登记一次，可以执行多次。
- `Subscribe<TEvent>`：登记订阅者。同一个模块可以对同一个事件登记多个订阅者，串行派发时它们按登记顺序执行。

### 登记窗口什么时候关闭

Registry 只在本模块的 `OnRegistering` 处理器执行期间可以修改。你可以在处理器里先 `await` 一些东西，再继续登记。处理器返回以后，窗口关闭，再调用任何登记方法都会抛 `RuntimeContractException`。

需要集中说明的限制如下：

- 只能在 `OnRegistering` 处理器自己的代码里登记。在 `context.Work.Run` 启动的子工作里登记，会被拒绝。
- 不要在 `OnLoading` 之后再登记，窗口已经关了。
- Registry 不是线程安全的，不要从多个线程同时使用。
- 处理器及它的必要子工作全部成功，这个模块的登记才算提交。任何一步抛异常，整个模块的登记都作废，启动失败。没有登记任何东西是合法的。

### 合并检查会拒绝哪些情况

所有模块的 `OnRegistering` 结束后，Runtime 把登记结果合并检查一次，有问题就一次列出全部，`StartAsync` 因此抛出 `RuntimeExecutionException`，其中的 `Failures` 带有这份问题清单（`Kind` 为 `Contract`）。会被拒绝的情况有：

| 情况 | 怎样改 |
|---|---|
| 同一个 Task 类被登记了两次 | 一个 Task 类只在它所属的模块里登记一次 |
| 同一个事件类型被定义了两次 | 一个事件类型只由一个模块定义 |
| 定义的事件是生命周期事件或 `TaskEvent` | 这些是内置事件，换成自己的事件类 |
| 通过 Registry 订阅 `OnRegistering` 或 `TaskEvent` | `OnRegistering` 只能用 `SubscribeRegister` 订阅；`TaskEvent` 不开放订阅 |
| 订阅了没有被任何模块定义的事件 | 检查事件类型是否被定义，定义它的模块是否已被应用引用 |
| 订阅别的模块的事件，但没有直接或间接依赖那个模块 | 在 csproj 里引用定义事件的模块 |

## 4. 事件

### 定义事件

事件是继承 `RuntimeEvent` 的类，保存一次派发所需的数据：

```csharp
public sealed class StorageChangedEvent(string key) : RuntimeEvent {
    public string Key { get; } = key;
}
```

每次发布都应该新建一个事件对象。Runtime 按准确的 CLR 类型匹配订阅者，订阅 `RuntimeEvent` 或基类不会收到子类事件。并行派发时，多个订阅者会同时读到同一个对象，所以事件数据应当是不可变的。

### 四种派发方式

派发方式在 `RegisterEvent` 时指定，之后固定：

```csharp
new EventDefinition<StorageChangedEvent>("Storage changed", EventDispatchMode.ParallelJoin)
```

| 方式 | 执行方式 | 适合的情况 |
|---|---|---|
| `SerialFailFast` | 按模块顺序一个一个执行，前一个失败就不再执行后面的 | 后一步依赖前一步成功，例如分阶段的初始化 |
| `SerialCollect` | 一个一个执行，某个失败也继续执行其余的，最后汇总所有错误 | 每个订阅者都必须有机会执行，例如清理、征求意见 |
| `ParallelJoin` | 同时执行所有订阅者，等全部结束后汇总所有错误 | 订阅者之间互不相关的通知，是最常用的选择 |
| `ParallelFailFast` | 同时执行，第一个失败出现时发布方的 `await` 就提前抛出错误，但其余订阅者继续被追踪到结束 | 发布方想尽早知道失败，但订阅者的工作不应被打断 |

串行时的顺序是：先按模块的加载顺序（被依赖的在前），同一模块内再按登记顺序。

`ParallelFailFast` 提前返回，不表示事件已经全部处理完。发布方不要因此就释放其他订阅者还在使用的资源。

### 订阅者的写法

订阅者是一个 `RuntimeEventHandler<TEvent>`，即接收事件对象和 `EventContext`、返回 `ValueTask` 的方法：

```csharp
private async ValueTask OnStorageChanged(StorageChangedEvent e, EventContext context) {
    await context.Work.Run("refresh", async work => {
        await RefreshAsync(e.Key, work.Cancellation);
    }).Completion;
}
```

`EventContext` 提供：

- `Work`：当前执行的 `WorkScope`，用来启动子工作、发布事件、调用别的 Task（第 6 节）。
- `Cancellation`：当前执行的取消令牌。
- `ExecutionId` 和 `SubscriberModule`：执行编号和订阅者所属模块，用于诊断。

也可以直接用 lambda 订阅：`e.Registry.Subscribe<StorageChangedEvent>((changed, _) => Log(changed.Key));`

只有“已可用”的模块的订阅者会参与一次普通事件的派发。“可用”的意思是：这个模块的 `OnLoading` 已经成功完成，并且还没有开始 `OnUnloading`。例如模块 A 在自己的 `OnLoading` 里发布事件时，依赖 A 且尚未加载的模块 B 的订阅者不会收到。

### 在处理器里发布

在订阅者、Task 或 `Run` 子工作里，用手上的 `WorkScope` 发布：

```csharp
await context.Work.PublishAsync(new StorageChangedEvent("pack"));   // 订阅者里
await e.Work.PublishAsync(new StorageChangedEvent(key));            // Task 里
```

`await` 在所有参与的订阅者及它们的必要子工作结束后才返回。订阅者抛出的异常会汇总成 `RuntimeExecutionException`，从这个 `await` 抛出（`ParallelFailFast` 时可能更早）。

### 在处理器外发布

模块的持续工作或其他后台入口没有 `WorkScope`，要通过 `ModuleWorkPort` 发布一个新的根事件：

```csharp
// rootWork 来自 wiring.RootWork，或者持续工作里的 context.RootWork
await rootWork.PublishAsync(new StorageChangedEvent("pack"));
```

根事件只在 Runtime 处于 `Running` 状态时被接受。启动过程中、预退出之后、正式停止之后提交，都会抛 `RuntimeContractException`。在启动阶段的生命周期处理器里要发布事件，请用 `context.Work.PublishAsync`。

### 谁能发布

一个事件只有定义它的模块可以发布。模块 B 订阅了模块 A 的事件，只能接收，不能自己发布；发布别的模块的事件，或发布没有登记的事件，都会抛 `RuntimeContractException`。生命周期事件和 `TaskEvent` 由 Runtime 自己派发，模块不能发布。

需要从别的模块拿到业务结果时，调用对方的 Task（第 5 节）。

## 5. Task

### 写一个 Task 类

```csharp
public sealed partial class InstallTask(TextWriter log) : RuntimeTask<InstallRequest, InstallResult> {
    public override TaskExitPolicy ExitPolicy => TaskExitPolicy.Wait;

    protected override async ValueTask<InstallResult> ExecuteAsync(TaskEvent<InstallRequest, InstallResult> e) {
        e.Output.ReportLog(new TaskLogEntry($"install {e.Input.PackName} started"));
        // ……实际工作……
        return new InstallResult(e.Input.PackName, Steps: 2);
    }
}
```

写 Task 类时遵守下面这些规则，违反的会在构建时报 HRT0008 至 HRT0011（第 9 节）：

- 类必须是 `sealed partial`，并且直接或间接继承 `RuntimeTask<TInput, TResult>`。
- 类不能带泛型参数；如果嵌套在别的类里，外层类也必须是 `partial`。
- 必须覆写 `ExitPolicy`，基类要求作者明确选择（见第 7 节）。
- 需要在界面里显示更友好的名字时，可以覆写 `Name`，默认是类的简单名称。它只用于显示和诊断，不是身份。

`ExecuteAsync` 的参数 `TaskEvent<TInput, TResult>` 提供：

- `Input`：这次提交的输入。
- `Cancellation`：取消令牌，Task 应当把它传给自己的异步调用。
- `Work`：`WorkScope`，用来启动子工作、发布事件、调用别的 Task。
- `Output`：报告进度和日志的出口。
- `ExecutionId`：这次执行的编号。

Task 对象登记一次，被执行多次，有时是同时执行。所以某次执行的输入、取消和结果都放在局部变量里，不要放进 Task 对象的字段。共享资源（例如上面的 `notes`）由业务自己加锁。

### 生成的 Submit 和 InvokeAsync

标记了 `partial` 的 Task 类，生成器会补上三个静态方法（以 `AddNoteTask` 为例）：

```csharp
// 宿主程序提交一个根 Task
TaskRun<int> run = AddNoteTask.Submit(host, new AddNote("hello"), queueKey: null, output: null, cancellation: default);

// 模块的持续工作等没有 WorkScope 的地方提交一个根 Task
TaskRun<int> run2 = AddNoteTask.Submit(context.RootWork, new AddNote("hello"));

// 在已经有 WorkScope 的地方（处理器、别的 Task 里）作为子 Task 调用，直接得到结果
int count = await AddNoteTask.InvokeAsync(e.Work, new AddNote("hello"));
```

`Submit` 返回 `TaskRun<TResult>`，表示一次已经被接纳的执行；`InvokeAsync` 直接返回结果，失败时抛异常。

`Submit` 的另外三个可选参数：

- `queueKey`：队列键。相同键的根 Task 按提交顺序一个一个执行，不同键的并行，`null` 表示不排队。不同的 Task 类使用同一个键时也共享顺序。键的含义由调用方决定，Runtime 只比较字符串（区分大小写）。
- `output`：`ITaskOutputSink<TResult>`，接收状态、进度、日志和最终结果。不需要时不传。
- `cancellation`：调用方的取消令牌。

`Submit` 不会在调用线程里执行你的 `ExecuteAsync`，哪怕是第一个 `await` 之前的部分。根准入关闭时（不在 `Running` 状态），`Submit` 抛 `RuntimeContractException`，不会产生 `TaskRun`。

### 进度和日志

```csharp
e.Output.ReportLog(new TaskLogEntry("install started"));
e.Output.ReportProgress(new TaskProgress("install", Done: 1, Total: 2));
```

报告同步交给接收端，调用要快速返回。在 `Run` 子工作里用 `context.Output?.ReportProgress(...)`，子工作的报告也会汇到同一个根 Task 上。没有提供 `output` 时，报告被直接丢弃。Task 结束以后再报告也会被忽略。

### 读取 Completion 里的结果

`run.Completion` 是一个 `Task<TaskCompletion<TResult>>`。它在 Task 的业务和输出收尾全部结束后才完成，而且失败和取消也是“正常完成”，所以必须检查内容：

```csharp
static TResult GetResult<TResult>(TaskCompletion<TResult> completion) {
    if (completion.OutputFailure is { } outputFailure) {
        throw new RuntimeExecutionException("输出失败。", [outputFailure]);
    }

    return completion.Business switch {
        TaskOutcome<TResult>.Succeeded success => success.Result,
        TaskOutcome<TResult>.Failed failure => throw new RuntimeExecutionException("Task 失败。", failure.Failures),
        TaskOutcome<TResult>.Canceled => throw new OperationCanceledException(),
        _ => throw new InvalidOperationException("未知结果。")
    };
}
```

- `Business` 是业务结果：`Succeeded`（带结果）、`Failed`（带 `RuntimeFailure` 列表）或 `Canceled`。
- `OutputFailure` 是输出接收端出的故障。业务成功而输出失败时，两者同时存在，成功的结果仍然保留。
- `run.State` 在任何时候都能读到当前状态（`Submitted`、`Queued`、`Running`、`Completing`，最后是 `Succeeded`、`Failed` 或 `Canceled`）。

Task 抛出的异常就是业务失败。Runtime 不区分“业务错误”和“程序缺陷”，都会出现在 `Failed.Failures` 里，每条 `RuntimeFailure` 的 `Exception` 字段保存原始异常。需要让调用方区分错误种类时，抛自己定义的异常类型，调用方从 `Failure.Exception` 里判断。

### 取消

调用方可以在提交时传取消令牌，也可以随时调用 `run.RequestCancellation()`。Task 一侧把 `e.Cancellation` 传给自己的异步调用即可响应：

```csharp
protected override async ValueTask<int> ExecuteAsync(TaskEvent<AddNote, int> e) {
    await Task.Delay(TimeSpan.FromSeconds(1), e.Cancellation);   // 取消时抛 OperationCanceledException
    e.Cancellation.ThrowIfCancellationRequested();               // 没有可传令牌的循环里自己检查
    return 1;
}
```

取消是协作式的。Task 响应取消后，`Completion` 里的结果是 `Canceled`。如果 Task 没有响应、正常返回了结果，结果就是 `Succeeded`。排队中的 Task 被取消时，会直接离队，不执行 `ExecuteAsync`。

### 调用别的模块的 Task

先在 csproj 里引用对方的模块，再调用它生成的 `InvokeAsync`：

```csharp
private async ValueTask OnStarting(RuntimeEvents.OnStarting e, EventContext context) {
    var replaced = await SaveValueTask.InvokeAsync(context.Work, new SaveValue("pack", "demo"));
}
```

被调用的 Task 作为当前工作树的子工作执行，有自己的输入和结果，不进入根队列，也不单独受退出策略影响。被调用的模块必须已经可用，也就是它的 `OnLoading` 已经成功完成、`OnUnloading` 还没有开始，否则抛 `RuntimeContractException`。因为依赖模块总是先加载，所以在自己的 `OnLoading` 之后（例如 `OnStarting`）调用依赖模块的 Task 是安全的；在自己的 `OnLoading` 里调用自己的 Task 则会被拒绝。

## 6. 工作树

### 用 WorkScope.Run 启动子工作

Task 或事件处理器里需要并行做几件事，或者需要把一小段工作单独追踪时，用 `Run`：

```csharp
protected override async ValueTask<InstallResult> ExecuteAsync(TaskEvent<InstallRequest, InstallResult> e) {
    var steps = new List<WorkHandle>();
    for (var i = 1; i <= 2; i++) {
        var step = i;
        steps.Add(e.Work.Run($"install step {step}", async context => {
            await Task.Delay(50 * step, context.Cancellation);
            context.Output?.ReportProgress(new TaskProgress("install", step, 2));
        }));
    }

    await Task.WhenAll(steps.Select(static step => step.Completion));

    var size = e.Work.Run<long>("measure", async context => await MeasureAsync(context.Cancellation));
    return new InstallResult(e.Input.PackName, (int)await size.Completion);
}
```

`Run` 有两种形式：返回 `WorkHandle`（无结果）和返回 `WorkHandle<T>`（有结果）。回调拿到的 `WorkContext` 有 `Cancellation`、`Work`（可以继续往下启动子工作）和 `Output`。

`Run` 登记的工作属于当前这次执行。即使你忘记 `await` 它的 `Completion`，当前执行也会等它结束才算完成。所以可以放心用它启动需要追踪的后台步骤。

### 失败与取消的传播

一个子工作失败，父执行会向它还没结束的其他子工作请求取消，等它们真正结束，然后把所有失败汇总。因为失败已经记进工作树的结果，父处理器即使用 `try/catch` 接住了 `await` 的异常，这次执行最终仍然是失败。需要“失败后恢复继续”的操作，应当在那一项子工作内部自己处理异常，处理完成后正常返回：

```csharp
var handle = e.Work.Run("optional step", async context => {
    try {
        await TryFastPathAsync(context.Cancellation);
    }
    catch (IOException) {
        await UseSlowPathAsync(context.Cancellation);   // 在内部恢复，工作本身成功结束
    }
});
```

### 后台任务要经过 Runtime 登记

需要在后台运行的代码，请通过 `Run`（短程工作）或 `ContinuousWork.Start`（长期运行，第 8 节）登记。自己用 `Task.Run`、`new Thread`、定时器回调启动后台代码时，Runtime 不知道它的存在：停止时不会等它结束，它还在使用的资源可能已经被卸载，它的错误也不会进入任何执行结果。Runtime 不会检测这种写法，所以需要作者自己遵守。

登记权限跟着“正在运行的工作”走：处理器或子工作运行期间可以登记自己的子工作，委托返回以后，这个工作自己的登记权限就关闭了。保存下来的 `WorkScope` 在委托返回后调用 `Run`，会抛 `RuntimeContractException`。

## 7. 生命周期

一个模块可以订阅六个生命周期事件。它们都只发给“自己模块”的订阅者，加载 A 时只执行 A 的 `OnLoading`，不会广播给别的模块。每个事件的 `e.TargetModule` 是当前模块的 Id。

| 事件 | 触发时机和顺序 | 该做什么 | 不该做什么 |
|---|---|---|---|
| `OnRegistering` | 启动时，依赖在前 | 登记事件、Task 和订阅（第 3 节） | 不做 I/O 和业务初始化 |
| `OnLoading` | 全部登记合并成功后，依赖在前，一次一个模块 | 打开自己拥有的资源，建立内部状态。成功后本模块才变为“可用” | 不调用自己的 Task；不启动后台循环 |
| `OnStarting` | 全部模块加载完后，依赖在前 | 经 `e.ContinuousWork.Start` 登记持续工作；可以调用依赖模块的 Task | 不做耗时很长的工作，要长期运行就交给持续工作 |
| `OnStopRequested` | 正式停止开始时，各模块依次判定 | 检查是否同意停止；不同意就调用 `e.Cancel()` | 不释放任何资源，不等待用户回答 |
| `OnStopping` | 排空 Task、停止持续工作之后，依赖在后（与启动相反） | 做依赖“运行中”状态的收尾，例如把缓冲数据写盘 | 不释放 `OnLoading` 打开的资源，那是 `OnUnloading` 的事 |
| `OnUnloading` | 最后，与加载相反的顺序 | 释放 `OnLoading` 取得的资源 | 不释放别人借给你的对象（例如能力表里的对象） |

几条具体的约定：

- **加载失败的清理**：如果某个模块的 `OnLoading` 抛了异常，这个模块不会收到 `OnUnloading`，已经成功加载的其他模块才会。所以 `OnLoading` 自己负责在失败时清理刚打开的部分资源，用 `try/catch` 或 `using` 写好。
- **只有成功的阶段才会反向清理**：`OnStopping` 只发给 `OnStarting` 成功完成的模块；`OnUnloading` 只发给 `OnLoading` 成功完成的模块。
- **失败不阻断收尾**：`OnStopRequested`、`OnStopping`、`OnUnloading` 用 `SerialCollect` 派发，某个模块失败，其他模块仍会执行，错误汇总进 `StopOutcome.Failures`。
- **启动失败**：任何一步失败，Runtime 会停止已启动的持续工作，对已经启动的模块反序执行 `OnStopping`，对已经成功加载的模块反序执行 `OnUnloading`，然后 `StartAsync` 抛 `RuntimeExecutionException`。同一个宿主不能重新启动。

在 `OnStopRequested` 里表示否决：

```csharp
private ValueTask OnStopRequested(RuntimeEvents.OnStopRequested e, EventContext context) {
    if (hasUnsavedWork) {
        e.Cancel();   // 否决这次停止；只标记，幂等，不影响其他模块的判定
    }

    return ValueTask.CompletedTask;
}
```

否决只用于“此刻不适合停止”的临时判断。不要用永久否决来表示“安装必须完成”，那种需求用 `Wait` 退出策略的 Task（见下文）。判定处理器抛异常时，这次停止以 `DecisionFailed` 结束，异常优先于否决。

### 停止判定、预退出和退出策略

宿主程序有两步式的退出流程，先是可撤销的预退出，再是正式停止：

```csharp
await host.PreStopAsync();        // 关闭新的根工作；让 CancelOnExit 的根 Task 取消
host.CancelPreStop();             // 可选：撤销预退出，回到 Running
var outcome = await host.StopAsync();   // 正式停止
```

- `PreStopAsync`：关闭根准入（之后 `Submit` 和根事件发布都被拒绝），向已经接纳的、`ExitPolicy` 为 `CancelOnExit` 的根 Task 发出取消请求，包括排队中的。`Wait` 的 Task 不受影响，继续运行。
- `StopAsync`：先对每个模块执行 `OnStopRequested` 判定。有异常返回 `DecisionFailed`，有否决返回 `Vetoed`，两种情况都恢复到停止前的状态。通过以后，等待所有尚未结束的根 Task 和根事件结束（排空），再请求持续工作停止并执行 `OnStopping`，最后 `OnUnloading`，返回 `Stopped`（清理失败则是 `CleanupFailed`，宿主进入 `Hanging`）。
- 直接从 `Running` 调用 `StopAsync`，不会应用 `CancelOnExit`，已有的 Task 会自然结束，所以长时间运行的 Task 会拖慢停止。
- 退出策略只作用于根执行。作为子 Task 被调用的 Task，即使声明了 `CancelOnExit`，也不会单独被取消。

选择 `ExitPolicy` 的参考：

| 策略 | 适合的 Task | 要求 |
|---|---|---|
| `Wait` | 中途打断会留下半成品的操作，例如安装、写存档 | 要能在合理时间内自己结束 |
| `CancelOnExit` | 可以安全放弃的操作，例如下载、扫描、预取 | 必须响应 `e.Cancellation`，取消后自己清理现场 |

Task 里不要等待用户回答。需要用户选择时，返回一个报告或抛出业务异常，由上层询问用户后重新提交。

## 8. 持续工作

### 登记

持续工作在 `OnStarting` 里登记。名字在本模块内唯一，区分大小写：

```csharp
private ValueTask OnStarting(RuntimeEvents.OnStarting e, EventContext context) {
    e.ContinuousWork.Start("heartbeat", RunHeartbeatAsync);
    return ValueTask.CompletedTask;
}

private async ValueTask RunHeartbeatAsync(ContinuousWorkContext context) {
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
    context.ReportReady();                                    // 满足启动条件了
    while (await timer.WaitForNextTickAsync(context.Stopping)) {
        // 每次心跳要做的事
    }
}
```

`OnStarting` 处理器返回以后，登记窗口关闭，再调用 `Start` 会抛 `RuntimeContractException`。重名同样抛该异常。

`ContinuousWorkContext` 提供：

- `Stopping`：停止信号。Runtime 要停止时触发它，循环里所有的等待都应当传入它。
- `RootWork`：`ModuleWorkPort`，可以发布本模块的事件或提交根 Task（见本节末尾的限制）。
- `ReportReady()`：报告本次运行已满足启动条件。

### ReportReady

启动阶段会等所有持续工作调用 `ReportReady()`（只等它，不等循环结束），全部就绪后宿主才进入 `Running`。持续工作在调用 `ReportReady()` 之前就结束或抛异常，启动失败。所以要在“连接已建立”这类条件真正满足后再调用，并且一定要调用。

### 出错和重试

持续工作应当通过异常报告故障。出错时先把自己对外提供的服务标记为不可用，清理自己的现场，然后继续抛出异常：

```csharp
private async ValueTask RunConnectionAsync(ContinuousWorkContext context) {
    try {
        await connection.OpenAsync(context.Stopping);
        gate.MarkAvailable();
        context.ReportReady();
        await connection.WatchAsync(context.Stopping);
    }
    catch {
        gate.MarkUnavailable();   // 先更新状态，再把错误继续抛出
        throw;
    }
}
```

不要只更新状态然后正常返回，这样会让故障看起来像是正常结束。因为与 `Stopping` 对应的取消异常会被当作“正常响应停止”，所以停止时不需要特殊处理。

一项持续工作失败以后：

- 只有这一项停下来。模块的 Task、事件订阅者、依赖它的其他模块都不受影响，也不会执行 `OnStopping` 或 `OnUnloading`。
- Runtime 不会自动重试。工作结束后，宿主的 `ContinuousWorkFailed` 事件会收到通知，由宿主程序决定是否重试。
- 宿主用 `host.RetryContinuousWork(new ModuleId("Acme.Notes"), "connection")` 重试。它只在宿主 `Running` 且上一次运行已经结束时被接受，会重新调用同一个委托，并给它一个新的 `ContinuousWorkContext`；模块不会重新构造，也不会重新登记。

### 依赖它的工作在服务不可用时等待

同一个模块里常有两项持续工作，一项（例如“连接”）提供服务，另一项（例如“同步”）使用这个服务。连接断开、正在重连时，推荐的做法是让同步工作异步等待“可用”信号，而不是把它停掉，因为停掉的工作不会自动恢复。信号对象是模块类里的一个私有字段，两项工作共用：

```csharp
// 写在模块里，模块类持有一个实例：private readonly ServiceGate gate = new();
internal sealed class ServiceGate {
    private readonly object gate = new();
    private TaskCompletionSource available = NewSignal();

    public Task WhenAvailableAsync(CancellationToken cancellation) {
        lock (gate) {
            return available.Task.WaitAsync(cancellation);
        }
    }

    public void MarkAvailable() {
        lock (gate) {
            available.TrySetResult();
        }
    }

    public void MarkUnavailable() {
        lock (gate) {
            if (available.Task.IsCompleted) {
                available = NewSignal();
            }
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
```

同步工作等待信号时传入自己的 `Stopping`，所以停止时它能立即退出：

```csharp
private async ValueTask RunSyncLoopAsync(ContinuousWorkContext context) {
    context.ReportReady();
    while (true) {
        await gate.WhenAvailableAsync(context.Stopping);   // 连接不可用时在这里等
        try {
            await UseServiceAsync(context.Stopping);
        }
        catch (ServiceUnavailableException) {
            // 服务在使用过程中又失效了：回到循环开头继续等
        }
    }
}
```

宿主重试连接工作、连接重新建立后，由连接工作调用 `MarkAvailable()`，同步工作的等待就自然结束。服务调用本身仍要处理“运行中再次失效”的情况，所以上面有 `catch`。

别的模块要用这个服务时，调用本模块的 Task；服务不可用时，Task 照实以异常结束，调用方据此处理。

### 持续工作里提交根工作的限制

持续工作经 `context.RootWork` 发布事件或提交 Task 时，与外部调用者一样受根准入限制。需要注意：

- 宿主进入 `Running` 之前、预退出开始之后，提交都会抛 `RuntimeContractException`。所以持续工作不要在 `ReportReady()` 之后立刻假定可以提交，要把这个异常当作“现在不能提交”处理。
- 正式停止时，Runtime 先等已有的根 Task 全部结束，之后才向持续工作发停止信号。所以持续工作里提交的 Task 如果会无限等待，会让直接调用的 `StopAsync` 一直等下去。让这类 Task 在服务不可用时尽快以业务异常结束，而不是无限等待。

## 9. 常见错误和生成器诊断

### 生成器诊断

这些都是编译错误，不是警告，因为标了特性却进不了清单的类会让模块在启动时悄悄缺席。诊断信息会随编译器语言显示简体中文或英文。

| 编号 | 含义 | 怎样改 |
|---|---|---|
| HRT0001 | 类标了 `[RuntimeModule]`，但没有实现 `IRuntimeModule` | 让类实现 `IRuntimeModule` |
| HRT0002 | 模块主类是抽象类或静态类，无法创建实例 | 去掉 `abstract` 或 `static` |
| HRT0003 | 模块主类是泛型类，或嵌套在泛型类里 | 改成非泛型类，嵌套时把外层类也改成非泛型 |
| HRT0004 | 生成的代码访问不到模块主类、它的外层类型或它的构造函数 | 主类、外层类型和构造函数都写成 `public` 或 `internal`，不要用 `private`、`protected` 或文件内可见（`file`） |
| HRT0005 | 模块包的 `Version` 不是合法的 SemVer 2.0 版本号，例如四段的 `1.0.0.1` 或空值 | 改成 `主版本.次版本.修订号`，需要时加 `-预发布标识` 和 `+构建元数据` |
| HRT0006 | 一个项目里有多个模块主类 | 一个模块包只留一个主类，其余拆成独立的模块项目 |
| HRT0007 | 模块主类声明了多个实例构造函数 | 只保留一个构造函数 |
| HRT0008 | Task 类没有声明为 `partial` | 加上 `partial` |
| HRT0009 | Task 类没有声明为 `sealed` | 加上 `sealed` |
| HRT0010 | Task 类带有泛型参数 | 改成具体类型；需要复用时用基类或辅助方法 |
| HRT0011 | 包含 Task 的外层类型没有声明为 `partial` | 把外层类型也改成 `partial` |

### 运行时的常见错误

下面这些错误来自运行时，大多是 `RuntimeContractException`（调用不符合规则）或 `RuntimeConfigurationException`（启动前检查发现的问题，会一次列出全部）：

| 现象（异常消息的大意） | 常见原因 | 怎样改 |
|---|---|---|
| Registry 的注册窗口已关闭 | 在 `OnRegistering` 之外或处理器返回后登记 | 把登记写进注册处理器自己的代码里 |
| Registry 只能由所属注册处理器修改 | 在 `Run` 子工作里登记 | 回到处理器本身登记 |
| 接线窗口已关闭，或已经接上注册处理器 | `SubscribeRegister` 调用了两次，或不在 `Connect` 里调用 | 只在 `Connect` 里调用一次 |
| 模块 X 不能发布事件 Y | 发布了别的模块的事件，或事件没有 `RegisterEvent` | 只发布自己定义并登记的事件 |
| Task 未登记、类型不匹配或所属模块不可执行 | 忘了 `RegisterTask`；被调用的模块还没加载完或已在卸载；在自己的 `OnLoading` 里调用自己的 Task | 登记 Task；调整调用时机 |
| Runtime 当前状态为 …，不接收新的根工作 | 在启动中、预退出后或停止中提交根 Task 或根事件 | 只在 `Running` 状态提交；启动期间的生命周期处理器里用 `context.Work` |
| 工作登记窗口已关闭，不能登记 … | 在处理器或工作委托返回后，用保存的 `WorkScope` 调用 `Run` | 在运行期间登记，不要保存 `WorkScope` 留作以后用 |
| 模块已登记持续工作 … | 同一个模块用了重复的持续工作名 | 换一个名字 |
| 持续工作初始登记窗口已关闭 | 在 `OnStarting` 之外或处理器返回后调用 `Start` | 在 `OnStarting` 里登记 |
| 持续工作在就绪前结束 | 没有调用 `ReportReady()` 就返回，或就绪前抛了异常 | 满足条件后调用 `ReportReady()` |
| Registry 合并失败（列出全部问题） | 见第 3 节的合并检查表 | 按表中的建议逐项修改 |
| 订阅了事件，但没有直接或间接依赖定义模块 | csproj 没有引用定义事件的模块 | 添加对该模块的引用 |
| 能力表缺少构造参数 | 宿主没有 `capabilities.Add<T>(...)` | 在宿主里补上；缺项会一次全部列出 |
| 模块 Id 重复、依赖不在列表里、依赖成环 | 模块清单有问题（手写列表时更常见） | 修正引用关系 |
| `RuntimeAlreadyCreatedException` | 一个进程里创建了第二个 `RuntimeHost` | 整个进程只创建一次 |

### 处理失败的几条通则

- Task 的业务失败不会抛给 `Submit` 的调用者，而是放在 `Completion` 里；用 `InvokeAsync` 调用子 Task 时，失败则以 `RuntimeExecutionException` 从 `await` 抛出，取消则抛 `OperationCanceledException`。
- `RuntimeExecutionException.Failures` 里是展平后的 `RuntimeFailure` 列表。`Kind` 为 `Execution` 表示你的代码抛出的异常，`Contract` 表示调用不符合规则，`Cleanup` 表示清理失败，`Output` 表示输出接收端的故障。
- 日志和错误消息里不要放口令、令牌或含敏感信息的完整请求。`RuntimeFailure` 是进程内的诊断对象，对外输出前要由上层转换。

更多规则的来由，见 [Event 框架规格](event-framework.md) 的第 2 节（工作树）、第 3 节（派发与发布权限）、第 4 节（接线与 Registry）、第 5 节（Task）、第 6 节（生命周期）和第 7 节（持续工作）。

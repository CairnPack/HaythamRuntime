# Haytham.Runtime.Native 方案

状态：暂缓（2026-11-06 用户决定）。

暂缓的原因是目前只有 HL 使用 Runtime，而 HL 通过自己的 C# 后端直接引用 Runtime 和模块包，在同一次 NativeAOT 发布里编进 HL 的原生库，对外只导出 HL 自己的 `hl_*`。所以本文的 `hrt_*` 通用 C 函数、Dart 包、npm 包、三个产品宿主、`[NativeTask]` 标记和按名字分派 Task 的部分，现在都没有调用方。本文第 2 节原本打算让 HL 复用的输出队列、Dart port 发送和 UTF-8 字符串处理，也因为没有第二个使用者，先留在 HL 里。

本文保留全文，作为以后的设计依据。出现 HL 以外、要直接使用 hc、gebinde、full 原生库的 Dart 或 JS 程序时再拿出来，到时候 HL 已有的输出队列和 Dart 发送实现就是提取共用代码的起点。

讨论中得出的几条和通用接口无关、HL 接入 Runtime 时同样要遵守的结论，已经转写到 [HL 原生库接入 Runtime](hl-native-runtime-migration.md)。

以下是暂缓前的原文。

本文是 Claude 和 Codex 的 Astra（模型 `gpt-6-astra`）讨论出来的设计。讨论时 Astra 只读了需求文档（本仓库的开发约束、Event 框架规格、生命周期草稿、模块清单文档、HL 接入提案，以及 HL 开发者指南的 C 接口、线程模型、构建和异常策略几节），没有读现有代码。Astra 先给出第一版，Claude 审查后分八轮追问，每轮一两个问题，双方都确定后由 Astra 整合成定稿。定稿交给用户后，用户对“输出失败时怎么办”做了决定，另一个 Claude 会话也代答了三个问题；Claude 把这些转告 Astra，又讨论了两轮，修改了第 2、第 4 节的相关内容。

文档分四部分：

1. 确定后的完整方案（第 1 到第 5 节）。
2. Claude 和 Astra 没有达成一致的地方（第 6 节）。
3. Claude 代用户回答过的问题，以及用户已经做的决定（第 7 节）。
4. 需要用户回答的问题（第 8 节）。

第 9 节记录讨论中撤回的设计，方便审查时了解为什么现在的方案是这个样子。

---

## 1. 项目怎么分

### 目录

```text
src/
  Haytham.Runtime/                    现有 C# 内核
  Haytham.Runtime.Generators/         现有生成器，增加 Native 宿主的生成内容
  Haytham.Runtime.Native/             C 边界实现、共享输出通道、跨语言发送
  Haytham.Runtime.Native.Hc/          hc 产品宿主
  Haytham.Runtime.Native.Gebinde/     gebinde 产品宿主
  Haytham.Runtime.Native.Full/        full 产品宿主
  Haytham.Runtime.Native.TestHost/    只用于验证的宿主
  Test/                               现有场景驱动
  Test.Modules/                       Test 和 TestHost 共用的验证模块
  Test.Storage/                       现有验证模块包

native/include/
  haytham_runtime.h                   通用 C 接口的头文件

bindings/
  dart/                               Dart 包
  npm/                                npm 包
```

这里的**宿主项目**指最后把 Runtime、模块和导出函数一起编进一个原生库的 C# 项目。

Astra 的定稿把 `Test.Modules` 和 `TestHost` 放在新的 `tests/` 目录下。Claude 改成放在 `src/` 下，因为仓库约定源码都放在 `src/`，现有的 `Test.Tests` 也在那里。这是目录位置的调整，不影响方案。

### 引用关系和产物

| 项目 | 引用 | 产物 |
|---|---|---|
| `Haytham.Runtime` | 不引用 Native，也不引用业务模块 | Runtime 托管程序集 |
| `Haytham.Runtime.Generators` | — | 编译期生成器，目标框架仍是 `netstandard2.0` |
| `Haytham.Runtime.Native` | Runtime | 托管类库，不单独发布成原生库 |
| `Native.Hc` | Native、HC 模块包 | `haytham_hc.dll`（以及对应的 `.so`、`.dylib`） |
| `Native.Gebinde` | Native、Gebinde 模块包和它的组装依赖 | `haytham_gebinde.*` |
| `Native.Full` | Native、HC、Gebinde、HC Store 到 Gebinde 的转接 | `haytham_full.*` |
| `Native.TestHost` | Native、验证模块 | 只用于验证的原生库，不是第四种公开产品 |
| Dart 包 | 头文件生成的绑定，加上所选产品和平台的原生库 | Dart API |
| npm 包 | Node-API 桥接，加上所选原生库 | JS/TS API、`.node` 桥接文件、原生库 |

其他语言的使用者仍然只能在 hc、gebinde、full 三种里选，不能自己挑模块。一个进程只加载一个带 Runtime 的原生库；npm 包里的 `.node` 文件只是调用 C 函数的桥接，里面没有第二份 .NET 运行时。

### 两个宿主属性

三个产品宿主和测试宿主的 csproj 都这样写：

```xml
<PropertyGroup>
  <HaythamRuntimeApplication>true</HaythamRuntimeApplication>
  <HaythamRuntimeNativeExports>true</HaythamRuntimeNativeExports>
  <PublishAot>true</PublishAot>
  <NativeLib>Shared</NativeLib>
  <IsAotCompatible>true</IsAotCompatible>
</PropertyGroup>
```

两个属性分别管不同的生成内容：

| 属性 | 生成器据此生成什么 |
|---|---|
| `HaythamRuntimeApplication` | 收集模块、排好顺序，生成现有的 `RuntimeApplication.Create(capabilities)`。这一项不变。 |
| `HaythamRuntimeNativeExports` | 新增。生成 `hrt_*` 导出函数、固定名字到 Task 的分派表，以及把它们接到 Native 的代码。 |

两个属性都通过现有的 `CompilerVisibleProperty` 交给生成器。分成两个属性，是因为 HL 的原生库也是承载 Runtime 的宿主、也引用 Native，但它只要自己的 `hl_*` 导出，不要 `hrt_*`。所以 HL 只声明第一个属性。

`PublishAot` 写在各个宿主的 csproj 里，不从命令行传，原因和现在的 Test 项目一样：命令行属性会传给生成器项目，而生成器的目标框架不支持 AOT。

### 三个产品宿主和测试宿主

hc、gebinde、full 三个产品宿主第一版就建，配好引用入口和发布设置。现在还没有真实的 HC、Gebinde 模块包，所以这三个宿主暂时不引用任何业务模块，也不拿验证模块充数。它们只是发布入口的骨架，不作为有业务功能的原生库发行。

完整的调用链由测试宿主验证。现在 `TestModule` 写在 Test 应用项目里，Test 和测试宿主都要用到它，所以把验证模块整理到 `Test.Modules` 类库里：Test 保留场景驱动，Test.Storage 仍是被依赖的模块包。两个宿主用同一套验证模块，不复制生命周期逻辑。

### 绑定包用的库

- Dart 包用 `ffigen` 从头文件生成底层绑定，不手写函数签名；上层包装成 Future 和事件流。
- npm 包用 `node-addon-api`，它是 Node-API 的 C++ 包装，用来处理 JS 调用和跨线程通知；上层包装成 Promise 和事件。

具体版本在实施时锁定，随验证结果一起记录。

---

## 2. HL 怎样复用

### 例子

HL 的 op 定义直接调用准确的 Task 类：

```csharp
var input = ParseInstallOp(inputJson);
var sink = new HlTaskOutputSink<InstallResult>(jobId, outputChannel);

var run = InstallTask.Submit(
    host,
    input,
    queueKey: GetInstanceQueueKey(input),
    output: sink);
```

`InstallTask.Submit` 是生成器已经为每个 Task 类生成的强类型方法。`outputChannel` 是从 Native 复用来的共享输出通道。

### 同一个原生库

HL 的后端项目引用 Native、Runtime 和需要的模块包，声明 `HaythamRuntimeApplication`、不声明 `HaythamRuntimeNativeExports`，在同一次 NativeAOT 发布里把 `hl_*`、Runtime 和模块编进一个原生库。HL 不加载预先编译好的 `haytham_full.*`。这是 HL 自己的产品构建，不代表向其他语言使用者开放自选模块。

### HL 复用哪些东西

| 类型或生成物 | HL 用不用 | 原因 |
|---|---|---|
| `NativeOutputChannel` | 用 | 进度合并、节流、队列、批量 pump、ACK 和完成事件顺序的规则两边完全相同，只维护一份 |
| `DartBatchSender` | 用 | 通过 Dart SDK 发送一个批次的实现两边相同 |
| `NativeUtf8` | 用 | HL 已经引用 Native，UTF-8 输入转换、输出字符串分配和释放的规则只维护一份；`hl_free` 调用同一个释放实现（这一条是代答，见第 6、第 7 节） |
| 固定名字到 Task 的分派表 | 不用 | HL 的 op 在定义处直接调用强类型 `Submit`，不需要再按名字查一次 |
| `NativeTaskOutputSink<TResult>` | 不用 | HL 自己把 Runtime 的输出转成产品格式，再交给共享通道 |
| 通用错误码和结果格式 | 不用 | HL 有自己的产品错误码、本地化错误键和业务异常映射 |
| Node 桥接 | 不用 | HL 只用 Dart |
| 强类型 `Submit`、`RuntimeApplication.Create` | 用 | 这是 Runtime 现有的能力，不属于 Native |
| 模块的 JSON 上下文 | 输入输出格式相同时用 | 它属于模块，不属于 Native |

这里的 **pump** 指从队列里取出事件、组成一批、发送出去的那个线程。**ACK** 指语言侧处理完一批之后发回的确认，用来限制还没处理完的批数。

`NativeOutputChannel` 不认识 HL 的 op 和产品错误码，也不持有 HL 的活动任务表。队列和“怎样发出一批”分成两个类型，是因为 Dart port、Node 的线程安全函数和普通 C 回调的发送方式不同，但队列规则相同。

上一个会话里曾建议“HL 复用 Native 的分派表、输出接收端到 C 回调的转换、错误码映射”。讨论的结论是这三样 HL 都不用，HL 实际复用的是输出队列、Dart 发送和 UTF-8 字符串处理这三部分，理由见上表。

### HL 保留的职责

HL 负责：

- op 到准确 Task 的调用，以及产品输入的转换。
- job id、活动任务记录、最新进度和 `hl_list`。
- 产品错误码和错误键。
- hot restart 时比较选项、接入新 port、重建界面。
- 安全退出的交互和产品生命周期入口。

HL 的任务记录在调用 `Submit` 之前就准备好，输出接收端直接持有这条记录，`Submit` 成功后再补上 `TaskRun`。这样任务很快产生输出时也能找到记录，不依赖“`Submit` 返回以后才会有报告”。接纳失败时，删掉这条还没有公开成 job 的记录。

共享通道提供暂停、替换发送端、把在途批数清零和恢复这几个方法。HL 决定什么时候允许重连，然后调用这些方法，不直接改通道内部的字段。这几个方法对应的是 HL 现有的 hot restart 场景。

### “C 接口由本仓库负责”和“HL 协议仍在 HL”怎样理解

开发约束里写着“C 函数和 Dart、npm 绑定包由本仓库负责”，HL 接入提案里又写着“C 接口导出、JSON 解析、错误码仍是 HL”。两句话说的是两套不同的协议：

- 本仓库负责通用的 `hrt_*`、公共头文件、Dart 包和 npm 包。
- HL 负责 `hl_*`、op、job、产品错误码、重连和 `hl_list`。

两边共享输出通道的实现，但不要求共享对外协议。HL 在 C# 里直接使用共享类型和强类型 Task 方法，不通过自己的 C 导出再绕回 Native。

---

## 3. 外部请求怎样变成 Task 调用

### 例子

对外开放的 Task 在类上写固定名字和 JSON 上下文：

```csharp
[NativeTask("gebinde.install", typeof(InstallJsonContext))]
public sealed partial class InstallTask
    : RuntimeTask<InstallInput, InstallResult>
{
    // ExecuteAsync 与 ExitPolicy
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(InstallInput), TypeInfoPropertyName = "Input")]
[JsonSerializable(typeof(InstallResult), TypeInfoPropertyName = "Result")]
public partial class InstallJsonContext : JsonSerializerContext
{
}
```

**JSON 上下文**是 System.Text.Json 源生成的入口：在一个 `JsonSerializerContext` 子类上列出类型，编译时就生成这些类型的序列化代码，运行时不用反射。

### 固定名字放在哪里

`NativeTaskAttribute` 定义在 `Haytham.Runtime` 里，只保存一个固定字符串和一个 JSON 上下文的 `Type`。它只是给生成器读的元数据，不含 C 导出、序列化执行或语言桥接代码，Runtime 内核也不用这个名字选择 Task。所以把它放在 Runtime 里，不违反“Runtime 只提供 C# 接口”。

固定名字由提供 Task 的模块直接写在类上，和实现一起确定。它和 `RuntimeTask.Name` 无关：类改名、命名空间改动、展示用的 `Name` 覆写，都不会改变 `"gebinde.install"`；改这个字符串就是改对外协议。

没有标记的 Task 照样可以给 C# 调用，只是不进入通用分派表。不另设“先声明名字、以后再绑定实现”的机制。

这样安排以后，模块包只依赖 Runtime 和 System.Text.Json，不依赖 Native。Gebinde CLI 这类只用 C# 的调用方引用模块包时，不会被带上 Native。

### 生成器在哪里生成什么

模块包的生成内容不变：模块清单和每个 Task 的强类型 `Submit`。

声明了 `HaythamRuntimeNativeExports` 的宿主，生成器从编译引用里读出带 `NativeTask` 特性的类、它的 `TInput` 和 `TResult`、JSON 上下文类型，然后在宿主里生成每个 Task 的提交代码：

```csharp
var input = JsonSerializer.Deserialize(
    utf8Json,
    InstallJsonContext.Default.Input);

var sink = new NativeTaskOutputSink<InstallResult>(
    output,
    InstallJsonContext.Default.Result);

return InstallTask.Submit(host, input!, queueKey, sink, cancellation);
```

以及按名字分派的代码：

```csharp
return name switch
{
    "gebinde.install" => SubmitInstall(...),
    "hc.prepareGame"  => SubmitPrepareGame(...),
    _ => throw UnknownNativeTask(name)
};
```

同一个宿主里两个 Task 用了同一个固定名字时，在编译时报错。

分派表只负责把外部名字换成准确的类型。Task 有没有登记、现在能不能接纳，仍按 Runtime 现有的 Registry 和准入规则判断，Native 不重复检查。

### JSON 上下文由谁写，哪些类型要公开

JSON 上下文由模块作者写在模块源码里，不由 Native 的生成器生成。原因是一个源码生成器生成的代码，同一次编译里的其他生成器看不到；如果由 Native 生成器生成 `JsonSerializerContext`，System.Text.Json 的生成器就不会再处理它。写在模块里，模块编译时 System.Text.Json 已经生成好序列化代码，宿主直接引用。

宿主的生成代码要能访问整条调用链，所以对外开放的 Task 有这些要求：

- Task 类是 `public`。
- `TInput`、`TResult` 是 `public`。
- JSON 上下文是 `public`。
- 生成的静态 `Submit` 可以公开调用。

模块主类本身不需要因此公开。

---

## 4. C 接口

### 全部入口

```c
typedef int32_t (*hrt_emit_batch_fn)(void* user, const char* batch_json);

enum {
    HRT_EMIT_ACCEPTED = 0,
    HRT_EMIT_CLOSED   = 1,
    HRT_EMIT_FAILED   = 2
};
```

批次回调的返回值：`ACCEPTED` 表示接收端已经处理或复制了这一批；`CLOSED` 表示接收端已经正常结束，Native 不再向它发送；`FAILED` 表示调用方自己无法继续接收，并且同意 Native 终止进程。接口只定义这三个值，调用方不返回其他值。

| 签名 | 同步还是异步 | 做什么 |
|---|---|---|
| `int32_t hrt_create(hrt_emit_batch_fn emit, void* user, char** out_json)` | 同步 | 建立普通 C 的输出接收端，创建唯一的 RuntimeHost。Node 桥接也用它。 |
| `int32_t hrt_create_dart(void* dart_api_dl_data, int64_t port, char** out_json)` | 同步 | 建立 Dart port 接收端，创建唯一的 RuntimeHost。 |
| `int32_t hrt_start(int64_t* call, char** out_json)` | 返回调用标识 | 在 .NET 一侧执行并等待 `StartAsync`，结果走事件。 |
| `int32_t hrt_pre_stop(int64_t* call, char** out_json)` | 返回调用标识 | 在 .NET 一侧执行并等待 `PreStopAsync`，结果走事件。 |
| `int32_t hrt_cancel_pre_stop(char** out_json)` | 同步 | 调用 `CancelPreStop()`。 |
| `int32_t hrt_stop(int64_t* call, char** out_json)` | 返回调用标识 | 在 .NET 一侧执行并等待 `StopAsync`，完整转发 `StopOutcome`。 |
| `int32_t hrt_submit(const char* task_name, const char* input_json, const char* queue_key, int64_t* id, char** out_json)` | 同步 | 转换输入，提交准确的 Task；接纳登记后立刻返回任务标识，不等业务执行。 |
| `int32_t hrt_cancel(int64_t run, char** out_json)` | 同步 | 对任务请求取消。 |
| `int32_t hrt_retry_continuous_work(const char* module, const char* work_name, int64_t* call, char** out_json)` | 同步接纳，就绪结果走事件 | 调用 `RetryContinuousWork`，之后只观察新工作是否就绪。 |
| `void hrt_ack(void)` | 同步 | 确认一批已经处理完。 |
| `void hrt_free(void* ptr)` | 同步 | 释放 Native 返回的字符串。 |

这些函数都没有 RuntimeHost 参数，因为一个进程只有一个 RuntimeHost。

异步入口返回成功，只表示这次调用已经发起，不表示状态检查已经通过或操作已经成功。方法之后正常返回还是抛错，都由这次调用的结果事件告诉调用方。Native 不排队、不合并、不重试生命周期调用；重复和并发调用由 Runtime 按现有规则拒绝。

第一版没有 `hrt_kill`。Flutter、Electron 和普通原生程序本来就能直接结束自己的进程，跨语言调用方要强制退出时直接退出即可。另外，如果由调用方传入一个终止回调，Native 不能从任意线程直接执行 Dart 或 JS；把退出请求排到语言线程后就返回，又不满足规格里“终止策略返回就表示终止没有完成”的规则。`RuntimeHost.Kill()` 和 `IRuntimeTerminationStrategy` 继续给 C# 调用方用，规格不变。

### 生命周期语义

Native 原样保留 Runtime 规格的行为：

- Create 只做创建前的检查和取得唯一宿主，不启动模块业务。
- Start 等加载、启动和持续工作 Ready；失败时保留启动错误和回滚错误。
- PreStop 关闭新的根准入，对 `CancelOnExit` 的任务请求取消，不等全部任务结束。
- CancelPreStop 只撤销还没正式提交的预退出，已经取消的任务不重启。
- 从 Running 直接 Stop 时，不自动套用 `CancelOnExit`。
- Stop 原样返回 `Stopped`、`Vetoed`、`DecisionFailed`、`CleanupFailed` 和全部 Failures。`Stopped` 可以带着非空的 Failures，也不代表所有输出都已送达。

### Create 怎样取得能力

第一版没有确定要由其他语言提供的创建选项或能力，所以 `hrt_create` 没有 `options_json` 参数。输出回调和 Dart port 只用于 Native 的输出通道，不放进模块的能力表。

宿主自己用 C# 创建的对象，通过一个可选的 `partial` 方法放进能力表。生成器在宿主里生成：

```csharp
internal static partial class NativeApplication
{
    static partial void ConfigureCapabilities(RuntimeCapabilities capabilities);

    internal static RuntimeHost CreateHost()
    {
        var capabilities = new RuntimeCapabilities();
        ConfigureCapabilities(capabilities);
        return RuntimeApplication.Create(capabilities);
    }
}
```

测试宿主写上实现：

```csharp
internal static partial class NativeApplication
{
    static partial void ConfigureCapabilities(RuntimeCapabilities capabilities)
    {
        capabilities.Add<TextWriter>(Console.Out);
        capabilities.Add<ScenarioSettings>(CreateScenarioSettings());
    }
}
```

`CreateScenarioSettings()` 是测试宿主自己的普通方法。

这里分成声明和实现两步，是因为生成器负责创建流程，而只有具体宿主知道要放哪些对象（测试宿主放 `TextWriter` 和场景设置，以后 full 可能放转接对象）。宿主没有对象要放时，可以不写实现：不带访问修饰符、返回 `void` 的 `partial` 方法没有实现时，编译器会把声明和调用一起去掉。

`hrt_create` 和 `hrt_create_dart` 都调用同一个 `CreateHost`。这个方法只组装配置和能力对象，不加载模块，不做业务初始化。对象由宿主提供、Runtime 借用，所有权规则不变。

### 输出队列的规则

`NativeOutputChannel` 按固定规则工作，不提供配置项。这些规则来自 HL 现有的事件通道：

- 同一个任务还没发出的进度只留最新一条。
- 每个任务的进度每 100 ms 最多发一次；`phase` 变化时立刻可以发，但仍受在途批数限制。
- log 每 100 ms 或攒满 256 行打包一次。
- log 积压按字节限制在 4 MB，超出就丢掉新来的行，并在下一条 log 或完成事件之前补一条 `dropped`。
- 每批最多 1,024 条事件，最多两批在途。
- 完成事件不合并，也不因为积压被丢掉。
- 同一个标识的完成事件排在它所有事件的最后；不同标识之间的顺序不作承诺。

下载 1 GB 文件、每 64 KB 报告一次进度时，约 16,000 次报告只是不断覆盖 Native 里这个任务的最新进度，不会变成 16,000 条发给 Dart 的消息。

每个事件在进入队列时就编码成 JSON，编码发生在这个任务自己的执行路径上：报告进度和 log 时、`CompleteAsync` 时。pump 只把已经编码好的事件拼成数组，不再序列化任何类型。这样编码失败时，能确定是哪个任务的问题，见下面的“输出失败时怎么办”。进度、log、生命周期结果和持续工作事件都用 Native 自己的 System.Text.Json 源生成上下文编码；只有 Task 的成功结果用模块提供的 JSON 上下文。

Task 收尾时，输出接收端停止并等延迟的报告结束，把最后的进度、log、丢弃标记和唯一的完成事件放进队列，然后 `CompleteAsync` 返回。`CompleteAsync` 不等 ACK，Runtime 随后可以完成任务、释放同键执行权。

### 三种发送方式

| 接收端 | 怎样发送和确认 |
|---|---|
| Dart | pump 调用 `Dart_PostCObject` 发送一批；主 isolate 处理完一整批后调用 `hrt_ack()`。 |
| Node | C++ 桥接通过 Node-API 线程安全函数把一批排进 JS 线程；JS 包装层同步分发完这一批后，由桥接调用 `hrt_ack()`。npm 使用者不用自己确认。 |
| 普通 C | pump 同步调用批次回调；回调返回 `ACCEPTED` 就算确认，不再另外调用 `hrt_ack()`。 |

**isolate** 是 Dart 的独立执行环境，同步的 FFI 调用会卡住调用它的 isolate。**线程安全函数**是 Node-API 提供的跨线程排队入口：原生线程不能直接调用 JS 函数，只能通过它把数据排进 JS 线程。

Node 这边，排进线程安全函数不算确认，要等 JS 分发完才确认，否则积压只是从 Dart 的消息队列换到了 Node 的队列。“分发完”不等待使用者在事件处理函数里另外启动的异步工作，否则一个很慢的 Promise 就能卡住全部输出。JS 处理时抛出的异常照常暴露，不因为要做 ACK 而吞掉。

普通 C 的调用方如果把批次复制到自己的异步队列后立刻返回，那部分积压就由它自己负责。第一版不为普通 C 另做异步确认。

Dart 用 SDK 的 port，不用 `NativeCallable.listener`，因为 HL 实测过 listener 失效后再被调用会让整个进程 abort。调用 `Dart_PostCObject` 的函数指针不标 `SuppressGCTransition`。

### 批次和事件格式

一批是一个 JSON 数组，每一项是 `[标识, 事件种类, 数据]`：

| 种类 | 意思 | 标识 |
|---|---|---|
| 1 | Task 进度 | 任务标识 |
| 2 | Task log 或丢弃数量 | 任务标识 |
| 3 | Task 最终业务结果 | 任务标识 |
| 4 | 异步生命周期调用的结果 | 调用标识 |
| 5 | 持续工作故障通知 | 固定为 0 |
| 6 | 持续工作重试是否就绪 | 重试的调用标识 |

任务和异步调用共用一个只增不减的正 `int64` 计数器，数字不复用；0 专门给不属于任何请求的宿主通知。同步的 Create、CancelPreStop、Cancel 不分配调用标识；提交被拒绝时计数器留下空号，不回收。这些标识只用来关联还在进行的执行和输出，执行结束后 Native 不保留结果记录，也没有读取或释放结果的接口。共享通道不分配标识，HL 继续用自己的 job id。

种类 1，进度。字段沿用 `TaskProgress`，可省略的字段还有 `done`、`total`、`item`：

```json
[2, 1, {"phase": "download", "bytes": 1073741824, "totalBytes": 1073741824}]
```

种类 2，log 和丢弃数量：

```json
[2, 2, {"lines": ["Download completed."]}]
[2, 2, {"dropped": 120}]
```

种类 3，Task 完成。三种结果分别是：

```json
[2, 3, {"kind": "succeeded", "result": {"path": "example/file.bin"}}]
[2, 3, {"kind": "failed", "failures": [{"kind": "execution", "message": "Download failed.", "module": "Example.Download"}]}]
[2, 3, {"kind": "canceled"}]
```

成功结果无法编码成 JSON 时（例如结果对象里有 `double.NaN`，System.Text.Json 默认会抛 `JsonException`），完成事件保留业务结果的 `kind`，省略 `result`，加上 `outputFailure`：

```json
[2, 3, {"kind": "succeeded", "outputFailure": {"kind": "output", "message": "任务结果无法编码为 JSON。"}}]
```

这条事件的意思是：业务执行成功了，但成功值没能传出来。它不把业务成功改称失败，也不用空对象或默认值冒充结果；原始成功值仍保存在 Runtime 的 `TaskCompletion.Business` 里。绑定包要同时检查 `kind` 和 `outputFailure`，不能把它当成“成功返回了一个空结果”。

完成事件平时带的是 `TaskOutcome`，只在上面这种情况下多一个 `outputFailure`。`CompleteAsync` 的顺序是：

1. 等这个任务延迟的报告结束。
2. 用模块的 JSON 上下文编码成功结果。
3. 编码失败时，用 Native 自己的固定类型构造上面这条替代完成事件。
4. 把唯一的完成事件放进队列。
5. 如果第 3 步发生过，`CompleteAsync` 在入队之后抛出这次输出故障，让 Runtime 记下 `Completion.OutputFailure`，再照常释放同键执行权；没有发生就正常返回。

这样调用方收到了明确的完成事件，Runtime 也保留了原始业务结果和输出故障；如果这个任务属于 Stop 排空的集合，输出故障还会进入 `StopOutcome.Failures`。替代事件是在同一次输出收尾里生成的，Native 观察 `Completion` 时看到这个输出故障，不再终止进程，也不发第二条完成事件。

替代事件只包含 Native 已有的整数标识、按 `TaskOutcome` 分支选的固定 `kind` 字符串、固定的错误类别和 Native 自己写的说明文字，用 Native 自己的源生成上下文编码，不经过模块的类型、转换器或异常对象。所以 `NaN`、模块转换器抛错、结果属性取值抛错这些问题，都不会让替代事件再次失败。原始异常作为 Runtime 输出故障的原因保留，不直接放进发给调用方的文字。

进度、log 和失败详情不需要这种替代：`TaskProgress`、`TaskLogEntry` 是 Runtime 定义的类型，只有字符串和可空整数；失败详情只转 `RuntimeFailure` 的 `Kind`、`Message`、`Module`，也都是字符串。它们由 Native 自己的上下文编码，不会遇到模块数据引起的失败。唯一不确定的输入是字符串里不成对的 UTF-16 代理字符（例如进度的 `Item` 是一个被截断的文件名）。Astra 预期 System.Text.Json 会把它替换成 U+FFFD，但这没有验证过；实现时用一个测试确认 .NET 10 的实际行为，如果实际会抛异常，再针对这个输入修改方案。

种类 4，生命周期结果。正常返回：

```json
[1, 4, {"operation": "start", "state": "running", "result": {}}]
[3, 4, {"operation": "stop", "state": "stopped", "result": {"kind": "stopped", "failures": []}}]
```

方法抛错（例如重复调用被 Runtime 拒绝）：

```json
[5, 4, {"operation": "stop", "state": "stopped", "error": {"kind": "contract", "message": "The runtime has already stopped."}}]
```

`result` 和 `error` 不会同时出现。Stop 的被否决、判定失败、清理失败都是正常返回的结果，放在 `result.kind` 里，不当作契约异常。`state` 是这次操作结束时的状态，事件送到时状态可能已经变了。

种类 5，持续工作故障，数据对应 `ContinuousWorkFailure`：

```json
[0, 5, {"module": "Example.Storage", "workName": "watchChanges", "failure": {"kind": "execution", "message": "The watcher stopped unexpectedly.", "module": "Example.Storage"}}]
```

Native 在 Start 之前订阅 `ContinuousWorkFailed`。第一次运行和重试后的运行发生意外故障，都用这条通知；正常停止不发。

种类 6，重试是否就绪：

```json
[8, 6, {"module": "Example.Storage", "workName": "watchChanges", "ready": true}]
```

`hrt_retry_continuous_work` 同步调用 `RetryContinuousWork`：登记失败时同步返回错误，不分配调用标识；成功时返回调用标识，表示这次重试已经接纳，还没有就绪。之后 Native 只等新工作的 `Ready`，发一条种类 6，然后解除关联，不观察它之后的结束。故障细节由种类 5 提供，种类 6 不重复携带。因为停止等原因没能就绪时，不一定另有种类 5。绑定包收到种类 6 就结束这次等待，不等种类 5，也不假设两者的先后。

| 情况 | 调用方收到的事件 |
|---|---|
| 就绪前发生故障 | 种类 6 `ready:false`；种类 5 故障详情 |
| 就绪后发生故障 | 之前已收到种类 6 `ready:true`；这时只收到种类 5 |
| 正常 Stop 时结束 | 没有持续工作的事件，整体收尾由 Stop 结果表达 |

### Submit

`Submit` 在接纳登记后立刻返回，不等待业务执行（这一条是代答，见第 7 节）。所以 `hrt_submit` 是同步函数：完成输入转换、按名字找到准确的 Task、交给 Runtime 接纳，拿到 `TaskRun` 后返回任务标识，最终结果走种类 3。被拒绝时同步返回错误，不产生任何任务事件。HL 的 `hl_start` 也可以保持同步返回 job id 的形状。

取消只是请求，任务最后是成功、失败还是取消，以种类 3 为准。

### 错误类别

通用接口只用下面几类状态，不照搬 HL 的产品错误表：

| 类别 | 意思 |
|---|---|
| 成功 | 同步调用完成，或异步调用已经发起 |
| 输入错误 | JSON 格式不对、输入不符合类型，或固定 Task 名字不存在 |
| 契约拒绝 | Runtime 拒绝了创建、提交或生命周期调用 |
| 内部故障 | 边界或执行内部出错，按下面的故障处理执行 |

Task 的成功、失败、取消和 Stop 的几种结果都放在事件数据里，不压成一个整数状态码。`RuntimeFailure` 转成 JSON 时保留 `Kind`、`Message`、`Module`；`Exception` 只转换必要的、去掉敏感信息的诊断内容，不直接序列化异常对象，也不加原类型没有的业务错误码。C# 异常不穿过 C 边界。

### 字符串和内存

沿用 HL 已经验证过的规则：

- 字符串都是 NUL 结尾的 UTF-8，JSON 用 camelCase。
- 输入字符串由调用方分配和释放，Native 只在调用期间借用；异步执行要保留时先复制。
- `out_json` 不为空时，Native 一定会设置它，成功时是结果 JSON，失败时是错误 JSON。
- 输出字符串由 `NativeMemory.Alloc` 分配，调用方用 `hrt_free` 释放刚好一次；`hrt_free(NULL)` 什么都不做。
- C 批次回调里的字符串只在回调期间有效，要保留就复制。
- Dart VM 在 `Dart_PostCObject` 返回前已经复制了消息；Node 桥接把批次复制到自己持有的数据里。
- 绑定包负责释放同步返回的字符串，应用代码不用自己处理内存。

回调和借用的对象要一直有效，直到最后一个使用它的工作结束。Stop 还在等待或处在 Hanging 时，不能提前释放仍在使用的资源。

### 线程规则

- 所有导出用 `[UnmanagedCallersOnly]` 和 Cdecl。导出方法体只调用一个 `[MethodImpl(MethodImplOptions.NoInlining)]` 的实现方法，`hrt_free` 也一样，不在导出方法体里直接调用任何 BCL 方法。这是 HL 验证过的写法，原因是导出方法体里内联进来的某些调用会让线程带着错误的 GC 模式回到调用方。
- 可能等待的 Start、PreStop、Stop 在 .NET 一侧执行，不占用 Dart 主 isolate 或 Node 事件循环。
- `hrt_cancel` 可能同步执行取消令牌上注册的回调，沿用“回调必须很快返回、不得抛异常”的约定，不为它另开线程。
- 业务报告同步交给输出接收端，不用 `System.Progress<T>`，避免报告被无序推迟到完成结果之后。

### 输出失败时怎么办

按故障影响的范围分开处理（这是用户的决定，见第 7 节）：

- **只影响一个任务的故障**：这个任务的成功结果编码失败。这个任务照样发出完成事件，内容带上 `outputFailure`，做法见上面种类 3 的说明。调用方知道这个任务出错，其他任务不受影响，错误也没有被吞掉。
- **整条输出通道失效**：Native 自己的输出基础设施出了故障，之后所有任务都送不出去。这时才终止进程。
- **普通 C 回调返回 `FAILED`**：这是调用方自己选的返回值，接口约定写明它表示调用方同意终止进程。

事件已经进入队列以后、pump 发送时的故障，由 pump 处理，不倒算成已经完成的任务的 `OutputFailure`。

| 位置或通道 | 具体触发条件 | Native 怎么做 |
|---|---|---|
| Task 成功结果 | 模块的结果里有不支持的值、模块转换器抛错、结果属性取值抛错等，导致编码失败 | 发出带 `outputFailure` 的替代完成事件，再让 Runtime 记下 `OutputFailure`；不终止进程 |
| Task 进度、log、失败详情 | 正常的字符串和整数数据 | 正常编码，不设专门的替代分支 |
| 种类 4 生命周期结果 | Native 固定结构加转换后的失败字段 | 正常编码，不设替代分支 |
| 种类 5、6 持续工作事件 | Native 固定结构、字符串和布尔值 | 正常编码，不设替代分支 |
| 上面这些 Native 固定结构，以及替代完成事件本身 | 实际发生内存分配失败，或编码实现出现意外错误，构造不出要发送的数据 | `FailFast`；不假称事件已经入队，也不再构造新的错误事件 |
| 共享队列和 pump | 入队、拼接已编码事件等操作发生意外故障，输出无法继续 | `FailFast` |
| 普通 C 回调 | 返回 `ACCEPTED` | 正常确认这一批 |
| 普通 C 回调 | 返回 `CLOSED` | 按接收端正常关闭处理，不再发送，不终止进程 |
| 普通 C 回调 | 返回 `FAILED` | `FailFast`，按接口约定这表示调用方同意终止进程 |
| Dart | `Dart_PostCObject` 返回 false | 按接收端已关闭处理，不终止进程；HL 按自己的暂停和重连规则恢复 |
| Dart | 构造发送对象所需的原生内存分配失败 | `FailFast` |
| Node | 线程安全函数入队成功 | 等 JS 同步分发完这一批后确认 |
| Node | 入队返回 `napi_closing` | 按 JS 环境正在关闭处理，不再发送，不终止进程 |
| Node | 入队返回其他错误 | 整条通道失效，`FailFast` |

`Environment.FailFast` 会立刻异常终止当前进程，不执行正常卸载，所以不能算安全退出。改成按范围分开以后，它只剩下两类触发：Native 自己控制的输出基础设施实际失败（内存分配失败、队列或 pump 的意外错误、Node 入队的意外错误），以及调用方用 `FAILED` 明确同意终止。这些情况下之后所有任务的结果都交不出去，而且没有另一条可靠的报错路径：只写日志会让调用方一直等不会来的结果；“下次调用时再报错”需要新增一种失效状态，而下次调用可能永远不会发生；等 Stop 汇总也不够，因为 Stop 只汇总本次排空集合里的输出故障，覆盖不了 pump 的故障。表里的“意外错误”不要求另加预测性检查，只是说明 Native 自己的输出基础设施真的失败时，不能吞掉错误后继续当作通道正常。

Node 桥接把 `napi_closing` 转成 `CLOSED`，其他入队失败转成 `FAILED`。缓冲区满或处理异常不能报成 `CLOSED`。

HL 遇到旧 port 正常关闭时，按 HL 现有规则：失败的这一批不重发，pump 暂停，还没取出的队列保留；重连时替换发送端、把在途批数清零。通用接口不因此增加重连、历史读取或重发。

---

## 5. 第一版的步骤和以后的事

### 实施顺序

按“先搭包含完整步骤的项目骨架，再逐步补功能”的顺序做，不用空方法或无条件成功代替还没实现的步骤：

1. **建立完整项目骨架。** 建 Native、三个产品宿主、测试宿主、公共头文件和两个绑定包。产品宿主先有发布结构，不冒充已经有业务功能。
2. **整理共用的验证模块。** 把 Test 里的验证模块移到 `Test.Modules`，Test 保留场景驱动，Test.Storage 仍是被依赖的模块包。测试宿主通过 `ConfigureCapabilities` 放入 `TextWriter` 和 `ScenarioSettings`。
3. **在测试宿主里接通完整流程。** 创建、启动、提交、排队、报告、取消、完成、预退出、撤销预退出、正式停止、持续工作故障和重试一次接通；JSON 分派、输出队列、ACK、输出收尾和错误转换也在这一步做。
4. **验证失败和收尾路径。** 以现有 13 个场景为起点，覆盖启动回滚、停止被否决、清理失败、排队中取消和输出错误。Runtime 的 Kill 继续由现有 C# 场景验证，Native 没有 Kill 导出。
5. **让 C、Dart、Node 调用真正发布出来的 NativeAOT 库。** 验证接收端处理慢、进度积压、完成事件顺序、内存释放、接收端关闭、同步导出在 GC 压力下的表现和跨线程通知。还要验证成功结果编码失败时的替代完成事件，以及字符串里不成对的 UTF-16 代理字符的实际编码行为。会触发 `FailFast` 的场景放在独立进程里验证。
6. **真实模块出现后再填产品宿主。** 加上实际的模块引用、业务 Task、JSON 类型和能力组装，跑产品路径，然后才发行有业务功能的 hc、gebinde、full。

验证测试宿主的发布命令例如：

```powershell
dotnet publish src/Haytham.Runtime.Native.TestHost -c Release -r win-x64 -warnaserror -p:TrimmerSingleWarn=false
```

第一版的完成标准是：完整的 Native 基础设施在测试宿主上，通过 C、Dart、Node 三种调用方式跑通。三个还没有模块的产品宿主不算“已经可用”。

### 平台

第一版在 win-x64 上开发和验证。linux-x64、osx-arm64 等平台，在对应系统上实际发布并跑过验证后再加入。验证记录写明 SDK、依赖版本、平台和实际覆盖的路径。（这一条是 Claude 代答的，见第 7 节。）

### HL 迁移放在哪里

本仓库验证共享通道的队列、暂停、替换发送端、在途批数清零和恢复。`hl_*` 的接线、产品协议、Flutter hot restart、`hl_list` 和安全退出的验收，放到 HL 仓库的迁移任务里。

HL 的 `hl_init`、`hl_shutdown` 现在是同步调用，而 Runtime 的 Start、Stop 要等待业务和清理，所以 HL 迁移时要改成异步等待，不能在 Dart 主 isolate 上同步等。安全退出的顺序调整为：PreStop 并等结果，等全部任务完成事件处理完，发起 Stop，检查 `StopOutcome` 和输出故障，最后关闭 HL 的事件通道。Stop 被否决、判定失败或清理失败时，不能关闭通道后报告退出成功。Native 不决定哪些 HL 业务可以否决 Stop，只转发结果。

### 留到以后

以后再加：真实模块、真实的能力入口（例如下载器、目录配置）、其他平台。

没有具体需求时不加：运行时自选模块、按名字注册任意回调、完成结果的读取和历史、自动重试、重发或重启器、额外的调度配置、普通 C 的异步确认。

---

## 6. Claude 和 Astra 没有达成一致的地方

讨论中 Astra 接受了 Claude 的每一处质疑，现在没有双方坚持不同意见的地方。

定稿时曾有一处按项目规则单独列出：HL 是否复用 Native 的 `NativeUtf8`。Astra 原来让 HL 复用它；Claude 认为这只是几行代码、HL 已经有自己的实现，这正是 HL 现有代码的做法，所以按规则不算达成一致，交给用户判断。之后另一个 Claude 会话按用户的规则“上游项目的现有写法不算约束，哪边好用哪边”代答为复用（见第 7 节），Astra 也按这个结论改了方案。现在第 2 节写的是 HL 复用 `NativeUtf8`；这条仍是代答，用户确认前可以改回。

另外，讨论中有两处是 Claude 提出、Astra 接受的修改，和现有代码无关，记在这里供审查：

- Astra 第一版让生命周期和 Task 的结果保存在 Native 里，调用方用 `hrt_read_result` 读、`hrt_release_result` 释放。Claude 指出调用方不知道什么时候去读，输出通道失败时这对接口也补不上，而且调用方忘了释放会一直占内存，和规格“不增加通用输出历史”冲突。改成结果随事件送出。
- Astra 第一版把 `NativeTaskAttribute` 放在 Native，模块要引用 Native。Claude 指出这样 Gebinde CLI 这类只用 C# 的调用方也会被带上 Native。改成特性放在 Runtime，分派代码全部在宿主里生成。

---

## 7. 代用户回答过的问题和用户已经做的决定

### 用户已经做的决定

**输出失败按影响范围分开处理。** 定稿原来把“拼装、序列化消息时抛异常”也列为终止进程的情况。这类故障最容易真的发生，而且多半来自单个任务的结果（例如结果对象里有 `double.NaN`），按原来的写法，一个模块的小 bug 会让整个启动器进程结束，其他在途任务一起中断。用户决定改成：只影响一个任务的故障，用带 `outputFailure` 的完成事件告诉调用方；整条通道意外失效时才终止进程；普通 C 回调返回 `FAILED` 表示调用方同意终止进程，写进接口约定。用户原话是“按照你这样分开处理会好很多，就按照你这样处理吧”。Claude 和 Astra 按这个决定又讨论了两轮，结果见第 4 节的种类 3 和“输出失败时怎么办”。

### 代答

下表前两条是这个会话的 Claude 代答的，后三条是另一个 Claude 会话按用户的偏好规则代答的。用户都还没有逐条确认。

| 问题 | 代答的内容 | 估计用户同意的概率 | 依据 |
|---|---|---|---|
| 第一版承诺哪些平台 | 第一版在 win-x64 开发和验证；其他平台在对应系统上实际发布并验证后再加入 | 约 85% | 开发约束要求验证结论注明平台，JIT 测试不能代替实际 NativeAOT 发布验证；目前只在 win-x64 验证过共享库；NativeAOT 不能跨操作系统编译 |
| HL 哪些业务允许否决正式 Stop | 不属于 Native 方案，从问题列表里去掉；Native 只原样转发 `StopOutcome` | 约 90% | 这次任务的范围只到 Native 的项目划分和 C 接口 |
| `Submit` 是否在接纳登记后立刻返回 | 立刻返回，不等待业务执行；`hrt_submit` 按同步接纳定稿 | 约 95% | Runtime 先满足 HL 的需求，Flutter 的 FFI 调用不能被业务卡住；[Event 架构设计评审](../event-architecture-review.md)第 1799 行也说根任务不在提交者线程执行是为了不阻塞 Flutter 的 FFI 调用 |
| `ContinuousWorkFailed` 的处理函数抛错时，是否放进 `StopOutcome.Failures` | 放进去；Native 原样序列化 | 约 85% | 用户的“不吞错”规则：这类错误没有别的调用方能收到，不公开就等于吞掉 |
| HL 是否复用 Native 的 `NativeUtf8` | 复用 | 约 85% | 用户的规则“上游项目的现有写法不算约束，哪边好用哪边”；HL 已经引用 Native，字符串规则只维护一份更好 |

---

## 8. 需要用户回答的问题

目前没有需要用户回答的问题。原来的三个问题中，`FailFast` 一项用户已经决定，`Submit` 和持续工作故障通知两项已经代答，见第 7 节。

还有一个需要在实现时用测试确认的技术细节，不需要用户决定：.NET 10 的 System.Text.Json 遇到不成对的 UTF-16 代理字符时，是替换成 U+FFFD 还是抛异常（见第 4 节种类 3 的说明）。

---

## 9. 讨论中撤回的设计

下面这些在 Astra 的第一版或中间轮次里出现过，讨论后撤回，现在的方案里没有：

- `hrt_read_result`／`hrt_release_result`：调用方读取和释放结果记录。撤回理由见第 6 节。
- 模块引用 Native、在模块程序集里生成 Native 入口：撤回理由见第 6 节。
- 引用 Native 就生成 `hrt_*`：曾经为了去掉开关而采用，后来 HL 要复用输出通道、引用了 Native 却不要 `hrt_*`，所以恢复了 `HaythamRuntimeNativeExports` 属性。
- 通用 Dart 绑定每次报告直接发一条消息：下载场景会把上万条消息排进 Dart 主 isolate，改成和 HL 相同的批量加 ACK。
- `hrt_kill` 和终止回调：调用方可以直接结束自己的进程，跨语言回调又满足不了规格对终止策略的要求。
- `hrt_create` 的 `options_json`：第一版没有确定的选项，不留空参数。
- `hrt_cancel_pre_stop` 返回调用标识：规格里 `CancelPreStop()` 是同步方法，改回同步。
- 种类 6 的 `completion` 阶段和 `ready` 失败时的故障详情：故障已经由种类 5 报告，调用方只需要知道服务是否恢复。
- 拼装、序列化消息时抛异常就终止进程：用户决定按影响范围分开处理，单个任务的结果编码失败改成带 `outputFailure` 的完成事件。
- pump 拼整批时再序列化：改成每个事件在进入队列时、在它所属任务的执行路径上编码，失败才能归到具体任务。
- 进度序列化失败的处理、接收端记录“本任务首个输出故障”、`outputFailure` 里的 `stage` 字段：进度和 log 是 Runtime 定义的类型，只有字符串和整数，不会遇到模块数据引起的编码失败，这条分支走不到；只剩成功结果一种情况后，`stage` 也没有必要。
- `hrt_submit` 的异步分支：`Submit` 已经按“接纳后立刻返回”定稿。

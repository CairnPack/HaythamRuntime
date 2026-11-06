# HaythamRuntime 开发约束

本文整理独立 Runtime 所需的已有约束，供后续搭建骨架使用。需求与生命周期以[生命周期草稿](haytham-runtime-lifecycle.md)为准。本文不是已经落地的构建配置或完整公共 API 规范。

## 仓库与实现状态

- `HaythamRuntime.slnx` 已包含 Runtime 类库、`Haytham.Runtime.Generators` 模块清单生成器（netstandard2.0）、Test 和 Test.Storage 验证项目，在子进程里运行这些场景的 xUnit 测试项目 Test.Tests，以及检查生成器诊断和输出的测试项目 Haytham.Runtime.Generators.Tests，源码放在 `src/`。事件框架已经按 [Event 框架规格](event-framework.md) 实现。17 个带检查点的验证场景覆盖主线、停止、回滚、重试、Kill、清理失败、Task 报告边界和退出策略快照，`dotnet test`（`src/Test.Tests`）和 `Test all` 都会在独立进程里逐个运行它们；这些场景还没有覆盖规格里的全部规则，所以不能说生命周期契约已经全部验证。
- 初始化时使用 .NET SDK `10.0.403`，现有两个项目目标框架为 `net10.0`。主要消费者使用 .NET 10 和 NativeAOT；SDK 锁定与发布平台仍需随验证明确。
- Runtime 引用两个微软维护的 NuGet 包（MIT）：`Microsoft.Extensions.Logging.Abstractions` 10.0.14 用于日志接口，`System.Threading.RateLimiting` 10.0.14 用于按键排队（每个键一个许可数为 1、先进先出的 `ConcurrencyLimiter`）。生成器测试使用 `Microsoft.CodeAnalysis.CSharp.SourceGenerators.Testing` 1.1.4。
- 当前许可证由根目录 `LICENSE` 定义为 MIT。不将消费者仓库的许可证、构建配置或发布规则直接套入本仓库。
- 文档使用简体中文；代码标识符使用英文；代码注释及 XML API 文档可以使用简体中文或英文。

## 依赖与嵌入边界

- 一个进程只允许创建一个 RuntimeHost；启动器和 CLI 分属不同进程，各有自己的实例。框架须限制公开构造和并发创建；重复入口、初始化失败及停止后的返回细则待定，但不得创建第二个实例。这条约束已经实现：`RuntimeHost.Create` 用原子操作占住创建权，重复或并发调用抛出 `RuntimeAlreadyCreatedException`；创建前的检查失败时会放开创建权，可以修正输入后再调用。
- Runtime 不引用 Haytham Core 或 Gebinde 的业务实现。C# 程序引用 Runtime 和需要的模块包，构建时由生成器收集模块并排好顺序，不逐个 new 模块或调用 AddModule；其他语言的程序原本计划使用我们预先编译的三种原生库（hc、gebinde、full），这部分已暂缓，见本节最后一条。详见[模块清单与 NativeAOT](module-entry-aot.md)。
- 无参数 `[RuntimeModule]` 标记模块主类，主类实现 IRuntimeModule。模块的 Id、版本、作者和说明来自模块项目 csproj 的 `PackageId`、`Version`、`Authors`、`Description`，不再手写 `ModuleDescriptor`，`IRuntimeModule` 也不再有静态 `Descriptor`。未标记的类不会被当成模块，已标记却不满足条件的类报编译错误。
- 模块通过构造函数获得外部对象。外部对象统一放在能力表（暂名 `RuntimeCapabilities`）里按类型存取，生成器按构造函数参数生成取值代码；只能有一个构造函数，不猜参数，缺少时在创建任何模块之前一次列出全部缺项，不传 null。构造函数暂不接收其他模块提供的服务。DI 只在组装入口使用，业务代码里不使用服务定位器。
- Runtime 不再引用 Semver，模块版本就是 NuGet 包版本，以字符串保存，格式由生成器在构建时按 SemVer 2.0 检查。
- 一个模块包只含一个模块主类，模块包之间的 NuGet 依赖就是初始化依赖；普通工具包没有模块，不进入加载顺序。模块包构建时，生成器从编译引用中找出依赖的其他模块，把它们的 Id 记进本包的清单特性；应用构建时合并这些记录并排序。PackageReference 和 ProjectReference 因此等价，不读取 `project.assets.json`。重复 Id、缺少依赖和成环在构建时报错。Runtime 等依赖加载成功后才加载依赖方，卸载按实际成功加载的顺序反向进行。`ModuleDependency` 旧草稿删除。
- OnRegistering 和 TaskEvent 内置在 Runtime 里，提供业务功能的部分（存储、下载、安装等）做成官方模块。
- 进程唯一入口可以持有受控的创建状态；模块实例、订阅、注册结果和任务状态仍由 RuntimeHost 管理，不散落在全局静态可变表中。
- Runtime 先建立最小 Event 能力并直接登记内置 OnRegistering、TaskEvent，再自动组装模块并接好 OnRegistering 订阅；锁定模块名单后派发 OnRegistering，等待订阅者提交 Registry，校验合并后锁定 Task／Event 注册结果。两次锁定不能合并。
- TaskEvent 是 Runtime 自己注册的一种 Event，沿用模块 Event 的执行机制。每次 Task 执行有独立信道，内部短程工作保留本次归属；信道隔离不替代共享资源同步。
- OnRegistering、TaskEvent 和模块 Event 使用同一执行机制；内置事件由引导代码直接登记，避免 OnRegistering 自注册循环。Registry 的接口见 [Event 框架规格](event-framework.md)；合并必须检查冲突及覆盖内置事件，失败不进入运行。业务初始化只在实际注册的对应事件执行时发生，构造和定义登记不自动初始化。
- Runtime 负责受托管执行和资源生命周期，调用方控制应用启停与进程退出。默认接入不能擅自接管控制台退出信号。
- Runtime 不拥有借用资源的释放权。需要等待的任务结束之前，不能释放其正在使用的资源。
- Haytham.Runtime 本身只提供 C# 接口，不含给其他语言调用的代码。目前只有 Launcher 使用 Runtime：Launcher 的 C# 后端直接引用 Runtime 和模块包，在同一次 NativeAOT 发布里编进自己的原生库，op、job、事件 port 等产品协议和 C 函数都由 Launcher 实现。给其他语言直接调用的通用 C 函数、Dart 和 npm 绑定包，以及 hc、gebinde、full 三种原生库，2026-11-06 起暂缓，等出现 Launcher 以外的使用者时再做，设计见 [Haytham.Runtime.Native 方案](plans/runtime-native.md)。

## 错误与清理

- 区分已知业务失败、调用契约拒绝、用户取消和内部故障，不把它们统一变成成功、空值或默认结果。
- 内部缺陷和失效状态必须显式暴露。捕获后写日志再继续正常执行，不算完成错误处理。
- 请求须有明确完成结果；持续工作失败也必须可观察，不能只结束后台线程而假装模块仍然正常。
- 清理仍须尽力完成其他有效的清理义务，同时保留原始故障和后续清理错误。具体异常汇总 API 尚未确定。
- 取消令牌和超时是协作机制，不证明工作已经终止。不能在工作未结束时宣称资源已安全释放。
- 不因托管生命周期自动引入重启器、监督线程或重试。应用如何对严重故障作出进程级响应，由明确的集成契约负责；Runtime 不吞错，也不未经约定自行退出调用方进程。
- Stop 否决、任务取消和停止事件传播分别设计；不能仅凭相似名称复用同一语义。
- 已确认的正常停止顺序：关闭新外部输入准入，OnStopRequested 否决则恢复；未否决则等待已有短程 Task 及必要内部工作自然结束，再在不可逆 OnStopping 中结束持续工作并等待真实结束，最后按实际成功加载顺序反序卸载自有资源。Task 失败回传原调用方并解除登记，不阻断 Stop；清理失败保留错误并继续仍安全的清理，最终 Hanging 并返回失败。
- 已确认 Kill 为调用方明确选择的旁路，在运行中、Stop 等待中及 Hanging 可越过否决、等待和卸载，执行约定的强制终止策略；不自动升级，不宣称安全释放。终止宿主进程会结束其中所有工作，不能承诺可靠强杀任意托管线程。OnStopRequested 处理器自身异常策略与 Event 载入点仍待定。

## 日志与机密

- 日志是必要的诊断能力，输出提供方由消费者接入。日志应能关联 Runtime、模块、阶段与任务。
- 不记录 token、凭据、含敏感数据的完整请求，或游戏命令行。错误和事件载荷同样需要避免泄露机密。
- 日志不能替代调用结果和错误传播；不为了排错引入无界的任务历史或订阅保留。
- 日志与 DI 可以独立接入，二者的需要不自动决定采用 Generic Host。
- 使用方把 `ILoggerFactory` 放进能力表，这是必需的能力，缺少时和其他缺项一起在创建模块之前报错。不需要输出时放 `NullLoggerFactory.Instance`。Runtime 用类别 `Haytham.Runtime` 记录生命周期状态、模块步骤、Task 和持续工作的开始与结束、首次观察到的故障，通过 scope 带上 `RuntimeId`、`ModuleId`、`Phase`、`ExecutionId`、`RootExecutionId`、`TaskType`、`EventType`、`WorkName` 中适用的字段。模块要写日志时，在构造函数里接收同一个 `ILoggerFactory`。
- Information 及以上级别只记录类型名、状态、计数这类标量，不记录异常消息、Task 输入输出、事件对象和排队键。同一个故障点另有一条 Debug 级别的记录（EventId 1104），把原始异常对象交给 logger，用于开发者在本机排错。这条记录不脱敏，所以使用方的发行版不能开启 Debug 级别。发行版日志因此看不到异常消息和堆栈；要不要由 HL 统一脱敏后再记录，是待决项，见[发行版日志里的异常详情和脱敏](plans/log-redaction.md)。

## NativeAOT 与验证

- 面向 NativeAOT 设计注册和调用路径，优先静态、类型化绑定；不得未经验证依赖反射扫描或动态代码生成。
- 模块范围在构建发布时固定。模块包在构建时导出清单特性和入口类，应用构建时由生成器读取引用程序集上的清单特性并收集；协议见[模块清单与 NativeAOT](module-entry-aot.md)，生成器已经按它实现，还缺的构建期检查见本文最后一段。NativeAOT 发布后不能加载新的托管 DLL，新增模块需要重新构建发布。
- 消费者将警告作为错误。后续项目须验证裁剪与 AOT 警告，不能通过静默关闭分析器或批量抑制警告掩盖兼容性问题。
- JIT 单元测试不能替代实际 NativeAOT 发布和原生调用验证；验证结论须注明 SDK、依赖版本、平台及实际覆盖路径。
- 测试失败先检查实现和契约，不以放宽断言、任意等待、重试或跳过来保留缺陷。
- 候选组件比较应提供相同的 DI、日志和生命周期语义，再比较维护成本、性能与依赖体积。

### 怎样做 NativeAOT 检查

`src/Test/Test.csproj` 里写了 `PublishAot`，所以直接发布就是 NativeAOT。发布时把警告当作错误，再在发布出来的程序里运行全部场景：

```powershell
dotnet publish src/Test/Test.csproj -c Release -r win-x64 -warnaserror -p:TrimmerSingleWarn=false
src/Test/bin/Release/net10.0/win-x64/publish/Test.exe all
```

`-p:TrimmerSingleWarn=false` 让裁剪和 AOT 警告指到具体的代码位置，而不是每个程序集只汇总成一条。最后一行输出“通过：17，失败：0”且退出码为 0，就说明检查通过。

`PublishAot` 只能写在 Test 项目里，不能用命令行的 `-p:PublishAot=true` 传入。原因是命令行属性会传给所有被引用的项目，生成器项目的目标框架是 netstandard2.0，不支持 AOT，会报 NETSDK1207。

在 Windows 上发布需要安装 Visual Studio 的“使用 C++ 的桌面开发”工作负载，并且 `PATH` 里要有 `C:\Program Files (x86)\Microsoft Visual Studio\Installer`。原因是 Visual Studio 18 的 `vcvarsall.bat` 会直接按名字调用 `vswhere.exe`，找不到时链接一步会失败，错误信息是“'vswhere.exe' 不是内部或外部命令”。在“Developer PowerShell for VS”里运行，或者在当前终端里先把这个目录加进 `PATH`，都可以解决。

### 已经做过的验证

2026-11-03 在以下环境验证：.NET SDK 10.0.403，ILCompiler 10.0.14，Visual Studio 18 Community（MSVC 14.51），Windows 11，目标平台 win-x64。

- 按上面的命令发布 Test，没有裁剪或 AOT 警告；在发布出来的 `Test.exe` 里运行 `all`，13 个场景全部通过。
- 另外在一个临时项目里，把同样的场景代码发布成共享库（`NativeLib=Shared`），通过 `[UnmanagedCallersOnly]` 导出的函数，从外部进程的线程上同步调用，每个场景用一个独立进程，13 个场景也全部通过。这对应 HL 原生库的使用方式。这个临时项目没有放进仓库；Native 项目暂缓以后，共享库方式的常规检查由 Launcher 在自己的原生库上做。
- 没有覆盖的部分：只验证了 win-x64，没有验证 Linux、macOS 和 arm64；也没有验证从 Dart 通过 FFI 调用的情况。

## 下一步工作边界

下面三段是 2026-10-31 定下的实施顺序，到 2026-11-03 已经按这个顺序全部做完，保留在这里作为记录。目前还没做的是补齐验证场景、日志接入和生成器的构建期检查；Haytham.Runtime.Native 项目已暂缓。

按 2026-10-31 最新实施顺序，先建立 RuntimeHost 状态外壳并明确进程唯一性，再接入模块静态描述、资格标记、生成清单、自动组装、基元 Event 和注册流程。这里管理的是运行实例，不要求先创建专用线程，也不以线程结束代替生命周期完成。

第一章先写状态类型与持有自身状态的实例外壳；已有消费者草图与 [runtime-entry-notes.md](runtime-entry-notes.md) 中的模块设计继续保留。初版粗粒度状态不等于完整协议，停止判定、排空和失败状态随对应路径补齐；不通过空的 Start／Stop 方法或无条件状态赋值伪装功能已经完成。

随后跑通“引导登记内置事件 → 自动组装与接线 → 锁定模块名单 → RegisterEvent 收集 Registry → 校验合并 → 锁定注册结果”，再执行实际加载与启动，接入 TaskEvent 信道，最后完善停止与卸载。Event 组织业务执行，不仅广播状态。详细章节见[手写教程总纲](runtime-authoring-roadmap.md)、[第一章](tutorials/01-consumer-entry.md)与[第二章](tutorials/02-module-entry.md)。

失败结果、工作归属和资源责任在相关功能首次实现时同步加入，不等整套框架完成后再集中补错。例如第一次加载就处理部分初始化失败，第一次 Task 执行就保证异常不会漏掉在途登记。复杂恢复和调度可以后补，基本错误传播不能后补。

完整依赖验证使用两个模块包／可打包项目，每包一个模块，由包依赖表达初始化顺序，覆盖生成清单、跨包发现、进程唯一性、并行子任务、持续工作、Stop 否决后继续运行、反序卸载和失败清理。多宿主场景使用独立测试进程，不在同进程创建两个 RuntimeHost。骨架尽早经过 NativeAOT 验证。

构建和验证命令写在 [AGENTS.md](../AGENTS.md) 里；生命周期验证目前是 `dotnet test` 运行的 17 个场景，还没有 `npm run verify-notes` 等 Launcher 专用工具。生成器已按 2026-11-01 确定的方案改写：为模块包生成入口类和清单特性，为声明了 `HaythamRuntimeApplication` 的项目生成 `RuntimeApplication.Create`；检查模块主类是否合格（HRT0001 至 HRT0004），一个项目声明多个模块主类时报 HRT0006，主类有多个构造函数时报 HRT0007。版本格式不是 SemVer 2.0 时报 HRT0005。构造参数规则、重复 Id、缺少依赖和成环的构建期诊断还没有做，见[模块清单与 NativeAOT](module-entry-aot.md)第 7 节；其中重复 Id、缺少依赖和成环目前由 Runtime 在启动时检查。脚本桥接与全面业务迁移不因本轮文档修订而开始。


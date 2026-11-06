# 模块清单、Attribute 与 NativeAOT

更新日期：2026-11-01。本篇记录模块怎样被找到、怎样创建，以及给其他语言使用时怎样发布。内容是 2026-11-01 讨论确定的方案，第 7 节第 1 步（骨架）已经按它实现，第 2 步的构建期错误检查还没有做。和[开发约束](development.md)、[第二章教程](tutorials/02-module-entry.md)一起看；教程第二章里还按旧方案写的部分，以本篇为准。

## 1. 两种使用方式

Runtime 有两类使用者，用法不一样。

**C# 程序**：引用 Haytham.Runtime 和需要的模块包（NuGet），在自己的程序里创建 RuntimeHost。引用了哪些模块包，就用哪些模块，不需要逐个 new 模块或调用 AddModule。

**其他语言的程序**（2026-11-06 起暂缓，见本节最后一段）：例如 Flutter 的 Dart 代码、Electron 后端的 TS/JS 代码。它们安装我们发布的 Dart 包或 npm 包，包里带着预先编译好的原生库，直接在 Dart 或 TS 里启动 Runtime、提交任务、接收事件，不需要写 C#。

原生库按产品分三种，每种按平台（例如 win-x64、linux-x64、osx-arm64）各编译一份：

| 原生库 | 包含的内容 |
|---|---|
| hc | Runtime 和 HC（Java 版启动核心） |
| gebinde | Runtime 和 Gebinde（整合包管理，不含 Java 版启动） |
| full | Runtime、HC、Gebinde，以及 HC Store 到 Gebinde 的转接 |

这三种原生库来自本仓库里的三个 C# 宿主项目。它们和普通 C# 程序一样引用模块包，再加上给其他语言调用的 C 函数，发布成 NativeAOT 原生库。C 函数和 Dart、npm 的绑定包由本仓库负责，放在独立的项目里（暂名 Haytham.Runtime.Native）；Haytham.Runtime 本身只提供 C# 接口，不含这部分代码。

其他语言的使用者只能在这三种里选，不能自己挑模块。原因是每个 NativeAOT 原生库都带着自己的一份 .NET 运行时，分开编译的模块之间不能直接传递 C# 对象，所以 Runtime 和模块必须在同一次发布里编译在一起。讨论过的三种自选做法都会带来过大的额外负担，所以不做：

- 每个模块单独编译成原生插件：每个插件都带一份 .NET 运行时，模块之间只能通过 C 函数和序列化数据交流。
- 放弃 NativeAOT，改为运行时加载普通 DLL：启动变慢，安装包要带几十 MB 的 .NET 运行时，还要靠反射找模块。
- 让开发者在自己机器上合并编译：开发者要装 .NET SDK 和 C++ 构建工具。

目前只有 Haytham Launcher 使用 Runtime。Launcher 属于上面的 C# 程序：它的后端项目引用 Runtime 和模块包，在同一次 NativeAOT 发布里编进自己的原生库，再由自己的 C 函数和 Dart 绑定给 Flutter 使用。所以三种原生库、Dart 包、npm 包和 Haytham.Runtime.Native 项目 2026-11-06 起暂缓，等出现 Launcher 以外的其他语言使用者时再做，设计记录见 [Haytham.Runtime.Native 方案](plans/runtime-native.md)。本文其余提到它们的地方都按暂缓理解。

## 2. 模块作者要写什么

模块主类只需要标记和接口：

```csharp
[RuntimeModule]
internal sealed class StorageModule : IRuntimeModule
{
}
```

模块的名字、版本、作者和说明写在模块项目的 csproj 里：

```xml
<PropertyGroup>
    <PackageId>Acme.Storage</PackageId>
    <Version>1.2.0</Version>
    <Authors>Acme</Authors>
    <Description>Provides storage capabilities.</Description>
</PropertyGroup>
```

对应关系如下：

- 模块 Id 就是 `PackageId`。没有打包的项目 `PackageId` 默认等于程序集名，所以同样有 Id。NuGet 的包 ID 不区分大小写，所以比较模块 Id 时也不区分大小写，显示时用 csproj 里的原样。
- 模块版本就是 `Version`，也就是 NuGet 包版本。它必须是合法的 SemVer 2.0 版本号，例如四段的 `1.0.0.1` 会在构建时报 HRT0005，这时生成器不生成这个模块的入口类和清单。
- 作者和说明来自 `Authors` 和 `Description`。

因此去掉了以前手写的 `ModuleDescriptor` 和 `ModuleDependency`，`IRuntimeModule` 也不再有静态的 `Descriptor` 属性，暂时是一个没有成员的接口，之后再加订阅事件这类成员。去掉静态抽象成员以后，`Func<IRuntimeModule>`、`List<IRuntimeModule>` 都能正常编译，以前遇到的 CS8920 限制不再存在。

Runtime 不再引用 Semver 包。比较版本和解析版本范围都由 NuGet 在还原时完成，Runtime 运行时只需要显示和记录版本号，所以版本以字符串保存。版本格式由生成器在构建时用 SemVer 2.0 的规则检查。

其他规则不变：

- 一个模块包只包含一个模块主类，出现多个时报 HRT0006。
- 模块包之间的包依赖就是模块的初始化依赖，普通工具包不含模块主类，不进入加载顺序。
- 标记了 `[RuntimeModule]` 却不满足条件的类（没有实现接口、抽象类、泛型类、生成的代码访问不到）报 HRT0001～HRT0004。

## 3. 生成器为模块包写的代码

模块包构建时，生成器在包里另外写一个公开的入口类，并在程序集上加一个清单特性。以程序集名 `Acme.Install` 为例，它依赖 `Acme.Storage`：

```csharp
[assembly: global::Haytham.Runtime.Modules.RuntimeModuleManifest(
    typeof(global::Haytham.Runtime.Generated.Exports.Acme_dInstall))]

namespace Haytham.Runtime.Generated.Exports;

public static class Acme_dInstall
{
    public static global::Haytham.Runtime.Modules.ModuleDefinition Definition { get; } = new(
        Id: "Acme.Install",
        Version: "1.0.0",
        Authors: ["Acme"],
        Description: "Installs game files.",
        Dependencies: ["Acme.Storage"],
        Create: static capabilities => new global::Acme.Install.InstallModule(
            capabilities.Require<global::Acme.Install.IDownloader>()));
}
```

上面只是示意。实际生成的入口类名加了 `M_` 前缀（例如 `M_Acme_dInstall`），点转成 `_d`、下划线转成 `_u`、其他字符转成 `_xHHHH`；入口类另有常量 `Id` 和 `Dependency0`、`Dependency1`……，下游生成器读这些常量取得 Id 和依赖，不需要执行 `Definition`。几个要点：

- 入口类和模块主类是两个类。主类是作者写的，入口类是生成器写的，两者在同一个程序集里，所以主类可以是 internal。
- 入口类的名字由程序集名按可逆的规则转换得到，例如 `Acme.Install` 转成 `Acme_dInstall`。同一个应用里不会有两个简单名称相同的程序集，所以入口类名不会重复。具体转换规则实现时定，只要求可逆。
- 外面的项目只会写到入口类的名字，不会写到模块主类的名字。所以两个模块包的主类即使命名空间加类名完全相同，也不会在使用方出现 CS0433。
- 引用了 Runtime 但没有模块主类的项目也会输出清单特性，只是不指向入口类。

**依赖从哪里来**：模块包构建时，生成器查看这个项目引用的所有程序集，凡是带清单特性并且有模块的，就把它的 Id 写进 `Dependencies`。

- NuGet 已经把当前目标框架下解析好的包（包括间接依赖的包）放进了编译引用，所以这样得到的依赖和包依赖一致。经过普通工具包间接依赖的模块也会被记下来，加载顺序仍然正确。
- PackageReference 和 ProjectReference 都会出现在编译引用里，所以开发时用项目引用、发布后用包引用，结果相同，不需要另外做映射。
- NuGet 的包依赖只按目标框架分组，不按 RID 分组，所以 RID 不影响模块之间的依赖。
- 不读取 `project.assets.json`，也不需要额外的 MSBuild 任务。
- 要求模块包之间用普通的引用方式。`PrivateAssets="all"` 和 `ExcludeAssets="compile"` 这两种写法不支持。

## 4. 使用方怎样把模块收集起来

承载 Runtime 的项目在 csproj 里声明自己的角色：

```xml
<PropertyGroup>
    <HaythamRuntimeApplication>true</HaythamRuntimeApplication>
</PropertyGroup>
<ItemGroup>
    <PackageReference Include="Haytham.Runtime" Version="1.0.0" />
    <PackageReference Include="Acme.Install" Version="1.0.0" />
</ItemGroup>
```

属性名是暂定的，通过 `CompilerVisibleProperty` 交给生成器。用这个属性而不用 `OutputType` 判断，是因为给其他语言用的原生库是 Library 类型，它同样承载 Runtime。

生成器看到这个属性后，读取所有引用程序集上的清单特性，加上本项目自己的模块（如果有），按依赖排好顺序，生成：

```csharp
internal static class RuntimeApplication
{
    internal static global::Haytham.Runtime.RuntimeHost Create(
        global::Haytham.Runtime.RuntimeCapabilities capabilities)
        => global::Haytham.Runtime.RuntimeHost.Create(
            [
                global::Haytham.Runtime.Generated.Exports.Acme_dStorage.Definition,
                global::Haytham.Runtime.Generated.Exports.Acme_dInstall.Definition,
            ],
            capabilities);
}
```

C# 程序只需要调用 `RuntimeApplication.Create(capabilities)`。没有这个属性的项目只为自己的模块写入口类，不做收集。

生成器在这一步就知道全部模块的 Id 和依赖，所以重复 Id、依赖的模块不在应用里、依赖成环，都在构建时报错。NuGet 和项目引用本身不允许循环引用，成环检查只是多一道保险。

`RuntimeHost.Create(definitions, capabilities)` 也可以直接调用，传入手写的模块列表，测试时可以这样用。它和生成的代码走同一套检查。

生成器打包在 Haytham.Runtime 包的 analyzers 目录里，直接引用 Runtime 包的项目会自动运行它。本仓库里用 ProjectReference 的项目仍按现在的方式显式引用生成器。

**不做的检查**：模块包缺少清单特性、两个模块包的主类完全同名。这两项主要防的是不熟悉规则的外部作者，模块都由我们自己提供时用处不大。

## 5. 带构造参数的模块

模块构造函数需要的外部对象（例如下载器、日志输出、数据目录配置）统一放在一张能力表里，暂名 `RuntimeCapabilities`，按类型存取。生成器按构造函数的参数逐个生成 `capabilities.Require<T>()` 调用，例如：

```csharp
new InstallModule(capabilities.Require<IDownloader>())
```

能力表里的对象从哪里来，按使用方式分：

- C# 程序直接把对象放进去，例如 `capabilities.Add<IDownloader>(downloader)`。用 DI 容器的程序从自己的容器里取出对象放进去。
- 其他语言的程序通过 C 函数传入配置值（字符串或 JSON）和回调函数，由 Haytham.Runtime.Native 包装成对应的 C# 对象，再放进能力表。

规则如下：

- 模块主类只能有一个构造函数，有多个时报编译错误，不猜用哪一个。
- 每个参数按类型从能力表取。可空参数（如 `IStorage?`）、带默认值的参数、`params`、`ref`／`out`／`in`、`string` 和数字类型的参数都报编译错误，因为它们都需要猜“缺少时传什么”或“要的是哪一个值”。配置值请包成一个选项类再传。
- 创建任何模块之前，Runtime 先检查所有模块需要的类型是否都在表里；缺了就一次列出全部缺项并停止，这时还没有创建任何模块。
- 能力表里的对象由放进去的一方负责释放，Runtime 只是借用。
- 能力表里必须有 `ILoggerFactory`，Runtime 自己也从这里取日志工厂，没有时和其他缺项一起报错。

还没定的两件事：

- **其他语言能提供哪些能力**：是 Runtime 预先定好的几种（每种有固定的 C 函数签名），还是按名字登记回调的通用方式。目前倾向预先定好几种，因为宿主传错时能在调用那一刻就报错。这只影响 Haytham.Runtime.Native 怎样往能力表里放东西，C# 这边按类型存取的方式不变。
- **构造函数能否直接拿到其他模块提供的服务**：例如 Install 的构造函数要 Storage 模块提供的 `IStorage`。暂不支持，原因是模块在启动组装时创建，那时被依赖的模块还没加载，拿到的对象还不能用；而且要支持它，还得另外设计“哪个模块提供哪个类型”。模块之间怎样互相调用，等 RegisterEvent 和 Registry 的设计确定后再定。

## 6. 内置部分和官方模块

RegisterEvent 和 TaskEvent 内置在 Runtime 里，不做成模块。判断标准是：Runtime 走完自己的启动和停止流程必须用到的东西内置，提供业务功能的东西做成模块。RegisterEvent 是模块登记 Task 和 Event 的途径，TaskEvent 是每次执行任务的通道，C 函数的“提交任务”也直接依赖它，所以两者都内置。它们和模块的 Event 使用同一套执行方式，模块看到的接口是一样的。

存储、下载、安装这类业务功能做成官方模块，和其他模块遵守同样的清单和依赖规则。HC、Gebinde 内部怎样划分成几个模块还没有讨论，等开始写这两个库的模块时再定；无论怎样划分，对外发布的仍然是第 1 节的三种原生库。

## 7. 实现顺序

先把整个项目骨架搭好，再逐步补上错误检查：

1. 先搭骨架：Runtime 的基础类型（`IRuntimeModule`、`RuntimeModuleAttribute`、`ModuleDefinition`、`RuntimeModuleManifestAttribute`、`RuntimeCapabilities`、`RuntimeHost.Create`），生成器的两种输出（模块包的入口类和清单特性、应用项目的收集代码），以及 RuntimeHost 启动时的各个步骤（检查模块列表、按依赖排序、检查能力是否齐全、创建模块、接上 RegisterEvent 订阅）。暂时用不到的参数和步骤也先留好位置，让整个执行框架一次成形。
2. 再补错误检查：版本格式、构造函数参数规则、重复 Id、缺少依赖、成环、缺少能力时列出全部缺项、创建失败时的清理。其中版本格式（HRT0005）和缺少能力时列出全部缺项已经做了；重复 Id、缺少依赖和成环目前由 Runtime 在启动时检查。
3. 给其他语言用的 Haytham.Runtime.Native 和三个宿主项目已暂缓，原因见第 1 节最后一段。

不先做只覆盖一部分功能的最小版本，因为那样以后要整块一起扩展。

## 8. NativeAOT 的限制

NativeAOT 不支持运行时加载新的托管程序集或运行时生成代码，例如 Assembly.LoadFile、Reflection.Emit。[4] 所以模块范围在构建发布时就固定了，发布后往目录里放一个新的 DLL 不会被加载，新增模块需要重新构建发布。加载原生库（`NativeLibrary.Load`）不受这个限制。

反射没有被全部禁止，但大范围扫描类型、按名字创建对象需要处理裁剪问题。[4][5] 本方案用构建时生成的代码直接调用具体类型，不依赖运行时反射。

源码生成器在编译时读取代码、生成额外的 C# 文件，不在 Runtime 运行时执行。[2][3] 生成器读不到用户代码运行后的结果，所以凡是要在构建时检查的信息（Id、版本、依赖）都来自 csproj 和引用程序集上的特性，而不是来自模块代码里的属性。

Attribute 本身只是元数据，不会自动创建或执行模块。[1]

进程里只有一个 RuntimeHost，不代表模块状态可以放在随意的全局静态变量里。模块对象、订阅、任务和资源仍由这个唯一的 RuntimeHost 管理。同一个进程如果加载了两个都包含 Runtime 的原生库，每个库里都有一份独立的 Runtime，所以宿主程序只应加载一个。

JIT 下能编译运行，不代表 NativeAOT 发布后也能运行。实际发布原生库并运行，才能确认模块收集、创建、事件和任务这些路径可用。

## 官方来源

1. [Attributes and reflection — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/csharp/advanced-topics/reflection-and-attributes/)：特性元数据与读取。
2. [Source Generators — Roslyn](https://github.com/dotnet/roslyn/blob/main/docs/features/source-generators.md)：生成器读取编译输入并添加源码。
3. [Incremental Generators Cookbook — Roslyn](https://github.com/dotnet/roslyn/blob/main/docs/features/incremental-generators.cookbook.md)：增量生成器与标注发现。
4. [Native AOT deployment — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)：动态加载与代码生成限制。
5. [Prepare .NET libraries for trimming — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/prepare-libraries-for-trimming)：反射保留与可分析路径。
6. [NuGet nuspec dependencies](https://learn.microsoft.com/en-us/nuget/reference/nuspec#dependencies-element)：包依赖与版本要求。

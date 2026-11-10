# Haytham.Runtime

面向 .NET 10 的模块化运行时。应用由多个模块包组成，Runtime 负责模块的组装、按依赖顺序加载、事件派发、任务调度和退出收尾。模块清单在编译期生成，因此支持 NativeAOT 发布。

Haytham.Runtime 为 Haytham Core、Gebinde 和 Haytham Launcher 开发，但不包含它们的业务代码，可用于任意 .NET 10 应用。

## 功能

- **编译期组装**：应用引用的模块包即为加载的模块，无需手动注册。模块清单和加载顺序由包内的源码生成器在编译期生成。
- **依赖顺序**：模块包之间的 NuGet 依赖决定加载顺序，被依赖的模块先加载、后卸载。启动失败时，已启动的模块按相反顺序回滚。
- **事件**：模块定义事件，依赖它的模块订阅。定义事件时可选择订阅者串行或并行执行，以及遇错即停或汇总错误。
- **任务**：可提交、排队、取消并报告进度的业务操作。生成器为每个任务生成类型安全的 `Submit` 方法；同一队列键下的任务按提交顺序依次执行。
- **持续工作**：模块启动后持续运行的后台循环，由 Runtime 负责启动、失败重试和停止。
- **受控退出**：停止前先征询各模块，模块可以否决。确定停止后，Runtime 按各任务声明的退出策略等待或取消任务，再卸载模块。
- **日志**：基于 `Microsoft.Extensions.Logging.Abstractions`，日志自动附带模块、阶段和执行身份。

## 安装

```shell
dotnet add package Haytham.Runtime
```

模块包和应用都需直接引用此包。源码生成器已包含在包内。

## 编写模块

模块是一个类库项目，其中有且仅有一个标记 `[RuntimeModule]` 的类。项目文件中的 `PackageId`、`Version`、`Authors` 和 `Description` 即模块的 Id、版本、作者和说明。

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <PackageId>Acme.Greeting</PackageId>
        <Version>1.0.0</Version>
        <Authors>Acme</Authors>
        <Description>Says hello.</Description>
    </PropertyGroup>

    <ItemGroup>
        <PackageReference Include="Haytham.Runtime" Version="0.2.0" />
    </ItemGroup>

</Project>
```

模块在注册阶段登记其提供的任务：

```csharp
using Haytham.Runtime.Events;
using Haytham.Runtime.Modules;
using Haytham.Runtime.Tasks;

namespace Acme.Greeting;

[RuntimeModule]
internal sealed class GreetingModule : IRuntimeModule {
    public void Connect(ModuleWiring wiring) => wiring.SubscribeRegister(OnRegistering);

    private ValueTask OnRegistering(RuntimeEvents.OnRegistering e, EventContext context) {
        e.Registry.RegisterTask(new GreetTask());
        return ValueTask.CompletedTask;
    }
}

// 任务类须声明为 sealed partial，生成器会为其补充 Submit 方法。
public sealed partial class GreetTask : RuntimeTask<string, string> {
    public override TaskExitPolicy ExitPolicy => TaskExitPolicy.Wait;

    protected override ValueTask<string> ExecuteAsync(TaskEvent<string, string> e) =>
        ValueTask.FromResult($"你好，{e.Input}！");
}
```

## 运行模块

应用在项目文件中设置 `HaythamRuntimeApplication`，并引用所需的模块包：

```xml
<PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <HaythamRuntimeApplication>true</HaythamRuntimeApplication>
</PropertyGroup>

<ItemGroup>
    <PackageReference Include="Haytham.Runtime" Version="0.2.0" />
    <PackageReference Include="Acme.Greeting" Version="1.0.0" />
</ItemGroup>
```

生成器据此生成 `RuntimeApplication.Create`，其中包含全部引用的模块：

```csharp
using Haytham.Runtime;
using Haytham.Runtime.Generated;
using Haytham.Runtime.Tasks;
using Acme.Greeting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// 模块构造函数所需的对象通过能力表按类型提供，其中 ILoggerFactory 为必需项。
var capabilities = new RuntimeCapabilities().Add<ILoggerFactory>(NullLoggerFactory.Instance);
var host = RuntimeApplication.Create(capabilities);

await host.StartAsync();

var run = GreetTask.Submit(host, "世界");
var completion = await run.Completion;
if (completion.Business is TaskOutcome<string>.Succeeded success) {
    Console.WriteLine(success.Result);   // 你好，世界！
}

var stop = await host.StopAsync();
Console.WriteLine(stop.Kind);
```

每个进程只能创建一个 `RuntimeHost`，重复调用 `Create` 会抛出 `RuntimeAlreadyCreatedException`。

## 当前状态

当前为 0.x 版本，公开 API 可能变化。

- 事件框架已按规格实现，涵盖模块组装、注册、加载启动、任务排队与输出、持续工作、预退出、停止卸载和强制结束（Kill）。
- NativeAOT 发布仅在 win-x64 上验证，Linux、macOS 和 arm64 尚未验证。
- 17 个验证场景全部通过，但尚未覆盖规格中的全部规则。
- 重复的模块 Id、缺少依赖和依赖成环目前在启动时报错，尚无编译期诊断。

## 文档

- [模块开发指南](https://github.com/CairnPack/HaythamRuntime/blob/main/docs/module-authoring.md)：模块的完整写法，包括事件、任务、生命周期、持续工作和常见错误。
- [Event 框架规格](https://github.com/CairnPack/HaythamRuntime/blob/main/docs/event-framework.md)：事件、注册、任务、生命周期和持续工作的完整规则。
- [模块清单与 NativeAOT](https://github.com/CairnPack/HaythamRuntime/blob/main/docs/module-entry-aot.md)：生成器在编译期收集模块、计算加载顺序的方式。
- [开发约束](https://github.com/CairnPack/HaythamRuntime/blob/main/docs/development.md)：依赖边界、错误处理、日志、NativeAOT 检查和打包发布。

## 开发

需要 .NET SDK 10.0.403：

```shell
dotnet build HaythamRuntime.slnx
dotnet test HaythamRuntime.slnx
```

目录结构：

```text
src/Haytham.Runtime/                   Runtime
src/Haytham.Runtime.Generators/        源码生成器
src/Haytham.Runtime.Generators.Tests/  生成器测试
src/Test/、src/Test.Storage/           验证场景的应用和模块
src/Test.Tests/                        验证场景测试
docs/                                  设计与开发文档
```

## 许可证

[MIT](https://github.com/CairnPack/HaythamRuntime/blob/main/LICENSE)

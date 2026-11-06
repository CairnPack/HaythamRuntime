# HaythamRuntime

为 Haytham Core、Gebinde 及其消费者提供可嵌入的独立运行环境，统一组织模块生命周期、事件执行、受托管任务和资源收尾。

目前事件框架已经按 [Event 框架规格](docs/event-framework.md) 实现：模块组装、OnRegistering 注册、加载启动、Task 排队与输出、持续工作、预退出、停止卸载和 Kill 都能从头运行。NativeAOT 发布已经在 win-x64 上验证过，做法和覆盖范围见[开发约束](docs/development.md)。验证场景接入了 `dotnet test`，但还没有覆盖规格里的全部规则；日志接入还没有做。

## 文档

- [分层职责与生命周期需求草稿](docs/haytham-runtime-lifecycle.md)：已确认方向、生命周期概览、待细化契约和验收目标。
- [Event 框架规格](docs/event-framework.md)：事件、工作树、Registry、Task、生命周期和持续工作的接口与规则。设计过程见[架构评审](docs/event-architecture-review.md)。
- [模块开发指南](docs/module-authoring.md)：从零写一个模块包，包括项目配置、登记事件和 Task、生命周期、持续工作和常见错误。
- [开发约束](docs/development.md)：依赖边界、错误处理、日志、NativeAOT 检查做法与后续工作。

Runtime 的后续设计在本仓库维护。文档从已有讨论整理而来，已明确区分设计方向与未完成的实现，不需要访问 Launcher 私有仓库才能阅读。

## 目录

```text
HaythamRuntime.slnx
src/Haytham.Runtime/              Runtime 源码
src/Haytham.Runtime.Generators/   模块清单和 Task 代码的生成器
src/Test/                         验证场景的控制台程序
src/Test.Storage/                 Test 用到的示例模块包
src/Test.Tests/                   在子进程里逐个运行验证场景的 xUnit 测试
docs/                             需求与开发文档
```

## 环境

使用 .NET SDK `10.0.403`，目标框架是 .NET 10。构建并运行全部验证场景：

```shell
dotnet build HaythamRuntime.slnx --nologo
dotnet test HaythamRuntime.slnx --nologo
```

NativeAOT 检查的命令和环境要求见[开发约束](docs/development.md)的“怎样做 NativeAOT 检查”。验证场景还没有覆盖规格里的全部规则，全部通过也不代表每条规则都检查过。

## 许可证

[MIT](LICENSE)。

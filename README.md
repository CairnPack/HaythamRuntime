# HaythamRuntime

为 Haytham Core、Gebinde 及其消费者提供可嵌入的独立运行环境，统一组织模块生命周期、事件执行、受托管任务和资源收尾。

目前事件框架已经按 [Event 框架规格](docs/event-framework.md) 实现：模块组装、OnRegistering 注册、加载启动、Task 排队与输出、持续工作、预退出、停止卸载和 Kill 都能从头运行。日志接入、NativeAOT 验证和正式测试还没有做。

## 文档

- [分层职责与生命周期需求草稿](docs/haytham-runtime-lifecycle.md)：已确认方向、生命周期概览、待细化契约和验收目标。
- [Event 框架规格](docs/event-framework.md)：事件、工作树、Registry、Task、生命周期和持续工作的接口与规则。设计过程见[架构评审](docs/event-architecture-review.md)。
- [模块开发指南](docs/module-authoring.md)：从零写一个模块包，包括项目配置、登记事件和 Task、生命周期、持续工作和常见错误。
- [开发约束](docs/development.md)：依赖边界、错误处理、日志、NativeAOT 与下一步骨架范围。

Runtime 的后续设计在本仓库维护。文档从已有讨论整理而来，已明确区分设计方向与未完成的实现，不需要访问 Launcher 私有仓库才能阅读。

## 目录

```text
HaythamRuntime.slnx
src/Haytham.Runtime/    Runtime 项目骨架
src/Test/               测试／验证项目骨架
docs/                   需求与开发文档
```

## 环境

初始化使用 .NET SDK `10.0.403`。目前可以列出解决方案中的项目：

```shell
dotnet sln HaythamRuntime.slnx list
```

项目当前目标为 .NET 10。实际构建与测试命令、AOT 配置和检查要求待骨架设计时补充；项目可构建不代表生命周期契约已经验证。

## 许可证

[MIT](LICENSE)。

# HaythamRuntime

为 Haytham Core、Gebinde 及其消费者提供可嵌入的独立运行环境，统一组织模块生命周期、事件执行、受托管任务和资源收尾。

目前处于骨架阶段：从模块组装、RegisterEvent 注册、加载启动、Task 执行到停止卸载的主线已经能从头运行，持续工作托管、Kill、日志接入和 NativeAOT 验证还没有做，见 [Event 框架骨架](docs/event-framework.md)。

## 文档

- [分层职责与生命周期需求草稿](docs/haytham-runtime-lifecycle.md)：已确认方向、生命周期概览、待细化契约和验收目标。
- [Event 框架骨架](docs/event-framework.md)：基元 Event、RegisterEvent／Registry、两次锁定、TaskEvent 和生命周期主线的现有写法与暂定做法。
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

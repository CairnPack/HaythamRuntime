# Repository Guidelines

## 项目结构

- `src/Haytham.Runtime/`：Runtime 源码。
- `src/Test/`：当前的手动验证项目，尚未接入正式测试框架。
- `docs/`：需求与开发约束；行为或契约变化时同步更新。
- `HaythamRuntime.slnx`：解决方案入口；不要提交 `bin/`、`obj/` 等构建产物。

## 构建与开发

使用 .NET SDK `10.0.403`，目标框架为 `net10.0`：

```shell
dotnet build HaythamRuntime.slnx --nologo
dotnet test HaythamRuntime.slnx --nologo
dotnet run --project src/Test/Test.csproj
```

当前没有正式测试用例，因此 `dotnet test` 不能证明生命周期契约已验证。提交前至少确认构建无警告，并说明实际运行过的验证命令。

## 编码与文档

遵循现有 C# 风格：4 个空格缩进，启用 nullable 和 implicit usings；公共成员使用 `PascalCase`，局部变量和参数使用 `camelCase`，接口以 `I` 开头。代码标识符、注释和 XML API 文档使用英文，需求与开发说明使用简体中文。架构、错误处理、资源所有权、日志和 NativeAOT 约束以 [`docs/development.md`](docs/development.md) 为准，不在本文件重复维护。

## 提交与 Pull Request

历史仅有 `Initial commit`，尚无固定提交格式。提交主题保持简短、祈使式，必要时加范围，例如 `runtime: define lifecycle contract`。PR 应说明目的、影响范围和验证命令；行为或契约变化需同步更新相关文档，并列出未完成项和关联 issue。只有确实涉及界面或文档渲染时才附截图。

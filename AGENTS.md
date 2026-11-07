# Repository Guidelines

## 项目结构

- `src/Haytham.Runtime/`：Runtime 源码。
- `src/Haytham.Runtime.Generators/`：模块清单生成器（netstandard2.0），由 `src/Directory.Build.props` 向它传递 MSBuild 属性。
- `src/Haytham.Runtime.Generators.Tests/`：生成器测试，检查每个诊断的报错位置、关键输出和版本格式，用 Roslyn 的生成器测试库在内存里编译。
- `src/Test/`、`src/Test.Storage/`：验证场景的应用项目和它依赖的模块包。
- `src/Test.Tests/`：xUnit 测试项目，每个场景一条用例，在子进程里运行 `Test.dll <场景名>`，因为一个进程只能创建一个 RuntimeHost。新增场景时加进 `ScenarioRunner.Names`，测试会自动包含它。
- `docs/`：需求与开发约束；行为或契约变化时同步更新。
- `HaythamRuntime.slnx`：解决方案入口；不要提交 `bin/`、`obj/` 等构建产物。

## 构建与开发

使用 .NET SDK `10.0.403`，目标框架为 `net10.0`：

```shell
dotnet build HaythamRuntime.slnx --nologo
dotnet test HaythamRuntime.slnx --nologo
dotnet run --project src/Test/Test.csproj -- all
dotnet pack src/Haytham.Runtime/Haytham.Runtime.csproj -c Release -o artifacts/packages
dotnet publish src/Test/Test.csproj -c Release -r win-x64 -warnaserror -p:TrimmerSingleWarn=false
src/Test/bin/Release/net10.0/win-x64/publish/Test.exe all
```

`dotnet test` 和 `Test` 加参数 `all` 运行的是同一批验证场景；场景还没有覆盖规格里的全部规则，所以全部通过也不等于生命周期契约已经全部验证。最后两条命令是 NativeAOT 检查，环境要求见 [`docs/development.md`](docs/development.md) 的“怎样做 NativeAOT 检查”；`dotnet pack` 打出 NuGet 包，CI 和发布到 nuget.org 的做法见同一文件的“打包与发布”。提交前至少确认构建无警告，并说明实际运行过的验证命令；改动 Runtime 的公开类型、泛型用法或生成器输出时，同时做一次 NativeAOT 检查。

## 编码与文档

遵循现有 C# 风格：4 个空格缩进，启用 nullable 和 implicit usings；公共成员使用 `PascalCase`，局部变量和参数使用 `camelCase`，接口以 `I` 开头。代码标识符使用英文；代码注释和 XML API 文档可以使用简体中文或英文；需求与开发说明使用简体中文。架构、错误处理、资源所有权、日志和 NativeAOT 约束以 [`docs/development.md`](docs/development.md) 为准，不在本文件重复维护。

## 提交与 Pull Request

历史仅有 `Initial commit`，尚无固定提交格式。提交主题保持简短、祈使式，必要时加范围，例如 `runtime: define lifecycle contract`。PR 应说明目的、影响范围和验证命令；行为或契约变化需同步更新相关文档，并列出未完成项和关联 issue。只有确实涉及界面或文档渲染时才附截图。

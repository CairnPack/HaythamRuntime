namespace Haytham.Runtime.Modules;

/// <summary>
/// 一个模块包的清单条目，由生成器根据模块项目的 csproj 和模块主类生成。
/// </summary>
/// <param name="Id">模块 Id，即模块项目的 <c>PackageId</c>，比较时不区分大小写。</param>
/// <param name="Version">模块版本，即 NuGet 包版本，格式由生成器在构建时检查。</param>
/// <param name="Authors">作者，来自 <c>Authors</c>。</param>
/// <param name="Description">说明，来自 <c>Description</c>。</param>
/// <param name="Dependencies">本模块包依赖的其他模块的 Id，来自编译引用里带模块的程序集。</param>
/// <param name="RequiredCapabilities">构造函数需要从能力表取的类型，Runtime 在创建任何模块之前检查它们是否齐全。</param>
/// <param name="Create">从能力表取出构造参数并创建模块主类。</param>
public sealed record ModuleDefinition(
    string Id,
    string Version,
    IReadOnlyList<string> Authors,
    string Description,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<Type> RequiredCapabilities,
    Func<RuntimeCapabilities, IRuntimeModule> Create);

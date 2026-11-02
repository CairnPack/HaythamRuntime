namespace Haytham.Runtime.Modules;

/// <summary>
/// 生成器加在每个引用了 Runtime 的程序集上，应用项目构建时通过它收集引用程序集里的模块。
/// </summary>
/// <remarks>
/// <paramref name="entryType"/> 是生成器写的入口类，它有一个静态属性 <c>Definition</c>，类型为 <see cref="ModuleDefinition"/>。
/// 程序集里没有模块主类时，<paramref name="entryType"/> 为 <see langword="null"/>。
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class RuntimeModuleManifestAttribute(Type? entryType) : Attribute {

    public Type? EntryType { get; } = entryType;

}

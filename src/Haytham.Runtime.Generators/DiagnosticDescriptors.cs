using System.Resources;
using Microsoft.CodeAnalysis;

namespace Haytham.Runtime.Generators;

/// <summary>
/// 模块主类或具体 Task 类不合格时，生成器报告的编译错误。
/// </summary>
/// <remarks>
/// 每条规则都是错误而不是警告。
/// 因为一个标了特性却进不了清单的类，如果只给警告，构建仍会成功，模块就会在启动时悄悄缺席。
/// 这些 ID 登记在 AnalyzerReleases.Unshipped.md 里。
/// </remarks>
internal static class DiagnosticDescriptors {
    private const string Category = "Haytham.Runtime.Modules";

    // 标题和消息文字放在 Resources.resx（英文）和 Resources.zh-Hans.resx（简体中文）里。
    // 编译器按自己的界面语言选择资源：中文环境显示中文，找不到对应语言时回退到英文。
    // 资源名默认是“根命名空间.文件名”，也就是 Haytham.Runtime.Generators.Resources。
    private static readonly ResourceManager Resources = new(
        "Haytham.Runtime.Generators.Resources",
        typeof(DiagnosticDescriptors).Assembly);

    // LocalizableResourceString 不会立刻读资源，而是等编译器真正显示错误时，才按当时的语言取出文字。
    private static LocalizableString Localize(string name) =>
        new LocalizableResourceString(name, Resources, typeof(DiagnosticDescriptors));

    // 只有特性标记，不能证明这个类真的是模块，还要实现 IRuntimeModule。
    public static readonly DiagnosticDescriptor MissingModuleInterface = new(
        id: "HRT0001",
        title: Localize("HRT0001Title"),
        messageFormat: Localize("HRT0001Message"),
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // 清单要调用 new 来创建实例，而抽象类和静态类都不能 new。
    public static readonly DiagnosticDescriptor NotCreatable = new(
        id: "HRT0002",
        title: Localize("HRT0002Title"),
        messageFormat: Localize("HRT0002Message"),
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // 像 Foo<T> 这样的泛型类没有确定的具体类型，生成器不知道 T 该填什么。
    public static readonly DiagnosticDescriptor GenericModule = new(
        id: "HRT0003",
        title: Localize("HRT0003Title"),
        messageFormat: Localize("HRT0003Message"),
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // 生成的清单是同一程序集里的另一个源文件，
    // 所以主类、包含它的类型和唯一实例构造函数都必须在同一程序集内可访问。
    public static readonly DiagnosticDescriptor InaccessibleModule = new(
        id: "HRT0004",
        title: Localize("HRT0004Title"),
        messageFormat: Localize("HRT0004Message"),
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // 模块包只允许包含一个 MainClass。
    // 一个项目就是一个包，所以同一次编译里出现多个合格的 MainClass 时，全部报错。
    public static readonly DiagnosticDescriptor MultipleModules = new(
        id: "HRT0006",
        title: Localize("HRT0006Title"),
        messageFormat: Localize("HRT0006Message"),
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // 不在多个构造函数之间猜选哪一个，模块主类只能有一个实例构造函数。
    public static readonly DiagnosticDescriptor MultipleConstructors = new(
        id: "HRT0007",
        title: Localize("HRT0007Title"),
        messageFormat: Localize("HRT0007Message"),
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor TaskNotPartial = new(
        id: "HRT0008",
        title: Localize("HRT0008Title"),
        messageFormat: Localize("HRT0008Message"),
        category: "Haytham.Runtime.Tasks",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor TaskNotSealed = new(
        id: "HRT0009",
        title: Localize("HRT0009Title"),
        messageFormat: Localize("HRT0009Message"),
        category: "Haytham.Runtime.Tasks",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor GenericTask = new(
        id: "HRT0010",
        title: Localize("HRT0010Title"),
        messageFormat: Localize("HRT0010Message"),
        category: "Haytham.Runtime.Tasks",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor TaskContainingTypeNotPartial = new(
        id: "HRT0011",
        title: Localize("HRT0011Title"),
        messageFormat: Localize("HRT0011Message"),
        category: "Haytham.Runtime.Tasks",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}

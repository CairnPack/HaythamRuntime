using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Haytham.Runtime.Generators;

// 为什么要定义下面这几个类型：
// 增量生成器会缓存每一步处理的结果，下次编译（比如你又敲了一个字）时，拿新结果和旧结果比较。
// 如果两者相等，后面的步骤就直接沿用缓存，不再重新执行。
// ISymbol、SyntaxNode、Location 这些对象都属于某一次具体的编译，换一次编译就是新对象，永远比较不相等。
// 如果把它们放进处理流程，缓存就永远失效，而且旧的编译结果会一直留在内存里释放不掉。
// 所以流程里只传递下面这些小 record，record 是按字段值来比较相等的。

/// <summary>
/// 一个标注了 <c>[RuntimeModule]</c> 的类，只保留生成器需要的纯数据。
/// </summary>
/// <param name="FullyQualifiedName">生成代码里使用的完整类型名，例如 <c>global::Test.StorageModule</c>。</param>
/// <param name="DisplayName">报错时显示的类名，例如 <c>Test.StorageModule</c>。</param>
/// <param name="Location">类名在源码里的位置；合格的类也要保留，因为 HRT0006 要在每个模块上报错。</param>
/// <param name="RequiredCapabilities">构造参数对应的 typeof 表达式，按参数顺序排列。</param>
/// <param name="CreationArguments">从能力表获取构造参数的表达式，按参数顺序排列。</param>
/// <param name="Diagnostic">这个类违反的第一条规则；为 <see langword="null"/> 表示它可以进入清单。</param>
internal sealed record ModuleCandidate(
    string FullyQualifiedName,
    string DisplayName,
    LocationInfo? Location,
    string RequiredCapabilities,
    string CreationArguments,
    DiagnosticInfo? Diagnostic);

/// <summary>
/// 以可比较的数据形式保存一条诊断，等到真正报告时才转成 <see cref="Diagnostic"/>。
/// </summary>
internal sealed record DiagnosticInfo(DiagnosticDescriptor Descriptor, LocationInfo? Location, string TypeName) {
    public Diagnostic ToDiagnostic() => Diagnostic.Create(Descriptor, Location?.ToLocation(), TypeName);
}

/// <summary>
/// 源码位置 <see cref="Microsoft.CodeAnalysis.Location"/> 中重建位置所需的几项数据：文件路径、字符范围和行列范围。
/// </summary>
internal sealed record LocationInfo(string FilePath, TextSpan TextSpan, LinePositionSpan LineSpan) {
    public static LocationInfo? From(Location location) =>
        // 不在源码文件里的位置（例如来自引用程序集）没有语法树，这时就不记录位置。
        location.SourceTree is null
            ? null
            : new LocationInfo(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);

    public Location ToLocation() => Location.Create(FilePath, TextSpan, LineSpan);
}
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Haytham.Runtime.Generators;

/// <summary>
/// 检查当前编译中标注了 <c>[RuntimeModule]</c> 的模块主类，生成公开入口类和程序集清单特性。
/// 模块信息来自 MSBuild 属性，构造参数从能力表获取；入口类用常量向下游公开 Id 和依赖。
/// 应用项目还会收集引用程序集与本项目的模块，按依赖顺序生成 <c>RuntimeApplication.Create</c>。
/// </summary>
/// <remarks>
/// 生成器只读取编译符号和元数据，不执行模块代码，也不在运行时使用反射。
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class RuntimeModuleGenerator : IIncrementalGenerator {

    // 通过元数据名称识别契约，不需要让 netstandard2.0 的生成器引用 net10.0 的 Runtime。
    private const string AttributeMetadataName = "Haytham.Runtime.Modules.RuntimeModuleAttribute";
    private const string ModuleInterfaceMetadataName = "Haytham.Runtime.Modules.IRuntimeModule";
    private const string ManifestMetadataName = "Haytham.Runtime.Modules.RuntimeModuleManifestAttribute";
    private const string ExportsNamespace = "Haytham.Runtime.Generated.Exports";
    private const string DependencyPrefix = "Dependency";

    // semver.org 官方表达式：限定整串与 ASCII 数字，不接受末尾换行。
    private static readonly Regex SemVerPattern = new(
        @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*))*))?(?:\+([0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?\z");

    internal static bool IsValidVersion(string version) => SemVerPattern.IsMatch(version);

    public void Initialize(IncrementalGeneratorInitializationContext context) {
        // 使用编译器的特性索引，只分析标记过的 class 和 record class。
        var candidates = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttributeMetadataName,
            predicate: static (node, _) => node is ClassDeclarationSyntax || node.IsKind(SyntaxKind.RecordDeclaration),
            transform: static (attributeContext, cancellationToken) => Analyze(attributeContext, cancellationToken));

        context.RegisterSourceOutput(
            candidates.Where(static candidate => candidate.Diagnostic is not null),
            static (sourceContext, candidate) => sourceContext.ReportDiagnostic(candidate.Diagnostic!.ToDiagnostic()));

        var modules = candidates.Where(static candidate => candidate.Diagnostic is null).Collect();
        var properties = context.AnalyzerConfigOptionsProvider.Select(static (options, _) => ReadProperties(options.GlobalOptions));
        var input = context.CompilationProvider.Combine(properties).Combine(modules);

        context.RegisterSourceOutput(input, static (sourceContext, value) => {
            var ((compilation, buildProperties), validModules) = value;
            if (ReportMultipleModules(sourceContext, validModules)) {
                validModules = ImmutableArray<ModuleCandidate>.Empty;
            }

            // 没有引用或定义 Runtime 契约的项目，不输出任何 Runtime 代码。
            if (compilation.GetTypeByMetadataName(ManifestMetadataName) is null) {
                return;
            }

            var referencedModules = ReadReferencedModules(compilation);
            var module = validModules.Length == 1 ? validModules[0] : null;
            if (module is not null && !IsValidVersion(buildProperties.Version)) {
                sourceContext.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.InvalidVersion, Location.None, buildProperties.Id, buildProperties.Version));
                return;
            }

            var entryName = ToIdentifier(compilation.AssemblyName!);
            sourceContext.AddSource("RuntimeModuleManifest.g.cs", EmitManifest(module, entryName, buildProperties, referencedModules));

            if (buildProperties.IsApplication) {
                var applicationModules = new List<ModuleReference>(referencedModules);
                if (module is not null) {
                    applicationModules.Add(new ModuleReference(
                        buildProperties.Id,
                        $"global::{ExportsNamespace}.{entryName}",
                        referencedModules.Select(static reference => reference.Id).ToImmutableArray()));
                }

                sourceContext.AddSource("RuntimeApplication.g.cs", EmitApplication(OrderModules(applicationModules)));
            }
        });
    }

    private static BuildProperties ReadProperties(AnalyzerConfigOptions options) {
        return new BuildProperties(
            Read("PackageId"), Read("Version"), Read("Authors"), Read("Description"),
            string.Equals(Read("HaythamRuntimeApplication"), "true", StringComparison.OrdinalIgnoreCase));

        string Read(string name) => options.TryGetValue($"build_property.{name}", out var value) ? value : string.Empty;
    }

    /// <summary>
    /// 一个模块包只包含一个合格主类；不合格的类已经单独报错，不再重复计数。
    /// </summary>
    private static bool ReportMultipleModules(SourceProductionContext context, ImmutableArray<ModuleCandidate> modules) {
        if (modules.Length <= 1) {
            return false;
        }

        var ordered = modules.OrderBy(static module => module.DisplayName, StringComparer.Ordinal).ToList();
        foreach (var module in ordered) {
            var others = string.Join(", ", ordered.Where(other => other != module).Select(static other => $"'{other.DisplayName}'"));
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.MultipleModules,
                module.Location?.ToLocation(),
                module.DisplayName,
                others));
        }

        return true;
    }

    /// <summary>
    /// 只报告主类违反的第一条规则，并将构造参数转成生成代码需要的类型名。
    /// </summary>
    private static ModuleCandidate Analyze(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();

        var type = (INamedTypeSymbol)context.TargetSymbol;
        var fullyQualifiedName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var displayName = type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        var location = context.TargetNode is TypeDeclarationSyntax declaration
            ? declaration.Identifier.GetLocation()
            : context.TargetNode.GetLocation();
        var locationInfo = LocationInfo.From(location);

        // 比较符号本身，避免把同名的其他接口当成 Runtime 契约。
        var moduleInterface = context.SemanticModel.Compilation.GetTypeByMetadataName(ModuleInterfaceMetadataName);
        if (moduleInterface is null
            || !type.AllInterfaces.Contains(moduleInterface, SymbolEqualityComparer.Default)) {
            return Fail(DiagnosticDescriptors.MissingModuleInterface);
        }

        if (type.IsAbstract || type.IsStatic) {
            return Fail(DiagnosticDescriptors.NotCreatable);
        }

        // 嵌套在泛型类里的类也需要确定外层类型参数。
        if (type.IsGenericType) {
            return Fail(DiagnosticDescriptors.GenericModule);
        }

        if (!IsAccessibleFromGeneratedCode(type)) {
            return Fail(DiagnosticDescriptors.InaccessibleModule);
        }

        if (type.InstanceConstructors.Length > 1) {
            return Fail(DiagnosticDescriptors.MultipleConstructors);
        }

        // 隐式无参构造函数同样在 InstanceConstructors 中，不需要另外处理。
        var constructor = type.InstanceConstructors[0];
        if (!IsAssemblyWide(constructor.DeclaredAccessibility)) {
            return Fail(DiagnosticDescriptors.InaccessibleModule);
        }

        var parameterTypes = constructor.Parameters.Select(static parameter => parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).ToList();
        return new ModuleCandidate(
            fullyQualifiedName,
            displayName,
            locationInfo,
            string.Join(", ", parameterTypes.Select(static typeName => $"typeof({typeName})")),
            string.Join(", ", parameterTypes.Select(static typeName => $"capabilities.Require<{typeName}>()")),
            Diagnostic: null);

        ModuleCandidate Fail(DiagnosticDescriptor descriptor) => new(
            fullyQualifiedName, displayName, locationInfo, string.Empty, string.Empty,
            new DiagnosticInfo(descriptor, locationInfo, displayName));
    }

    private static bool IsAccessibleFromGeneratedCode(INamedTypeSymbol type) {
        for (var current = type; current is not null; current = current.ContainingType) {
            if (current.IsFileLocal || !IsAssemblyWide(current.DeclaredAccessibility)) {
                return false;
            }
        }

        return true;
    }

    // 同一程序集中的入口类能访问这些类型和构造函数。
    private static bool IsAssemblyWide(Accessibility accessibility) =>
        accessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal;

    /// <summary>
    /// 清单指向公开入口类；Id 和 DependencyN 是常量，不需要执行 Definition 就能读取。
    /// </summary>
    private static List<ModuleReference> ReadReferencedModules(Compilation compilation) {
        var modules = new List<ModuleReference>();
        var manifestType = compilation.GetTypeByMetadataName(ManifestMetadataName);
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols) {
            var manifest = assembly.GetAttributes().FirstOrDefault(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, manifestType));
            if (manifest is null || manifest.ConstructorArguments[0].Value is not INamedTypeSymbol entryType) {
                continue;
            }

            var fields = entryType.GetMembers().OfType<IFieldSymbol>().Where(static field => field.IsConst).ToList();
            var id = (string)fields.Single(static field => field.Name == "Id").ConstantValue!;
            var dependencies = fields
                .Where(static field => field.Name.StartsWith(DependencyPrefix, StringComparison.Ordinal))
                .Select(static field => (string)field.ConstantValue!)
                .OrderBy(static dependency => dependency, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static dependency => dependency, StringComparer.Ordinal)
                .ToImmutableArray();
            modules.Add(new ModuleReference(id, entryType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), dependencies));
        }

        return modules;
    }

    /// <summary>
    /// 每次取出依赖均已输出的一层，再按 Id 排序；缺依赖或成环时保留输入顺序。
    /// </summary>
    private static List<ModuleReference> OrderModules(List<ModuleReference> modules) {
        var pending = new List<ModuleReference>(modules);
        var ordered = new List<ModuleReference>();
        var emittedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0) {
            var layer = pending.Where(module => module.Dependencies.All(emittedIds.Contains))
                .OrderBy(static module => module.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static module => module.Id, StringComparer.Ordinal)
                .ToList();
            if (layer.Count == 0) {
                return modules;
            }

            foreach (var module in layer) {
                ordered.Add(module);
                emittedIds.Add(module.Id);
                pending.Remove(module);
            }
        }

        return ordered;
    }

    private static string EmitManifest(ModuleCandidate? module, string entryName, BuildProperties properties, List<ModuleReference> references) {
        var builder = StartSource();
        var entryType = module is null ? "null" : $"typeof(global::{ExportsNamespace}.{entryName})";
        builder.AppendLine($"[assembly: global::{ManifestMetadataName}({entryType})]");
        if (module is null) {
            return builder.ToString();
        }

        var dependencies = references.Select(static reference => reference.Id)
            .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static id => id, StringComparer.Ordinal)
            .ToList();
        var authors = properties.Authors.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(static author => author.Trim()).Where(static author => author.Length > 0);
        builder.AppendLine();
        builder.AppendLine($"namespace {ExportsNamespace};");
        builder.AppendLine();
        builder.AppendLine($"public static class {entryName} {{");
        builder.AppendLine();
        builder.AppendLine("    // 常量同时供 Definition 和下游生成器读取，不执行模块代码。");
        builder.AppendLine($"    public const string Id = {Literal(properties.Id)};");
        for (var index = 0; index < dependencies.Count; index++) {
            builder.AppendLine($"    public const string {DependencyPrefix}{index} = {Literal(dependencies[index])};");
        }
        builder.AppendLine();
        builder.AppendLine("    public static global::Haytham.Runtime.Modules.ModuleDefinition Definition { get; } = new(");
        builder.AppendLine("        Id: Id,");
        builder.AppendLine($"        Version: {Literal(properties.Version)},");
        builder.AppendLine($"        Authors: [{string.Join(", ", authors.Select(Literal))}],");
        builder.AppendLine($"        Description: {Literal(properties.Description)},");
        builder.AppendLine($"        Dependencies: [{string.Join(", ", Enumerable.Range(0, dependencies.Count).Select(static index => $"{DependencyPrefix}{index}"))}],");
        builder.AppendLine($"        RequiredCapabilities: [{module.RequiredCapabilities}],");
        builder.AppendLine($"        Create: static capabilities => new {module.FullyQualifiedName}({module.CreationArguments}));");
        builder.AppendLine();
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string EmitApplication(List<ModuleReference> modules) {
        var builder = StartSource();
        builder.AppendLine("namespace Haytham.Runtime.Generated;");
        builder.AppendLine();
        builder.AppendLine("internal static class RuntimeApplication {");
        builder.AppendLine();
        builder.AppendLine("    internal static global::Haytham.Runtime.RuntimeHost Create(global::Haytham.Runtime.RuntimeCapabilities capabilities)");
        builder.AppendLine("        => global::Haytham.Runtime.RuntimeHost.Create(");
        builder.AppendLine("            [");
        foreach (var module in modules) {
            builder.AppendLine($"                {module.EntryTypeName}.Definition,");
        }
        builder.AppendLine("            ],");
        builder.AppendLine("            capabilities);");
        builder.AppendLine();
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static StringBuilder StartSource() {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("// 由 Haytham.Runtime.Generators.RuntimeModuleGenerator 生成，请勿手动修改。");
        builder.AppendLine("#nullable enable");
        builder.AppendLine();
        return builder;
    }

    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

    // 固定前缀避免数字开头和关键字；点转成 _d，下划线转成 _u，其他字符按 UTF-16 转成 _xHHHH。
    // 每个转义都能独立解码，所以不同程序集名不会被压成同一个标识符。
    private static string ToIdentifier(string assemblyName) {
        var builder = new StringBuilder("M_");
        foreach (var character in assemblyName) {
            if (character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9') {
                builder.Append(character);
            } else if (character == '.') {
                builder.Append("_d");
            } else if (character == '_') {
                builder.Append("_u");
            } else {
                builder.Append("_x").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    private sealed record BuildProperties(string Id, string Version, string Authors, string Description, bool IsApplication);

    private sealed record ModuleReference(string Id, string EntryTypeName, ImmutableArray<string> Dependencies);

}

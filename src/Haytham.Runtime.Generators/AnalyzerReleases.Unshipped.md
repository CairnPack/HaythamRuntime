; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
HRT0001 | Haytham.Runtime.Modules | Error | RuntimeModuleGenerator: marked class does not implement IRuntimeModule
HRT0002 | Haytham.Runtime.Modules | Error | RuntimeModuleGenerator: marked class is abstract or static
HRT0003 | Haytham.Runtime.Modules | Error | RuntimeModuleGenerator: marked class is generic
HRT0004 | Haytham.Runtime.Modules | Error | RuntimeModuleGenerator: marked class is not accessible from generated code
HRT0006 | Haytham.Runtime.Modules | Error | RuntimeModuleGenerator: project declares more than one module class
HRT0007 | Haytham.Runtime.Modules | Error | RuntimeModuleGenerator: marked class declares more than one instance constructor

namespace Haytham.Runtime.Modules;

[AttributeUsage(
    AttributeTargets.Class, 
    AllowMultiple = false, 
    Inherited = false)]
public sealed class RuntimeModuleAttribute: Attribute;
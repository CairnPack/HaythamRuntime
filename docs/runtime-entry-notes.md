# HaythamRuntime的入口及其生命周期的定义



## RuntimeBuilder

此处描述`RuntimeBuilder`的具体职责边界，及其方法具体用法。  
对于已实例化的 `RuntimeBuilder`，有以下方法来添加mod注册

`AddModule<T>( T module ) where T: IRuntimeModule`

### RutimeMoudle与其接口的定义

使用`[RuntimeMoudle("id")]`来注册模块主类，并使用 Roslyn 以支持 Native AOT 关于特性其实现声明：

```csharp
[AttributeUsage(
    AttributeTargets.Class,
    AllowMultiple = false,
    Inherited = false)]
//使用Semver进行版本解析
public sealed class RuntimeModuleAttribute(string id, SemVersion version) : Attribute
{
    public string ID { get; } = id;
    public SemVersion Version { get; } = version;
    
    //TODO...
}
```

使用`IRuntimeModule`来规范主类的职责

```csharp
public interface IRuntimeModule
{
    ModuleDescriptor Descriptor { get; }
    
    //TODO...
}
```

其中`ModuleDescriptor`是一个记录模块信息的记录体，包含以下字段：

- `Map<IRuntimeModule, SemVersion> dependentions`
- `List<String> author`
- `String description`
- `SemVersion version`
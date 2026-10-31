namespace Haytham.Runtime;

/// <summary>
/// 模块构造函数需要的外部对象，按类型存取。
/// 表里的对象由放进去的一方负责释放，Runtime 只是借用。
/// </summary>
public sealed class RuntimeCapabilities {

    private readonly Dictionary<Type, object> items = new();

    public RuntimeCapabilities Add<T>(T value) where T : class {
        ArgumentNullException.ThrowIfNull(value);
        if (!items.TryAdd(typeof(T), value)) {
            throw new InvalidOperationException($"能力表里已经有 {typeof(T).FullName}。");
        }

        return this;
    }

    public bool Contains(Type type) => items.ContainsKey(type);

    /// <summary>
    /// 取出 <typeparamref name="T"/>。生成的创建代码调用它；Runtime 在创建模块之前已经检查过所有类型都在表里。
    /// </summary>
    public T Require<T>() where T : class {
        return items.TryGetValue(typeof(T), out var value)
            ? (T)value
            : throw new InvalidOperationException($"能力表里没有 {typeof(T).FullName}。");
    }

}

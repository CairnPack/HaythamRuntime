namespace Haytham.Runtime.Events;

/// <summary>
/// 注册事件：模块名单锁定后，Runtime 按依赖顺序逐个模块派发，模块在处理器里把 Task、Event 定义和订阅登记到 <see cref="Registry"/>。
/// </summary>
/// <remarks>
/// 处理器结束就等于提交了这份 Registry，之后再调用它会报错。
/// 所有模块提交后，Runtime 统一检查冲突并合并，然后锁定注册结果。
/// 这里只登记，不要开始下载、打开数据库这类业务，业务初始化放到对应事件（例如 <see cref="RuntimeEvents.OnLoad"/>）里。
/// </remarks>
public sealed class RegisterEvent : RuntimeEvent {

    internal RegisterEvent(Registry registry) => Registry = registry;

    public string ModuleId => Registry.ModuleId;

    public Registry Registry { get; }

}

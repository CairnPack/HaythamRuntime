using Haytham.Runtime.Events;

namespace Haytham.Runtime.Modules;

/// <summary>
/// 模块主类实现的接口。模块主类还要标记 <see cref="RuntimeModuleAttribute"/>，由生成器写进模块清单。
/// </summary>
public interface IRuntimeModule {

    /// <summary>
    /// Runtime 创建模块之后、锁定模块名单之前调用一次，用来接上 <see cref="RegisterEvent"/> 的订阅。
    /// 这时只能订阅 <see cref="RegisterEvent"/>；其他事件的订阅、Event 定义和 Task 都在 RegisterEvent 执行时通过 <see cref="Registry"/> 提交。
    /// </summary>
    void Subscribe(EventSubscriber events);

}

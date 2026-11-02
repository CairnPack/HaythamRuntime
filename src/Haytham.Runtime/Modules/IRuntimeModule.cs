using Haytham.Runtime.Events;

namespace Haytham.Runtime.Modules;

/// <summary>
/// 模块主类实现的接口。模块主类还要标记 <see cref="RuntimeModuleAttribute"/>，由生成器写进模块清单。
/// </summary>
public interface IRuntimeModule {

    /// <summary>
    /// 创建模块后接上注册处理器，业务初始化在生命周期事件中进行。
    /// </summary>
    void Connect(ModuleWiring wiring);

}

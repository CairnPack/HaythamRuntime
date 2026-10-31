namespace Haytham.Runtime;

/// <summary>
/// <see cref="RuntimeHost.RequestStopAsync"/> 的结果。清理失败不在这里表示，而是抛出 <see cref="RuntimeCleanupException"/>。
/// </summary>
public enum StopOutcome
{
    /// <summary>
    /// 有模块否决了停止，Runtime 恢复接收 Task，继续运行。
    /// </summary>
    Vetoed,

    /// <summary>
    /// 已接纳的 Task 都已结束，所有模块已停止并卸载。
    /// </summary>
    Stopped
}

namespace Haytham.Runtime;

public enum RuntimeState
{
    Created,
    Starting,
    Running,
    Stopping,
    Stopped,
    /// <summary>
    /// 清理失败，部分资源可能没有释放。Runtime 不能继续使用，错误已返回给调用方。
    /// </summary>
    Hanging
}

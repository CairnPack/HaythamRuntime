namespace Haytham.Runtime.Events;

/// <summary>
/// 事件的订阅者失败。顺序执行的事件只有第一个失败的订阅者，并行执行的事件包含全部失败的订阅者。
/// </summary>
public sealed class RuntimeEventException(Type eventType, IReadOnlyList<EventHandlerFailure> failures)
    : Exception($"事件 {eventType.Name} 的订阅者失败：{string.Join("；", failures.Select(static failure => $"{failure.ModuleId}: {failure.Error.Message}"))}",
        failures[0].Error) {

    public Type EventType { get; } = eventType;

    public IReadOnlyList<EventHandlerFailure> Failures { get; } = failures;

}

/// <param name="ModuleId">失败的订阅者所属的模块。</param>
public sealed record EventHandlerFailure(string ModuleId, Exception Error);

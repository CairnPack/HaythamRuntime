namespace Haytham.Runtime;

/// <summary>
/// 模块列表、能力表或 Registry 不符合要求，Runtime 没有进入运行。<see cref="Problems"/> 一次列出全部问题。
/// </summary>
public sealed class RuntimeConfigurationException(string message, IReadOnlyList<string> problems)
    : Exception($"{message}{Environment.NewLine}{string.Join(Environment.NewLine, problems.Select(static problem => $"- {problem}"))}") {

    public IReadOnlyList<string> Problems { get; } = problems;

}

/// <summary>
/// 停止或启动失败后的清理没有全部完成，Runtime 处于 <see cref="RuntimeState.Hanging"/>。
/// </summary>
/// <remarks>
/// <see cref="OriginalError"/> 是引发清理的原始故障（正常停止时为 <see langword="null"/>），
/// <see cref="AggregateException.InnerExceptions"/> 是各个清理步骤的错误。
/// </remarks>
public sealed class RuntimeCleanupException(Exception? originalError, IReadOnlyList<Exception> cleanupErrors)
    : AggregateException("Runtime 的清理没有全部完成。", cleanupErrors) {

    public Exception? OriginalError { get; } = originalError;

}

namespace Haytham.Runtime;

public enum RuntimeFailureKind {
    Execution,
    Contract,
    Cleanup,
    Output
}

public sealed record RuntimeFailure(
    RuntimeFailureKind Kind,
    string Message,
    Exception? Exception = null,
    ModuleId? Module = null);

public sealed class RuntimeContractException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

public sealed class RuntimeExecutionException(string message, IReadOnlyList<RuntimeFailure> failures)
    : Exception(message) {
    public IReadOnlyList<RuntimeFailure> Failures { get; } = failures;
}

public sealed class RuntimeAlreadyCreatedException(string message) : InvalidOperationException(message);

public sealed class RuntimeConfigurationException(string message, IReadOnlyList<string> problems)
    : Exception($"{message}{Environment.NewLine}{string.Join(Environment.NewLine, problems.Select(static problem => $"- {problem}"))}") {
    public IReadOnlyList<string> Problems { get; } = problems;
}

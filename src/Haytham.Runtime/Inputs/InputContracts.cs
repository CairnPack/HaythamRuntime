using Haytham.Runtime.Tasks;

namespace Haytham.Runtime.Inputs;

/// <summary>标记使用指定 JSON 上下文的外部输入目录。</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class InputCatalogAttribute(Type jsonContext) : Attribute {
    public Type JsonContext { get; } = jsonContext;
}

/// <summary>由宿主解释的业务失败信息。</summary>
public readonly record struct InputFailure(int Code, string Error, IReadOnlyDictionary<string, object?>? Args = null);

/// <summary>输入结果映射得到的对外成功值或业务失败。</summary>
public abstract record InputResult<TReply> {
    private protected InputResult() { }
    /// <summary>携带对外成功值的映射结果。</summary>
    public sealed record Success(TReply Reply) : InputResult<TReply>;
    /// <summary>携带业务失败信息的映射结果。</summary>
    public sealed record Failure(InputFailure Error) : InputResult<TReply>;
}

/// <summary>外部输入未被接收的原因。</summary>
public enum InputRejection { UnknownName, WrongKind, InvalidInput, NotAccepting }
/// <summary>外部输入对应的执行种类。</summary>
public enum InputKind { Task, Call }

/// <summary>供绑定工具读取的一项输入声明。</summary>
public sealed record InputManifestEntry(string Name, InputKind Kind, Type InputType, Type ReplyType, string FieldName);

/// <summary>外部 Task 输入的非泛型运行句柄。</summary>
public sealed class InputRun {
    private readonly Action requestCancellation;

    internal InputRun(ExecutionId id, Action requestCancellation, Task<RuntimeFailure?> completion) {
        Id = id;
        this.requestCancellation = requestCancellation;
        Completion = completion;
    }

    public ExecutionId Id { get; }
    /// <summary>运行结束后完成，结果是输出收尾失败；没有失败时为 null。任务本身的结果已经交给 <see cref="IInputOutput"/>。</summary>
    public Task<RuntimeFailure?> Completion { get; }
    public void RequestCancellation() => requestCancellation();
}

/// <summary>启动外部 Task 输入得到的运行句柄或拒绝。</summary>
public abstract record InputStartResult {
    private protected InputStartResult() { }
    /// <summary>已登记的外部 Task 输入。</summary>
    public sealed record Started(InputRun Run) : InputStartResult;
    /// <summary>未接收外部 Task 输入的原因和说明。</summary>
    public sealed record Rejected(InputRejection Rejection, string Message) : InputStartResult;
}

/// <summary>外部同步输入的回复、业务失败或拒绝。</summary>
public abstract record InputCallResult {
    private protected InputCallResult() { }
    /// <summary>同步输入成功完成后的 JSON 回复。</summary>
    public sealed record Completed(string ReplyJson) : InputCallResult;
    /// <summary>同步输入正常执行后返回的业务失败。</summary>
    public sealed record BusinessFailed(InputFailure Failure) : InputCallResult;
    /// <summary>未接收同步输入的原因和说明。</summary>
    public sealed record Rejected(InputRejection Rejection, string Message) : InputCallResult;
}

/// <summary>宿主接收外部 Task 的报告和最终结果的接口。</summary>
public interface IInputOutput {
    void ReportState(TaskExecutionState state);
    void ReportProgress(TaskProgress progress);
    void ReportLog(TaskLogEntry entry);
    ValueTask CompleteAsync(InputOutcome outcome);
}

/// <summary>交给宿主的外部 Task 最终结果。</summary>
public abstract record InputOutcome {
    private protected InputOutcome() { }
    /// <summary>外部 Task 成功完成后的 JSON 回复。</summary>
    public sealed record Succeeded(string ReplyJson) : InputOutcome;
    /// <summary>外部 Task 正常执行后返回的业务失败。</summary>
    public sealed record BusinessFailed(InputFailure Failure) : InputOutcome;
    /// <summary>外部 Task 执行或结果输出时发生的故障。</summary>
    public sealed record Failed(IReadOnlyList<RuntimeFailure> Failures) : InputOutcome;
    /// <summary>外部 Task 已经取消。</summary>
    public sealed record Canceled : InputOutcome;
}

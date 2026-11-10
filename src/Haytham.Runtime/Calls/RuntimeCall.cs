namespace Haytham.Runtime.Calls;

/// <summary>由模块登记的纯内存同步处理器。</summary>
public abstract class RuntimeCall<TInput, TResult> {
    protected internal abstract TResult Invoke(TInput input);
}

/// <summary>类型化同步调用的返回值或准入拒绝。</summary>
public abstract record CallOutcome<TResult> {
    private protected CallOutcome() { }

    /// <summary>同步处理器已经完成并返回结果。</summary>
    public sealed record Completed(TResult Result) : CallOutcome<TResult>;
    /// <summary>当前生命周期状态不接收同步调用。</summary>
    public sealed record NotAccepting : CallOutcome<TResult>;
}

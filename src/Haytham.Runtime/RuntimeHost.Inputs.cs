using Haytham.Runtime.Calls;
using Haytham.Runtime.Inputs;
using Haytham.Runtime.Tasks;

namespace Haytham.Runtime;

public sealed partial class RuntimeHost {
    private int activeCalls;
    private TaskCompletionSource? callsFinished;

    public InputStartResult StartInput(string name, string inputJson, IInputOutput output, CancellationToken cancellation = default) {
        if (!IsInputTableReady()) {
            return new InputStartResult.Rejected(InputRejection.NotAccepting, "输入表尚未登记。");
        }

        var binding = Events.FindInput(name);
        return binding is null
            ? new InputStartResult.Rejected(InputRejection.UnknownName, "未知输入名字。")
            : binding.Start(this, inputJson, output, cancellation);
    }

    public InputCallResult CallInput(string name, string inputJson) {
        if (!IsInputTableReady()) {
            return new InputCallResult.Rejected(InputRejection.NotAccepting, "输入表尚未登记。");
        }

        var binding = Events.FindInput(name);
        return binding is null
            ? new InputCallResult.Rejected(InputRejection.UnknownName, "未知输入名字。")
            : binding.Call(this, inputJson);
    }

    public CallOutcome<TResult> Call<TCall, TInput, TResult>(TInput input) where TCall : RuntimeCall<TInput, TResult> {
        var call = TryEnterCall<TCall, TInput, TResult>();
        if (call is null) {
            return new CallOutcome<TResult>.NotAccepting();
        }

        try {
            return new CallOutcome<TResult>.Completed(call.Invoke(input));
        }
        finally {
            ExitCall();
        }
    }

    internal RuntimeCall<TInput, TResult>? TryEnterCall<TCall, TInput, TResult>() where TCall : RuntimeCall<TInput, TResult> {
        // 停止也用这把锁关闭准入，计数必须同时增加，避免漏等刚接收的调用。
        lock (Gate) {
            if (State is not (RuntimeState.Running or RuntimeState.PreStopping)) {
                return null;
            }

            var call = Events.GetCall<TCall, TInput, TResult>();
            if (activeCalls++ == 0) {
                callsFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            return call;
        }
    }

    internal void ExitCall() {
        lock (Gate) {
            if (--activeCalls == 0) {
                callsFinished!.SetResult();
            }
        }
    }

    internal Task WaitForCalls() => activeCalls == 0 ? Task.CompletedTask : callsFinished!.Task;

    private bool IsInputTableReady() => Events is not null && Events.IsLocked;

    internal TaskRun<TResult>? TrySubmitInput<TTask, TInput, TResult>(TInput input, string? queueKey,
        ITaskOutputSink<TResult> output, CancellationToken cancellation) where TTask : RuntimeTask<TInput, TResult> =>
        tasks.TrySubmit<TTask, TInput, TResult>(input, queueKey, output, cancellation);
}

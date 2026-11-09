using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Haytham.Runtime.Events;
using Haytham.Runtime.Tasks;

namespace Haytham.Runtime.Inputs;

/// <summary>把一个外部名字绑定到 Task，并定义排队键和结果映射。</summary>
public sealed class TaskInput<TTask, TInput, TResult, TReply>(string name,
    Func<TResult, InputResult<TReply>> mapResult, Func<TInput, string?>? queueKey = null)
    where TTask : RuntimeTask<TInput, TResult> {

    public void Register(Registry registry, JsonTypeInfo<TInput> inputType, JsonTypeInfo<TReply> replyType) =>
        registry.RegisterInput(new Binding(name, mapResult, queueKey, inputType, replyType));

    private sealed class Binding(string name, Func<TResult, InputResult<TReply>> mapResult,
        Func<TInput, string?>? queueKey, JsonTypeInfo<TInput> inputType, JsonTypeInfo<TReply> replyType)
        : InputBinding(name, InputKind.Task) {

        internal override InputStartResult Start(RuntimeHost host, string json, IInputOutput output, CancellationToken cancellation) {
            if (!InputJson.TryRead(json, inputType, out var input)) {
                return new InputStartResult.Rejected(InputRejection.InvalidInput, "输入 JSON 无效或为 null。");
            }

            var run = host.TrySubmitInput<TTask, TInput, TResult>(input, queueKey?.Invoke(input),
                new Output(output, mapResult, replyType), cancellation);
            return run is null
                ? new InputStartResult.Rejected(InputRejection.NotAccepting, "当前不接收 Task 输入。")
                : new InputStartResult.Started(new InputRun(run.Id, run.RequestCancellation, run.Completion));
        }
    }

    private sealed class Output(IInputOutput output, Func<TResult, InputResult<TReply>> mapResult,
        JsonTypeInfo<TReply> replyType) : ITaskOutputSink<TResult> {
        public void ReportState(TaskExecutionState state) => output.ReportState(state);
        public void ReportProgress(TaskProgress progress) => output.ReportProgress(progress);
        public void ReportLog(TaskLogEntry entry) => output.ReportLog(entry);

        public async ValueTask CompleteAsync(TaskOutcome<TResult> outcome) {
            InputOutcome mapped;
            try {
                mapped = outcome switch {
                    TaskOutcome<TResult>.Succeeded success => MapSuccess(success.Result),
                    TaskOutcome<TResult>.Failed failed => new InputOutcome.Failed(failed.Failures),
                    TaskOutcome<TResult>.Canceled => new InputOutcome.Canceled(),
                    _ => throw new RuntimeContractException("未知 Task 结果。")
                };
            }
            catch (Exception error) {
                // 宿主仍需要完成事件来结束等待；重新抛出让调度器保留输出失败。
                var failure = new RuntimeFailure(RuntimeFailureKind.Output, "输入结果映射或 JSON 序列化失败。", error);
                await output.CompleteAsync(new InputOutcome.Failed([failure]));
                throw;
            }

            await output.CompleteAsync(mapped);
        }

        private InputOutcome MapSuccess(TResult result) {
            return mapResult(result) switch {
                InputResult<TReply>.Success success => new InputOutcome.Succeeded(JsonSerializer.Serialize(success.Reply, replyType)),
                InputResult<TReply>.Failure failure => new InputOutcome.BusinessFailed(failure.Error),
                _ => throw new RuntimeContractException("未知输入映射结果。")
            };
        }
    }
}

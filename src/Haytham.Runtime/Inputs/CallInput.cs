using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Haytham.Runtime.Calls;
using Haytham.Runtime.Events;

namespace Haytham.Runtime.Inputs;

/// <summary>把一个外部名字绑定到同步处理器，并定义结果映射。</summary>
public sealed class CallInput<TCall, TInput, TResult, TReply>(string name, Func<TResult, InputResult<TReply>> mapResult)
    where TCall : RuntimeCall<TInput, TResult> {

    public void Register(Registry registry, JsonTypeInfo<TInput> inputType, JsonTypeInfo<TReply> replyType) =>
        registry.RegisterInput(new Binding(name, mapResult, inputType, replyType));

    private sealed class Binding(string name, Func<TResult, InputResult<TReply>> mapResult,
        JsonTypeInfo<TInput> inputType, JsonTypeInfo<TReply> replyType) : InputBinding(name, InputKind.Call) {

        internal override InputCallResult Call(RuntimeHost host, string json) {
            if (!InputJson.TryRead(json, inputType, out var input)) {
                return new InputCallResult.Rejected(InputRejection.InvalidInput, "输入 JSON 无效或为 null。");
            }

            var call = host.TryEnterCall<TCall, TInput, TResult>();
            if (call is null) {
                return new InputCallResult.Rejected(InputRejection.NotAccepting, "当前不接收同步输入。");
            }

            try {
                return mapResult(call.Invoke(input)) switch {
                    InputResult<TReply>.Success success => new InputCallResult.Completed(JsonSerializer.Serialize(success.Reply, replyType)),
                    InputResult<TReply>.Failure failure => new InputCallResult.BusinessFailed(failure.Error),
                    _ => throw new RuntimeContractException("未知输入映射结果。")
                };
            }
            finally {
                host.ExitCall();
            }
        }
    }
}

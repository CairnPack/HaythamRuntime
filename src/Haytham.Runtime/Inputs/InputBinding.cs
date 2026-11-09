using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Haytham.Runtime.Inputs;

internal abstract class InputBinding(string name, InputKind kind) {
    internal string Name { get; } = name;
    internal InputKind Kind { get; } = kind;

    internal virtual InputStartResult Start(RuntimeHost host, string json, IInputOutput output, CancellationToken cancellation) =>
        new InputStartResult.Rejected(InputRejection.WrongKind, "该输入是同步调用。");

    internal virtual InputCallResult Call(RuntimeHost host, string json) =>
        new InputCallResult.Rejected(InputRejection.WrongKind, "该输入是 Task。");
}

internal static class InputJson {
    internal static bool TryRead<T>(string json, JsonTypeInfo<T> typeInfo, out T input) {
        try {
            input = JsonSerializer.Deserialize(json, typeInfo)!;
            return input is not null;
        }
        catch (JsonException) {
            input = default!;
            return false;
        }
    }
}

namespace Haytham.Runtime;

public readonly record struct ExecutionId(Guid Value);

public readonly struct ModuleId : IEquatable<ModuleId> {
    public ModuleId(string value) {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public bool Equals(ModuleId other) => StringComparer.OrdinalIgnoreCase.Equals(Value, other.Value);
    public override bool Equals(object? obj) => obj is ModuleId other && Equals(other);
    public override int GetHashCode() => Value is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
    public override string ToString() => Value;
    public static bool operator ==(ModuleId left, ModuleId right) => left.Equals(right);
    public static bool operator !=(ModuleId left, ModuleId right) => !left.Equals(right);
}

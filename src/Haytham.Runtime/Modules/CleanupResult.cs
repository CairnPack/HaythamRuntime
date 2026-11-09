namespace Haytham.Runtime.Modules;

public readonly record struct CleanupResult {
    private CleanupResult(string? message) => Message = message;

    public string? Message { get; }
    public bool IsDone => Message is null;
    public static CleanupResult Done => new(null);
    public static CleanupResult Failed(string message) => new(message);
}

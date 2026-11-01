namespace Haytham.Runtime;

public enum StopOutcomeKind {
    Stopped,
    Vetoed,
    DecisionFailed,
    CleanupFailed
}

public sealed record StopOutcome(StopOutcomeKind Kind, IReadOnlyList<RuntimeFailure> Failures);

public interface IRuntimeTerminationStrategy {
    void Terminate();
}

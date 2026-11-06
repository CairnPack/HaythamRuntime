using Microsoft.Extensions.Logging;

namespace Haytham.Runtime.Logging;

internal static partial class RuntimeLog {
    [LoggerMessage(1000, LogLevel.Information, "Runtime created with {ModuleCount} modules.")]
    internal static partial void RuntimeCreated(ILogger logger, int moduleCount);

    [LoggerMessage(1001, LogLevel.Debug, "Module lifecycle step started.")]
    internal static partial void ModuleStepStarted(ILogger logger);

    [LoggerMessage(1002, LogLevel.Debug, "Module lifecycle step completed.")]
    internal static partial void ModuleStepCompleted(ILogger logger);

    [LoggerMessage(1004, LogLevel.Information, "Runtime state changed from {PreviousState} to {State}.")]
    internal static partial void RuntimeStateChanged(ILogger logger, string previousState, string state);

    [LoggerMessage(EventId = 1005, Message = "Stop finished with outcome {Outcome}, state {State}, and {FailureCount} failures.")]
    internal static partial void StopFinished(ILogger logger, LogLevel level, string outcome, string state, int failureCount);

    [LoggerMessage(1006, LogLevel.Warning, "Forced termination requested from state {State}.")]
    internal static partial void KillRequested(ILogger logger, string state);

    [LoggerMessage(1007, LogLevel.Error, "Forced termination did not complete: {Reason}; exception type {ExceptionType}.")]
    internal static partial void KillNotCompleted(ILogger logger, string reason, string? exceptionType);

    [LoggerMessage(1100, LogLevel.Error, "Execution failure observed; kind {FailureKind}; exception type {ExceptionType}.")]
    internal static partial void FailureObserved(ILogger logger, string failureKind, string? exceptionType);

    [LoggerMessage(1101, LogLevel.Error, "Task output reporting failed; exception type {ExceptionType}.")]
    internal static partial void TaskOutputReportFailed(ILogger logger, string? exceptionType);

    [LoggerMessage(1102, LogLevel.Error, "Task output completion failed; exception type {ExceptionType}.")]
    internal static partial void TaskOutputCompletionFailed(ILogger logger, string? exceptionType);

    [LoggerMessage(1103, LogLevel.Error, "Continuous work failure notification failed; exception type {ExceptionType}.")]
    internal static partial void ContinuousWorkNotificationFailed(ILogger logger, string? exceptionType);

    [LoggerMessage(1104, LogLevel.Debug, "Exception details for failure event {FailureEventId}.")]
    internal static partial void FailureDetails(ILogger logger, int failureEventId, Exception exception);

    [LoggerMessage(2000, LogLevel.Debug, "Task accepted; queued {Queued}.")]
    internal static partial void TaskAccepted(ILogger logger, bool queued);

    [LoggerMessage(2001, LogLevel.Debug, "Task execution started.")]
    internal static partial void TaskStarted(ILogger logger);

    [LoggerMessage(EventId = 2002, Message = "Task finished with outcome {Outcome}, {BusinessFailureCount} business failures, and output failure {HasOutputFailure}.")]
    internal static partial void TaskFinished(ILogger logger, LogLevel level, string outcome, int businessFailureCount, bool hasOutputFailure);

    [LoggerMessage(3000, LogLevel.Debug, "Continuous work started; retry {IsRetry}.")]
    internal static partial void ContinuousWorkStarted(ILogger logger, bool isRetry);

    [LoggerMessage(3001, LogLevel.Debug, "Continuous work is ready.")]
    internal static partial void ContinuousWorkReady(ILogger logger);

    [LoggerMessage(EventId = 3002, Message = "Continuous work finished with outcome {Outcome} and {FailureCount} failures.")]
    internal static partial void ContinuousWorkFinished(ILogger logger, LogLevel level, string outcome, int failureCount);
}

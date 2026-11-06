using Haytham.Runtime.Logging;
using Microsoft.Extensions.Logging;

namespace Haytham.Runtime.Tasks;

public sealed record TaskProgress(
    string Phase,
    long? Done = null,
    long? Total = null,
    long? Bytes = null,
    long? TotalBytes = null,
    string? Item = null);

public sealed record TaskLogEntry(string Message);

public interface ITaskOutputSink<TResult> {
    void ReportState(TaskExecutionState state);
    void ReportProgress(TaskProgress progress);
    void ReportLog(TaskLogEntry entry);
    ValueTask CompleteAsync(TaskOutcome<TResult> outcome);
}

public sealed class TaskOutput {
    private readonly ILogger logger;
    private readonly Action<TaskProgress> reportProgress;
    private readonly Action<TaskLogEntry> reportLog;
    private readonly object gate = new();
    private bool open = true;

    internal TaskOutput(ILogger logger, Action<TaskProgress> reportProgress, Action<TaskLogEntry> reportLog) {
        this.logger = logger;
        this.reportProgress = reportProgress;
        this.reportLog = reportLog;
    }

    public void ReportProgress(TaskProgress progress) {
        ArgumentNullException.ThrowIfNull(progress);
        Report(reportProgress, progress);
    }

    public void ReportLog(TaskLogEntry entry) {
        ArgumentNullException.ThrowIfNull(entry);
        Report(reportLog, entry);
    }

    internal void Close() {
        lock (gate) {
            open = false;
        }
    }

    private void Report<T>(Action<T> report, T value) {
        lock (gate) {
            if (!open) {
                throw new RuntimeContractException("Task 已经结束，报告入口已关闭；TaskOutput 只能在本次执行和它登记的必要子工作里使用。");
            }

            try {
                report(value);
            }
            catch (Exception error) {
                RuntimeLog.TaskOutputReportFailed(logger, error.GetType().FullName);
                RuntimeLog.FailureDetails(logger, 1101, error);
                throw new RuntimeExecutionException("Task 报告失败。",
                    [new RuntimeFailure(RuntimeFailureKind.Output, "Task 报告失败。", error)]);
            }
        }
    }
}

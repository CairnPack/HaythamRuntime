using Haytham.Runtime.Events;
using Haytham.Runtime.Work;

namespace Haytham.Runtime.Tasks;

internal sealed class RuntimeTaskScheduler(RuntimeHost host) {
    internal TaskRun<TResult> Submit<TTask, TInput, TResult>(TInput input, string? queueKey,
        ITaskOutputSink<TResult>? output, CancellationToken cancellation) where TTask : RuntimeTask<TInput, TResult> {
        var registration = host.Events.GetTask<TTask, TInput, TResult>();
        if (queueKey is not null) {
            return SubmitQueued<TInput, TResult>(registration, input, queueKey, output, cancellation);
        }

        WorkNode node;
        TaskRun<TResult> run;
        RuntimeFailure? outputFailure = null;
        var reports = new TaskOutput(progress => output?.ReportProgress(progress), entry => output?.ReportLog(entry));
        lock (host.Gate) {
            host.RequireRootAdmission();
            node = new WorkNode(host, registration.Module, cancellation, reports);
            run = new TaskRun<TResult>(node.Id, node.RequestCancellation);
            host.RegisterRoot(node.Id, new RootExecution(run.Completion, () => outputFailure));
        }

        // 不在提交线程执行用户代码，包括第一个 await 之前的部分。
        _ = Task.Run(async () => {
            TaskOutcome<TResult> outcome;
            try {
                var result = await node.ExecuteAsync(async current => {
                    output?.ReportState(TaskExecutionState.Submitted);
                    run.SetState(TaskExecutionState.Running);
                    output?.ReportState(TaskExecutionState.Running);
                    return await host.Events.ExecuteTaskAsync((RuntimeTask<TInput, TResult>)registration.Task, input, current);
                });
                outcome = new TaskOutcome<TResult>.Succeeded(result);
            }
            catch (RuntimeExecutionException error) {
                outcome = new TaskOutcome<TResult>.Failed(error.Failures);
            }
            catch (OperationCanceledException) {
                outcome = new TaskOutcome<TResult>.Canceled();
            }

            reports.Close();
            run.SetState(TaskExecutionState.Completing);
            outputFailure = await CompleteOutputAsync(output, outcome);
            run.SetState(outcome switch {
                TaskOutcome<TResult>.Succeeded => TaskExecutionState.Succeeded,
                TaskOutcome<TResult>.Failed => TaskExecutionState.Failed,
                TaskOutcome<TResult>.Canceled => TaskExecutionState.Canceled,
                _ => throw new RuntimeContractException("未知 Task 结果。")
            });

            lock (host.Gate) {
                run.Complete(new TaskCompletion<TResult>(outcome, outputFailure));
                host.RemoveRoot(node.Id);
            }
        });
        return run;
    }

    private TaskRun<TResult> SubmitQueued<TInput, TResult>(TaskRegistration registration, TInput input, string queueKey,
        ITaskOutputSink<TResult>? output, CancellationToken cancellation) =>
        throw new NotImplementedException("同键 FIFO、排队取消及同键执行权释放尚未实现。");

    private static async ValueTask<RuntimeFailure?> CompleteOutputAsync<TResult>(ITaskOutputSink<TResult>? output, TaskOutcome<TResult> outcome) {
        if (output is null) {
            return null;
        }

        try {
            output.ReportState(TaskExecutionState.Completing);
            output.ReportState(outcome switch {
                TaskOutcome<TResult>.Succeeded => TaskExecutionState.Succeeded,
                TaskOutcome<TResult>.Failed => TaskExecutionState.Failed,
                TaskOutcome<TResult>.Canceled => TaskExecutionState.Canceled,
                _ => throw new RuntimeContractException("未知 Task 结果。")
            });
            await output.CompleteAsync(outcome);
            return null;
        }
        catch (Exception error) {
            return new RuntimeFailure(RuntimeFailureKind.Output, "Task 输出收尾失败。", error);
        }
    }
}

using Haytham.Runtime.Work;

namespace Haytham.Runtime.Tasks;

internal sealed class RuntimeTaskScheduler(RuntimeHost host) {
    private readonly Dictionary<string, LinkedList<TaskCompletionSource<bool>>> queues = new(StringComparer.Ordinal);

    internal TaskRun<TResult> Submit<TTask, TInput, TResult>(TInput input, string? queueKey,
        ITaskOutputSink<TResult>? output, CancellationToken cancellation) where TTask : RuntimeTask<TInput, TResult> {
        WorkNode node;
        TaskRun<TResult> run;
        RuntimeFailure? outputFailure = null;
        var reports = new TaskOutput(progress => output?.ReportProgress(progress), entry => output?.ReportLog(entry));
        var turn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        LinkedListNode<TaskCompletionSource<bool>>? position = null;
        CancellationTokenRegistration queuedCancellation = default;
        var queued = false;
        RuntimeTask<TInput, TResult> task;
        lock (host.Gate) {
            host.RequireRootAdmission();
            var registration = host.Events.GetTask<TTask, TInput, TResult>();
            task = (RuntimeTask<TInput, TResult>)registration.Task;
            node = new WorkNode(host, registration.Module, cancellation, reports);
            run = new TaskRun<TResult>(node.Id, node.RequestCancellation);
            host.RegisterRoot(node.Id, new RootExecution(run.Completion, () => outputFailure,
                registration.ExitPolicy == TaskExitPolicy.CancelOnExit ? node.RequestCancellation : null));

            if (queueKey is not null) {
                if (!queues.TryGetValue(queueKey, out var queue)) {
                    queue = new LinkedList<TaskCompletionSource<bool>>();
                    queues.Add(queueKey, queue);
                }

                queued = queue.Count > 0;
                position = queue.AddLast(turn);
                if (queued) {
                    queuedCancellation = node.Cancellation.Register(() => CancelQueued(position));
                }
            }

            if (!queued) {
                turn.SetResult(true);
            }
        }

        // 不在提交线程执行用户代码，包括第一个 await 之前的部分。
        _ = Task.Run(RunAsync);
        return run;

        async Task RunAsync() {
            TaskOutcome<TResult> outcome;
            try {
                var result = await node.ExecuteAsync(async current => {
                    ReportState(TaskExecutionState.Submitted);
                    if (queued) {
                        ReportState(TaskExecutionState.Queued);
                    }

                    var execute = await turn.Task;
                    if (!execute) {
                        throw new OperationCanceledException(current.Cancellation);
                    }

                    ReportState(TaskExecutionState.Running);
                    return await host.Events.ExecuteTaskAsync(task, input, current);
                });
                outcome = new TaskOutcome<TResult>.Succeeded(result);
            }
            catch (RuntimeExecutionException error) {
                outcome = new TaskOutcome<TResult>.Failed(error.Failures);
            }
            catch (OperationCanceledException) {
                outcome = new TaskOutcome<TResult>.Canceled();
            }
            finally {
                queuedCancellation.Dispose();
            }

            reports.Close();
            var finalState = outcome switch {
                TaskOutcome<TResult>.Succeeded => TaskExecutionState.Succeeded,
                TaskOutcome<TResult>.Failed => TaskExecutionState.Failed,
                TaskOutcome<TResult>.Canceled => TaskExecutionState.Canceled,
                _ => throw new RuntimeContractException("未知 Task 结果。")
            };
            run.SetState(TaskExecutionState.Completing);
            outputFailure = await CompleteOutputAsync(output, outcome, finalState);
            run.SetState(finalState);

            lock (host.Gate) {
                run.Complete(new TaskCompletion<TResult>(outcome, outputFailure));
                host.RemoveRoot(node.Id);
                if (position?.List is { } queue) {
                    var wasFirst = queue.First == position;
                    queue.Remove(position);
                    if (wasFirst && queue.First is { } next) {
                        next.Value.SetResult(true);
                    }
                    else if (queue.Count == 0) {
                        queues.Remove(queueKey!);
                    }
                }
            }
        }

        void ReportState(TaskExecutionState state) {
            run.SetState(state);
            try {
                output?.ReportState(state);
            }
            catch (Exception error) {
                throw new RuntimeExecutionException("Task 状态报告失败。",
                    [new RuntimeFailure(RuntimeFailureKind.Output, "Task 状态报告失败。", error)]);
            }
        }
    }

    private void CancelQueued(LinkedListNode<TaskCompletionSource<bool>> position) {
        lock (host.Gate) {
            if (!position.Value.Task.IsCompleted) {
                position.List!.Remove(position);
                position.Value.SetResult(false);
            }
        }
    }

    private static async ValueTask<RuntimeFailure?> CompleteOutputAsync<TResult>(ITaskOutputSink<TResult>? output,
        TaskOutcome<TResult> outcome, TaskExecutionState finalState) {
        if (output is null) {
            return null;
        }

        try {
            output.ReportState(TaskExecutionState.Completing);
            output.ReportState(finalState);
            await output.CompleteAsync(outcome);
            return null;
        }
        catch (Exception error) {
            return new RuntimeFailure(RuntimeFailureKind.Output, "Task 输出收尾失败。", error);
        }
    }
}

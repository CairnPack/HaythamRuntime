using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using Haytham.Runtime;
using Haytham.Runtime.Generated;
using Haytham.Runtime.Tasks;
using Test.Storage;

namespace Test;

internal static partial class ScenarioRunner {
    internal static readonly string[] Names = ["default", "parallel-fail-fast", "queue", "cancel-and-fail", "output-failure", "pre-stop",
        "stop-decision", "start-rollback", "continuous-retry", "merge-errors", "repeat-calls", "kill", "kill-strategy",
        "cleanup-failure", "task-report-boundary", "exit-policy-snapshot", "continuous-stop-signals",
        "input-dispatch", "input-call-stop", "input-cleanup", "input-merge-errors", "input-output-failure",
        "input-missing-input-metadata", "input-missing-reply-metadata"];
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    internal static async Task<int> RunAsync(string[] args) {
        var name = args[0];
        if (args.Length != 1 || (name != "all" && !Names.Contains(name))) {
            Console.WriteLine($"FAIL {name}: 场景入口参数错误，可用场景：{string.Join(", ", Names)}，或 all。");
            return 1;
        }

        if (name == "all") {
            return await RunAllAsync();
        }

        try {
            await Task.Run(() => RunScenarioAsync(name)).WaitAsync(Timeout);
            Console.WriteLine($"PASS {name}");
            return 0;
        }
        catch (TimeoutException) {
            Console.WriteLine($"FAIL {name}: 10 秒内未完成；场景超时，需结合最后到达的检查点排查 Runtime 或场景实现。");
            return 1;
        }
        catch (Exception error) {
            Console.WriteLine($"FAIL {name}: {error.Message.ReplaceLineEndings(" ")}");
            return 1;
        }
    }

    private static async Task<int> RunAllAsync() {
        var passed = 0;
        foreach (var name in Names) {
            var start = new ProcessStartInfo(Environment.ProcessPath!) {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            // dotnet 托管启动时需要把当前程序集路径一并传给子进程。
            if (string.Equals(Path.GetFileNameWithoutExtension(start.FileName), "dotnet", StringComparison.OrdinalIgnoreCase)) {
                start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
            }

            start.ArgumentList.Add(name);
            try {
                using var child = Process.Start(start)!;
                var output = child.StandardOutput.ReadToEndAsync();
                var errors = child.StandardError.ReadToEndAsync();
                var timedOut = false;
                try {
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                }
                catch (TimeoutException) {
                    timedOut = true;
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync();
                }

                Console.Write(await output);
                Console.Error.Write(await errors);
                if (timedOut) {
                    Console.WriteLine($"FAIL {name}: 子进程超过 15 秒，已终止。");
                }
                else if (child.ExitCode == 0) {
                    passed++;
                }
                else {
                    Console.WriteLine($"子进程 {name} 退出码：{child.ExitCode}");
                }
            }
            catch (Exception error) {
                Console.WriteLine($"FAIL {name}: 场景子进程启动或回收失败：{error.Message.ReplaceLineEndings(" ")}");
            }
        }

        Console.WriteLine($"通过：{passed}，失败：{Names.Length - passed}");
        return passed == Names.Length ? 0 : 1;
    }

    private static async Task RunScenarioAsync(string name) {
        if (name is "input-dispatch" or "input-call-stop" or "input-cleanup" or "input-merge-errors" or "input-output-failure"
            or "input-missing-input-metadata" or "input-missing-reply-metadata") {
            await RunInputScenarioAsync(name);
            return;
        }

        if (name == "default") {
            Console.WriteLine("检查规格：第 6 节 StartAsync、PreStopAsync、CancelPreStop 和 StopAsync；现有演示最终停止结果为 Stopped。");
            var result = await Demo.RunAsync();
            Check(result.Kind == StopOutcomeKind.Stopped && result.Failures.Count == 0, "现有演示最终停止结果不是无故障的 Stopped。");
            return;
        }

        var specification = name switch {
            "parallel-fail-fast" => "第 3 节第 5 条：发布提前报告首错，所属执行等待慢订阅者结束。",
            "queue" => "第 5 节第 12 到 14、21 条：同键按接纳顺序执行，完成输出后交接；排队取消不执行业务。",
            "cancel-and-fail" => "第 2 节第 10 条：有效取消为 Canceled，真实失败优先，收到取消后正常完成仍成功。",
            "output-failure" => "§5.20、5.23、§6.18 第 6 到 9 步、§6.21、6.22、6.24：Stop 等待输出收尾，输出失败仍交接同键并汇入 Stopped 的结果。",
            "cleanup-failure" => "§6.24、6.25、§1.9：依赖方卸载失败且仍持有资源时，不释放被依赖资源，返回 CleanupFailed、Hanging 并保留原错。",
            "task-report-boundary" => "§5.27、5.28、5.21 第 2 步：进度同步交给接收端，根 Completion 完成后封闭报告入口。",
            "exit-policy-snapshot" => "§5.4、§6.12：ExitPolicy 在登记时固定，之后修改配置不影响预退出策略。",
            "continuous-stop-signals" => "§6.23、§7.14：先向全部持续工作发停止信号，再等待它们实际结束。",
            "pre-stop" => "第 6 节第 12 到 14 条：预退出取消 CancelOnExit，Wait 继续，撤销后恢复准入。",
            "stop-decision" => "第 6 节第 18、19 条：否决与判定异常均恢复运行，之后可正常停止。",
            "start-rollback" => "第 6 节第 11 条：启动失败保留错误，停止已启动模块并反序卸载，回滚后不得重新启动。",
            "continuous-retry" => "第 7 节第 4、6 到 8、11 到 14 条：持续工作故障可观察、不影响模块 Task，重试运行与旧 handle 隔离并受宿主收尾管理。",
            "merge-errors" => "第 4 节第 9、10 条：合并一次列出所有登记问题，合并失败不执行 OnLoading。",
            "repeat-calls" => "第 6 节第 4、26 条：并发启动同步拒绝，重复停止、无预退出撤销和第二次创建均拒绝。",
            "kill" => "第 6 节第 28 条、第 8 节第 8 条：Created 拒绝 Kill，缺少终止策略时报告终止未完成。",
            "kill-strategy" => "第 6 节第 28 条、第 8 节第 8 条：Created 拒绝 Kill，运行时调用能力表中的终止策略。",
            _ => throw new InvalidOperationException("场景分派错误。")
        };
        Console.WriteLine($"检查规格：{specification}");
        if (name is "stop-decision" or "start-rollback" or "continuous-retry" or "merge-errors" or "repeat-calls" or "kill" or "kill-strategy"
            or "cleanup-failure" or "continuous-stop-signals") {
            await RunLifecycleScenarioAsync(name);
            return;
        }

        var settings = new ScenarioSettings {
            VetoFirstStop = false,
            ParallelFailFast = name == "parallel-fail-fast",
            Lifecycle = name == "output-failure" ? new LifecycleProbe() : null
        };
        var host = RuntimeApplication.Create(new RuntimeCapabilities().Add<ILoggerFactory>(NullLoggerFactory.Instance).Add<TextWriter>(TextWriter.Null).Add(settings));
        await host.StartAsync();
        switch (name) {
            case "parallel-fail-fast":
                await CheckParallelAsync(host);
                break;
            case "queue":
                await CheckQueueAsync(host, cancelThird: false);
                await CheckQueueAsync(host, cancelThird: true);
                break;
            case "cancel-and-fail":
                await CheckCancellationAsync(host);
                break;
            case "output-failure":
                await CheckOutputFailureAsync(host, settings.Lifecycle!);
                return;
            case "task-report-boundary":
                await CheckTaskReportBoundaryAsync(host);
                break;
            case "exit-policy-snapshot":
                await CheckExitPolicySnapshotAsync(host, settings);
                break;
            case "pre-stop":
                await CheckPreStopAsync(host);
                break;
        }

        var stop = await host.StopAsync();
        Check(stop.Kind == StopOutcomeKind.Stopped && stop.Failures.Count == 0 && host.State == RuntimeState.Stopped,
            "场景收尾未得到无故障的 Stopped。");
    }

    private static async Task CheckParallelAsync(RuntimeHost host) {
        var probe = new ParallelProbe();
        var run = ParallelPublishTask.Submit(host, probe);
        try {
            var error = await probe.PublishFailure.Task;
            await probe.PublisherReturned.Task;
            Check(error.Failures.Any(failure => ReferenceEquals(failure.Exception, probe.ExpectedFailure)), "PublishAsync 未报告快订阅者的原始错误。");
            Check(!probe.SlowFinished.Task.IsCompleted && !run.Completion.IsCompleted,
                "慢订阅者未释放时，所属执行已经完成。");
            Console.WriteLine("检查点：PublishAsync 已返回首错，发布方已返回，慢订阅者仍受阻，根执行尚未完成。");
        }
        finally {
            probe.ReleaseSlow.TrySetResult();
        }

        var completion = await run.Completion;
        Check(probe.SlowFinished.Task.IsCompletedSuccessfully, "根执行没有等慢订阅者实际结束。");
        Check(completion.Business is TaskOutcome<bool>.Failed failed
            && failed.Failures.Any(failure => ReferenceEquals(failure.Exception, probe.ExpectedFailure)),
            "订阅者故障未汇入所属执行的 Failed 结果。");
        Check(completion.OutputFailure is null, "并行场景出现意外输出故障。");
    }

    private static async Task CheckQueueAsync(RuntimeHost host, bool cancelThird) {
        var inputs = new ScenarioTaskInput[3];
        var outputs = new ScenarioOutput[3];
        var runs = new TaskRun<int>[3];
        for (var i = 0; i < 3; i++) {
            var index = i;
            outputs[i] = new ScenarioOutput();
            inputs[i] = new ScenarioTaskInput {
                OnStart = () => {
                    if (index > 0) {
                        Check(outputs[index - 1].Finished && runs[index - 1].Completion.IsCompleted,
                            $"同键第 {index + 1} 项在前一项完成输出和 Completion 之前开始。");
                    }
                }
            };
            runs[i] = ScenarioWaitTask.Submit(host, inputs[i], "queue", outputs[i]);
        }

        await inputs[0].Started.Task;
        await Task.WhenAll(outputs[1].Queued.Task, outputs[2].Queued.Task);
        Check(inputs[1].ExecutionCount == 0 && inputs[2].ExecutionCount == 0, "同键排队项提前调用了 ExecuteAsync。");
        if (cancelThird) {
            runs[2].RequestCancellation();
            await outputs[2].Completing.Task;
            outputs[2].Release.TrySetResult();
            var canceled = await runs[2].Completion;
            Check(canceled.Business is TaskOutcome<int>.Canceled && canceled.OutputFailure is null
                && runs[2].State == TaskExecutionState.Canceled && inputs[2].ExecutionCount == 0,
                "排队中的第三项未直接取消，或调用了 ExecuteAsync。");
            Check(!runs[0].Completion.IsCompleted && !runs[1].Completion.IsCompleted, "排队取消意外等待或结束了前两项。");
        }

        var count = cancelThird ? 2 : 3;
        for (var i = 0; i < count; i++) {
            await inputs[i].Started.Task;
            Check(inputs[i].ExecutionCount == 1, $"同键第 {i + 1} 项执行次数不为 1。");
            inputs[i].Release.SetResult();
            await outputs[i].Completing.Task;
            Check(!runs[i].Completion.IsCompleted, "CompleteAsync 尚未结束时 Completion 已完成。");
            if (i + 1 < count) {
                Check(inputs[i + 1].ExecutionCount == 0, "输出收尾受阻时后一个同键 Task 已开始。");
            }

            outputs[i].Release.SetResult();
            RequireSuccess(await runs[i].Completion);
        }

        Console.WriteLine(cancelThird ? "检查点：第三项在前两项结束前取消，ExecuteAsync 调用次数为 0。"
            : "检查点：三项按提交顺序执行，每次均在前一项完成输出和 Completion 后开始。");
    }

    private static async Task CheckCancellationAsync(RuntimeHost host) {
        foreach (var behavior in new[] { CancellationBehavior.Cancel, CancellationBehavior.Fail, CancellationBehavior.Complete }) {
            var input = new ScenarioTaskInput { Behavior = behavior };
            var run = ScenarioWaitTask.Submit(host, input);
            await input.Started.Task;
            run.RequestCancellation();
            await input.CancellationObserved.Task;
            input.Release.TrySetResult();
            var completion = await run.Completion;
            var expected = behavior switch {
                CancellationBehavior.Cancel => completion.Business is TaskOutcome<int>.Canceled && run.State == TaskExecutionState.Canceled,
                CancellationBehavior.Fail => completion.Business is TaskOutcome<int>.Failed failed && run.State == TaskExecutionState.Failed
                    && failed.Failures.Any(failure => ReferenceEquals(failure.Exception, input.ExpectedFailure)),
                CancellationBehavior.Complete => completion.Business is TaskOutcome<int>.Succeeded { Result: 42 } && run.State == TaskExecutionState.Succeeded,
                _ => false
            };
            Check(expected && completion.OutputFailure is null, $"取消行为 {behavior} 的业务结果或状态不符合规格：{completion}。");
            Console.WriteLine($"检查点：{behavior} => {run.State}");
        }
    }

    private static async Task CheckOutputFailureAsync(RuntimeHost host, LifecycleProbe probe) {
        var firstInput = new ScenarioTaskInput();
        var output = new ScenarioOutput { FailOnComplete = true };
        var first = ScenarioWaitTask.Submit(host, firstInput, "output-failure", output);
        var secondInput = new ScenarioTaskInput {
            OnStart = () => Check(output.Finished && first.Completion.IsCompleted, "输出失败项尚未结束就交出了同键执行权。")
        };
        var second = ScenarioWaitTask.Submit(host, secondInput, "output-failure");
        await firstInput.Started.Task;
        firstInput.Release.SetResult();
        await output.Completing.Task;
        Check(!first.Completion.IsCompleted && secondInput.ExecutionCount == 0, "输出收尾尚未结束时 Completion 或后项已推进。");
        var stopping = host.StopAsync();
        Check(!stopping.IsCompleted && probe.Unloaded.Count == 0,
            $"首项 CompleteAsync 仍受阻时 Stop 已完成或 OnUnloading 已执行：Stop 完成={stopping.IsCompleted}，卸载次数={probe.Unloaded.Count}。");
        Console.WriteLine("检查点 1：首项 CompleteAsync 受阻，Stop 尚未完成，OnUnloading 未执行。");
        output.Release.SetResult();
        var completion = await first.Completion;
        Check(completion.Business is TaskOutcome<int>.Succeeded { Result: 42 } && first.State == TaskExecutionState.Succeeded,
            "输出失败覆盖了成功业务结果。");
        Check(completion.OutputFailure is { Kind: RuntimeFailureKind.Output } failure
            && ReferenceEquals(failure.Exception, output.ExpectedFailure), "OutputFailure 未保留输出收尾错误。");
        await secondInput.Started.Task;
        secondInput.Release.SetResult();
        RequireSuccess(await second.Completion);
        Console.WriteLine("检查点：首项业务 Succeeded、OutputFailure 非空，同键下一项成功执行。");
        var stop = await stopping;
        Check(stop.Kind == StopOutcomeKind.Stopped && host.State == RuntimeState.Stopped
            && probe.Unloaded.SequenceEqual(probe.Loaded.AsEnumerable().Reverse())
            && stop.Failures.Any(item => item.Kind == RuntimeFailureKind.Output && ReferenceEquals(item.Exception, output.ExpectedFailure)),
            $"资源释放后 Stop 未返回包含原始输出故障的 Stopped：结果={stop.Kind}，状态={host.State}，故障数={stop.Failures.Count}，卸载顺序={string.Join(" → ", probe.Unloaded)}。");
        Console.WriteLine("检查点 2：两个模块均已反序卸载，Stop 返回 Stopped，Failures 保留原始输出故障。");
    }

    private static async Task CheckPreStopAsync(RuntimeHost host) {
        var cancelInput = new ScenarioTaskInput();
        var waitInput = new ScenarioTaskInput();
        var cancel = ScenarioCancelTask.Submit(host, cancelInput);
        var wait = ScenarioWaitTask.Submit(host, waitInput);
        await Task.WhenAll(cancelInput.Started.Task, waitInput.Started.Task);
        await host.PreStopAsync();
        Check(host.State == RuntimeState.PreStopping, "PreStopAsync 没有进入 PreStopping。");
        await cancelInput.CancellationObserved.Task;
        Check((await cancel.Completion).Business is TaskOutcome<int>.Canceled && cancel.State == TaskExecutionState.Canceled,
            "预退出未取消 CancelOnExit Task。");
        Check(!waitInput.CancellationObserved.Task.IsCompleted && !wait.Completion.IsCompleted,
            "预退出取消或提前完成了 Wait Task。");
        try {
            ScenarioWaitTask.Submit(host, new ScenarioTaskInput());
            throw new InvalidOperationException("Runtime 行为不符合规格：预退出仍接纳新根 Task。");
        }
        catch (RuntimeContractException) { }

        waitInput.Release.SetResult();
        RequireSuccess(await wait.Completion);
        host.CancelPreStop();
        Check(host.State == RuntimeState.Running, "CancelPreStop 未恢复 Running。");
        var resumedInput = new ScenarioTaskInput();
        resumedInput.Release.SetResult();
        RequireSuccess(await ScenarioWaitTask.Submit(host, resumedInput).Completion);
        Check(cancelInput.ExecutionCount == 1 && cancel.State == TaskExecutionState.Canceled, "撤销预退出重启了已取消 Task。");
        Console.WriteLine("检查点：CancelOnExit 已取消，Wait 正常完成；撤销预退出恢复 Running 并成功接纳新 Task。");
    }

    private static void RequireSuccess(TaskCompletion<int> completion) => Check(
        completion.Business is TaskOutcome<int>.Succeeded { Result: 42 } && completion.OutputFailure is null,
        $"预期业务成功且无输出故障，实际为 {completion}。");

    private static void Check(bool condition, string reason) {
        if (!condition) {
            throw new InvalidOperationException($"Runtime 行为不符合规格：{reason}");
        }
    }
}

internal sealed class ScenarioOutput : ITaskOutputSink<int> {
    private int finished;

    internal TaskCompletionSource Queued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Completing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool FailOnComplete { get; init; }
    internal bool Finished => Volatile.Read(ref finished) != 0;
    internal Exception ExpectedFailure { get; } = new InvalidOperationException("输出接收端故意失败。");

    public void ReportState(TaskExecutionState state) {
        if (state == TaskExecutionState.Queued) {
            Queued.TrySetResult();
        }
    }

    public void ReportProgress(TaskProgress progress) { }
    public void ReportLog(TaskLogEntry entry) { }

    public async ValueTask CompleteAsync(TaskOutcome<int> outcome) {
        Completing.SetResult();
        await Release.Task;
        Volatile.Write(ref finished, 1);
        if (FailOnComplete) {
            throw ExpectedFailure;
        }
    }
}

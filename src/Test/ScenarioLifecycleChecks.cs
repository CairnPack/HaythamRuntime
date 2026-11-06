using Haytham.Runtime;
using Haytham.Runtime.Generated;
using Haytham.Runtime.Work;
using Test.Storage;

namespace Test;

internal static partial class ScenarioRunner {
    private static async Task RunLifecycleScenarioAsync(string name) {
        var settings = new ScenarioSettings {
            VetoFirstStop = name == "stop-decision",
            StopDecision = name == "stop-decision" ? new StopDecisionProbe() : null,
            FailOnStarting = name == "start-rollback",
            MergeErrors = name == "merge-errors",
            Lifecycle = name is "start-rollback" or "merge-errors" ? new LifecycleProbe() : null,
            Starting = name == "repeat-calls" ? new StartingProbe() : null,
            ContinuousRetry = name == "continuous-retry" ? new ContinuousRetryProbe() : null,
            CleanupFailure = name == "cleanup-failure" ? new CleanupFailureProbe() : null,
            ContinuousStopSignals = name == "continuous-stop-signals" ? new ContinuousStopSignalsProbe() : null
        };
        var capabilities = new RuntimeCapabilities().Add<TextWriter>(TextWriter.Null).Add(settings);
        var strategy = name == "kill-strategy" ? new ScenarioTerminationStrategy() : null;
        if (strategy is not null) {
            capabilities.Add<IRuntimeTerminationStrategy>(strategy);
        }

        var host = RuntimeApplication.Create(capabilities);
        switch (name) {
            case "start-rollback":
                await CheckStartRollbackAsync(host, settings.Lifecycle!);
                return;
            case "merge-errors":
                await CheckMergeErrorsAsync(host, settings.Lifecycle!);
                return;
            case "repeat-calls":
                await CheckRepeatCallsAsync(host, settings.Starting!, capabilities);
                return;
            case "kill":
            case "kill-strategy":
                await CheckKillAsync(host, strategy);
                return;
            case "continuous-retry":
                await CheckContinuousRetryAsync(host, settings.ContinuousRetry!);
                return;
            case "cleanup-failure":
                await CheckCleanupFailureAsync(host, settings.CleanupFailure!);
                return;
            case "continuous-stop-signals":
                await CheckContinuousStopSignalsAsync(host, settings.ContinuousStopSignals!);
                return;
        }

        await host.StartAsync();
        await CheckStopDecisionAsync(host, settings.StopDecision!);
        RequireStopped(host, await host.StopAsync());
    }

    private static async Task CheckStopDecisionAsync(RuntimeHost host, StopDecisionProbe probe) {
        var veto = await host.StopAsync();
        Check(veto.Kind == StopOutcomeKind.Vetoed && veto.Failures.Count == 0 && host.State == RuntimeState.Running,
            "停止否决未返回 Vetoed 并恢复 Running。");
        await CheckResumedTaskAsync(host);
        Console.WriteLine("检查点：Vetoed 后恢复 Running，继续提交 Task 成功。");

        var failed = await host.StopAsync();
        Check(failed.Kind == StopOutcomeKind.DecisionFailed && host.State == RuntimeState.Running
            && failed.Failures.Any(failure => ReferenceEquals(failure.Exception, probe.ExpectedFailure)),
            "停止判定异常未返回 DecisionFailed、保留原错并恢复 Running。");
        await CheckResumedTaskAsync(host);
        Console.WriteLine("检查点：DecisionFailed 后恢复 Running，继续提交 Task 成功；下一次正常停止。");
    }

    private static async Task CheckResumedTaskAsync(RuntimeHost host) {
        var input = new ScenarioTaskInput();
        input.Release.SetResult();
        RequireSuccess(await ScenarioWaitTask.Submit(host, input).Completion);
    }

    private static async Task CheckStartRollbackAsync(RuntimeHost host, LifecycleProbe probe) {
        try {
            await host.StartAsync();
            Check(false, "OnStarting 抛错后 StartAsync 没有失败。");
        }
        catch (RuntimeExecutionException error) {
            Check(error.Failures.Any(failure => ReferenceEquals(failure.Exception, probe.ExpectedFailure)),
                "启动失败未保留 OnStarting 的原始错误。");
        }

        var storage = new ModuleId("Test.Storage");
        Check(probe.Started.Contains(storage) && probe.Stopping.Count(module => module == storage) == 1,
            "已经启动的 Storage 模块没有恰好收到一次 OnStopping。");
        Check(probe.Loaded.SequenceEqual(new[] { storage, new ModuleId("Test") })
            && probe.Unloaded.SequenceEqual(probe.Loaded.AsEnumerable().Reverse()),
            "两个模块没有按依赖顺序加载，或未按实际加载的相反顺序卸载。");
        Check(host.State == RuntimeState.Stopped, "启动回滚完成后状态不是 Stopped。");
        RequireContract(() => { _ = host.StartAsync(); }, "启动失败回滚后再次 StartAsync");
        Console.WriteLine($"检查点：原始启动错误已保留，Storage 收到 OnStopping，卸载顺序 {string.Join(" → ", probe.Unloaded)}，最终 Stopped 且拒绝重启。");
    }

    private static async Task CheckMergeErrorsAsync(RuntimeHost host, LifecycleProbe probe) {
        try {
            await host.StartAsync();
            Check(false, "无效 Registry 没有使 StartAsync 失败。");
        }
        catch (RuntimeExecutionException error) {
            var configuration = error.Failures.Select(failure => failure.Exception).OfType<RuntimeConfigurationException>().SingleOrDefault();
            Check(configuration is not null, "启动错误中没有 Registry 合并的配置错误。");
            var problems = configuration!.Problems;
            Check(problems.Count == 2
                && problems.Any(problem => problem.Contains(typeof(ScenarioWaitTask).FullName!, StringComparison.Ordinal) && problem.Contains("重复登记", StringComparison.Ordinal))
                && problems.Any(problem => problem.Contains(typeof(UnregisteredScenarioEvent).FullName!, StringComparison.Ordinal) && problem.Contains("未定义", StringComparison.Ordinal)),
                $"没有一次列出重复 Task 和未登记事件两个问题：{string.Join("；", problems)}");
            foreach (var problem in problems) {
                Console.WriteLine($"检查点：合并错误：{problem}");
            }
        }

        Check(probe.Loaded.Count == 0, "Registry 合并失败仍执行了模块 OnLoading。");
        Check(host.State == RuntimeState.Stopped, "Registry 合并失败后没有回收到 Stopped。");
        Console.WriteLine("检查点：所有模块的 OnLoading 调用次数为 0。");
    }

    private static async Task CheckRepeatCallsAsync(RuntimeHost host, StartingProbe probe, RuntimeCapabilities capabilities) {
        var first = host.StartAsync();
        await probe.Entered.Task;
        try {
            Check(host.State == RuntimeState.Starting && !first.IsCompleted, "首个 StartAsync 未受控地停留在启动阶段。");
            RequireContract(() => { _ = host.StartAsync(); }, "首个 StartAsync 尚未完成时再次 StartAsync");
        }
        finally {
            probe.Release.TrySetResult();
        }

        await first;
        Check(host.State == RuntimeState.Running, "首个 StartAsync 未成功进入 Running。");
        RequireContract(host.CancelPreStop, "没有预退出时 CancelPreStop");
        RequireStopped(host, await host.StopAsync());
        RequireContract(() => { _ = host.StopAsync(); }, "Stopped 后再次 StopAsync");
        try {
            RuntimeApplication.Create(capabilities);
            Check(false, "第二次 Create 没有被拒绝。");
        }
        catch (RuntimeAlreadyCreatedException) {
            Console.WriteLine("检查点：第二次 Create 抛 RuntimeAlreadyCreatedException。");
        }
    }

    private static async Task CheckContinuousRetryAsync(RuntimeHost host, ContinuousRetryProbe probe) {
        var notification = new TaskCompletionSource<ContinuousWorkFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notificationCount = 0;
        host.ContinuousWorkFailed += failure => {
            Interlocked.Increment(ref notificationCount);
            notification.TrySetResult(failure);
        };
        await host.StartAsync();
        var initial = probe.InitialHandle;
        await initial.Ready;
        Check(!initial.Completion.IsCompleted && probe.RunCount == 1, "首轮 Ready 成功时工作已经结束或重复运行。");
        probe.Fail.SetResult();
        var failure = await notification.Task;
        var outcome = await initial.Completion;
        var storage = new ModuleId("Test.Storage");
        Check(failure.Module == storage && failure.WorkName == "retry-probe"
            && failure.Failure.Module == storage && ReferenceEquals(failure.Failure.Exception, probe.ExpectedFailure),
            "ContinuousWorkFailed 未提供正确的模块 Id、工作名或原始错误。");
        Check(outcome.Kind == ContinuousWorkOutcomeKind.Failed
            && outcome.Failures.Any(item => ReferenceEquals(item.Exception, probe.ExpectedFailure)),
            "持续工作的 Completion 未保留 Failed 和原始错误。");
        Check(host.State == RuntimeState.Running && probe.RunCount == 1, "持续工作故障改变了宿主运行状态或触发自动重试。");
        var saved = await SaveValueTask.Submit(host, new SaveValue("continuous-retry", "still-running")).Completion;
        Check(saved.Business is Haytham.Runtime.Tasks.TaskOutcome<bool>.Succeeded { Result: false } && saved.OutputFailure is null,
            "Storage 持续工作失败后，同模块 Task 未正常执行。");
        Console.WriteLine("检查点：故障通知包含 Test.Storage、retry-probe 和原始错误，同模块 Task 仍成功执行。");

        var retried = host.RetryContinuousWork(storage, "retry-probe");
        Check(!ReferenceEquals(initial, retried) && !ReferenceEquals(initial.Ready, retried.Ready)
            && !ReferenceEquals(initial.Completion, retried.Completion) && retried.Module == storage && retried.Name == initial.Name,
            "重试没有返回独立的新 handle、Ready 和 Completion。");
        await retried.Ready;
        var stopping = await probe.RetryStarted.Task;
        initial.RequestStop();
        Check(!stopping.IsCancellationRequested && !retried.Completion.IsCompleted && probe.RunCount == 2,
            "旧 handle 的 RequestStop 影响了新运行，或重试次数不为 1。");
        Check(ReferenceEquals(await initial.Completion, outcome), "重试改变了旧 handle 的结束结果。");
        Console.WriteLine("检查点：新运行 Ready 成功，旧 handle 请求停止不影响新运行。");

        var stop = host.StopAsync();
        await probe.StopRequested.Task;
        try {
            Check(!stop.IsCompleted && !retried.Completion.IsCompleted && !probe.Finished.Task.IsCompleted,
                "新运行尚未真正结束，宿主已经完成停止。");
        }
        finally {
            probe.ReleaseStop.TrySetResult();
        }

        RequireStopped(host, await stop);
        var stopped = await retried.Completion;
        Check(stopped.Kind == ContinuousWorkOutcomeKind.Stopped && stopped.Failures.Count == 0
            && probe.Finished.Task.IsCompletedSuccessfully && Volatile.Read(ref notificationCount) == 1 && probe.RunCount == 2,
            "宿主未等待新运行正常结束，或正常停止产生了故障通知或额外重试。");
        Console.WriteLine("检查点：宿主等待新运行实际结束，结果为 Stopped，故障通知总数为 1。");
    }

    private static async Task CheckKillAsync(RuntimeHost host, ScenarioTerminationStrategy? strategy) {
        RequireContract(host.Kill, "Created 状态调用 Kill");
        Check(strategy is null || strategy.CallCount == 0, "Created 状态的 Kill 调用了终止策略。");
        await host.StartAsync();
        try {
            host.Kill();
            Check(false, "Kill 在未结束进程的情况下正常返回。");
        }
        catch (RuntimeExecutionException error) {
            Check(error.Message.Contains("终止未完成", StringComparison.Ordinal) && error.Failures.Count > 0,
                "Kill 没有明确报告终止未完成及原因。");
            if (strategy is null) {
                Check(error.Failures.Any(failure => failure.Exception is InvalidOperationException
                    && failure.Message.Contains(nameof(IRuntimeTerminationStrategy), StringComparison.Ordinal)),
                    "缺少终止策略的错误没有指出 IRuntimeTerminationStrategy。");
                Console.WriteLine("检查点：缺少 IRuntimeTerminationStrategy，Kill 报告终止未完成。");
            }
            else {
                Check(strategy.CallCount == 1 && error.Failures.Any(failure => ReferenceEquals(failure.Exception, strategy.ExpectedFailure)),
                    "Kill 未恰好调用一次测试终止策略，或未保留专用异常。");
                Console.WriteLine("检查点：测试终止策略调用次数为 1，专用异常已保留，实际进程继续执行场景检查。");
            }
        }

        Check(host.State == RuntimeState.Running, "终止未完成却改变了宿主的 Running 状态。");
        RequireStopped(host, await host.StopAsync());
    }

    private static void RequireStopped(RuntimeHost host, StopOutcome outcome) => Check(
        outcome.Kind == StopOutcomeKind.Stopped && outcome.Failures.Count == 0 && host.State == RuntimeState.Stopped,
        $"场景收尾不是无故障的 Stopped：{outcome.Kind}，状态 {host.State}。");

    private static void RequireContract(Action action, string operation) {
        try {
            action();
            Check(false, $"{operation} 没有同步抛 RuntimeContractException。");
        }
        catch (RuntimeContractException) {
            Console.WriteLine($"检查点：{operation} 同步抛 RuntimeContractException。");
        }
    }
}

internal sealed class ScenarioTerminationException() : Exception("测试策略模拟进程已经结束。");

internal sealed class ScenarioTerminationStrategy : IRuntimeTerminationStrategy {
    internal int CallCount { get; private set; }
    internal ScenarioTerminationException ExpectedFailure { get; } = new();

    public void Terminate() {
        CallCount++;
        throw ExpectedFailure;
    }
}

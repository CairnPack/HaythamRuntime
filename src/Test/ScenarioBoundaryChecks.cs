using System.Collections.Concurrent;
using Haytham.Runtime;
using Haytham.Runtime.Tasks;
using Haytham.Runtime.Work;
using Test.Storage;

namespace Test;

internal static partial class ScenarioRunner {
    private static async Task CheckCleanupFailureAsync(RuntimeHost host, CleanupFailureProbe probe) {
        await host.StartAsync();
        var stop = await host.StopAsync();
        Check(!probe.ReleaseExecuted, "依赖 Storage 的 Test 模块卸载失败，Storage 的释放动作却已执行。");
        Console.WriteLine("检查点 3：Test 卸载失败，它依赖的 Storage 没有被释放。");
        Check(stop.Kind == StopOutcomeKind.CleanupFailed && host.State == RuntimeState.Hanging
            && stop.Failures.Any(failure => ReferenceEquals(failure.Exception, probe.ExpectedFailure)),
            $"清理失败未返回 CleanupFailed、Hanging 并保留原始异常：结果={stop.Kind}，状态={host.State}，故障数={stop.Failures.Count}。");
        Console.WriteLine("检查点 4：Stop 返回 CleanupFailed，宿主为 Hanging，Failures 保留 Test 卸载的原始异常。");
    }

    private static async Task CheckTaskReportBoundaryAsync(RuntimeHost host) {
        var probe = new ReportBoundaryProbe();
        RequireSuccess(await ScenarioReportTask.Submit(host, probe, output: probe).Completion);
        Check(probe.Records.SequenceEqual(new[] { "调用前", "已接收", "调用后" }),
            $"进度报告未同步交给接收端：实际顺序={string.Join(" → ", probe.Records)}。");
        Console.WriteLine("检查点 5：进度报告顺序为 调用前 → 已接收 → 调用后。");

        var received = probe.Records.Count;
        var rejected = false;
        try {
            probe.SavedOutput.ReportProgress(new TaskProgress("完成后报告"));
        }
        catch (RuntimeContractException) {
            rejected = true;
        }

        Check(rejected && probe.Records.Count == received,
            $"根 Completion 完成后，保存的 TaskOutput 再次报告没有被拒绝，或仍送到了接收端：拒绝={rejected}，报告前记录数={received}，报告后记录数={probe.Records.Count}。");
        Console.WriteLine("检查点 6：根 Completion 完成后再次报告抛出 RuntimeContractException，接收端没有收到。");
    }

    private static async Task CheckExitPolicySnapshotAsync(RuntimeHost host, ScenarioSettings settings) {
        settings.SnapshotExitPolicy = TaskExitPolicy.CancelOnExit;
        var input = new ScenarioTaskInput { Behavior = CancellationBehavior.Complete };
        var run = ScenarioPolicyTask.Submit(host, input);
        await input.Started.Task;
        await host.PreStopAsync();
        Check(!input.CancellationObserved.Task.IsCompleted && !run.Completion.IsCompleted,
            $"登记时为 Wait 的根 Task 在配置改为 CancelOnExit 后被预退出取消或提前完成：收到取消={input.CancellationObserved.Task.IsCompleted}，任务完成={run.Completion.IsCompleted}。");
        input.Release.SetResult();
        RequireSuccess(await run.Completion);
        Check(!input.CancellationObserved.Task.IsCompleted, "登记时为 Wait 的根 Task 在预退出后继续执行期间收到了取消。");
        Console.WriteLine("检查点 7：登记时的 Wait 策略不受后续配置修改影响，PreStopAsync 未取消根 Task，业务继续完成并成功。");
    }

    private static async Task CheckContinuousStopSignalsAsync(RuntimeHost host, ContinuousStopSignalsProbe probe) {
        await host.StartAsync();
        Check(probe.Handles.Count == 2 && probe.Handles.All(handle => handle.Ready.IsCompletedSuccessfully && !handle.Completion.IsCompleted),
            "两项持续工作未同时就绪并保持运行。");
        Console.WriteLine("检查点：两项持续工作均已 Ready，每项收到停止信号后等待另一项也收到信号。");
        RequireStopped(host, await host.StopAsync());
        var outcomes = await Task.WhenAll(probe.Handles.Select(handle => handle.Completion));
        Check(probe.StopSignals.All(signal => signal.Task.IsCompletedSuccessfully)
            && outcomes.All(outcome => outcome.Kind == ContinuousWorkOutcomeKind.Stopped && outcome.Failures.Count == 0),
            "Stop 未向两项持续工作都发送停止信号并等待它们正常结束。");
        Console.WriteLine("检查点 8：两项持续工作都收到停止信号后正常结束，Stop 已完成。");
    }
}

internal sealed class ReportBoundaryProbe : ITaskOutputSink<int> {
    internal ConcurrentQueue<string> Records { get; } = new();
    internal TaskOutput SavedOutput { get; set; } = null!;

    public void ReportState(TaskExecutionState state) { }
    public void ReportProgress(TaskProgress progress) => Records.Enqueue("已接收");
    public void ReportLog(TaskLogEntry entry) { }
    public ValueTask CompleteAsync(TaskOutcome<int> outcome) => ValueTask.CompletedTask;
}

using System.Text.Json.Serialization;
using Haytham.Runtime;
using Haytham.Runtime.Calls;
using Haytham.Runtime.Events;
using Haytham.Runtime.Inputs;
using Haytham.Runtime.Modules;
using Haytham.Runtime.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Test;

internal static partial class ScenarioRunner {
    private static async Task RunInputScenarioAsync(string name) {
        var probe = new InputProbe {
            RegisterInputs = name switch {
                "input-missing-input-metadata" => MissingInputInputs.Register,
                "input-missing-reply-metadata" => MissingReplyInputs.Register,
                _ => ProbeInputs.Register
            }
        };
        var definitions = new List<ModuleDefinition> {
            new("Inputs", "1.0.0", [], "输入验证", [], [], _ => new InputModule(probe))
        };
        if (name == "input-merge-errors") {
            definitions.Add(new("Duplicate", "1.0.0", [], "重名输入验证", [], [], _ => new InputModule(new InputProbe())));
        }

        var host = RuntimeHost.Create(definitions,
            new RuntimeCapabilities().Add<ILoggerFactory>(NullLoggerFactory.Instance));
        Check(host.Call<ProbeCall, NumberInput, NumberReply>(new(0)) is CallOutcome<NumberReply>.NotAccepting,
            "Created 不应接收同步调用。");
        if (name == "input-merge-errors") {
            await CheckInputMergeAsync(host, probe);
            return;
        }
        if (name is "input-missing-input-metadata" or "input-missing-reply-metadata") {
            await CheckMissingJsonMetadataAsync(host, probe, name == "input-missing-input-metadata");
            return;
        }

        await host.StartAsync();
        switch (name) {
            case "input-dispatch":
                await CheckInputDispatchAsync(host, probe);
                break;
            case "input-call-stop":
                await CheckInputCallsAsync(host, probe);
                break;
            case "input-cleanup":
                await CheckRegisteredCleanupAsync(host, probe);
                break;
            case "input-output-failure":
                await CheckInputOutputFailureAsync(host, probe);
                break;
        }
    }

    private static async Task CheckInputMergeAsync(RuntimeHost host, InputProbe probe) {
        RuntimeExecutionException? failure = null;
        try {
            await host.StartAsync();
        }
        catch (RuntimeExecutionException error) {
            failure = error;
        }

        Check(failure?.Failures.Any(item => item.Exception is RuntimeConfigurationException configuration
            && configuration.Problems.Any(problem => problem.Contains("输入 probe.task 重复登记"))) == true,
            "跨模块重名输入没有在 Registry 合并时报错。");
        Check(!probe.Loaded, "输入合并失败后执行了 OnLoading。");
        Console.WriteLine("检查点：跨模块重名输入在合并时失败，没有加载模块。");
    }

    private static async Task CheckMissingJsonMetadataAsync(RuntimeHost host, InputProbe probe, bool missingInput) {
        RuntimeExecutionException? failure = null;
        try {
            await host.StartAsync();
        }
        catch (RuntimeExecutionException error) {
            failure = error;
        }

        var typeName = missingInput ? nameof(NumberInput) : nameof(NumberReply);
        var contextName = missingInput ? nameof(MissingInputJson) : nameof(MissingReplyJson);
        Check(failure?.Failures.Any(item => item.Exception is RuntimeContractException contract
            && contract.Message.Contains(typeName) && contract.Message.Contains(contextName)) == true,
            "缺少 JSON 元数据没有在启动时报告类型和上下文。");
        Check(!probe.Loaded, "缺少 JSON 元数据后仍进入了模块加载。");
        Console.WriteLine("检查点：输入或回复缺少 JSON 元数据时，启动在登记阶段失败并指出类型和上下文。");
    }

    private static async Task CheckInputDispatchAsync(RuntimeHost host, InputProbe probe) {
        var output = new InputOutput();
        CheckStartRejection(host.StartInput("missing", "{}", output), InputRejection.UnknownName);
        CheckCallRejection(host.CallInput("missing", "{}"), InputRejection.UnknownName);
        CheckStartRejection(host.StartInput("probe.call", "{}", output), InputRejection.WrongKind);
        CheckCallRejection(host.CallInput("probe.task", "{}"), InputRejection.WrongKind);
        foreach (var json in new[] { "{", "null", "{\"Value\":\"不是整数\"}" }) {
            CheckStartRejection(host.StartInput("probe.task", json, output), InputRejection.InvalidInput);
            CheckCallRejection(host.CallInput("probe.call", json), InputRejection.InvalidInput);
        }

        var manifest = ProbeInputs.Manifest;
        Check(manifest.Count == 2 && manifest[0] == new InputManifestEntry("probe.task", InputKind.Task,
            typeof(NumberInput), typeof(NumberReply), "Task") && manifest[1].Kind == InputKind.Call,
            "输入 Manifest 内容不正确。");
        Check(host.CallInput("probe.call", "{\"Value\":7}") is InputCallResult.Completed { ReplyJson: "{\"Value\":7}" },
            "同步输入没有使用源生成 JSON 元数据返回成功值。");
        Check(host.CallInput("probe.call", "{\"Value\":-1}") is InputCallResult.BusinessFailed { Failure.Code: 42 },
            "同步映射的业务失败被当成成功。");

        var failedOutput = new InputOutput();
        await Start(host, -1, failedOutput).Completion;
        Check(failedOutput.Outcome is InputOutcome.BusinessFailed { Failure.Code: 42 }, "Task 映射的业务失败被当成成功。");
        Console.WriteLine("检查点：未知名字、两向种类错误、非法及 null JSON 均返回拒绝，成功和业务失败分别返回。");

        await CheckInputQueueAsync(host, probe);
        await host.PreStopAsync();
        CheckStartRejection(host.StartInput("probe.task", "{}", output), InputRejection.NotAccepting);
        Check(host.CallInput("probe.call", "{}") is InputCallResult.Completed, "预退出时同步输入被拒绝。");
        Check(host.Call<ProbeCall, NumberInput, NumberReply>(new(0)) is CallOutcome<NumberReply>.Completed,
            "预退出时类型化同步调用被拒绝。");
        RequireStopped(host, await host.StopAsync());
        CheckCallRejection(host.CallInput("probe.call", "{}"), InputRejection.NotAccepting);
        CheckStartRejection(host.StartInput("probe.task", "{}", output), InputRejection.NotAccepting);
    }

    private static async Task CheckInputQueueAsync(RuntimeHost host, InputProbe probe) {
        var firstOutput = new InputOutput { HoldCompletion = true };
        var first = Start(host, 1, firstOutput);
        await firstOutput.Completing.Task;
        var secondOutput = new InputOutput();
        var second = Start(host, 2, secondOutput);
        await secondOutput.Queued.Task;
        Check(probe.Executed.SequenceEqual(new[] { -1, 1 }), "第一项输出未入队时，第二项已执行业务。");
        Check(!first.Completion.IsCompleted && first.Id != second.Id, "输入句柄未代表独立运行或输出尚未完成就完成了句柄。");
        firstOutput.Release.SetResult();
        await Task.WhenAll(first.Completion, second.Completion);
        Check(probe.Executed.SequenceEqual(new[] { -1, 1, 2 })
            && secondOutput.Outcome is InputOutcome.Succeeded { ReplyJson: "{\"Value\":2}" }, "同键输入没有顺序完成。");
        Console.WriteLine("检查点：同键输入等待前项完成输出后才执行业务。");
    }

    private static async Task CheckInputOutputFailureAsync(RuntimeHost host, InputProbe probe) {
        var expected = new InvalidOperationException("结果映射故意失败。");
        ProbeInputs.BeforeMap = () => throw expected;
        var firstOutput = new InputOutput { HoldCompletion = true };
        var first = Start(host, 1, firstOutput);
        await Task.WhenAny(first.Completion, firstOutput.Completing.Task);
        Check(firstOutput.Outcome is InputOutcome.Failed failed && failed.Failures.Count == 1
            && failed.Failures[0].Kind == RuntimeFailureKind.Output
            && ReferenceEquals(failed.Failures[0].Exception, expected), "结果映射失败后，宿主没有收到带原异常的 Failed 完成事件。");

        ProbeInputs.BeforeMap = null;
        var secondOutput = new InputOutput();
        var second = Start(host, 2, secondOutput);
        await secondOutput.Queued.Task;
        Check(!first.Completion.IsCompleted && probe.Executed.SequenceEqual(new[] { 1 }),
            "失败完成事件还未入队，同键的下一项已开始执行。");
        firstOutput.Release.SetResult();
        var outputFailure = await first.Completion;
        Check(outputFailure is { Kind: RuntimeFailureKind.Output } && ReferenceEquals(outputFailure.Exception, expected),
            "映射原异常没有保留为输入运行句柄的输出失败。");
        await second.Completion;
        Check(secondOutput.Outcome is InputOutcome.Succeeded && probe.Executed.SequenceEqual(new[] { 1, 2 }),
            "映射失败后，同键的下一项没有正常完成。");
        RequireStopped(host, await host.StopAsync());
        Console.WriteLine("检查点：映射失败仍通知宿主，并保留输出失败；完成通知入队后同键继续执行。");
    }

    private static async Task CheckInputCallsAsync(RuntimeHost host, InputProbe probe) {
        var entered = NewSignal();
        using var release = new ManualResetEventSlim();
        probe.CallAction = input => {
            entered.SetResult();
            release.Wait();
            return new NumberReply(input.Value);
        };
        var call = Task.Run(() => host.CallInput("probe.call", "{}"));
        await entered.Task;
        probe.VetoFirst = true;
        var stop = host.StopAsync();
        Check(host.State == RuntimeState.StopRequested && !stop.IsCompleted && probe.StopRequests == 0,
            "已有同步调用未返回，Stop 就已派发停止判定。");
        CheckCallRejection(host.CallInput("probe.call", "{}"), InputRejection.NotAccepting);
        Check(host.Call<ProbeCall, NumberInput, NumberReply>(new(0)) is CallOutcome<NumberReply>.NotAccepting,
            "StopRequested 仍接收类型化调用。");
        release.Set();
        Check(await call is InputCallResult.Completed, "已接收的同步输入没有完成。");
        Check((await stop).Kind == StopOutcomeKind.Vetoed && host.State == RuntimeState.Running,
            "停止否决后没有回到 Running。");
        probe.CallAction = input => new NumberReply(input.Value);
        Check(host.CallInput("probe.call", "{}") is InputCallResult.Completed, "否决后同步输入未恢复。");

        await CheckCallMappingWaitAsync(host, probe);
        Check(host.Call<ProbeCall, NumberInput, NumberReply>(new(3)) is CallOutcome<NumberReply>.Completed,
            "判定失败后类型化调用未恢复。");
        CheckCallThrows(host, probe);
        RequireStopped(host, await host.StopAsync());
        Console.WriteLine("检查点：停止等已进入的同步调用，否决及判定失败均恢复接收；处理器异常原样传出。");
    }

    private static async Task CheckCallMappingWaitAsync(RuntimeHost host, InputProbe probe) {
        var entered = NewSignal();
        using var release = new ManualResetEventSlim();
        ProbeInputs.BeforeMap = () => {
            entered.SetResult();
            release.Wait();
        };
        var call = Task.Run(() => host.CallInput("probe.call", "{}"));
        await entered.Task;
        await host.PreStopAsync();
        probe.FailNextDecision = true;
        var stop = host.StopAsync();
        Check(!stop.IsCompleted && probe.StopRequests == 1, "同步输入还在映射结果，Stop 就已派发停止判定。");
        release.Set();
        Check(await call is InputCallResult.Completed, "停止等待期间同步输入映射未完成。");
        ProbeInputs.BeforeMap = null;
        Check((await stop).Kind == StopOutcomeKind.DecisionFailed && host.State == RuntimeState.PreStopping,
            "判定失败后没有回到 PreStopping。");
    }

    private static void CheckCallThrows(RuntimeHost host, InputProbe probe) {
        var expected = new InvalidOperationException("同步处理器故意失败。");
        probe.CallAction = _ => throw expected;
        Exception? observed = null;
        try {
            host.CallInput("probe.call", "{}");
        }
        catch (InvalidOperationException error) {
            observed = error;
        }
        Check(ReferenceEquals(expected, observed), "同步处理器异常未原样传给调用方。");
        probe.CallAction = input => new NumberReply(input.Value);
    }

    private static async Task CheckRegisteredCleanupAsync(RuntimeHost host, InputProbe probe) {
        var firstEntered = NewSignal();
        var releaseFirst = NewSignal();
        var secondEntered = NewSignal();
        var releaseSecond = NewSignal();
        var stoppingCleanupEntered = NewSignal();
        var releaseStoppingCleanup = NewSignal();
        var expected = new InvalidOperationException("后台清理故意失败。");
        var marker = new AsyncLocal<string?> { Value = "调用方上下文" };
        await host.PreStopAsync();
        probe.Port.RegisterCleanup(async () => {
            Check(marker.Value is null, "清理继承了调用方 ExecutionContext。");
            firstEntered.SetResult();
            await releaseFirst.Task;
            probe.Port.RegisterCleanup(async () => {
                secondEntered.SetResult();
                await releaseSecond.Task;
                return CleanupResult.Done;
            });
            return CleanupResult.Done;
        });
        await firstEntered.Task;
        probe.OnStopping = () => probe.Port.RegisterCleanup(async () => {
            stoppingCleanupEntered.SetResult();
            await releaseStoppingCleanup.Task;
            return CleanupResult.Done;
        });
        probe.Port.RegisterCleanup(() => new ValueTask<CleanupResult>(CleanupResult.Failed("返回清理失败")));
        probe.Port.RegisterCleanup(() => throw expected);
        var stop = host.StopAsync();
        Check(host.State == RuntimeState.Draining && !stop.IsCompleted, "Stop 未等待预退出登记的清理。");
        releaseFirst.SetResult();
        await secondEntered.Task;
        Check(!probe.Stopping && !stop.IsCompleted, "Draining 只等了第一批快照，漏等后来登记的清理。");
        releaseSecond.SetResult();
        await stoppingCleanupEntered.Task;
        Check(host.State == RuntimeState.Stopping && !probe.Unloaded && !stop.IsCompleted,
            "OnStopping 登记的清理未完成就开始卸载。");
        releaseStoppingCleanup.SetResult();
        var outcome = await stop;
        Check(outcome.Kind == StopOutcomeKind.Stopped && host.State == RuntimeState.Stopped && probe.Unloaded,
            "登记清理失败不应阻塞模块卸载。");
        Check(outcome.Failures.Count == 2 && outcome.Failures.All(failure => failure.Kind == RuntimeFailureKind.Cleanup)
            && outcome.Failures.Any(failure => failure.Message == "返回清理失败")
            && outcome.Failures.Any(failure => ReferenceEquals(failure.Exception, expected)),
            "返回失败或抛出异常的清理没有完整进入 StopOutcome。");
        var rejected = false;
        try {
            probe.Port.RegisterCleanup(() => new ValueTask<CleanupResult>(CleanupResult.Done));
        }
        catch (RuntimeContractException) {
            rejected = true;
        }
        Check(rejected, "停止后仍接收清理登记。");
        Console.WriteLine("检查点：两次循环排空均等待新清理，清理失败保留到停止结果且继续卸载。");
    }

    private static InputRun Start(RuntimeHost host, int value, InputOutput output) {
        var result = host.StartInput("probe.task", $"{{\"Value\":{value}}}", output);
        Check(result is InputStartResult.Started, "合法 Task 输入未被接收。");
        return ((InputStartResult.Started)result).Run;
    }

    private static void CheckStartRejection(InputStartResult result, InputRejection expected) =>
        Check(result is InputStartResult.Rejected rejected && rejected.Rejection == expected && rejected.Message.Length > 0,
            $"Task 输入拒绝结果不符，预期 {expected}，实际 {result}。");

    private static void CheckCallRejection(InputCallResult result, InputRejection expected) =>
        Check(result is InputCallResult.Rejected rejected && rejected.Rejection == expected && rejected.Message.Length > 0,
            $"同步输入拒绝结果不符，预期 {expected}，实际 {result}。");

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class InputProbe {
    internal Action<Registry> RegisterInputs { get; init; } = ProbeInputs.Register;
    internal ModuleWorkPort Port { get; set; } = null!;
    internal bool Loaded { get; set; }
    internal bool Stopping { get; set; }
    internal bool Unloaded { get; set; }
    internal bool VetoFirst { get; set; }
    internal bool FailNextDecision { get; set; }
    internal int StopRequests { get; set; }
    internal Action? OnStopping { get; set; }
    internal List<int> Executed { get; } = [];
    internal Func<NumberInput, NumberReply> CallAction { get; set; } = input => new(input.Value);
}

internal sealed class InputModule(InputProbe probe) : IRuntimeModule {
    public void Connect(ModuleWiring wiring) {
        probe.Port = wiring.RootWork;
        wiring.SubscribeRegister((e, _) => {
            e.Registry.RegisterTask(new ProbeTask(probe));
            e.Registry.RegisterCall(new ProbeCall(probe));
            probe.RegisterInputs(e.Registry);
            e.Registry.Subscribe<RuntimeEvents.OnLoading>((_, _) => {
                probe.Loaded = true;
                return ValueTask.CompletedTask;
            });
            e.Registry.Subscribe<RuntimeEvents.OnStopRequested>(OnStopRequested);
            e.Registry.Subscribe<RuntimeEvents.OnStopping>((_, _) => {
                probe.Stopping = true;
                probe.OnStopping?.Invoke();
                return ValueTask.CompletedTask;
            });
            e.Registry.Subscribe<RuntimeEvents.OnUnloading>((_, _) => {
                probe.Unloaded = true;
                return ValueTask.CompletedTask;
            });
            return ValueTask.CompletedTask;
        });
    }

    private ValueTask OnStopRequested(RuntimeEvents.OnStopRequested e, EventContext context) {
        probe.StopRequests++;
        if (probe.FailNextDecision) {
            probe.FailNextDecision = false;
            throw new InvalidOperationException("输入场景停止判定故意失败。");
        }
        if (probe.VetoFirst && probe.StopRequests == 1) {
            e.Cancel();
        }
        return ValueTask.CompletedTask;
    }
}

internal sealed record NumberInput(int Value);
internal sealed record NumberReply(int Value);

internal sealed partial class ProbeTask(InputProbe probe) : RuntimeTask<NumberInput, NumberReply> {
    public override TaskExitPolicy ExitPolicy => TaskExitPolicy.Wait;
    protected override ValueTask<NumberReply> ExecuteAsync(TaskEvent<NumberInput, NumberReply> e) {
        probe.Executed.Add(e.Input.Value);
        e.Output.ReportProgress(new TaskProgress("输入"));
        e.Output.ReportLog(new TaskLogEntry("已执行"));
        return new(new NumberReply(e.Input.Value));
    }
}

internal sealed class ProbeCall(InputProbe probe) : RuntimeCall<NumberInput, NumberReply> {
    protected override NumberReply Invoke(NumberInput input) => probe.CallAction(input);
}

[InputCatalog(typeof(ProbeJson))]
internal static partial class ProbeInputs {
    internal static Action? BeforeMap { get; set; }
    public static readonly TaskInput<ProbeTask, NumberInput, NumberReply, NumberReply> Task =
        new(name: "probe.task", queueKey: static _ => "shared", mapResult: Map);
    public static readonly CallInput<ProbeCall, NumberInput, NumberReply, NumberReply> Call =
        new(name: "probe.call", mapResult: Map);

    private static InputResult<NumberReply> Map(NumberReply result) {
        BeforeMap?.Invoke();
        return result.Value < 0
            ? new InputResult<NumberReply>.Failure(new InputFailure(42, "业务失败"))
            : new InputResult<NumberReply>.Success(result);
    }
}

[JsonSerializable(typeof(NumberInput))]
[JsonSerializable(typeof(NumberReply))]
internal partial class ProbeJson : JsonSerializerContext;

[InputCatalog(typeof(MissingInputJson))]
internal static partial class MissingInputInputs {
    public static readonly CallInput<ProbeCall, NumberInput, NumberReply, NumberReply> Call =
        new(name: "missing.input", mapResult: static reply => new InputResult<NumberReply>.Success(reply));
}

[JsonSerializable(typeof(NumberReply))]
internal partial class MissingInputJson : JsonSerializerContext;

[InputCatalog(typeof(MissingReplyJson))]
internal static partial class MissingReplyInputs {
    public static readonly TaskInput<ProbeTask, NumberInput, NumberReply, NumberReply> Task =
        new(name: "missing.reply", mapResult: static reply => new InputResult<NumberReply>.Success(reply));
}

[JsonSerializable(typeof(NumberInput))]
internal partial class MissingReplyJson : JsonSerializerContext;

internal sealed class InputOutput : IInputOutput {
    internal bool HoldCompletion { get; init; }
    internal InputOutcome? Outcome { get; private set; }
    internal TaskCompletionSource Queued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Completing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ReportState(TaskExecutionState state) {
        if (state == TaskExecutionState.Queued) {
            Queued.SetResult();
        }
    }
    public void ReportProgress(TaskProgress progress) { }
    public void ReportLog(TaskLogEntry entry) { }
    public async ValueTask CompleteAsync(InputOutcome outcome) {
        Outcome = outcome;
        Completing.SetResult();
        if (HoldCompletion) {
            await Release.Task;
        }
    }
}

using Haytham.Runtime;
using Haytham.Runtime.Generated;
using Haytham.Runtime.Tasks;
using Test;
using Test.Storage;

var capabilities = new RuntimeCapabilities().Add<TextWriter>(Console.Out);
var host = RuntimeApplication.Create(capabilities);
Console.WriteLine($"state: {host.State}");

await host.StartAsync();
Console.WriteLine($"state: {host.State}");

var first = host.Submit<InstallTask, InstallRequest, InstallResult>(new InstallRequest("demo-pack"),
    queueKey: "install", output: new ConsoleTaskOutput<InstallResult>("first"));
var second = host.Submit<InstallTask, InstallRequest, InstallResult>(new InstallRequest("demo-pack-2"),
    queueKey: "install", output: new ConsoleTaskOutput<InstallResult>("second"));
foreach (var completion in await Task.WhenAll(first.Completion, second.Completion)) {
    Console.WriteLine($"install result: {GetResult(completion)}");
}

var save = host.Submit<SaveValueTask, SaveValue, bool>(new SaveValue("pack", "demo-pack"));
Console.WriteLine($"save result: replaced = {GetResult(await save.Completion)}");

var firstStop = await host.StopAsync();
Console.WriteLine($"stop: {firstStop.Kind}, state: {host.State}");
save = host.Submit<SaveValueTask, SaveValue, bool>(new SaveValue("pack", "demo-pack-2"));
Console.WriteLine($"save result after veto: replaced = {GetResult(await save.Completion)}");

var secondStop = await host.StopAsync();
Console.WriteLine($"stop: {secondStop.Kind}, state: {host.State}");

try {
    RuntimeApplication.Create(capabilities);
    throw new Exception("第二次 Create 应当被拒绝。");
}
catch (RuntimeAlreadyCreatedException error) {
    Console.WriteLine($"second create rejected: {error.Message}");
}

static TResult GetResult<TResult>(TaskCompletion<TResult> completion) {
    if (completion.OutputFailure is { } outputFailure) {
        throw new RuntimeExecutionException("样例输出失败。", [outputFailure]);
    }

    return completion.Business switch {
        TaskOutcome<TResult>.Succeeded success => success.Result,
        TaskOutcome<TResult>.Failed failure => throw new RuntimeExecutionException("样例 Task 失败。", failure.Failures),
        TaskOutcome<TResult>.Canceled => throw new OperationCanceledException(),
        _ => throw new Exception("未知 Task 结果。")
    };
}

sealed class ConsoleTaskOutput<TResult>(string name) : ITaskOutputSink<TResult> {
    public void ReportState(TaskExecutionState state) => Console.WriteLine($"[{name}] state: {state}");
    public void ReportProgress(TaskProgress progress) =>
        Console.WriteLine($"[{name}] progress: {progress.Phase} {progress.Done}/{progress.Total}");
    public void ReportLog(TaskLogEntry entry) => Console.WriteLine($"[{name}] {entry.Message}");

    public async ValueTask CompleteAsync(TaskOutcome<TResult> outcome) {
        await Task.Delay(25);
        Console.WriteLine($"[{name}] final output: {outcome}");
    }
}

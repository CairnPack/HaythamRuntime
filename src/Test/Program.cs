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

var install = host.Submit<InstallTask, InstallRequest, InstallResult>(new InstallRequest("demo-pack"));
Console.WriteLine($"install result: {GetResult(await install.Completion)}");

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

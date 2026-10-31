using Haytham.Runtime;
using Haytham.Runtime.Generated;
using Test;
using Test.Storage;

// 能力表里放模块构造函数需要的对象；这里两个模块都只需要一个日志输出。
var capabilities = new RuntimeCapabilities().Add<TextWriter>(Console.Out);

// RuntimeApplication 由生成器根据引用的模块包生成，模块已按依赖排好序。
var runtime = RuntimeApplication.Create(capabilities);
Console.WriteLine($"state: {runtime.State}");

await runtime.StartAsync();
Console.WriteLine($"state: {runtime.State}");

var install = await runtime.RunTaskAsync<InstallRequest, InstallResult>(new InstallRequest("demo-pack"));
Console.WriteLine($"install result: {install}");

var replaced = await runtime.RunTaskAsync<SaveValue, bool>(new SaveValue("pack", "demo-pack"));
Console.WriteLine($"save result: replaced = {replaced}");

// 第一次停止请求被 TestModule 否决，Runtime 继续运行并接收 Task。
Console.WriteLine($"stop: {await runtime.RequestStopAsync()}, state: {runtime.State}");
replaced = await runtime.RunTaskAsync<SaveValue, bool>(new SaveValue("pack", "demo-pack-2"));
Console.WriteLine($"save result after veto: replaced = {replaced}");

Console.WriteLine($"stop: {await runtime.RequestStopAsync()}, state: {runtime.State}");

// 同一进程不能创建第二个 RuntimeHost。
try {
    RuntimeApplication.Create(capabilities);
}
catch (InvalidOperationException error) {
    Console.WriteLine($"second create rejected: {error.Message}");
}

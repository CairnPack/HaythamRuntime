using Haytham.Runtime.Events;
using Haytham.Runtime.Modules;
using Haytham.Runtime.Tasks;
using Test.Storage;

namespace Test;

public sealed record InstallRequest(string PackName);

public sealed record InstallResult(string PackName, int Steps);

/// <summary>
/// 手动验证用的模块，依赖 Test.Storage：订阅 Storage 的事件，登记一个安装 Task，并否决第一次停止请求。
/// </summary>
[RuntimeModule]
public sealed class TestModule(TextWriter log) : IRuntimeModule {

    private bool vetoed;

    public void Subscribe(EventSubscriber events) => events.Subscribe<RegisterEvent>(OnRegister);

    private ValueTask OnRegister(RegisterEvent e, CancellationToken cancellationToken) {
        e.Registry.Subscribe<RuntimeEvents.OnLoad>((load, _) => Log($"[{load.ModuleId}] load"));
        e.Registry.Subscribe<RuntimeEvents.OnStart>((start, _) => Log($"[{start.ModuleId}] start"));
        e.Registry.Subscribe<RuntimeEvents.OnStopRequested>(OnStopRequested);
        e.Registry.Subscribe<RuntimeEvents.OnStopping>((stopping, _) => Log($"[{stopping.ModuleId}] stopping"));
        e.Registry.Subscribe<RuntimeEvents.OnUnload>((unload, _) => Log($"[{unload.ModuleId}] unload"));
        e.Registry.Subscribe<StorageChangedEvent>((changed, _) => Log($"[{e.ModuleId}] saw storage change: {changed.Key}"));
        e.Registry.AddTask<InstallRequest, InstallResult>(OnInstall);
        return Log($"[{e.ModuleId}] registered");
    }

    private ValueTask OnStopRequested(RuntimeEvents.OnStopRequested e, CancellationToken cancellationToken) {
        if (vetoed) {
            return ValueTask.CompletedTask;
        }

        vetoed = true;
        e.Cancel();
        return Log("[Test] veto the first stop request");
    }

    /// <summary>
    /// 在本次信道里开始两项短程工作后直接返回，Runtime 会等它们结束再把结果交给调用方。
    /// </summary>
    private ValueTask OnInstall(TaskEvent<InstallRequest, InstallResult> e, CancellationToken cancellationToken) {
        for (var i = 1; i <= 2; i++) {
            var step = i;
            e.Run(async token => {
                await Task.Delay(50 * step, token);
                await log.WriteLineAsync($"[Test] install {e.Input.PackName}: step {step} done");
            });
        }

        e.SetResult(new InstallResult(e.Input.PackName, Steps: 2));
        return ValueTask.CompletedTask;
    }

    private ValueTask Log(string message) {
        log.WriteLine(message);
        return ValueTask.CompletedTask;
    }

}

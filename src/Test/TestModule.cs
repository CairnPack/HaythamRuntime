using Haytham.Runtime.Events;
using Haytham.Runtime.Modules;
using Haytham.Runtime.Tasks;
using Test.Storage;

namespace Test;

public sealed record InstallRequest(string PackName);
public sealed record InstallResult(string PackName, int Steps);

[RuntimeModule]
public sealed class TestModule(TextWriter log, ScenarioSettings settings) : IRuntimeModule {
    private bool vetoed;

    public void Connect(ModuleWiring wiring) => wiring.SubscribeRegister(OnRegistering);

    private ValueTask OnRegistering(RuntimeEvents.OnRegistering e, EventContext context) {
        e.Registry.Subscribe<RuntimeEvents.OnLoading>((loading, _) => Log($"[{loading.TargetModule}] load"));
        e.Registry.Subscribe<RuntimeEvents.OnStarting>((starting, _) => Log($"[{starting.TargetModule}] start"));
        e.Registry.Subscribe<RuntimeEvents.OnStopRequested>(OnStopRequested);
        e.Registry.Subscribe<RuntimeEvents.OnStopping>((stopping, _) => Log($"[{stopping.TargetModule}] stopping"));
        e.Registry.Subscribe<RuntimeEvents.OnUnloading>((unloading, _) => Log($"[{unloading.TargetModule}] unload"));
        e.Registry.Subscribe<StorageChangedEvent>((changed, _) => Log($"[{e.TargetModule}] saw storage change: {changed.Key}"));
        e.Registry.RegisterTask(new InstallTask(log));
        e.Registry.RegisterTask(new ScenarioWaitTask());
        e.Registry.RegisterTask(new ScenarioCancelTask());
        if (settings.ParallelFailFast) {
            e.Registry.Subscribe<ParallelProbeEvent>(async (probe, _) => {
                probe.Probe.SlowStarted.SetResult();
                await probe.Probe.ReleaseSlow.Task;
                probe.Probe.SlowFinished.SetResult();
            });
        }
        return Log($"[{e.TargetModule}] registered");
    }

    private ValueTask OnStopRequested(RuntimeEvents.OnStopRequested e, EventContext context) {
        if (!settings.VetoFirstStop || vetoed) {
            return ValueTask.CompletedTask;
        }

        vetoed = true;
        e.Cancel();
        return Log("[Test] veto the first stop request");
    }

    private ValueTask Log(string message) {
        log.WriteLine(message);
        return ValueTask.CompletedTask;
    }
}

public sealed partial class InstallTask(TextWriter log) : RuntimeTask<InstallRequest, InstallResult> {
    public override TaskExitPolicy ExitPolicy => TaskExitPolicy.Wait;

    protected override async ValueTask<InstallResult> ExecuteAsync(TaskEvent<InstallRequest, InstallResult> e) {
        e.Output.ReportLog(new TaskLogEntry($"install {e.Input.PackName} started"));
        for (var i = 1; i <= 2; i++) {
            var step = i;
            e.Work.Run($"install step {step}", async context => {
                await Task.Delay(50 * step, context.Cancellation);
                await log.WriteLineAsync($"[Test] install {e.Input.PackName}: step {step} done");
                context.Output!.ReportProgress(new TaskProgress("install", step, 2));
            });
        }

        await SaveValueTask.InvokeAsync(e.Work, new SaveValue("pack", e.Input.PackName));
        return new InstallResult(e.Input.PackName, Steps: 2);
    }
}

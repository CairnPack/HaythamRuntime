using Haytham.Runtime.Events;
using Haytham.Runtime.Modules;
using Haytham.Runtime.Tasks;
using Test.Storage;

namespace Test;

public sealed record InstallRequest(string PackName);
public sealed record InstallResult(string PackName, int Steps);

[RuntimeModule]
public sealed class TestModule(TextWriter log, ScenarioSettings settings) : IRuntimeModule {
    private int stopRequests;

    public void Connect(ModuleWiring wiring) => wiring.SubscribeRegister(OnRegistering);

    private ValueTask OnRegistering(RuntimeEvents.OnRegistering e, EventContext context) {
        e.Registry.Subscribe<RuntimeEvents.OnLoading>((loading, _) => {
            settings.Lifecycle?.Loaded.Add(loading.TargetModule);
            return Log($"[{loading.TargetModule}] load");
        });
        e.Registry.Subscribe<RuntimeEvents.OnStarting>(OnStarting);
        e.Registry.Subscribe<RuntimeEvents.OnStopRequested>(OnStopRequested);
        e.Registry.Subscribe<RuntimeEvents.OnStopping>((stopping, _) => {
            settings.Lifecycle?.Stopping.Add(stopping.TargetModule);
            return Log($"[{stopping.TargetModule}] stopping");
        });
        e.Registry.Subscribe<RuntimeEvents.OnUnloading>((unloading, _) => {
            settings.Lifecycle?.Unloaded.Add(unloading.TargetModule);
            return Log($"[{unloading.TargetModule}] unload");
        });
        e.Registry.Subscribe<StorageChangedEvent>((changed, _) => Log($"[{e.TargetModule}] saw storage change: {changed.Key}"));
        e.Registry.RegisterTask(new InstallTask(log));
        e.Registry.RegisterTask(new ScenarioWaitTask());
        e.Registry.RegisterTask(new ScenarioCancelTask());
        if (settings.MergeErrors) {
            e.Registry.Subscribe<UnregisteredScenarioEvent>((_, _) => ValueTask.CompletedTask);
            e.Registry.RegisterTask(new ScenarioWaitTask());
        }
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
        var request = ++stopRequests;
        if (settings.StopDecision is { } probe && request == 2) {
            throw probe.ExpectedFailure;
        }

        if (!settings.VetoFirstStop || request != 1) {
            return ValueTask.CompletedTask;
        }

        e.Cancel();
        return Log("[Test] veto the first stop request");
    }

    private async ValueTask OnStarting(RuntimeEvents.OnStarting e, EventContext context) {
        settings.Lifecycle?.Started.Add(e.TargetModule);
        await Log($"[{e.TargetModule}] start");
        if (settings.Starting is { } probe) {
            probe.Entered.SetResult();
            await probe.Release.Task;
        }

        if (settings.FailOnStarting) {
            throw settings.Lifecycle!.ExpectedFailure;
        }
    }

    private ValueTask Log(string message) {
        log.WriteLine(message);
        return ValueTask.CompletedTask;
    }
}

internal sealed class UnregisteredScenarioEvent : RuntimeEvent;

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

using Haytham.Runtime.Events;
using Haytham.Runtime.Modules;
using Haytham.Runtime.Tasks;
using Haytham.Runtime.Work;

namespace Test.Storage;

public sealed record SaveValue(string Key, string Value);

public sealed class StorageChangedEvent(string key) : RuntimeEvent {
    public string Key { get; } = key;
}

[RuntimeModule]
internal sealed class StorageModule(TextWriter log, ScenarioSettings settings) : IRuntimeModule {
    private Dictionary<string, string>? store;

    public void Connect(ModuleWiring wiring) => wiring.SubscribeRegister(OnRegistering);

    private ValueTask OnRegistering(RuntimeEvents.OnRegistering e, EventContext context) {
        e.Registry.RegisterEvent(new EventDefinition<StorageChangedEvent>("Storage changed", EventDispatchMode.ParallelJoin));
        e.Registry.Subscribe<RuntimeEvents.OnLoading>(OnLoading);
        e.Registry.Subscribe<RuntimeEvents.OnStarting>(OnStarting);
        e.Registry.Subscribe<RuntimeEvents.OnStopping>(OnStopping);
        e.Registry.Subscribe<RuntimeEvents.OnUnloading>(OnUnloading);
        e.Registry.RegisterTask(new SaveValueTask(this));
        if (settings.ParallelFailFast) {
            e.Registry.RegisterEvent(new EventDefinition<ParallelProbeEvent>("并行首错检查", EventDispatchMode.ParallelFailFast));
            e.Registry.Subscribe<ParallelProbeEvent>(async (probe, _) => {
                await probe.Probe.SlowStarted.Task;
                throw probe.Probe.ExpectedFailure;
            });
            e.Registry.RegisterTask(new ParallelPublishTask());
        }
        log.WriteLine($"[{e.TargetModule}] registered");
        return ValueTask.CompletedTask;
    }

    private ValueTask OnLoading(RuntimeEvents.OnLoading e, EventContext context) {
        settings.Lifecycle?.Loaded.Add(e.TargetModule);
        store = new Dictionary<string, string>();
        log.WriteLine($"[{e.TargetModule}] load: storage opened");
        return ValueTask.CompletedTask;
    }

    private ValueTask OnUnloading(RuntimeEvents.OnUnloading e, EventContext context) {
        settings.Lifecycle?.Unloaded.Add(e.TargetModule);
        if (settings.CleanupFailure is { } probe) {
            probe.ReleaseExecuted = true;
        }
        store = null;
        log.WriteLine($"[{e.TargetModule}] unload: storage released");
        return ValueTask.CompletedTask;
    }

    private ValueTask OnStarting(RuntimeEvents.OnStarting e, EventContext context) {
        settings.Lifecycle?.Started.Add(e.TargetModule);
        if (settings.ContinuousStopSignals is { } signals) {
            for (var i = 0; i < 2; i++) {
                var index = i;
                signals.Handles.Add(e.ContinuousWork.Start($"stop-signal-{index}", context => signals.ExecuteAsync(index, context)));
            }
        }
        else {
            e.ContinuousWork.Start("heartbeat", RunHeartbeatAsync);
        }
        if (settings.ContinuousRetry is { } probe) {
            probe.InitialHandle = e.ContinuousWork.Start("retry-probe", probe.ExecuteAsync);
        }
        return ValueTask.CompletedTask;
    }

    private ValueTask OnStopping(RuntimeEvents.OnStopping e, EventContext context) {
        settings.Lifecycle?.Stopping.Add(e.TargetModule);
        log.WriteLine($"[{e.TargetModule}] stopping");
        return ValueTask.CompletedTask;
    }

    private async ValueTask RunHeartbeatAsync(ContinuousWorkContext context) {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        log.WriteLine("[Test.Storage] heartbeat started");
        context.ReportReady();
        try {
            while (await timer.WaitForNextTickAsync(context.Stopping)) {
                log.WriteLine("[Test.Storage] heartbeat");
            }
        }
        finally {
            log.WriteLine("[Test.Storage] heartbeat stopped");
        }
    }

    internal bool Save(SaveValue input) {
        var replaced = store!.ContainsKey(input.Key);
        store[input.Key] = input.Value;
        return replaced;
    }
}

public sealed partial class SaveValueTask : RuntimeTask<SaveValue, bool> {
    private readonly StorageModule storage;

    internal SaveValueTask(StorageModule storage) => this.storage = storage;

    public override TaskExitPolicy ExitPolicy => TaskExitPolicy.Wait;

    protected override async ValueTask<bool> ExecuteAsync(TaskEvent<SaveValue, bool> e) {
        var replaced = storage.Save(e.Input);
        await e.Work.PublishAsync(new StorageChangedEvent(e.Input.Key));
        return replaced;
    }
}

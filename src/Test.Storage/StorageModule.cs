using Haytham.Runtime.Events;
using Haytham.Runtime.Modules;
using Haytham.Runtime.Tasks;

namespace Test.Storage;

public sealed record SaveValue(string Key, string Value);

public sealed class StorageChangedEvent(string key) : RuntimeEvent {
    public string Key { get; } = key;
}

[RuntimeModule]
internal sealed class StorageModule(TextWriter log) : IRuntimeModule {
    private Dictionary<string, string>? store;

    public void Connect(ModuleWiring wiring) => wiring.SubscribeRegister(OnRegistering);

    private ValueTask OnRegistering(RuntimeEvents.OnRegistering e, EventContext context) {
        e.Registry.RegisterEvent(new EventDefinition<StorageChangedEvent>("Storage changed", EventDispatchMode.ParallelJoin));
        e.Registry.Subscribe<RuntimeEvents.OnLoading>(OnLoading);
        e.Registry.Subscribe<RuntimeEvents.OnUnloading>(OnUnloading);
        e.Registry.RegisterTask(new SaveValueTask(this));
        log.WriteLine($"[{e.TargetModule}] registered");
        return ValueTask.CompletedTask;
    }

    private ValueTask OnLoading(RuntimeEvents.OnLoading e, EventContext context) {
        store = new Dictionary<string, string>();
        log.WriteLine($"[{e.TargetModule}] load: storage opened");
        return ValueTask.CompletedTask;
    }

    private ValueTask OnUnloading(RuntimeEvents.OnUnloading e, EventContext context) {
        store = null;
        log.WriteLine($"[{e.TargetModule}] unload: storage released");
        return ValueTask.CompletedTask;
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

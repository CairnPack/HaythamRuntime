using Haytham.Runtime.Events;
using Haytham.Runtime.Modules;
using Haytham.Runtime.Work;

namespace Haytham.Runtime;

internal sealed class RuntimeLifecycle(RuntimeHost host, IReadOnlyList<ModuleDefinition> definitions, RuntimeCapabilities capabilities) {
    private readonly List<(ModuleId Id, IRuntimeModule Instance)> modules = [];
    private readonly List<ModuleId> loaded = [];
    private readonly List<ModuleId> started = [];
    private int busy;

    internal Task StartAsync() {
        Begin(nameof(StartAsync), RuntimeState.Created);
        host.SetState(RuntimeState.Starting);
        return StartCoreAsync();
    }

    private async Task StartCoreAsync() {
        try {
            try {
                host.InitializeEvents(definitions);
                host.Events.RegisterBuiltIns();

                foreach (var definition in definitions) {
                    modules.Add((new ModuleId(definition.Id), definition.Create(capabilities)));
                }

                foreach (var module in modules) {
                    var wiring = new ModuleWiring(host, module.Id, host.Events);
                    try {
                        module.Instance.Connect(wiring);
                    }
                    finally {
                        wiring.Close();
                    }
                }

                host.Events.LockModules();

                var registries = new List<Registry>();
                foreach (var module in modules) {
                    var registry = new Registry(host.Events, module.Id);
                    try {
                        await DispatchAsync(new RuntimeEvents.OnRegistering(module.Id, registry), registry.Close);
                    }
                    finally {
                        registry.Close();
                    }

                    registry.Accept();
                    registries.Add(registry);
                }

                host.Events.MergeAndLock(registries);

                foreach (var module in modules) {
                    await DispatchAsync(new RuntimeEvents.OnLoading(module.Id));
                    loaded.Add(module.Id);
                    host.SetModuleAvailable(module.Id, true);
                }

                foreach (var module in modules) {
                    var scope = host.ContinuousWork.OpenScope(module.Id);
                    try {
                        await DispatchAsync(new RuntimeEvents.OnStarting(module.Id, scope), scope.Close);
                    }
                    finally {
                        scope.Close();
                    }

                    started.Add(module.Id);
                }

                await host.ContinuousWork.WaitReadyAsync();
                lock (host.Gate) {
                    host.ContinuousWork.ConfirmReady();
                    host.SetState(RuntimeState.Running);
                    host.SetAdmission(true);
                }
            }
            catch (Exception error) {
                var failures = GetFailures(error);
                var cleanup = new List<RuntimeFailure>();
                await StopContinuousWorkAsync(cleanup);
                // 已经执行过 OnStarting 的模块先反序收到 OnStopping，再统一卸载。
                await StopModulesAsync(cleanup);
                var unloaded = await UnloadModulesAsync(cleanup);
                failures.AddRange(cleanup);
                host.SetState(unloaded ? RuntimeState.Stopped : RuntimeState.Hanging);
                throw new RuntimeExecutionException("Runtime 启动失败。", failures);
            }
        }
        finally {
            End();
        }
    }

    internal Task PreStopAsync() => throw new NotImplementedException("可撤销的预退出和 CancelOnExit 政策尚未实现。");

    internal void CancelPreStop() => throw new NotImplementedException("预退出撤销尚未实现。");

    internal async Task<StopOutcome> StopAsync() {
        Begin(nameof(StopAsync), RuntimeState.Running, RuntimeState.PreStopping);
        try {
            lock (host.Gate) {
                host.SetAdmission(false);
                host.SetState(RuntimeState.StopRequested);
            }

            var failures = new List<RuntimeFailure>();
            var vetoed = false;
            foreach (var module in modules) {
                var request = new RuntimeEvents.OnStopRequested(module.Id);
                try {
                    await DispatchAsync(request);
                }
                catch (Exception error) {
                    failures.AddRange(GetFailures(error));
                }

                vetoed |= request.IsCanceled;
            }

            if (failures.Count > 0) {
                RestoreRunning();
                return new StopOutcome(StopOutcomeKind.DecisionFailed, failures);
            }

            if (vetoed) {
                RestoreRunning();
                return new StopOutcome(StopOutcomeKind.Vetoed, []);
            }

            host.SetState(RuntimeState.Draining);
            await DrainAsync(host.SnapshotRoots(), failures);

            host.SetState(RuntimeState.Stopping);
            await StopContinuousWorkAsync(failures);
            await StopModulesAsync(failures);

            host.SetState(RuntimeState.Unloading);
            var unloaded = await UnloadModulesAsync(failures);
            host.SetState(unloaded ? RuntimeState.Stopped : RuntimeState.Hanging);
            return new StopOutcome(unloaded ? StopOutcomeKind.Stopped : StopOutcomeKind.CleanupFailed, failures);
        }
        finally {
            End();
        }
    }

    internal void Kill() => throw new NotImplementedException("终止策略调用及终止未完成的错误报告尚未实现。");

    private async Task DispatchAsync(LifecycleEvent e, Action? onHandlerReturned = null) {
        var node = new WorkNode(host, e.TargetModule, CancellationToken.None, null);
        await node.ExecuteAsync(async current => {
            await host.Events.DispatchAsync(e, current, onHandlerReturned);
            return true;
        });
    }

    private static async Task DrainAsync(RootExecution[] roots, List<RuntimeFailure> failures) {
        foreach (var root in roots) {
            try {
                await root.Completion;
            }
            catch (OperationCanceledException) when (root.Completion.IsCanceled) {
            }
            catch (Exception error) {
                failures.AddRange(GetFailures(error));
            }

            if (root.GetOutputFailure() is { } outputFailure) {
                failures.Add(outputFailure);
            }
        }
    }

    private async Task StopContinuousWorkAsync(List<RuntimeFailure> failures) {
        try {
            await host.ContinuousWork.StopAllAsync();
        }
        catch (Exception error) {
            failures.AddRange(GetFailures(error, RuntimeFailureKind.Cleanup));
        }
    }

    private async Task StopModulesAsync(List<RuntimeFailure> failures) {
        for (var i = started.Count - 1; i >= 0; i--) {
            try {
                await DispatchAsync(new RuntimeEvents.OnStopping(started[i]));
            }
            catch (Exception error) {
                failures.AddRange(GetFailures(error, RuntimeFailureKind.Cleanup));
            }
        }

        started.Clear();
    }

    private async Task<bool> UnloadModulesAsync(List<RuntimeFailure> failures) {
        var blocked = new HashSet<ModuleId>();
        for (var i = loaded.Count - 1; i >= 0; i--) {
            var module = loaded[i];
            if (blocked.Contains(module)) {
                continue;
            }

            host.SetModuleAvailable(module, false);
            try {
                await DispatchAsync(new RuntimeEvents.OnUnloading(module));
                loaded.RemoveAt(i);
            }
            catch (Exception error) {
                failures.AddRange(GetFailures(error, RuntimeFailureKind.Cleanup));
                // 卸载失败的模块可能仍持有依赖资源，只继续清理无关模块。
                blocked.UnionWith(host.Events.GetDependencies(module));
            }
        }

        return loaded.Count == 0;
    }

    private void RestoreRunning() {
        lock (host.Gate) {
            host.SetState(RuntimeState.Running);
            host.SetAdmission(true);
        }
    }

    private void Begin(string operation, params RuntimeState[] allowed) {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) {
            throw new RuntimeContractException($"另一个生命周期操作正在进行，不能调用 {operation}。");
        }

        if (!allowed.Contains(host.State)) {
            End();
            throw new RuntimeContractException($"当前状态为 {host.State}，不能调用 {operation}。");
        }
    }

    private void End() => Volatile.Write(ref busy, 0);

    private static List<RuntimeFailure> GetFailures(Exception error, RuntimeFailureKind kind = RuntimeFailureKind.Execution) =>
        error is RuntimeExecutionException execution
            ? execution.Failures.Select(failure => kind == RuntimeFailureKind.Execution ? failure : failure with { Kind = kind }).ToList()
            : [new RuntimeFailure(error is RuntimeContractException ? RuntimeFailureKind.Contract : kind, error.Message, error)];
}

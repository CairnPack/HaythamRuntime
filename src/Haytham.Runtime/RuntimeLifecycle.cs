using Haytham.Runtime.Events;
using Haytham.Runtime.Modules;
using Haytham.Runtime.Work;
using Haytham.Runtime.Logging;
using Microsoft.Extensions.Logging;

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
                    var moduleId = new ModuleId(definition.Id);
                    using var scope = BeginModuleScope(moduleId, "ModuleConstruction");
                    RuntimeLog.ModuleStepStarted(host.Logger);
                    try {
                        modules.Add((moduleId, definition.Create(capabilities)));
                        RuntimeLog.ModuleStepCompleted(host.Logger);
                    }
                    catch (Exception error) {
                        throw new RuntimeExecutionException("模块构造失败。", GetFailures(error));
                    }
                }

                foreach (var module in modules) {
                    using var scope = BeginModuleScope(module.Id, "ModuleConnect");
                    RuntimeLog.ModuleStepStarted(host.Logger);
                    var wiring = new ModuleWiring(host, module.Id, host.Events);
                    try {
                        module.Instance.Connect(wiring);
                    }
                    catch (Exception error) {
                        throw new RuntimeExecutionException("模块接线失败。", GetFailures(error));
                    }
                    finally {
                        wiring.Close();
                    }
                    RuntimeLog.ModuleStepCompleted(host.Logger);
                }

                host.Events.LockModules();

                var registries = new List<Registry>();
                foreach (var module in modules) {
                    var registry = new Registry(module.Id);
                    try {
                        await DispatchAsync(new RuntimeEvents.OnRegistering(module.Id, registry), registry.Close);
                    }
                    finally {
                        registry.Close();
                    }

                    registries.Add(registry);
                }

                using (host.Logger.BeginScope(new Dictionary<string, object?> {
                    ["RuntimeId"] = host.RuntimeId, ["Phase"] = "RegistryMerge"
                })) {
                    try {
                        host.Events.MergeAndLock(registries);
                    }
                    catch (Exception error) {
                        throw new RuntimeExecutionException("Registry 合并失败。", GetFailures(error));
                    }
                }

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
                await DisposeTaskSchedulerAsync(cleanup);
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

    internal Task PreStopAsync() {
        Begin(nameof(PreStopAsync), RuntimeState.Running);
        try {
            RootExecution[] roots;
            lock (host.Gate) {
                host.SetAdmission(false);
                host.SetState(RuntimeState.PreStopping);
                roots = host.SnapshotRoots();
            }

            var failures = new List<RuntimeFailure>();
            foreach (var root in roots) {
                try {
                    root.RequestExitCancellation?.Invoke();
                }
                catch (Exception error) {
                    failures.AddRange(GetFailures(error));
                }
            }

            return failures.Count == 0 ? Task.CompletedTask
                : Task.FromException(new RuntimeExecutionException("预退出取消请求未全部完成。", failures));
        }
        finally {
            End();
        }
    }

    internal void CancelPreStop() {
        Begin(nameof(CancelPreStop), RuntimeState.PreStopping);
        try {
            RestoreState(RuntimeState.Running);
        }
        finally {
            End();
        }
    }

    internal Task<StopOutcome> StopAsync() {
        Begin(nameof(StopAsync), RuntimeState.Running, RuntimeState.PreStopping);
        return StopCoreAsync(host.State);
    }

    private async Task<StopOutcome> StopCoreAsync(RuntimeState previousState) {
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
                RestoreState(previousState);
                return FinishStop(StopOutcomeKind.DecisionFailed, failures);
            }

            if (vetoed) {
                RestoreState(previousState);
                return FinishStop(StopOutcomeKind.Vetoed, []);
            }

            RootExecution[] roots;
            lock (host.Gate) {
                host.SetState(RuntimeState.Draining);
                roots = host.SnapshotRoots();
            }

            await DrainAsync(roots, failures);
            await DisposeTaskSchedulerAsync(failures);

            host.SetState(RuntimeState.Stopping);
            await StopContinuousWorkAsync(failures);
            await StopModulesAsync(failures);

            host.SetState(RuntimeState.Unloading);
            var unloaded = await UnloadModulesAsync(failures);
            host.SetState(unloaded ? RuntimeState.Stopped : RuntimeState.Hanging);
            return FinishStop(unloaded ? StopOutcomeKind.Stopped : StopOutcomeKind.CleanupFailed, failures);
        }
        finally {
            End();
        }
    }

    internal void Kill() {
        if (host.State is not (RuntimeState.Running or RuntimeState.PreStopping or RuntimeState.StopRequested
            or RuntimeState.Draining or RuntimeState.Stopping or RuntimeState.Unloading or RuntimeState.Hanging)) {
            throw new RuntimeContractException($"当前状态为 {host.State}，不能调用 {nameof(Kill)}。");
        }

        using var scope = host.Logger.BeginScope(new Dictionary<string, object?> { ["RuntimeId"] = host.RuntimeId });
        RuntimeLog.KillRequested(host.Logger, host.State.ToString());
        var hasStrategy = capabilities.Contains(typeof(IRuntimeTerminationStrategy));
        try {
            capabilities.Require<IRuntimeTerminationStrategy>().Terminate();
        }
        catch (Exception error) {
            RuntimeLog.KillNotCompleted(host.Logger, hasStrategy ? "Threw" : "MissingStrategy", error.GetType().FullName);
            if (hasStrategy) RuntimeLog.FailureDetails(host.Logger, 1007, error);
            throw new RuntimeExecutionException("Runtime 终止未完成。", error is RuntimeExecutionException execution
                ? execution.Failures : [new RuntimeFailure(error is RuntimeContractException or RuntimeConfigurationException
                    ? RuntimeFailureKind.Contract : RuntimeFailureKind.Execution, error.Message, error)]);
        }

        RuntimeLog.KillNotCompleted(host.Logger, "Returned", null);
        throw new RuntimeExecutionException("Runtime 终止未完成。",
            [new RuntimeFailure(RuntimeFailureKind.Execution, "终止策略意外返回。")]);
    }

    private async Task DispatchAsync(LifecycleEvent e, Action? onHandlerReturned = null) {
        using var scope = BeginModuleScope(e.TargetModule, e.GetType().Name);
        RuntimeLog.ModuleStepStarted(host.Logger);
        var node = new WorkNode(host, e.TargetModule, CancellationToken.None, null) {
            Phase = e.GetType().Name, EventType = e.GetType().FullName
        };
        await node.ExecuteAsync(async current => {
            await host.Events.DispatchAsync(e, current, onHandlerReturned);
            return true;
        });
        RuntimeLog.ModuleStepCompleted(host.Logger);
    }

    private IDisposable? BeginModuleScope(ModuleId module, string phase) =>
        host.Logger.BeginScope(new Dictionary<string, object?> {
            ["RuntimeId"] = host.RuntimeId, ["ModuleId"] = module.Value, ["Phase"] = phase
        });

    private StopOutcome FinishStop(StopOutcomeKind kind, IReadOnlyList<RuntimeFailure> failures) {
        using var scope = host.Logger.BeginScope(new Dictionary<string, object?> { ["RuntimeId"] = host.RuntimeId });
        RuntimeLog.StopFinished(host.Logger, failures.Count == 0 ? LogLevel.Information : LogLevel.Error,
            kind.ToString(), host.State.ToString(), failures.Count);
        return new StopOutcome(kind, failures);
    }

    private static async Task DrainAsync(RootExecution[] roots, List<RuntimeFailure> failures) {
        foreach (var root in roots) {
            try {
                await root.Completion;
            }
            catch (Exception) {
                // 根事件的执行失败由原调用方接收，排空只等待实际结束。
            }

            if (root.GetOutputFailure() is { } outputFailure) {
                failures.Add(outputFailure);
            }
        }
    }

    private async ValueTask DisposeTaskSchedulerAsync(List<RuntimeFailure> failures) {
        try {
            await host.DisposeTaskSchedulerAsync();
        }
        catch (Exception error) {
            using var scope = host.Logger.BeginScope(new Dictionary<string, object?> { ["RuntimeId"] = host.RuntimeId });
            if (error is not RuntimeExecutionException) host.ObserveFailure(error, RuntimeFailureKind.Cleanup);
            failures.Add(new RuntimeFailure(RuntimeFailureKind.Cleanup, "Task 调度器释放失败。", error));
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

    private void RestoreState(RuntimeState state) {
        lock (host.Gate) {
            host.SetState(state);
            host.SetAdmission(state == RuntimeState.Running);
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

    private List<RuntimeFailure> GetFailures(Exception error, RuntimeFailureKind kind = RuntimeFailureKind.Execution) {
        if (error is RuntimeExecutionException execution) {
            return execution.Failures.Select(failure => kind != RuntimeFailureKind.Execution && failure.Kind == RuntimeFailureKind.Execution
                ? failure with { Kind = kind } : failure).ToList();
        }

        kind = error is RuntimeContractException or RuntimeConfigurationException ? RuntimeFailureKind.Contract : kind;
        using var scope = host.Logger.BeginScope(new Dictionary<string, object?> { ["RuntimeId"] = host.RuntimeId });
        host.ObserveFailure(error, kind);
        return [new RuntimeFailure(kind, error.Message, error)];
    }
}

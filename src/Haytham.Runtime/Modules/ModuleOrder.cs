namespace Haytham.Runtime.Modules;

/// <summary>
/// 检查模块列表并按依赖排序：被依赖的模块排在前面，没有依赖关系的模块保持传入的顺序。
/// </summary>
/// <remarks>
/// 生成的收集代码已经排过序，这里再排一次，因为 <see cref="RuntimeHost.Create"/> 也接受手写的列表。
/// </remarks>
internal static class ModuleOrder {

    public static IReadOnlyList<ModuleDefinition> Sort(IReadOnlyList<ModuleDefinition> definitions) {
        var problems = new List<string>();
        var byId = new Dictionary<string, ModuleDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions) {
            if (!byId.TryAdd(definition.Id, definition)) {
                problems.Add($"模块 Id {definition.Id} 重复。");
            }
        }

        foreach (var definition in definitions) {
            foreach (var dependency in definition.Dependencies.Where(dependency => !byId.ContainsKey(dependency))) {
                problems.Add($"模块 {definition.Id} 依赖的 {dependency} 不在模块列表里。");
            }
        }

        if (problems.Count > 0) {
            throw new RuntimeConfigurationException("模块列表不完整。", problems);
        }

        var ordered = new List<ModuleDefinition>(definitions.Count);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions) {
            Visit(definition);
        }

        return ordered;

        void Visit(ModuleDefinition definition) {
            if (visited.Contains(definition.Id)) {
                return;
            }

            if (!visiting.Add(definition.Id)) {
                throw new RuntimeConfigurationException("模块依赖成环。", [$"环中包含模块 {definition.Id}。"]);
            }

            foreach (var dependency in definition.Dependencies) {
                Visit(byId[dependency]);
            }

            visiting.Remove(definition.Id);
            visited.Add(definition.Id);
            ordered.Add(definition);
        }
    }

}

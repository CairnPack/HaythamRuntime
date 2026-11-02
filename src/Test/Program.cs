using Test;

if (args.Length == 0) {
    await Demo.RunAsync();
    return 0;
}

return await ScenarioRunner.RunAsync(args);

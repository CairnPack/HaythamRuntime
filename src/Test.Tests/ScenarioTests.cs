using System.Diagnostics;
using Xunit;

namespace Test.Tests;

/// <summary>
/// 每个场景在独立子进程里运行 Test.dll，因为一个进程只能创建一个 RuntimeHost。
/// </summary>
public sealed class ScenarioTests {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public static TheoryData<string> Scenarios => new(ScenarioRunner.Names);

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Scenario(string name) {
        var start = new ProcessStartInfo("dotnet") {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Test.dll"));
        start.ArgumentList.Add(name);

        var cancellation = TestContext.Current.CancellationToken;
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync(cancellation);
        var errors = child.StandardError.ReadToEndAsync(cancellation);
        var timedOut = false;
        try {
            await child.WaitForExitAsync(cancellation).WaitAsync(Timeout, cancellation);
        }
        catch (TimeoutException) {
            timedOut = true;
        }
        finally {
            // 超时或测试被取消时，结束场景进程，不让它留在后台。
            if (!child.HasExited) {
                child.Kill(entireProcessTree: true);
            }
        }

        var log = await output + await errors;
        Assert.False(timedOut, $"场景超过 {Timeout.TotalSeconds} 秒，已终止。\n{log}");
        Assert.True(child.ExitCode == 0, $"退出码 {child.ExitCode}。\n{log}");
    }
}

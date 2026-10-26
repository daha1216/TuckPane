using TuckPane.Core;

internal static class LaunchEnvironmentChecks
{
    internal static void Run()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ELECTRON_RUN_AS_NODE"] = "1",
            ["ELECTRON_NO_ATTACH_CONSOLE"] = "1",
            ["NODE_OPTIONS"] = "--require ./hook.js",
            ["PATH"] = @"C:\Windows",
            ["TUCKPANE_PERF_TRACE"] = "1"
        };

        IReadOnlyList<string> removed = LaunchEnvironment.Scrub(Read, Remove);
        Require(removed.Contains("ELECTRON_RUN_AS_NODE") && removed.Contains("NODE_OPTIONS") &&
                removed.Contains("ELECTRON_NO_ATTACH_CONSOLE"),
            "启动宿主留下的 Electron/Node 开关必须被清除。");
        Require(!values.ContainsKey("ELECTRON_RUN_AS_NODE") && !values.ContainsKey("NODE_OPTIONS") &&
                !values.ContainsKey("ELECTRON_NO_ATTACH_CONSOLE"),
            "被清除的开关不能残留在环境里。");
        Require(values.ContainsKey("PATH") && values.ContainsKey("TUCKPANE_PERF_TRACE"),
            "与启动宿主无关的变量必须原样保留。");

        Require(LaunchEnvironment.Scrub(Read, Remove).Count == 0, "重复清理必须幂等且不产生改动。");

        Require(LaunchEnvironment.InheritedOverrides.Contains("ELECTRON_RUN_AS_NODE"),
            "覆盖清单必须包含会让 Electron 以 Node 模式运行的开关。");

        Console.WriteLine("PASS --launch-environment: inherited Electron/Node switches are removed, unrelated variables survive, scrubbing is idempotent.");

        string? Read(string name) => values.TryGetValue(name, out string? value) ? value : null;
        void Remove(string name) => values.Remove(name);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

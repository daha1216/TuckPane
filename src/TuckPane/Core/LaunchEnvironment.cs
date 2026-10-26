namespace TuckPane.Core;

// TuckPane can be started from a Node or Electron host — a terminal, an IDE run configuration, an
// automation shell. Those hosts leave their own switches in the environment, and every target we
// shell-execute inherits them: an Electron app started from a shortcut then runs as a plain Node
// process, with no window and an immediate exit, which looks exactly like a dead click. Strip the
// switches once at startup; nothing in TuckPane reads them.
internal static class LaunchEnvironment
{
    internal static readonly string[] InheritedOverrides =
    [
        "ELECTRON_RUN_AS_NODE",
        "ELECTRON_NO_ATTACH_CONSOLE",
        "ELECTRON_ENABLE_LOGGING",
        "ELECTRON_ENABLE_STACK_DUMPING",
        "ELECTRON_DEFAULT_ERROR_MODE",
        "NODE_OPTIONS"
    ];

    internal static IReadOnlyList<string> Scrub(Func<string, string?> read, Action<string> remove)
    {
        var removed = new List<string>();
        foreach (string name in InheritedOverrides)
        {
            if (read(name) is null) continue;
            remove(name);
            removed.Add(name);
        }
        return removed;
    }

    internal static void ScrubCurrentProcess() =>
        Scrub(Environment.GetEnvironmentVariable, name => Environment.SetEnvironmentVariable(name, null));
}

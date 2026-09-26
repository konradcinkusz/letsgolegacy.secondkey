namespace SecondKey.Cli.Tests;

/// <summary>
/// A test that sends a process a POSIX signal. Windows has no way to send one console
/// process its Ctrl+C without the others that share the console, so there it is reported
/// as skipped, with the reason — never passed.
/// </summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Sends a POSIX signal; Windows cannot signal one console process on its own.";
        }
    }
}

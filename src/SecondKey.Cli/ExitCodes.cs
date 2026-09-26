namespace SecondKey.Cli;

/// <summary>
/// Every exit code <c>sk</c> returns, so a pipeline can branch on them. Documented in
/// docs/cli.md; changing one is a breaking change for every pipeline that uses it.
/// </summary>
public static class ExitCodes
{
    /// <summary>Done: the verdict passed, the gate passed, the artifact is valid.</summary>
    public const int Success = 0;

    /// <summary>The thing checked failed: a regression in the verdict, a blocking finding at the gate.</summary>
    public const int Failed = 1;

    /// <summary>No regression, but a fix candidate needs a person's decision.</summary>
    public const int Review = 2;

    /// <summary>An input — an artifact or the configuration — is invalid; nothing was decided.</summary>
    public const int InvalidInput = 3;

    /// <summary>The command could not complete: a system unreachable, a file unwritable.</summary>
    public const int RuntimeError = 4;

    /// <summary>The command line itself is wrong.</summary>
    public const int Usage = 64;

    /// <summary>The command exists in the plan but not yet in this build.</summary>
    public const int NotImplemented = 70;
}

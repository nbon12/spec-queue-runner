namespace SpecRunner.Cli;

/// <summary>
/// Exit codes are a contract with launchd (contracts/cli-commands.md): 0 = work done /
/// nothing to do / lock held; 1 = config invalid; 2 = environment/prerequisite failure;
/// 3 = unexpected internal error. Exit 0 covers "lock held" deliberately — overlapping
/// ticks are normal, not an alertable condition.
/// </summary>
public enum ExitCode
{
    Ok = 0,
    ConfigInvalid = 1,
    EnvironmentFailure = 2,
    InternalError = 3,
}

/// <summary>
/// Parses the subcommand and routes it. Real command bodies (tick, doctor, version)
/// arrive in later phases; this establishes the surface and the exit-code contract.
/// </summary>
public static class CommandDispatcher
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: spec-runner <tick|doctor|version> [args]");
            return (int)ExitCode.ConfigInvalid;
        }

        return args[0] switch
        {
            "version" => Version(),
            "tick" or "doctor" => NotYetImplemented(args[0]),
            _ => Unknown(args[0]),
        };
    }

    private static int Version()
    {
        var version = typeof(CommandDispatcher).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        Console.WriteLine($"spec-runner {version}");
        return (int)ExitCode.Ok;
    }

    private static int NotYetImplemented(string command)
    {
        Console.Error.WriteLine($"'{command}' is not implemented yet (Phase 2+).");
        return (int)ExitCode.InternalError;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"unknown command: {command}");
        return (int)ExitCode.ConfigInvalid;
    }
}

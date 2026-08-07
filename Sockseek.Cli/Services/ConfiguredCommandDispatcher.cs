using Sockseek.Core.Settings;

namespace Sockseek.Cli;

[Flags]
internal enum ConfiguredCommandOptions
{
    None = 0,
    Remote = 1,
    DataDirectory = 2,
}

/// <summary>
/// Resolves standard configuration before dispatching top-level commands that
/// have their own positional grammar. Command-specific parsers never receive
/// config/profile options and never need to load configuration themselves.
/// </summary>
internal static class ConfiguredCommandDispatcher
{
    public static async Task<Program.CliExitCode?> TryRunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        if (args.Length == 0)
            return null;

        string command = args[0].ToLowerInvariant();
        try
        {
            if (command is "chat" or "room" or "notifications" or "notification")
            {
                if (HasHelp(args))
                    return await ChatCommandRunner.RunAsync(args, remote: null, cancellationToken)
                        .ConfigureAwait(false);
                ConfiguredCommandInvocation invocation = ConfiguredCommandInvocation.Create(
                    args, ConfiguredCommandOptions.Remote);
                return await ChatCommandRunner.RunAsync(
                    invocation.CommandArguments,
                    invocation.Remote.ServerUrl,
                    cancellationToken).ConfigureAwait(false);
            }

            if (command is "share" or "transfers" or "transfer")
            {
                if (HasHelp(args))
                    return await DaemonResourceCommandRunner.RunAsync(args, remote: null, cancellationToken)
                        .ConfigureAwait(false);
                ConfiguredCommandInvocation invocation = ConfiguredCommandInvocation.Create(
                    args, ConfiguredCommandOptions.Remote);
                return await DaemonResourceCommandRunner.RunAsync(
                    invocation.CommandArguments,
                    invocation.Remote.ServerUrl,
                    cancellationToken).ConfigureAwait(false);
            }

            if (command == "database")
            {
                if (HasHelp(args))
                    return null;
                ConfiguredCommandInvocation invocation = ConfiguredCommandInvocation.Create(
                    args, ConfiguredCommandOptions.DataDirectory);
                return await DatabaseCommandRunner.RunAsync(
                    invocation.CommandArguments.Skip(1).ToArray(),
                    invocation.ConfigFile,
                    invocation.Daemon).ConfigureAwait(false);
            }

            return null;
        }
        catch (Exception ex) when (ex is ArgumentException
                                   || ex.Message.StartsWith("Input error:", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                ex.Message.StartsWith("Input error:", StringComparison.Ordinal)
                    ? ex.Message
                    : $"Input error: {ex.Message}");
            return Program.CliExitCode.UsageError;
        }
    }

    private static bool HasHelp(IReadOnlyList<string> args)
        => args.Any(argument => argument is "-h" or "--help");
}

internal sealed record ConfiguredCommandInvocation(
    string[] CommandArguments,
    ConfigFile ConfigFile,
    DaemonSettings Daemon,
    RemoteSettings Remote)
{
    public static ConfiguredCommandInvocation Create(
        IReadOnlyList<string> args,
        ConfiguredCommandOptions options)
    {
        var commandArguments = new List<string>(args.Count);
        var bindingArguments = new List<string>();

        for (int index = 0; index < args.Count; index++)
        {
            string original = args[index];
            SplitOption(original, out string option, out string? attachedValue);
            bool valueless = option is "--nc" or "--no-config";
            bool standard = option is "-c" or "--config" or "--profile"
                || valueless
                || options.HasFlag(ConfiguredCommandOptions.Remote)
                    && option is "--remote" or "--server-url"
                || options.HasFlag(ConfiguredCommandOptions.DataDirectory)
                    && option == "--data-dir";
            if (!standard)
            {
                commandArguments.Add(original);
                continue;
            }

            bindingArguments.Add(option);
            if (valueless)
            {
                if (attachedValue is not null)
                    bindingArguments.Add(RequireValue(option, attachedValue));
                else if (index + 1 < args.Count && bool.TryParse(args[index + 1], out _))
                    bindingArguments.Add(args[++index]);
                continue;
            }

            string value = attachedValue
                ?? (index + 1 < args.Count ? args[++index] : "");
            bindingArguments.Add(RequireValue(option, value));
        }

        var (configFile, _, _, _, daemon, remote) =
            ConfigManager.LoadAndBindAll(bindingArguments);
        return new ConfiguredCommandInvocation(
            commandArguments.ToArray(), configFile, daemon, remote);
    }

    private static void SplitOption(
        string argument,
        out string option,
        out string? attachedValue)
    {
        int equals = argument.IndexOf('=');
        if (equals <= 0)
        {
            option = argument.ToLowerInvariant();
            attachedValue = null;
            return;
        }

        option = argument[..equals].ToLowerInvariant();
        attachedValue = argument[(equals + 1)..];
    }

    private static string RequireValue(string option, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('-'))
            throw new ArgumentException($"Option '{option}' requires a value.");
        return value;
    }
}

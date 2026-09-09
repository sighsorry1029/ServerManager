using System;
using ServerManager.Commands;
using ServerManager.Events;

namespace ServerManager.Discord;

/// <summary>Discord presentation adapter over shared bounded server-console execution.</summary>
internal sealed class DiscordRconCapture : IDisposable
{
    private readonly ServerConsoleExecutor _executor;
    internal bool IsAvailable => _executor.IsAvailable;

    internal DiscordRconCapture(Action<string> log) => _executor = new ServerConsoleExecutor(log);

    internal DiscordCommands.Result Execute(string line, int maximumCharacters)
    {
        ServerManagerCommandResult result = _executor.Execute(line, maximumCharacters, out bool observedResult);
        return observedResult ? DiscordCommands.FormatIntegrationResult(result) :
            new DiscordCommands.Result(result.Success, result.Code, result.Message);
    }

    public void Dispose() => _executor.Dispose();
}

using Parrot.Tools;

namespace Parrot.Core.Tests;

// A tool call that finishes, so a turn can reach its next boundary without
// being stopped to get there. HeldTool is its opposite and they are the two
// halves of what a tool round can do to a drain.
internal sealed class SettledTool(string result) : ITool
{
    public string Name => "settled";

    public string Description => "Finishes at once.";

    public string ParametersJson => """{"type":"object","properties":{}}""";

    public Task<string> Execute(string argumentsJson, CancellationToken cancellationToken) =>
        Task.FromResult(result);
}

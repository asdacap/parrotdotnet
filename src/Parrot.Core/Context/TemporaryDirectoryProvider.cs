using Parrot.Agent;

namespace Parrot.Context;

internal sealed class TemporaryDirectoryProvider(string temporaryDirectory) : ISystemPromptProvider
{
    public string Key => "runtime:user-session-context:01-temporary-directory";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new StaticSystemPrompt($"Writable temporary directory for shell commands: {temporaryDirectory}");
    }
}

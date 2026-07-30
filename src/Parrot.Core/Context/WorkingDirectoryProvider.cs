using Parrot.Agent;

namespace Parrot.Context;

internal sealed class WorkingDirectoryProvider(string workingDirectory) : ISystemPromptProvider
{
    public string Key => "runtime:system-context:06-working-directory";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new StaticSystemPrompt($"Working directory: {workingDirectory}");
    }
}

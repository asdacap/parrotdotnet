using Parrot.Agent;

namespace Parrot.Context;

// A provider creates one prompt product per agent session, allowing prompt
// contributors to retain only the state that belongs to that session.
internal interface ISystemPromptProvider
{
    string Key { get; }

    ISystemPrompt Materialize(AgentIdentity identity);
}

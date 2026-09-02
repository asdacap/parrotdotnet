using Parrot.Security;

namespace Parrot.Agent;

internal interface IAgentProfile
{
    string Id { get; }

    string Prompt { get; }

    IReadOnlyList<string>? AllowedTools { get; }

    IReadOnlyList<string> DisabledTools { get; }

    int MaxTurns { get; }

    bool EnforceActiveWorkCompletion { get; }

    bool IsUserSelectable { get; }

    bool IsAgentSelectable { get; }

    SecurityProfile SecurityProfile { get; }
}

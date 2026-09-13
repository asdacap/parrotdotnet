using Parrot.Agent;
using Parrot.Config;

namespace Parrot.Queues;

internal sealed class QueueActiveWorkBlocker(
    IAgentQueues queues,
    IPromptTemplateCatalog promptTemplates) : IActiveWorkBlocker
{
    public ActiveWorkBlockerResult? Observe()
    {
        var nonemptyQueues = queues.Snapshot().Queues
            .Where(static queue => queue.Size > 0)
            .OrderBy(static queue => queue.Name)
            .Select(queue => promptTemplates.Render(
                "agent-session.nonempty-queue-item",
                [
                    new PromptTemplateArgument("name", queue.Name),
                    new PromptTemplateArgument("size", queue.Size.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ]))
            .ToArray();
        return nonemptyQueues.Length == 0
            ? null
            : new ActiveWorkBlockerResult(
                null,
                promptTemplates.Render(
                    "agent-session.nonempty-queues-reminder",
                    [new PromptTemplateArgument("queues", string.Join('\n', nonemptyQueues))]));
    }
}

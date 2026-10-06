using System.Collections.Concurrent;
using Parrot.Llm;

namespace Parrot.Core.Tests;

/// <summary>Answers each request with the reply chosen by the test from the request itself.</summary>
internal sealed class AgentTaskReplyProvider(Func<LLMRequest, CancellationToken, Task<string>> reply) : ILLMProvider
{
    private readonly ConcurrentQueue<LLMRequest> _requests = new();

    public string Id => "agent-task-reply";

    internal IReadOnlyList<LLMRequest> Requests => [.. _requests];

    public IReadOnlyList<LLMModel> SeedModels() => [];

    public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LLMModel>>([]);

    public async IAsyncEnumerable<LLMEvent> Call(
        LLMRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _requests.Enqueue(request);
        var answer = await reply(request, cancellationToken).ConfigureAwait(false);
        yield return LLMEvent.Completed("stop", 1, 0, 1, answer, []);
    }

    internal static string Prompt(LLMRequest request) =>
        request.Messages.Last(message => message.Role == LLMRole.User).Content;

    internal static string Conversation(LLMRequest request) =>
        string.Join('\n', request.Messages.Where(message => message.Role == LLMRole.User).Select(message => message.Content));

    /// <summary>A task agent's request, as opposed to its owner's, by the AgentTask payload executor profile prompt.</summary>
    internal static bool IsTaskAgent(LLMRequest request) =>
        request.Messages.Any(message => message.Role == LLMRole.System
            && message.Content.Contains("AgentTask payload executor", StringComparison.Ordinal));
}

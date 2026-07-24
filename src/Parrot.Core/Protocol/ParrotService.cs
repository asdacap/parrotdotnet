using Grpc.Core;
using Parrot.Agent;
using Parrot.Events;
using Parrot.Llm;

namespace Parrot.Protocol;

// The gRPC contract. Commands in, one flat event stream out. It owns nothing
// and constructs no long-lived dependency.
internal sealed class ParrotService(ILLMProvider provider) : ParrotAgent.ParrotAgentBase
{
    public override async Task Chat(
        ChatRequest request,
        IServerStreamWriter<Event> responseStream,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);

        var cancellationToken = context.CancellationToken;
        var events = new EventBroker();
        var session = new AgentSession(Identifier.New(), provider, events);

        var turn = session.Run(request.Model, request.Prompt, cancellationToken);

        await foreach (var published in events.Subscribe(cancellationToken).ConfigureAwait(false))
        {
            await responseStream.WriteAsync(published, cancellationToken).ConfigureAwait(false);
        }

        // The drain completes the channel, so awaiting after the loop cannot
        // deadlock and still surfaces anything the turn threw.
        await turn.ConfigureAwait(false);
    }
}

namespace Parrot.Questions;

/// <summary>Coordinates user question requests and their pending replies for a session.</summary>
internal interface IQuestionBroker : IDisposable
{
    /// <summary>Waits for the user to answer the supplied questions.</summary>
    Task<QuestionReply> Ask(IReadOnlyList<QuestionDefinition> questions, CancellationToken cancellationToken);

    /// <summary>Returns pending user question requests in stable order.</summary>
    IReadOnlyList<PendingQuestionRequest> Pending();

    /// <summary>Settles a pending question request with a validated reply.</summary>
    void Reply(string requestId, QuestionReply reply);

    /// <summary>Rejects a pending question request.</summary>
    void Reject(string requestId);
}

namespace Parrot.Questions;

/// <summary>Holds the single question the owning agent may have open with its parent.</summary>
internal interface IChildQuestion
{
    /// <summary>Opens a question; throws QuestionRejectedException when one is already open.</summary>
    OpenChildQuestion Open(IReadOnlyList<QuestionDefinition> questions);

    /// <summary>Captures the open question, or null when none is open.</summary>
    PendingChildQuestionRequest? Snapshot();

    /// <summary>Validates the reply against the open question, settles it, and closes it.</summary>
    void Reply(QuestionReply reply);

    /// <summary>Drops the exact open question without settling it; returns false when it was already settled.</summary>
    bool Withdraw(OpenChildQuestion open);

    /// <summary>Fails the open question because the asking or answering side is closing.</summary>
    void Close();
}

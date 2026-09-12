namespace Parrot.Llm;

/// <summary>Records byte counts for one provider request attempt.</summary>
internal interface IProviderAttemptDiagnostics
{
    /// <summary>Gets the number of request bytes recorded for the attempt.</summary>
    long? RequestBytes { get; }

    /// <summary>Gets the number of response bytes recorded for the attempt.</summary>
    long? ResponseBytes { get; }

    /// <summary>Records the request body byte count.</summary>
    void RecordRequestBytes(int count);

    /// <summary>Marks that a response was obtained.</summary>
    void MarkResponseObtained();

    /// <summary>Adds response bytes to the attempt total.</summary>
    void RecordResponseBytes(int count);
}

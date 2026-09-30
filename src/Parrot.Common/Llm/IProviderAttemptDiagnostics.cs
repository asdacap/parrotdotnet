namespace Parrot.Llm;

/// <summary>Records byte counts and transport close status for one provider request attempt.</summary>
internal interface IProviderAttemptDiagnostics
{
    /// <summary>Gets the number of request bytes recorded for the attempt.</summary>
    long? RequestBytes { get; }

    /// <summary>Gets the number of response bytes recorded for the attempt.</summary>
    long? ResponseBytes { get; }

    /// <summary>Gets the close status the server sent, if the transport was closed by the server.</summary>
    int? CloseStatus { get; }

    /// <summary>Records the request body byte count.</summary>
    void RecordRequestBytes(int count);

    /// <summary>Marks that a response was obtained.</summary>
    void MarkResponseObtained();

    /// <summary>Adds response bytes to the attempt total.</summary>
    void RecordResponseBytes(int count);

    /// <summary>Records the close status the server sent.</summary>
    void RecordCloseStatus(int status);
}

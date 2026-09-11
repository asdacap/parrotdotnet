namespace Parrot.Diagnostics;

/// <summary>Allowlisted operational metadata. Identifiers and labels must not contain user payloads or paths.</summary>
internal sealed record DiagnosticEvent(string Category, string Operation, DiagnosticSeverity Severity)
{
    public string? UserSessionId { get; init; }

    public string? AgentSessionId { get; init; }

    public string? CorrelationId { get; init; }

    public string? RequestId { get; init; }

    public string? Transport { get; init; }

    public string? ProviderId { get; init; }

    public string? ModelId { get; init; }

    public string? ToolName { get; init; }

    public string? Outcome { get; init; }

    public long? DurationMilliseconds { get; init; }

    public long? Count { get; init; }

    public string? ErrorCode { get; init; }

    public static string ClassifyFailure(Exception failure) => failure switch
    {
        OperationCanceledException => "cancelled",
        UnauthorizedAccessException => "access_denied",
        IOException => "io",
        TimeoutException => "timeout",
        HttpRequestException => "http",
        ArgumentException => "argument",
        InvalidOperationException => "invalid_operation",
        _ => "unexpected",
    };
}

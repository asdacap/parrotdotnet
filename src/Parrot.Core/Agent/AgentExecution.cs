using System.Text;

namespace Parrot.Agent;

internal sealed record AgentExecution(AgentExecutionStatus Status, string Output, string Error)
{
    private const int MaxAgentMessageBytes = 1024 * 1024;

    public static AgentExecution Succeeded(string output) =>
        new(AgentExecutionStatus.Succeeded, output, string.Empty);

    public static AgentExecution Failed(string error) =>
        new(AgentExecutionStatus.Failed, string.Empty, error);

    public static AgentExecution Canceled() =>
        new(AgentExecutionStatus.Canceled, string.Empty, "interrupted");

    public string FormatCompletion(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var notification = new StringBuilder("Agent task notification\nChild agent session: ");
        AppendBounded(notification, identity.SessionId);
        _ = notification.Append("\nChild agent name: ");
        AppendBounded(notification, identity.Name);
        _ = notification.Append("\nStatus: ");
        AppendBounded(notification, StatusText());

        if (Error.Length > 0)
        {
            _ = notification.Append("\nError:\n");
            AppendBounded(notification, Error);
        }

        if (Output.Length > 0)
        {
            _ = notification.Append("\nOutput:\n");
            AppendBounded(notification, Output);
        }

        return notification.ToString();
    }

    private static void AppendBounded(StringBuilder notification, string value)
    {
        var remaining = MaxAgentMessageBytes - Encoding.UTF8.GetByteCount(notification.ToString());

        if (remaining <= 0)
        {
            return;
        }

        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Utf8SequenceLength > remaining)
            {
                return;
            }

            _ = notification.Append(rune);
            remaining -= rune.Utf8SequenceLength;
        }
    }

    private string StatusText() =>
        Status switch
        {
            AgentExecutionStatus.Succeeded => "succeeded",
            AgentExecutionStatus.Failed => "failed",
            _ => "canceled",
        };
}

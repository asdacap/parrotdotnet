using System.Text;
using System.Text.Json;
using Parrot.Llm;
using Parrot.Llm.Wire;

namespace Parrot.Core.Tests;

internal static class ReasoningHistoryFixture
{
    public static async Task<LLMEvent> Parse(
        string protocol, string providerId, string reasoning, bool callsTool, CancellationToken cancellationToken)
    {
        var text = callsTool ? "tool preface" : "done";
        var nativeReasoning = JsonSerializer.Serialize(reasoning, WireJsonContext.Default.String);
        var wire = protocol == "chat"
            ? "data: {\"choices\":[{\"delta\":{\"content\":\"" + text
                + "\",\"reasoning_content\":" + nativeReasoning
                + (callsTool ? ",\"tool_calls\":[{\"index\":0,\"id\":\"call-1\",\"function\":{\"name\":\"settled\",\"arguments\":\"{}\"}}]" : string.Empty)
                + "},\"finish_reason\":\"" + (callsTool ? "tool_calls" : "stop") + "\"}]}\n\n"
            : "data: {\"type\":\"response.output_text.delta\",\"delta\":\"" + text + "\"}\n\n"
                + (callsTool ? "data: {\"type\":\"response.output_item.done\",\"output_index\":2,\"item\":{\"type\":\"function_call\",\"id\":\"fc_1\",\"call_id\":\"call-1\",\"name\":\"settled\",\"arguments\":\"{}\"}}\n\n" : string.Empty)
                + "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"output\":[{\"type\":\"reasoning\",\"id\":\"rs_1\",\"encrypted_content\":"
                + nativeReasoning + ",\"summary\":[]},{\"type\":\"message\",\"id\":\"msg_1\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"" + text + "\"}]}"
                + (callsTool ? ",{\"type\":\"function_call\",\"id\":\"fc_1\",\"call_id\":\"call-1\",\"name\":\"settled\",\"arguments\":\"{}\"}" : string.Empty)
                + "]}}\n\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(wire));
        var events = protocol == "chat"
            ? ChatCompletionsAdapter.Parse(stream, 4096, providerId, cancellationToken)
            : ResponsesAdapter.Parse(stream, 4096, providerId, cancellationToken);
        LLMEvent? completed = null;
        await foreach (var published in events)
        {
            if (published.Kind == LLMEventKind.Completed)
            {
                completed = published;
            }
        }

        return completed ?? throw new InvalidOperationException("No completed response.");
    }

    public static JsonDocument Encode(string protocol, string providerId, IReadOnlyList<LLMMessage> messages)
    {
        var request = new LLMRequest { Model = "model", Messages = messages };
        return JsonDocument.Parse(protocol == "chat"
            ? ChatCompletionsAdapter.Encode(request, providerId)
            : ResponsesAdapter.Encode(request, providerId));
    }

    public static string[] ReadReasoning(string protocol, JsonDocument encoded) => protocol == "chat"
        ? [.. encoded.RootElement.GetProperty("messages").EnumerateArray()
            .Where(item => item.TryGetProperty("reasoning_content", out _))
            .Select(item => item.GetProperty("reasoning_content").GetString() ?? string.Empty)]
        : [.. encoded.RootElement.GetProperty("input").EnumerateArray()
            .Where(item => item.GetProperty("type").GetString() == "reasoning")
            .Select(item => item.GetProperty("encrypted_content").GetString() ?? string.Empty)];
}

using System.Text.Json;

namespace LiteRtLmSharp;

/// <summary>The author of a <see cref="LiteRtMessage"/> in a conversation history.</summary>
public enum LiteRtMessageRole
{
    /// <summary>A system instruction. Usually set via <see cref="LiteRtConversationOptions.SystemMessage"/>
    /// rather than as a history message; included for round-trip fidelity.</summary>
    System,
    /// <summary>A user turn.</summary>
    User,
    /// <summary>An assistant turn. Serialized to the wire role <c>"model"</c> (the engine may emit either
    /// <c>"model"</c> or <c>"assistant"</c> in its responses; both are normalized to this).</summary>
    Model,
    /// <summary>A tool-results turn, carrying the outputs of executed tools (see <see cref="ToolResults"/>).</summary>
    Tool,
}

/// <summary>
/// One message in a conversation history, used to <b>restore</b> a conversation: pass a list of these
/// as <see cref="LiteRtConversationOptions.History"/> and the engine re-prefills them when the
/// conversation is created. Build them with the factory methods (<see cref="User"/>, <see cref="Model"/>,
/// <see cref="Tool(System.Collections.Generic.IEnumerable{LiteRtToolResult})"/>, <see cref="System"/>);
/// capture an assistant turn from a live reply with <see cref="LiteRtResponse.ToMessage"/>.
/// <para>
/// The LiteRT-LM C API exposes no way to read a conversation's history back, so the round-trip is
/// <i>caller-owned</i>: record each turn as it happens, persist with <see cref="Serialize"/>, and
/// reload with <see cref="Deserialize"/> (or feed the serialized JSON straight to
/// <see cref="LiteRtConversationOptions.HistoryJson"/>). Restoring replays the history through prefill;
/// it is not a zero-cost KV-cache snapshot.
/// </para>
/// </summary>
public sealed class LiteRtMessage
{
    private LiteRtMessage(
        LiteRtMessageRole role, string? text,
        IReadOnlyList<LiteRtToolCall> toolCalls, IReadOnlyList<LiteRtToolResult> toolResults)
    {
        Role = role;
        Text = text;
        ToolCalls = toolCalls;
        ToolResults = toolResults;
    }

    /// <summary>Who authored the message.</summary>
    public LiteRtMessageRole Role { get; }

    /// <summary>The text content, or <c>null</c> when the message carries only tool calls or tool results.</summary>
    public string? Text { get; }

    /// <summary>Tool calls the assistant requested on a <see cref="LiteRtMessageRole.Model"/> turn (empty otherwise).</summary>
    public IReadOnlyList<LiteRtToolCall> ToolCalls { get; }

    /// <summary>Tool outputs carried by a <see cref="LiteRtMessageRole.Tool"/> turn (empty otherwise).</summary>
    public IReadOnlyList<LiteRtToolResult> ToolResults { get; }

    /// <summary>A system message. Prefer <see cref="LiteRtConversationOptions.SystemMessage"/> for the
    /// active system prompt — set the system prompt in one place only (the native side prepends
    /// <c>SystemMessage</c> before the history, so doing both yields two system turns).</summary>
    public static LiteRtMessage System(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new LiteRtMessage(LiteRtMessageRole.System, text, [], []);
    }

    /// <summary>A user message.</summary>
    public static LiteRtMessage User(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new LiteRtMessage(LiteRtMessageRole.User, text, [], []);
    }

    /// <summary>An assistant ("model") message, optionally carrying the tool calls it requested.</summary>
    public static LiteRtMessage Model(string text, IReadOnlyList<LiteRtToolCall>? toolCalls = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new LiteRtMessage(LiteRtMessageRole.Model, text, toolCalls ?? [], []);
    }

    /// <summary>A tool-results message, carrying the outputs you executed for the assistant's tool calls.</summary>
    public static LiteRtMessage Tool(IEnumerable<LiteRtToolResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return new LiteRtMessage(LiteRtMessageRole.Tool, null, [], [.. results]);
    }

    /// <summary>A tool-results message, carrying the outputs you executed for the assistant's tool calls.</summary>
    public static LiteRtMessage Tool(params LiteRtToolResult[] results) => Tool((IEnumerable<LiteRtToolResult>)results);

    /// <summary>Builds a model turn allowing null text (a pure tool-call turn). Used by
    /// <see cref="LiteRtResponse.ToMessage"/>, where the public, non-null <see cref="Model"/> overload
    /// would reject a tool-call-only reply.</summary>
    internal static LiteRtMessage ModelTurn(string? text, IReadOnlyList<LiteRtToolCall> toolCalls)
        => new(LiteRtMessageRole.Model, text, toolCalls, []);

    /// <summary>Serializes a history to the LiteRT-LM messages-array JSON — the same format
    /// <see cref="LiteRtConversationOptions.HistoryJson"/> accepts and <see cref="Deserialize"/> reads.
    /// Persist this string to restore the conversation later.</summary>
    public static string Serialize(IEnumerable<LiteRtMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return LiteRtJson.Messages([.. messages]);
    }

    /// <summary>Parses a messages-array JSON (as produced by <see cref="Serialize"/>) back into a typed
    /// history. Unknown roles map to the closest known one (<c>"assistant"</c> → <see cref="LiteRtMessageRole.Model"/>);
    /// throws <see cref="ArgumentException"/> when the JSON is not an array.</summary>
    public static IReadOnlyList<LiteRtMessage> Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new ArgumentException("History JSON is not valid JSON.", nameof(json), ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new ArgumentException(
                    $"History JSON must be an array of messages (got {doc.RootElement.ValueKind}).", nameof(json));

            var list = new List<LiteRtMessage>(doc.RootElement.GetArrayLength());
            foreach (JsonElement msg in doc.RootElement.EnumerateArray())
                list.Add(ParseMessage(msg));
            return list;
        }
    }

    private static LiteRtMessage ParseMessage(JsonElement msg)
    {
        string role = msg.TryGetProperty("role", out var r) ? r.GetString() ?? "" : "";
        LiteRtMessageRole parsedRole = role switch
        {
            "system" => LiteRtMessageRole.System,
            "tool" => LiteRtMessageRole.Tool,
            "model" or "assistant" => LiteRtMessageRole.Model,
            _ => LiteRtMessageRole.User,
        };

        string? text = null;
        var toolResults = new List<LiteRtToolResult>();
        if (msg.TryGetProperty("content", out var content))
        {
            // The wire format allows content three ways: an array of parts, a single part object, or a
            // bare string (a raw system message, or a foreign client's message). Handle all three so the
            // HistoryJson escape hatch round-trips externally-produced JSON, matching the live-response
            // parser (LiteRtResponse.ExtractText), which already accepts string content.
            switch (content.ValueKind)
            {
                case JsonValueKind.String:
                    text = content.GetString();
                    break;
                case JsonValueKind.Array:
                    foreach (JsonElement part in content.EnumerateArray())
                        ParseContentPart(part, ref text, toolResults);
                    break;
                case JsonValueKind.Object:
                    ParseContentPart(content, ref text, toolResults);
                    break;
            }
        }

        var toolCalls = new List<LiteRtToolCall>();
        if (msg.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement call in calls.EnumerateArray())
            {
                JsonElement fn = call.TryGetProperty("function", out var f) ? f : call;
                string name = fn.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                string args = "{}";
                if (fn.TryGetProperty("arguments", out var a) || fn.TryGetProperty("args", out a))
                    args = a.ValueKind == JsonValueKind.String ? a.GetString() ?? "{}" : a.GetRawText();
                if (name.Length > 0)
                    toolCalls.Add(new LiteRtToolCall(name, args));
            }
        }

        return new LiteRtMessage(parsedRole, text, toolCalls, toolResults);
    }

    /// <summary>Reads one content part (a <c>{"type":"tool_response",…}</c> tool output or a
    /// <c>{"text":…}</c> text part), accumulating text and tool results.</summary>
    private static void ParseContentPart(JsonElement part, ref string? text, List<LiteRtToolResult> toolResults)
    {
        string type = part.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        if (type == "tool_response" && part.TryGetProperty("name", out var tn))
        {
            string name = tn.GetString() ?? "";
            string resultJson = part.TryGetProperty("response", out var resp) ? resp.GetRawText() : "null";
            if (name.Length > 0)
                toolResults.Add(new LiteRtToolResult(name, resultJson));
        }
        else if (part.TryGetProperty("text", out var textEl))
        {
            text = (text ?? "") + (textEl.GetString() ?? "");
        }
    }
}

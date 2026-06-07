using System.Buffers;
using System.Text;
using System.Text.Json;

namespace LiteLMSharp;

/// <summary>
/// A tool the model may call. <see cref="ParametersJson"/> is a JSON-Schema object string
/// (the <c>parameters</c> of a Gemini/OpenAI function declaration), e.g.
/// <c>{"type":"object","properties":{"location":{"type":"string"}},"required":["location"]}</c>.
/// </summary>
public sealed record LiteRtTool(string Name, string? Description, string ParametersJson)
{
    /// <summary>An empty parameter schema, for tools that take no arguments.</summary>
    public const string NoParameters = """{"type":"object","properties":{}}""";
}

/// <summary>A tool/function call emitted by the model. <see cref="ArgumentsJson"/> is a raw JSON object.</summary>
public sealed record LiteRtToolCall(string Name, string ArgumentsJson);

/// <summary>The result of executing a tool, to be sent back to the model.
/// <see cref="ResultJson"/> is a raw JSON value (object/array/scalar).</summary>
public sealed record LiteRtToolResult(string Name, string ResultJson);

/// <summary>
/// A model response: either plain <see cref="Text"/> or one or more <see cref="ToolCalls"/>.
/// <see cref="RawJson"/> always holds the unparsed response (escape hatch).
/// </summary>
public sealed class LiteRtResponse
{
    internal LiteRtResponse(string? text, IReadOnlyList<LiteRtToolCall> toolCalls, string rawJson)
    {
        Text = text;
        ToolCalls = toolCalls;
        RawJson = rawJson;
    }

    /// <summary>The assistant text, or null when the model emitted tool calls.</summary>
    public string? Text { get; }

    /// <summary>Tool calls the model wants executed (empty for a plain text answer).</summary>
    public IReadOnlyList<LiteRtToolCall> ToolCalls { get; }

    /// <summary>The raw response JSON from the engine.</summary>
    public string RawJson { get; }

    /// <summary>True when the model requested one or more tool calls.</summary>
    public bool IsToolCall => ToolCalls.Count > 0;

    /// <summary>
    /// Parses an engine response JSON into a <see cref="LiteRtResponse"/>.
    /// Recognized shapes (tolerant):
    ///   tool call:  {"role":"assistant","tool_calls":[{"function":{"name","arguments"}}]}
    ///   text:       {"role":"assistant","content":[{"type":"text","text":"..."}]}
    /// </summary>
    internal static LiteRtResponse Parse(string json)
    {
        if (string.IsNullOrEmpty(json))
            return new LiteRtResponse(string.Empty, [], json);

        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array && calls.GetArrayLength() > 0)
            {
                var list = new List<LiteRtToolCall>(calls.GetArrayLength());
                foreach (var call in calls.EnumerateArray())
                {
                    // Prefer {"function":{"name","arguments"}}, fall back to {"name","args"|"arguments"}.
                    JsonElement fn = call.TryGetProperty("function", out var f) ? f : call;
                    string name = StripControlTokens(fn.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "");
                    string args = "{}";
                    if (fn.TryGetProperty("arguments", out var a) || fn.TryGetProperty("args", out a))
                        args = a.ValueKind == JsonValueKind.String ? StripControlTokens(a.GetString() ?? "{}") : CleanJson(a);
                    if (name.Length > 0)
                        list.Add(new LiteRtToolCall(name, args));
                }
                if (list.Count > 0)
                    return new LiteRtResponse(null, list, json);
            }

            return new LiteRtResponse(ExtractText(root), [], json);
        }
        catch (JsonException)
        {
            return new LiteRtResponse(json, [], json);
        }
    }

    // Gemma's chat template uses special tokens (e.g. <|"|>) as string delimiters; with constrained
    // decoding they can leak into parsed tool-call arguments. Strip them so tools get clean values.
    private static string StripControlTokens(string s)
        => s.Replace("<|\"|>", string.Empty, StringComparison.Ordinal);

    /// <summary>Re-serializes a JSON element, stripping control tokens from all string values.</summary>
    private static string CleanJson(JsonElement el)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
            WriteCleaned(el, w);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);

        static void WriteCleaned(JsonElement e, Utf8JsonWriter w)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    w.WriteStartObject();
                    foreach (var p in e.EnumerateObject()) { w.WritePropertyName(StripControlTokens(p.Name)); WriteCleaned(p.Value, w); }
                    w.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    w.WriteStartArray();
                    foreach (var item in e.EnumerateArray()) WriteCleaned(item, w);
                    w.WriteEndArray();
                    break;
                case JsonValueKind.String:
                    w.WriteStringValue(StripControlTokens(e.GetString() ?? string.Empty));
                    break;
                case JsonValueKind.Number:
                    w.WriteRawValue(e.GetRawText());
                    break;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    w.WriteBooleanValue(e.GetBoolean());
                    break;
                default:
                    w.WriteNullValue();
                    break;
            }
        }
    }

    /// <summary>Extracts <c>content[0].text</c> (or a bare <c>content</c> string) from a message element.</summary>
    internal static string ExtractText(JsonElement root)
    {
        if (root.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String)
                return content.GetString() ?? string.Empty;
            if (content.ValueKind == JsonValueKind.Array && content.GetArrayLength() > 0
                && content[0].TryGetProperty("text", out var textEl))
                return textEl.GetString() ?? string.Empty;
        }
        return string.Empty;
    }
}

/// <summary>JSON builders for the LiteRT-LM conversation message/tool wire format.</summary>
internal static class LiteRtJson
{
    /// <summary>{"role":"user","content":[{"type":"text","text":...}]}</summary>
    public static string UserMessage(string text)
        => Build(w =>
        {
            w.WriteStartObject();
            w.WriteString("role", "user");
            w.WriteStartArray("content");
            w.WriteStartObject();
            w.WriteString("type", "text");
            w.WriteString("text", text);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        });

    /// <summary>{"type":"text","text":...} — for the system message config.</summary>
    public static string SystemMessage(string text)
        => Build(w =>
        {
            w.WriteStartObject();
            w.WriteString("type", "text");
            w.WriteString("text", text);
            w.WriteEndObject();
        });

    /// <summary>[{"type":"function","function":{"name","description","parameters":&lt;schema&gt;}}]</summary>
    public static string Tools(IReadOnlyList<LiteRtTool> tools)
        => Build(w =>
        {
            w.WriteStartArray();
            foreach (var t in tools)
            {
                w.WriteStartObject();
                w.WriteString("type", "function");
                w.WriteStartObject("function");
                w.WriteString("name", t.Name);
                if (!string.IsNullOrEmpty(t.Description))
                    w.WriteString("description", t.Description);
                w.WritePropertyName("parameters");
                w.WriteRawValue(string.IsNullOrWhiteSpace(t.ParametersJson) ? LiteRtTool.NoParameters : t.ParametersJson);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });

    /// <summary>{"role":"tool","content":[{"name":...,"response":&lt;result&gt;}]}</summary>
    public static string ToolResults(IEnumerable<LiteRtToolResult> results)
        => Build(w =>
        {
            w.WriteStartObject();
            w.WriteString("role", "tool");
            w.WriteStartArray("content");
            foreach (var r in results)
            {
                w.WriteStartObject();
                w.WriteString("name", r.Name);
                w.WritePropertyName("response");
                w.WriteRawValue(string.IsNullOrWhiteSpace(r.ResultJson) ? "null" : r.ResultJson);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });

    private static string Build(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
            write(w);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

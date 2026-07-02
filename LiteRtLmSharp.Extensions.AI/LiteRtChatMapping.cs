using System.Text.Json;
using Microsoft.Extensions.AI;

namespace LiteRtLmSharp.Extensions.AI;

/// <summary>
/// Maps Microsoft.Extensions.AI chat types onto the stateless LiteRtLmSharp conversation model. MEAI passes
/// the full message list every call; LiteRtLmSharp conversations are stateful, so each call rebuilds a fresh
/// one: every message except the last becomes restored <see cref="LiteRtConversationOptions.History"/>
/// (replayed through prefill) and the final turn is the action that triggers generation — a user message to
/// send, or the results of executed tools to hand back (the function-calling continuation).
/// </summary>
internal static class LiteRtChatMapping
{
    /// <summary>
    /// What triggers generation for a request: either user <paramref name="UserText"/> (with optional image/audio
    /// <paramref name="Attachments"/>) to send, or the <paramref name="ToolResults"/> of executed tools to return
    /// to the model (function-calling continuation).
    /// </summary>
    internal readonly record struct SendTrigger(
        string? UserText, IReadOnlyList<LiteRtToolResult>? ToolResults, IReadOnlyList<LiteRtAttachment>? Attachments)
    {
        /// <summary>True when this trigger returns tool results rather than sending a user message.</summary>
        public bool IsToolResults => ToolResults is { Count: > 0 };

        /// <summary>True when the user message carries image/audio attachments.</summary>
        public bool HasAttachments => Attachments is { Count: > 0 };
    }

    /// <summary>
    /// Splits <paramref name="messages"/> into the prior turns to restore as history and the final action
    /// that triggers generation. The list must be non-empty and end with either a <see cref="ChatRole.User"/>
    /// message (send its text) or a <see cref="ChatRole.Tool"/> message (return the tool results — the
    /// function-calling continuation, as appended by <c>FunctionInvokingChatClient</c> / Semantic Kernel).
    /// </summary>
    public static (IReadOnlyList<LiteRtMessage> History, SendTrigger Trigger) Split(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        IReadOnlyList<ChatMessage> list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        if (list.Count == 0)
            throw new ArgumentException("The message list is empty; add at least one user message.", nameof(messages));

        // A FunctionResultContent carries only a CallId, so map every call id seen in the conversation back to
        // its tool name (from the assistant turns that requested the calls) to translate results to native names.
        IReadOnlyDictionary<string, string> callIdToName = BuildCallIdToName(list);

        var history = new List<LiteRtMessage>(list.Count - 1);
        for (int i = 0; i < list.Count - 1; i++)
            history.Add(ToMessage(list[i], callIdToName));

        ChatMessage last = list[list.Count - 1];

        if (last.Role == ChatRole.Tool)
        {
            IReadOnlyList<LiteRtToolResult> results = ToToolResults(last, callIdToName);
            if (results.Count == 0)
                throw new ArgumentException(
                    "The final tool message carried no function results to return to the model.", nameof(messages));
            return (history, new SendTrigger(null, results, null));
        }

        if (last.Role == ChatRole.User)
            return (history, new SendTrigger(last.Text ?? string.Empty, null, ToAttachments(last)));

        throw new ArgumentException(
            $"The message list must end with a user message, or a tool message carrying function results " +
            $"(the last message's role was '{last.Role.Value}').", nameof(messages));
    }

    /// <summary>Maps every <see cref="FunctionCallContent"/>'s <c>CallId</c> in the conversation to its tool name.</summary>
    private static IReadOnlyDictionary<string, string> BuildCallIdToName(IReadOnlyList<ChatMessage> list)
    {
        Dictionary<string, string>? map = null;
        foreach (ChatMessage m in list)
            foreach (AIContent c in m.Contents)
                if (c is FunctionCallContent { CallId: { Length: > 0 } id } call)
                    (map ??= new Dictionary<string, string>(StringComparer.Ordinal))[id] = call.Name;
        return map ?? EmptyMap;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyMap = new Dictionary<string, string>(0);

    /// <summary>Maps one MEAI message to a native history message, preserving tool calls (on an assistant
    /// turn) and tool results (on a tool turn).</summary>
    private static LiteRtMessage ToMessage(ChatMessage message, IReadOnlyDictionary<string, string> callIdToName)
    {
        if (message.Role == ChatRole.System) return LiteRtMessage.System(message.Text ?? string.Empty);
        if (message.Role == ChatRole.User)
        {
            // Carry any image/audio on a prior user turn into the restored history (re-encoded through
            // prefill), so a multi-turn multimodal thread keeps its earlier media.
            IReadOnlyList<LiteRtAttachment>? userAttachments = ToAttachments(message);
            return userAttachments is { Count: > 0 }
                ? LiteRtMessage.User(message.Text ?? string.Empty, userAttachments)
                : LiteRtMessage.User(message.Text ?? string.Empty);
        }
        if (message.Role == ChatRole.Tool) return LiteRtMessage.Tool(ToToolResults(message, callIdToName));

        if (message.Role == ChatRole.Assistant)
        {
            IReadOnlyList<LiteRtToolCall> calls = ToToolCalls(message);
            return LiteRtMessage.Model(message.Text ?? string.Empty, calls.Count > 0 ? calls : null);
        }

        throw new NotSupportedException(
            $"Chat message role '{message.Role.Value}' is not supported by the LiteRtLmSharp chat client. " +
            "Roles handled: system, user, assistant, tool.");
    }

    /// <summary>Extracts the assistant's <see cref="FunctionCallContent"/> as native tool calls.</summary>
    private static IReadOnlyList<LiteRtToolCall> ToToolCalls(ChatMessage message)
    {
        List<LiteRtToolCall>? calls = null;
        foreach (AIContent c in message.Contents)
            if (c is FunctionCallContent fcc)
                (calls ??= []).Add(new LiteRtToolCall(fcc.Name, SerializeArguments(fcc.Arguments)));
        return calls ?? (IReadOnlyList<LiteRtToolCall>)[];
    }

    /// <summary>Extracts a tool message's <see cref="FunctionResultContent"/> as native tool results,
    /// resolving each result's tool name from its <c>CallId</c>.</summary>
    private static IReadOnlyList<LiteRtToolResult> ToToolResults(ChatMessage message, IReadOnlyDictionary<string, string> callIdToName)
    {
        List<LiteRtToolResult>? results = null;
        foreach (AIContent c in message.Contents)
            if (c is FunctionResultContent frc)
            {
                string name = frc.CallId is { Length: > 0 } id && callIdToName.TryGetValue(id, out string? n)
                    ? n
                    : frc.CallId ?? string.Empty;
                (results ??= []).Add(new LiteRtToolResult(name, SerializeResult(frc.Result)));
            }
        return results ?? (IReadOnlyList<LiteRtToolResult>)[];
    }

    /// <summary>Maps a user message's image/audio content (inline <see cref="DataContent"/>, or a file-path
    /// <see cref="UriContent"/>) to native <see cref="LiteRtAttachment"/>s, in order, or <c>null</c> when there
    /// are none. Remote (non-file) URIs are skipped — the on-device engine cannot fetch them; supply bytes
    /// (a <see cref="DataContent"/>) or a local file instead.</summary>
    private static IReadOnlyList<LiteRtAttachment>? ToAttachments(ChatMessage message)
    {
        List<LiteRtAttachment>? list = null;
        foreach (AIContent c in message.Contents)
        {
            LiteRtAttachment? attachment = c switch
            {
                DataContent dc when dc.HasTopLevelMediaType("image") => LiteRtAttachment.Image(dc.Data.Span),
                DataContent dc when dc.HasTopLevelMediaType("audio") => LiteRtAttachment.Audio(dc.Data.Span),
                // IsAbsoluteUri guards IsFile, which throws on a relative Uri — a relative/remote URI then
                // falls through to null (skipped) rather than throwing, like any other unfetchable reference.
                UriContent uc when uc.Uri.IsAbsoluteUri && uc.Uri.IsFile && uc.HasTopLevelMediaType("image") => LiteRtAttachment.ImageFile(uc.Uri.LocalPath),
                UriContent uc when uc.Uri.IsAbsoluteUri && uc.Uri.IsFile && uc.HasTopLevelMediaType("audio") => LiteRtAttachment.AudioFile(uc.Uri.LocalPath),
                _ => null,
            };
            if (attachment is not null)
                (list ??= []).Add(attachment);
        }
        return list;
    }

    /// <summary>Builds a <see cref="FunctionCallContent"/> from a native tool call. The native protocol has no
    /// call ids, so one is synthesized (stable within a response); the connector recovers the tool name from it
    /// via <see cref="BuildCallIdToName"/> when the result comes back.</summary>
    public static FunctionCallContent ToFunctionCall(LiteRtToolCall call, int index)
        => new($"{call.Name}_{index}", call.Name, ParseArguments(call.ArgumentsJson));

    private static string SerializeArguments(IDictionary<string, object?>? arguments)
        => arguments is { Count: > 0 } ? JsonSerializer.Serialize(arguments) : "{}";

    // Tool results must be valid JSON for the native tool_response writer (Utf8JsonWriter.WriteRawValue
    // validates, so a plain-text return like "{user} not found" or "[no results]" would throw and abort the
    // send). Serializing always yields valid JSON and matches MEAI's own result handling: a string result
    // becomes a JSON string; a structured result (object / JsonElement) becomes JSON structure. Return
    // structured data from your function (not a pre-serialized string) when you want the model to see an object.
    private static string SerializeResult(object? result)
        => result is null ? "null" : JsonSerializer.Serialize(result);

    private static IDictionary<string, object?>? ParseArguments(string argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson)) return null;
        try { return JsonSerializer.Deserialize<IDictionary<string, object?>>(argsJson); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Builds the <see cref="LiteRtConversationOptions"/> for a request, or <c>null</c> when there is nothing
    /// to set (no history, options, or tools) so a plain conversation is created.
    /// </summary>
    public static LiteRtConversationOptions? ToConversationOptions(IReadOnlyList<LiteRtMessage> history, ChatOptions? options)
    {
        LiteRtSamplerParams? sampler = ToSampler(options);
        bool? enableThinking = GetEnableThinking(options);
        bool constrained = GetConstrainedDecoding(options);
        IReadOnlyList<LiteRtTool>? tools = ToTools(options);
        int maxOutput = options?.MaxOutputTokens ?? 0;

        if (history.Count == 0 && sampler is null && maxOutput == 0 && enableThinking is null && tools is null && !constrained)
            return null;

        return new LiteRtConversationOptions
        {
            History = history.Count > 0 ? history : null,
            Sampler = sampler,
            MaxOutputTokens = maxOutput,
            EnableThinking = enableThinking,
            Tools = tools,
            EnableConstrainedDecoding = constrained,
        };
    }

    /// <summary>
    /// Maps the function tools in <see cref="ChatOptions.Tools"/> to native <see cref="LiteRtTool"/>s (non-function
    /// tools are ignored), honoring <see cref="ChatOptions.ToolMode"/>. The native API has no tool-choice flag, so:
    /// <see cref="NoneChatToolMode"/> advertises <b>no</b> tools (emulating "none" — the model can't call what it
    /// isn't given); a <see cref="RequiredChatToolMode"/> naming a function advertises <b>only</b> that one (so the
    /// model's only option is the required tool). Returns <c>null</c> when there are no tools to advertise.
    /// </summary>
    private static IReadOnlyList<LiteRtTool>? ToTools(ChatOptions? o)
    {
        if (o?.Tools is not { Count: > 0 } tools)
            return null;

        if (o.ToolMode is NoneChatToolMode)
            return null;

        // RequireSpecific("x") → offer only x; RequireAny / Auto → offer all.
        string? only = (o.ToolMode as RequiredChatToolMode)?.RequiredFunctionName is { Length: > 0 } name ? name : null;

        List<LiteRtTool>? list = null;
        foreach (AITool t in tools)
            if (t is AIFunction f && (only is null || f.Name == only))
                (list ??= []).Add(new LiteRtTool(
                    f.Name,
                    string.IsNullOrEmpty(f.Description) ? null : f.Description,
                    f.JsonSchema.ValueKind == JsonValueKind.Object ? f.JsonSchema.GetRawText() : LiteRtTool.NoParameters));
        return list;
    }

    /// <summary>
    /// The best-effort instruction to add for a <see cref="RequiredChatToolMode"/> request, or <c>null</c> for
    /// Auto/None. The native API has no forced tool choice (no equivalent of a server's <c>tool_choice: required</c>),
    /// so "required" is approximated by instructing the model to call a tool. Paired with <see cref="ToTools"/>
    /// offering only the named function, this nudges — but does not guarantee — a tool call.
    /// </summary>
    public static string? RequiredToolInstruction(ChatOptions? o)
    {
        if (o?.Tools is not { Count: > 0 } tools || o.ToolMode is not RequiredChatToolMode required)
            return null;
        // Emphatic phrasing (MUST / DO NOT) is a recognized instruction-following nudge; it is best-effort and
        // a starting point to refine, not a guarantee on a small on-device model.
        if (required.RequiredFunctionName is { Length: > 0 } name)
            // Only require the named tool if it is actually offered — RequireSpecific with a missing name leaves
            // ToTools advertising nothing, so a "you MUST use foo" instruction would be misleading.
            return tools.OfType<AIFunction>().Any(f => f.Name == name)
                ? $"You MUST use the `{name}` tool to answer this request. DO NOT reply with plain text."
                : null;
        return "You MUST use one of the available tools to answer this request. DO NOT reply with plain text.";
    }

    /// <summary>
    /// Returns <paramref name="history"/> with the <see cref="RequiredToolInstruction"/> folded into the system
    /// prompt for a <see cref="RequiredChatToolMode"/> request — appended to the leading system message, or added as
    /// one when there is none — and unchanged otherwise (Auto/None).
    /// </summary>
    public static IReadOnlyList<LiteRtMessage> WithRequiredToolInstruction(IReadOnlyList<LiteRtMessage> history, ChatOptions? options)
    {
        if (RequiredToolInstruction(options) is not { } instruction)
            return history;

        var list = new List<LiteRtMessage>(history);
        for (int i = 0; i < list.Count; i++)
            if (list[i].Role == LiteRtMessageRole.System)
            {
                list[i] = LiteRtMessage.System($"{list[i].Text}\n\n{instruction}");
                return list;
            }
        list.Insert(0, LiteRtMessage.System(instruction));
        return list;
    }

    /// <summary>The <c>AdditionalProperties</c> key (on the <see cref="ChatResponse"/> / <see cref="ChatResponseUpdate"/>)
    /// under which the connector leaves <see cref="UsageBenchmarkNote"/> when the input/output token split is absent
    /// (<see cref="UsageDetails"/> has no property bag of its own).</summary>
    public const string UsageBenchmarkNoteKey = "litertlm.usage_note";

    /// <summary>The hint left under <see cref="UsageBenchmarkNoteKey"/> explaining why the input/output token split
    /// is missing and how to get it.</summary>
    public const string UsageBenchmarkNote =
        "UsageDetails.InputTokenCount/OutputTokenCount are null because the engine was not loaded with " +
        "LiteRtEngineOptions.EnableBenchmark = true; only TotalTokenCount is available without it.";

    /// <summary>
    /// Builds the token <see cref="UsageDetails"/> for a completed turn. <paramref name="totalTokens"/> (the
    /// conversation's KV-cache size = prompt + reply, from <see cref="LiteRtConversation.TokenCount"/>) is always
    /// set, at no cost. The input/output split (<see cref="UsageDetails.InputTokenCount"/> /
    /// <see cref="UsageDetails.OutputTokenCount"/>) is taken from <paramref name="benchmark"/> when present — which
    /// requires the engine to have been loaded with <see cref="LiteRtEngineOptions.EnableBenchmark"/> = <c>true</c>;
    /// when it is <c>null</c> those stay <c>null</c> (the caller leaves <see cref="UsageBenchmarkNote"/> nearby).
    /// </summary>
    public static UsageDetails BuildUsage(int totalTokens, LiteRtBenchmarkInfo? benchmark)
    {
        var usage = new UsageDetails { TotalTokenCount = totalTokens };
        if (benchmark is not null)
        {
            usage.InputTokenCount = benchmark.LastPrefillTokenCount;
            usage.OutputTokenCount = benchmark.LastDecodeTokenCount;
        }
        return usage;
    }

    /// <summary>Maps the sampler knobs from <see cref="ChatOptions"/>, or <c>null</c> when none are set.</summary>
    private static LiteRtSamplerParams? ToSampler(ChatOptions? o)
    {
        if (o is null || (o.Temperature is null && o.TopP is null && o.TopK is null && o.Seed is null))
            return null;

        var defaults = new LiteRtSamplerParams();
        return new LiteRtSamplerParams
        {
            Strategy = LiteRtSamplerType.TopP,
            TopK = o.TopK ?? defaults.TopK,
            TopP = o.TopP ?? defaults.TopP,
            Temperature = o.Temperature ?? defaults.Temperature,
            // ChatOptions.Seed is a long?; the native seed is an int. Unset falls back to the
            // cross-binding default (0, deterministic) like every official LiteRT-LM binding.
            Seed = o.Seed is { } seed ? unchecked((int)seed) : defaults.Seed,
        };
    }

    /// <summary>Reads the optional <c>enable_thinking</c> flag from <see cref="ChatOptions.AdditionalProperties"/>.</summary>
    private static bool? GetEnableThinking(ChatOptions? o)
        => GetBoolProperty(o, "enable_thinking");

    /// <summary>Reads the optional <c>enable_constrained_decoding</c> flag (see
    /// <see cref="LiteRtChatOptions.EnableConstrainedDecoding"/>); default <c>false</c>.</summary>
    private static bool GetConstrainedDecoding(ChatOptions? o)
        => GetBoolProperty(o, "enable_constrained_decoding") ?? false;

    private static bool? GetBoolProperty(ChatOptions? o, string key)
        => o?.AdditionalProperties is { } props && props.TryGetValue(key, out object? v) ? AsBool(v) : null;

    /// <summary>Coerces a boolean flag stored in a property bag, tolerating the boxed <see cref="bool"/> the typed
    /// setters write, a <see cref="string"/>, and a <see cref="JsonElement"/> (true/false, or a string) that arrives
    /// when options are deserialized from JSON/YAML. Shared so every reader (and <see cref="LiteRtChatOptions"/>) agrees.</summary>
    internal static bool? AsBool(object? v) => v switch
    {
        bool b => b,
        string s when bool.TryParse(s, out bool r) => r,
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => false,
        JsonElement { ValueKind: JsonValueKind.String } e when bool.TryParse(e.GetString(), out bool r) => r,
        _ => null,
    };
}

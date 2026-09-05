using System.Globalization;
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

    /// <summary>
    /// Maps a <b>continuation</b> request on an existing stateful conversation (the caller set
    /// <see cref="ChatOptions.ConversationId"/>) to the action that triggers generation. Unlike
    /// <see cref="Split"/>, there is <b>no</b> history: the live native conversation already holds every prior
    /// turn, so per the MEAI stateful contract the incoming <paramref name="messages"/> are only the ones the
    /// provider has not seen yet — typically the new user turn, or the results of executed tools (the
    /// function-calling continuation). The last message decides the trigger: a <see cref="ChatRole.User"/>
    /// message sends its text (and any image/audio); a <see cref="ChatRole.Tool"/> message returns its
    /// function results. Any assistant / model turn in the incoming list is ignored (the native conversation
    /// already holds it, and <c>FunctionInvokingChatClient</c> does not resend it). A
    /// <see cref="ChatRole.System"/> message throws <see cref="InvalidOperationException"/>: a live
    /// conversation's preface is fixed at creation and cannot be rewritten mid-thread.
    /// </summary>
    /// <param name="messages">The new, not-yet-seen messages for this turn.</param>
    /// <param name="knownCallIdToName">The synthesized-call-id → tool-name map accumulated on the stored
    /// conversation as it emitted tool calls. Authoritative for resolving a <see cref="FunctionResultContent"/>'s
    /// tool name, because the assistant turn that named the call is not resent on a continuation.</param>
    /// <exception cref="ArgumentException">The list is empty, ends with an unsupported role, or a final tool
    /// message carried no results.</exception>
    /// <exception cref="InvalidOperationException">A system message was included.</exception>
    public static SendTrigger SplitContinuation(
        IEnumerable<ChatMessage> messages, IReadOnlyDictionary<string, string> knownCallIdToName)
    {
        ArgumentNullException.ThrowIfNull(messages);
        IReadOnlyList<ChatMessage> list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        if (list.Count == 0)
            throw new ArgumentException(
                "The continuation message list is empty; send the new user message (or the tool results).",
                nameof(messages));

        // A system message cannot re-preface a live conversation (the preface is fixed at creation).
        foreach (ChatMessage m in list)
            if (m.Role == ChatRole.System)
                throw new InvalidOperationException(
                    "A system message cannot be sent on an existing stateful conversation (ChatOptions." +
                    "ConversationId is set): the system prompt is fixed when the conversation is created. " +
                    "On a continuation send ONLY the new messages (typically just the latest user turn or " +
                    "the tool results), not the full history. Start a new conversation (omit ConversationId) " +
                    "to change the system prompt.");

        // Resolve tool names from the conversation's accumulated map first (the assistant tool-call turn is
        // not resent on a continuation), supplemented by any FunctionCallContent present in this list.
        IReadOnlyDictionary<string, string> callIdToName = MergeCallIdToName(knownCallIdToName, list);

        ChatMessage last = list[list.Count - 1];

        if (last.Role == ChatRole.Tool)
        {
            IReadOnlyList<LiteRtToolResult> results = ToToolResults(last, callIdToName);
            if (results.Count == 0)
                throw new ArgumentException(
                    "The final tool message carried no function results to return to the model.", nameof(messages));
            return new SendTrigger(null, results, null);
        }

        if (last.Role == ChatRole.User)
            return new SendTrigger(last.Text ?? string.Empty, null, ToAttachments(last));

        throw new ArgumentException(
            $"A continuation request must end with a user message, or a tool message carrying function results " +
            $"(the last message's role was '{last.Role.Value}').", nameof(messages));
    }

    /// <summary>Overlays any <see cref="FunctionCallContent"/> ids found in <paramref name="list"/> onto the
    /// conversation's accumulated <paramref name="known"/> map (the list wins for a shared id), returning the
    /// combined view. Returns <paramref name="known"/> unchanged when the list adds nothing.</summary>
    private static IReadOnlyDictionary<string, string> MergeCallIdToName(
        IReadOnlyDictionary<string, string> known, IReadOnlyList<ChatMessage> list)
    {
        Dictionary<string, string>? merged = null;
        foreach (ChatMessage m in list)
            foreach (AIContent c in m.Contents)
                if (c is FunctionCallContent { CallId: { Length: > 0 } id } call)
                    (merged ??= new Dictionary<string, string>(known, StringComparer.Ordinal))[id] = call.Name;
        return merged ?? known;
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
    /// Builds the <see cref="LiteRtConversationOptions"/> for a request by merging the per-call MEAI
    /// <paramref name="options"/> over an optional per-client <paramref name="template"/>, or <c>null</c> when
    /// nothing at all is set so a plain conversation is created.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Merge rules</b> (per-call MEAI value wins where it supplies one; the template fills the rest):
    /// <list type="bullet">
    /// <item><b>History</b> is always per-call — the client derives it from the message list; a template
    /// carrying history is rejected at construction (see <see cref="ValidateTemplate"/>).</item>
    /// <item><b>Sampler</b> / <b>EnableThinking</b> / <b>EnableConstrainedDecoding</b> / <b>Tools</b>:
    /// the per-call value (from <paramref name="options"/>) wins when present; the template's value applies
    /// otherwise. For tools, "present" means <see cref="ChatOptions.Tools"/> is non-empty — a
    /// <see cref="NoneChatToolMode"/> then still advertises none (it does not fall back to the template).</item>
    /// <item><b>SystemMessage</b>: the per-request system message (folded into <paramref name="history"/> as a
    /// leading <see cref="LiteRtMessageRole.System"/> turn by the client) wins; the template's
    /// <see cref="LiteRtConversationOptions.SystemMessage"/> is applied <b>only</b> when the history carries no
    /// system turn — never two system turns.</item>
    /// <item><b>MaxOutputTokens</b>: the template's conversation-level value is honored as the session default;
    /// <see cref="ChatOptions.MaxOutputTokens"/> continues to map per-send (see below), overriding it.</item>
    /// <item><b>LoraPath</b> / <b>AudioLoraPath</b> / <b>StreamToolCalls</b> / <b>VisualTokenBudget</b> /
    /// <b>FilterThinkingFromKvCache</b> / <b>ExtraContext</b> / <b>ThinkingTokenBudget</b> /
    /// <b>PromptTemplate</b>: template only — MEAI has no per-request surface for them.</item>
    /// <item><b>ConstraintProvider</b>: armed automatically (LlGuidance) when the request carries a
    /// JSON-schema <see cref="ChatOptions.ResponseFormat"/> (see <see cref="ToConstraint"/>), else taken from
    /// the template — the template form matters in stateful mode, where the provider must exist from the
    /// conversation's first call for later schema-constrained calls to work.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <see cref="ChatOptions.MaxOutputTokens"/> is deliberately <b>not</b> mapped here — it is a MEAI
    /// per-request option, so the connector maps it to the native per-send cap (see <see cref="ToSendOptions"/>)
    /// rather than the conversation-level session config. The client creates a fresh conversation per call, so
    /// the two scopes are behaviorally identical for the output cap; per-send is the more faithful mapping and
    /// avoids allocating a session config when the output cap is the only option set. This is safe for the
    /// multimodal encoder side effect: a multimodal engine forces the session config into existence regardless
    /// (see <c>LiteRtConversation.Create</c>'s <c>engineIsMultimodal</c> branch), so dropping the output cap
    /// from these options can never flip that behavior; the per-send cap likewise never creates a session config.
    /// </para>
    /// </remarks>
    public static LiteRtConversationOptions? ToConversationOptions(
        IReadOnlyList<LiteRtMessage> history, ChatOptions? options, LiteRtConversationOptions? template = null)
    {
        // Sampler / thinking / constrained / tools: the per-call MEAI value wins when present, else the template.
        LiteRtSamplerParams? sampler = ToSampler(options) ?? template?.Sampler;
        bool? enableThinking = GetEnableThinking(options) ?? template?.EnableThinking;
        bool constrained = GetBoolProperty(options, "enable_constrained_decoding")
            ?? template?.EnableConstrainedDecoding ?? false;
        // "Present" for tools = ChatOptions carries tools; then ToTools honors ToolMode (None → no tools, and it
        // does NOT fall back to the template). Only a request with no tools at all uses the template's tools.
        IReadOnlyList<LiteRtTool>? tools = options?.Tools is { Count: > 0 } ? ToTools(options) : template?.Tools;

        // System message: the per-request system message (a leading System turn in history) wins; the template's
        // SystemMessage is used only when the request carries none — guaranteeing there are never two system
        // turns (a Required-tool instruction the client may have inserted also counts as a system turn here).
        bool historyHasSystem = false;
        for (int i = 0; i < history.Count; i++)
            if (history[i].Role == LiteRtMessageRole.System) { historyHasSystem = true; break; }
        string? systemMessage = historyHasSystem ? null : template?.SystemMessage;

        // Template-only conversation settings (MEAI has no per-request surface for these).
        int maxOutputTokens = template?.MaxOutputTokens ?? 0;
        string? loraPath = template?.LoraPath;
        string? audioLoraPath = template?.AudioLoraPath;
        bool streamToolCalls = template?.StreamToolCalls ?? false;
        int visualTokenBudget = template?.VisualTokenBudget ?? 0;
        bool filterThinkingFromKvCache = template?.FilterThinkingFromKvCache ?? false;
        string? extraContext = template?.ExtraContext;
        int? thinkingTokenBudget = template?.ThinkingTokenBudget;
        string? promptTemplate = template?.PromptTemplate;

        // A JSON-schema ResponseFormat needs the LlGuidance provider on the conversation so the
        // per-send constraint (ToSendOptions) has something to enforce it. The template can also
        // pre-arm the provider — useful in stateful mode, where the conversation is created on the
        // first call and a later call cannot add the provider retroactively.
        bool hasSchemaFormat = ToConstraint(options) is not null;
        LiteRtConstraintProvider? constraintProvider =
            (hasSchemaFormat ? LiteRtConstraintProvider.LlGuidance : (LiteRtConstraintProvider?)null)
            ?? template?.ConstraintProvider;

        if (hasSchemaFormat)
        {
            // Schema-constrained sampling masks EVERY generated token to schema-conforming
            // continuations, so the model cannot produce the tool-call format — combining the two
            // silently breaks tool calling. Reject it in MEAI vocabulary rather than let it
            // misbehave (unlike OpenAI-style providers, this runtime cannot scope the schema to the
            // final answer only).
            if (tools is { Count: > 0 })
                throw new ArgumentException(
                    "A JSON-schema ChatOptions.ResponseFormat cannot be combined with ChatOptions.Tools on " +
                    "this provider: the schema is enforced during sampling for the whole reply, which makes " +
                    "emitting a tool call impossible. Run the tool phase without the ResponseFormat, then " +
                    "request the schema-formatted answer in a separate call.", nameof(options));
            // Tool-calling constrained decoding is meaningless without tools and mutually exclusive
            // with the LlGuidance provider at the native level — the schema request wins.
            constrained = false;
        }

        if (history.Count == 0 && sampler is null && enableThinking is null && tools is null && !constrained
            && systemMessage is null && maxOutputTokens == 0 && loraPath is null && audioLoraPath is null
            && !streamToolCalls && visualTokenBudget == 0 && !filterThinkingFromKvCache && extraContext is null
            && thinkingTokenBudget is null && promptTemplate is null && constraintProvider is null)
            return null;

        return new LiteRtConversationOptions
        {
            History = history.Count > 0 ? history : null,
            Sampler = sampler,
            EnableThinking = enableThinking,
            Tools = tools,
            EnableConstrainedDecoding = constrained,
            SystemMessage = systemMessage,
            MaxOutputTokens = maxOutputTokens,
            LoraPath = loraPath,
            AudioLoraPath = audioLoraPath,
            StreamToolCalls = streamToolCalls,
            VisualTokenBudget = visualTokenBudget,
            FilterThinkingFromKvCache = filterThinkingFromKvCache,
            ExtraContext = extraContext,
            ThinkingTokenBudget = thinkingTokenBudget,
            PromptTemplate = promptTemplate,
            ConstraintProvider = constraintProvider,
        };
    }

    /// <summary>
    /// Validates a per-client conversation-options <paramref name="template"/>: it must not set
    /// <see cref="LiteRtConversationOptions.History"/> or <see cref="LiteRtConversationOptions.HistoryJson"/>.
    /// The chat client derives conversation history from the per-call message list, so template history would be
    /// silently ignored every call and is almost certainly a mistake. <c>null</c> is allowed (no template).
    /// </summary>
    /// <exception cref="ArgumentException">The template sets <c>History</c> or <c>HistoryJson</c>.</exception>
    public static void ValidateTemplate(LiteRtConversationOptions? template)
    {
        if (template is null)
            return;
        if (template.History is { Count: > 0 } || !string.IsNullOrEmpty(template.HistoryJson))
            throw new ArgumentException(
                "The conversation-options template must not set History or HistoryJson: the chat client derives " +
                "conversation history from the per-call message list, so template history would be ignored on " +
                "every call. Remove History/HistoryJson from the template (persist and restore history via the " +
                "per-call messages instead).",
                nameof(template));
    }

    /// <summary>
    /// Builds the per-send <see cref="LiteRtSendOptions"/> for a request from the MEAI per-request options, or
    /// <c>null</c> when there is nothing to set. Maps <see cref="ChatOptions.MaxOutputTokens"/> to the native
    /// per-send output cap (<see cref="LiteRtSendOptions.MaxOutputTokens"/>) — see <see cref="ToConversationOptions"/>
    /// for why the per-send scope is used — the OpenAI-style penalties, a JSON-schema response format, and the
    /// <c>no_repeat_ngram_size</c> / <c>suppress_tokens</c> bag keys (<see cref="LiteRtChatOptions.NoRepeatNgramSize"/>
    /// / <see cref="LiteRtChatOptions.SuppressTokens"/>) onto the native logit processors.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><c>no_repeat_ngram_size</c> is zero or negative, or a
    /// suppressed token id is negative.</exception>
    /// <exception cref="ArgumentException"><c>suppress_tokens</c> holds a value that is not a token id.</exception>
    public static LiteRtSendOptions? ToSendOptions(ChatOptions? options)
    {
        int maxOutput = options?.MaxOutputTokens ?? 0;

        // MEAI's FrequencyPenalty / PresencePenalty are the OpenAI-style subtractive penalties — the
        // exact semantics of the native repetition-penalty config's matching fields (v0.15.0+).
        LiteRtRepetitionPenaltyOptions? penalties =
            options is { FrequencyPenalty: not null } or { PresencePenalty: not null }
                ? new LiteRtRepetitionPenaltyOptions
                {
                    FrequencyPenalty = options.FrequencyPenalty ?? 0f,
                    PresencePenalty = options.PresencePenalty ?? 0f,
                }
                : null;

        LiteRtConstraint? constraint = ToConstraint(options);

        // The two logit processors MEAI has no typed option for; LiteRtChatOptions writes these keys.
        LiteRtNoRepeatNgramOptions? noRepeat = GetInt32Property(options, "no_repeat_ngram_size") is { } ngram
            ? new LiteRtNoRepeatNgramOptions { NgramSize = ngram }
            : null;
        IReadOnlyList<int>? suppress = GetInt32ListProperty(options, "suppress_tokens");
        if (suppress is { Count: 0 })
            suppress = null;

        if (maxOutput <= 0 && penalties is null && constraint is null && noRepeat is null && suppress is null)
            return null;
        return new LiteRtSendOptions
        {
            MaxOutputTokens = maxOutput > 0 ? maxOutput : 0,
            RepetitionPenalties = penalties,
            Constraint = constraint,
            NoRepeatNgram = noRepeat,
            SuppressTokens = suppress,
        };
    }

    /// <summary>
    /// Maps a JSON-schema <see cref="ChatOptions.ResponseFormat"/> to a native LlGuidance constraint
    /// (v0.15.0+): the schema is enforced during sampling, so the reply is guaranteed to be a
    /// conforming JSON document — real structured output, not prompt hope. Schema-less
    /// <see cref="ChatResponseFormatJson"/> ("JSON mode") and text format map to <c>null</c>
    /// (unconstrained): without a schema there is nothing precise to enforce, and the pre-v0.15.0
    /// behavior (prompt-driven) is preserved. In stateful mode the constraint requires the
    /// conversation to have the provider from its first call (see <see cref="ToConversationOptions"/>).
    /// </summary>
    internal static LiteRtConstraint? ToConstraint(ChatOptions? options) =>
        options?.ResponseFormat is ChatResponseFormatJson { Schema: { } schema }
            ? LiteRtConstraint.FromJsonSchema(schema.GetRawText())
            : null;

    /// <summary>
    /// Maps one streamed <see cref="LiteRtStreamChunk"/> to the MEAI content it contributes to a
    /// <see cref="ChatResponseUpdate"/>'s text stream, or <c>null</c> when the chunk contributes nothing there.
    /// An <see cref="LiteRtStreamChunkKind.Answer"/> delta becomes <see cref="TextContent"/>; a
    /// <see cref="LiteRtStreamChunkKind.Thinking"/> delta becomes <see cref="TextReasoningContent"/>. Every other
    /// kind is deliberately ignored (returns <c>null</c>): <see cref="LiteRtStreamChunkKind.ToolCall"/> chunks are
    /// handled separately by the client (they carry <see cref="FunctionCallContent"/> and a finish reason), and a
    /// <see cref="LiteRtStreamChunkKind.ToolCallDelta"/> raw progress fragment — or any chunk kind a future native
    /// version adds — is dropped rather than mis-surfaced, so an unknown chunk can never break the stream.
    /// </summary>
    public static AIContent? ToStreamingTextContent(LiteRtStreamChunk chunk) => chunk.Kind switch
    {
        LiteRtStreamChunkKind.Answer when chunk.Text.Length > 0 => new TextContent(chunk.Text),
        LiteRtStreamChunkKind.Thinking when chunk.Text.Length > 0 => new TextReasoningContent(chunk.Text),
        _ => null,
    };

    /// <summary>The <c>AdditionalProperties</c> key (on a <see cref="ChatResponseUpdate"/>) under which the
    /// connector surfaces a raw tool-call progress fragment when the conversation-options template enables
    /// <see cref="LiteRtConversationOptions.StreamToolCalls"/>.</summary>
    public const string ToolCallDeltaKey = "litertlm.tool_call_delta";

    /// <summary>
    /// Maps a <see cref="LiteRtStreamChunkKind.ToolCallDelta"/> chunk to a content-less
    /// <see cref="ChatResponseUpdate"/> that carries the raw fragment under <see cref="ToolCallDeltaKey"/> in its
    /// <c>AdditionalProperties</c>, or <c>null</c> for any other chunk kind (or an empty delta). These deltas only
    /// ever arrive when the template enabled <see cref="LiteRtConversationOptions.StreamToolCalls"/>, so surfacing
    /// them here is opt-in by construction — invisible (no update) otherwise. Use the deltas for progress display
    /// only; act on the complete <see cref="FunctionCallContent"/> the following tool-call chunk carries.
    /// </summary>
    public static ChatResponseUpdate? ToToolCallDeltaUpdate(LiteRtStreamChunk chunk, string? modelId)
    {
        if (chunk.Kind != LiteRtStreamChunkKind.ToolCallDelta || chunk.Text.Length == 0)
            return null;
        var update = new ChatResponseUpdate { Role = ChatRole.Assistant, ModelId = modelId };
        (update.AdditionalProperties ??= new())[ToolCallDeltaKey] = chunk.Text;
        return update;
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

    /// <summary>Reads an integer knob from <see cref="ChatOptions.AdditionalProperties"/> (<c>null</c> when absent
    /// or not an integer). Shared by <see cref="ToSendOptions"/> and the <see cref="LiteRtChatOptions"/> getters.</summary>
    internal static int? GetInt32Property(ChatOptions? o, string key)
        => o?.AdditionalProperties is { } props && props.TryGetValue(key, out object? v) ? AsInt32(v) : null;

    /// <summary>Reads a token-id list knob from <see cref="ChatOptions.AdditionalProperties"/> (<c>null</c> when
    /// absent). See <see cref="AsInt32List"/> for the accepted shapes.</summary>
    internal static IReadOnlyList<int>? GetInt32ListProperty(ChatOptions? o, string key)
        => o?.AdditionalProperties is { } props && props.TryGetValue(key, out object? v) ? AsInt32List(v, key) : null;

    /// <summary>Coerces an integer stored in a property bag: boxed integers, an integral floating-point
    /// number (Semantic Kernel's settings converter round-trips ExtensionData through JSON, so an
    /// <c>int[]</c> arrives as boxed <see cref="double"/>s), a numeric string, or a <see cref="JsonElement"/>
    /// number/string (options deserialized from JSON/YAML). Anything else is <c>null</c>.</summary>
    internal static int? AsInt32(object? v) => v switch
    {
        int i => i,
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        short sh => sh,
        byte b => b,
        double d when double.IsInteger(d) && d is >= int.MinValue and <= int.MaxValue => (int)d,
        float f when float.IsInteger(f) && f is >= int.MinValue and <= int.MaxValue => (int)f,
        decimal m when decimal.Truncate(m) == m && m is >= int.MinValue and <= int.MaxValue => (int)m,
        string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int r) => r,
        JsonElement { ValueKind: JsonValueKind.Number } e when e.TryGetInt32(out int r) => r,
        JsonElement { ValueKind: JsonValueKind.String } e when int.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int r) => r,
        _ => null,
    };

    /// <summary>
    /// Coerces a token-id list stored in a property bag: the <c>int[]</c> the typed setters write, any
    /// <see cref="IEnumerable{T}"/> of <see cref="int"/>, a <see cref="JsonElement"/> array of numbers, a
    /// comma-separated string, or a sequence of objects each coercible by <see cref="AsInt32"/>. Absent
    /// (<c>null</c>) stays <c>null</c>; a present value whose elements are not all integers is a caller error.
    /// </summary>
    /// <exception cref="ArgumentException">The value is present but is not a list of integers.</exception>
    internal static IReadOnlyList<int>? AsInt32List(object? v, string key = "suppress_tokens")
    {
        switch (v)
        {
            case null:
                return null;
            case int[] arr:
                return arr;
            case IReadOnlyList<int> list:
                return list;
            case IEnumerable<int> seq:
                return seq.ToArray();
            case JsonElement { ValueKind: JsonValueKind.String } e:
                return AsInt32List(e.GetString(), key);
            case string s:
                return Coerce(s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            case JsonElement { ValueKind: JsonValueKind.Array } e:
                return Coerce(e.EnumerateArray().Cast<object>());
            case System.Collections.IEnumerable objs when v is not string:
                return Coerce(objs.Cast<object?>());
            default:
                throw Invalid(v);
        }

        IReadOnlyList<int> Coerce(IEnumerable<object?> items)
        {
            var ids = new List<int>();
            foreach (object? item in items)
                ids.Add(AsInt32(item) ?? throw Invalid(item));
            return ids;
        }

        ArgumentException Invalid(object? item) => new(
            $"'{key}' must be a list of token ids (integers); got {item?.GetType().Name ?? "null"} '{item}'.");
    }
}

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LiteRtLmSharp.Native;
using Xunit;
using Xunit.Abstractions;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// The C API 1.1 binary interface of the conversation functions (<c>c/conversation.h</c>) and of the stream
/// chunk getters (<c>c/engine.h</c>), called directly: status returns, out pointers written on success and set
/// to NULL on failure, scalar outputs left unwritten on failure, the by-value constraint provider, and the
/// failure statuses the public API cannot reach (it never passes a null handle, an undeclared enum value or a
/// JSON array). The builders and the null-handle cases need no model; the rest load the model named by
/// LITERTLM_TEST_MODEL on the CPU backend and are skipped without it.
/// </summary>
public sealed unsafe class ConversationAbiTests(ITestOutputHelper output)
{
    static ConversationAbiTests() => LiteRtEngine.SetMinLogLevel(3); // registers the resolver

    private const string UserMessage = """{"role":"user","content":[{"type":"text","text":"Reply with one word: hello"}]}""";

    private static string? LastMessage() => Marshal.PtrToStringUTF8(LiteRtLmNative.litert_lm_get_last_error_message());

    /// <summary>Every conversation-config setter returns OK for a valid value; the provider is passed by value,
    /// None unsets it, and an undeclared provider or a null session config is rejected.</summary>
    [Fact]
    public void ConversationConfig_SettersReturnStatuses_AndTheProviderIsByValue()
    {
        nint config;
        Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_config_create(&config));
        Assert.NotEqual(nint.Zero, config);
        nint session, thinking;
        Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_session_config_create(&session));
        Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_thinking_config_create(&thinking));
        try
        {
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_config_set_session_config(config, session));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_config_set_system_message(
                config, """[{"type":"text","text":"Be brief."}]"""));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_config_set_tools(config, "[]"));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_config_set_messages(config, "[]"));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_config_set_enable_constrained_decoding(config, false));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_config_set_extra_context(config, "{}"));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_config_set_filter_channel_content_from_kv_cache(config, true));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_config_set_stream_tool_calls(config, true, "tool_call"));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_config_set_prompt_template(config, ""));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_config_set_thinking_config(config, thinking));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_config_set_thinking_config(config, nint.Zero));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_config_set_constraint_provider(
                config, LiteRtLmConstraintProviderType.LlGuidance));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_config_set_constraint_provider(
                config, LiteRtLmConstraintProviderType.None));

            Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_conversation_config_set_constraint_provider(
                config, (LiteRtLmConstraintProviderType)7));
            output.WriteLine($"undeclared provider: {LastMessage()}");
            Assert.Equal("Unknown LiteRtLmConstraintProviderType.", LastMessage());
            Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_conversation_config_set_session_config(config, nint.Zero));
            output.WriteLine($"null session config: {LastMessage()}");
        }
        finally
        {
            LiteRtLmNative.litert_lm_thinking_config_delete(thinking);
            LiteRtLmNative.litert_lm_session_config_delete(session);
            LiteRtLmNative.litert_lm_conversation_config_delete(config);
            NativeError.Clear();
        }
    }

    /// <summary>Every optional-args setter returns OK for a valid value; an undeclared constraint type and a
    /// tool choice that is not one of the header's shapes are rejected, and a null tool choice clears.</summary>
    [Fact]
    public void OptionalArgs_SettersReturnStatuses_AndRejectUndeclaredValues()
    {
        nint args;
        Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_optional_args_create(&args));
        Assert.NotEqual(nint.Zero, args);
        nint repetition, ngram, suppress, thinking;
        Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_repetition_penalty_config_create(&repetition));
        Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_no_repeat_ngram_config_create(&ngram));
        Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_suppress_tokens_config_create(&suppress));
        Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_thinking_config_create(&thinking));
        try
        {
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_optional_args_set_visual_token_budget(args, 70));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_optional_args_set_max_output_tokens(args, 16));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_optional_args_set_repetition_penalty_config(args, repetition));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_optional_args_set_no_repeat_ngram_config(args, ngram));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_optional_args_set_suppress_tokens_config(args, suppress));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_optional_args_set_thinking_config(args, thinking));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_optional_args_set_constraint(
                args, LiteRtLmConstraintType.Regex, "[a-z]+"));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_optional_args_set_tool_choice(args, "\"auto\""));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_optional_args_set_tool_choice(args, "\"none\""));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_optional_args_set_tool_choice(args, null));

            Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_conversation_optional_args_set_constraint(
                args, (LiteRtLmConstraintType)7, "x"));
            output.WriteLine($"undeclared constraint type: {LastMessage()}");
            Assert.Equal("Unknown LiteRtLmConstraintType.", LastMessage());
            Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_conversation_optional_args_set_tool_choice(args, "not json"));
            output.WriteLine($"tool choice that is not JSON: {LastMessage()}");
            Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_conversation_optional_args_set_tool_choice(args, "\"sometimes\""));
            output.WriteLine($"undeclared tool choice: {LastMessage()}");
            Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_conversation_optional_args_set_max_output_tokens(nint.Zero, 16));
        }
        finally
        {
            LiteRtLmNative.litert_lm_repetition_penalty_config_delete(repetition);
            LiteRtLmNative.litert_lm_no_repeat_ngram_config_delete(ngram);
            LiteRtLmNative.litert_lm_suppress_tokens_config_delete(suppress);
            LiteRtLmNative.litert_lm_thinking_config_delete(thinking);
            LiteRtLmNative.litert_lm_conversation_optional_args_delete(args);
            NativeError.Clear();
        }
    }

    /// <summary>Every conversation function rejects a null handle with INVALID_ARGUMENT: pointer outputs are set
    /// to NULL, the token count and "is final" are not written, and a stream that cannot start never calls its
    /// callback.</summary>
    [Fact]
    public void NullHandles_AreRejected_WithoutWritingScalarOutputs()
    {
        const nint Sentinel = 0x5A5A;
        nint ptr = Sentinel;
        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_conversation_create(nint.Zero, nint.Zero, &ptr));
        Assert.Equal(nint.Zero, ptr);
        output.WriteLine($"create without an engine: {LastMessage()}");

        ptr = Sentinel;
        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_conversation_clone(nint.Zero, &ptr));
        Assert.Equal(nint.Zero, ptr);
        ptr = Sentinel;
        Assert.Equal(LiteRtStatusCode.InvalidArgument,
            LiteRtLmNative.litert_lm_conversation_send_message(nint.Zero, UserMessage, null, nint.Zero, &ptr));
        Assert.Equal(nint.Zero, ptr);
        ptr = Sentinel;
        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_conversation_render_message_to_string(nint.Zero, UserMessage, &ptr));
        Assert.Equal(nint.Zero, ptr);
        ptr = Sentinel;
        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_conversation_render_preface_to_string(nint.Zero, &ptr));
        Assert.Equal(nint.Zero, ptr);
        ptr = Sentinel;
        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_conversation_get_benchmark_info(nint.Zero, &ptr));
        Assert.Equal(nint.Zero, ptr);
        ptr = Sentinel;
        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_json_response_get_string(nint.Zero, &ptr));
        Assert.Equal(nint.Zero, ptr);

        nuint count = 77;
        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_conversation_get_token_count(nint.Zero, &count));
        Assert.Equal((nuint)77, count);
        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_conversation_cancel_process(nint.Zero));
        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_conversation_wait_until_done(nint.Zero));

        Callback.Reset();
        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_conversation_send_message_stream(
            nint.Zero, UserMessage, null, nint.Zero, &Callback.OnChunk, nint.Zero));
        Assert.Equal(0, Callback.Chunks);

        // The chunk getters: a null chunk is rejected, the text and error outputs are set to NULL, and
        // "is final" keeps its value.
        ptr = Sentinel;
        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_stream_chunk_get_text(nint.Zero, &ptr));
        Assert.Equal(nint.Zero, ptr);
        ptr = Sentinel;
        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_stream_chunk_get_error(nint.Zero, &ptr));
        Assert.Equal(nint.Zero, ptr);
        byte isFinal = 0xA5;
        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_stream_chunk_is_final(nint.Zero, &isFinal));
        Assert.Equal(0xA5, isFinal);
        output.WriteLine($"null chunk: {LastMessage()}");
        NativeError.Clear();
    }

    /// <summary>A conversation on the model: create, render (lent strings), send (an owned response with a lent
    /// JSON string), the token count, a wait on an idle conversation, clone, and the benchmark info of an
    /// engine without benchmarking, which fails natively. The message must be a JSON object: a JSON array and
    /// text that is not JSON are rejected before anything is generated.</summary>
    [SkippableFact]
    public void Conversation_OnTheModel_FollowsTheStatusAndOwnershipRules()
    {
        using LiteRtEngine engine = LoadEngine();
        nint config, conversation;
        Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_config_create(&config));
        try
        {
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_config_set_system_message(
                config, """[{"type":"text","text":"Be brief."}]"""));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_create(engine.Handle.Ptr, config, &conversation));
        }
        finally
        {
            LiteRtLmNative.litert_lm_conversation_config_delete(config);   // the create copied it
        }
        Assert.NotEqual(nint.Zero, conversation);
        try
        {
            nuint count = 99;
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_get_token_count(conversation, &count));
            Assert.Equal((nuint)0, count);

            nint text;
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_render_preface_to_string(conversation, &text));
            string? preface = Marshal.PtrToStringUTF8(text);
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_render_message_to_string(conversation, UserMessage, &text));
            string? rendered = Marshal.PtrToStringUTF8(text);
            output.WriteLine($"preface '{preface}', message '{rendered}'");
            Assert.Contains("Be brief.", preface, StringComparison.Ordinal);
            Assert.Contains("hello", rendered, StringComparison.Ordinal);

            nint response = 1;
            Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_conversation_send_message(
                conversation, "[" + UserMessage + "]", null, nint.Zero, &response));
            Assert.Equal(nint.Zero, response);
            output.WriteLine($"array message: {LastMessage()}");
            Assert.Equal("INVALID_ARGUMENT: Message must be a JSON object.", LastMessage());
            Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_conversation_send_message(
                conversation, "not json", null, nint.Zero, &response));
            output.WriteLine($"text that is not JSON: {LastMessage()}");
            NativeError.Clear();

            Callback.Reset();
            Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_conversation_send_message_stream(
                conversation, "[" + UserMessage + "]", null, nint.Zero, &Callback.OnChunk, nint.Zero));
            Assert.Equal(0, Callback.Chunks);
            NativeError.Clear();

            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_send_message(
                conversation, UserMessage, null, nint.Zero, &response));
            Assert.NotEqual(nint.Zero, response);
            try
            {
                nint json;
                Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_json_response_get_string(response, &json));
                string? reply = Marshal.PtrToStringUTF8(json);
                output.WriteLine($"reply {reply}");
                Assert.Contains("\"role\":\"assistant\"", reply, StringComparison.Ordinal);
            }
            finally
            {
                LiteRtLmNative.litert_lm_json_response_delete(response);
            }

            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_get_token_count(conversation, &count));
            Assert.True(count > 0, $"count {count}");
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_wait_until_done(conversation));

            nint info = 1;
            LiteRtStatusCode benchmark = LiteRtLmNative.litert_lm_conversation_get_benchmark_info(conversation, &info);
            output.WriteLine($"benchmark info without benchmarking: {benchmark}: {LastMessage()}");
            Assert.NotEqual(LiteRtStatusCode.Ok, benchmark);
            Assert.Equal(nint.Zero, info);
            NativeError.Clear();

            nint clone;
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_clone(conversation, &clone));
            Assert.NotEqual(nint.Zero, clone);
            LiteRtLmNative.litert_lm_conversation_delete(clone);
        }
        finally
        {
            LiteRtLmNative.litert_lm_conversation_delete(conversation);
        }
    }

    /// <summary>A stream through the C API: the callback receives chunks and exactly one final chunk, and after
    /// <c>wait_until_done</c> returns no callback runs any more, which is the barrier the stream teardown relies
    /// on before it frees the callback's state.</summary>
    [SkippableFact]
    public void Stream_WaitUntilDone_IsTheLastCallbackBarrier()
    {
        using LiteRtEngine engine = LoadEngine();
        nint conversation;
        Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_create(engine.Handle.Ptr, nint.Zero, &conversation));
        try
        {
            Callback.Reset();
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_send_message_stream(
                conversation, UserMessage, null, nint.Zero, &Callback.OnChunk, nint.Zero));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_wait_until_done(conversation));
            int chunks = Callback.Chunks, finals = Callback.Finals;
            Thread.Sleep(200);
            output.WriteLine($"chunks {chunks}, finals {finals}, errors {Callback.Errors}, failed getters {Callback.FailedGetters}");
            Assert.Equal(1, finals);
            Assert.True(chunks >= 2, $"chunks {chunks}");
            Assert.Equal(0, Callback.Errors);
            Assert.Equal(0, Callback.FailedGetters);
            Assert.Equal(chunks, Callback.Chunks);   // nothing after the wait
        }
        finally
        {
            LiteRtLmNative.litert_lm_conversation_delete(conversation);
        }
    }

    /// <summary>Sampler values that the public <see cref="LiteRtSamplerParams"/> rejects up front (k = 0, a NaN
    /// temperature) reach the runtime only through the C API, which rejects them when the conversation is
    /// created.</summary>
    [SkippableTheory]
    [InlineData((int)LiteRtLmSamplerType.TopP, 0, 1.0f)]
    [InlineData((int)LiteRtLmSamplerType.TopK, 0, 1.0f)]
    [InlineData((int)LiteRtLmSamplerType.TopP, 40, float.NaN)]
    public void Conversation_WithInvalidSamplerValues_FailsAtCreation(int samplerType, int topK, float temperature)
    {
        var type = (LiteRtLmSamplerType)samplerType;
        using LiteRtEngine engine = LoadEngine();
        nint config, session, sampler;
        Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_config_create(&config));
        Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_session_config_create(&session));
        Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_sampler_params_create(type, &sampler));
        try
        {
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_sampler_params_set_top_k(sampler, topK));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_sampler_params_set_top_p(sampler, 0.9f));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_sampler_params_set_temperature(sampler, temperature));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_session_config_set_sampler_params(session, sampler));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_conversation_config_set_session_config(config, session));

            nint conversation = 1;
            LiteRtStatusCode status = LiteRtLmNative.litert_lm_conversation_create(engine.Handle.Ptr, config, &conversation);
            output.WriteLine($"{type} k={topK} t={temperature}: {status}: {LastMessage()}");
            if (status == LiteRtStatusCode.Ok)
                LiteRtLmNative.litert_lm_conversation_delete(conversation);
            Assert.Equal(LiteRtStatusCode.InvalidArgument, status);
            Assert.Equal(nint.Zero, conversation);
        }
        finally
        {
            LiteRtLmNative.litert_lm_sampler_params_delete(sampler);
            LiteRtLmNative.litert_lm_session_config_delete(session);
            LiteRtLmNative.litert_lm_conversation_config_delete(config);
            NativeError.Clear();
        }
    }

    private static LiteRtEngine LoadEngine()
    {
        string? model = Environment.GetEnvironmentVariable("LITERTLM_TEST_MODEL");
        Skip.If(string.IsNullOrEmpty(model) || !File.Exists(model), "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");
        return LiteRtEngine.Load(new LiteRtEngineOptions { ModelPath = model!, Backend = LiteRtBackend.Cpu, MaxNumTokens = 1024 });
    }

    /// <summary>A stream callback that only counts, through the three chunk getters (the only calls the
    /// threading model allows it). The tests run one at a time, so static counters are enough.</summary>
    private static class Callback
    {
        private static int _chunks, _finals, _errors, _failedGetters;
        public static int Chunks => Volatile.Read(ref _chunks);
        public static int Finals => Volatile.Read(ref _finals);
        public static int Errors => Volatile.Read(ref _errors);
        public static int FailedGetters => Volatile.Read(ref _failedGetters);

        public static void Reset() => _chunks = _finals = _errors = _failedGetters = 0;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static void OnChunk(nint callbackData, nint chunk)
        {
            Interlocked.Increment(ref _chunks);
            nint text, error;
            byte isFinal = 1;
            if (LiteRtLmNative.litert_lm_stream_chunk_get_text(chunk, &text) != LiteRtStatusCode.Ok
                || LiteRtLmNative.litert_lm_stream_chunk_get_error(chunk, &error) != LiteRtStatusCode.Ok
                || LiteRtLmNative.litert_lm_stream_chunk_is_final(chunk, &isFinal) != LiteRtStatusCode.Ok)
            {
                Interlocked.Increment(ref _failedGetters);
                return;
            }
            if (error != nint.Zero)
                Interlocked.Increment(ref _errors);
            if (isFinal != 0)
                Interlocked.Increment(ref _finals);
        }
    }
}

/// <summary>
/// Conversation behaviour that LiteRT-LM v0.20.0 changed and the binding passes through, pinned through the
/// public API on the model named by LITERTLM_TEST_MODEL (CPU backend; skipped without it).
/// </summary>
public sealed class ConversationRuntimeBehaviourTests(ITestOutputHelper output)
{
    private static readonly LiteRtSamplerParams Argmax = new() { Strategy = LiteRtSamplerType.TopP, TopK = 1 };

    private static LiteRtEngine LoadEngine(int maxNumTokens, bool benchmark = false)
    {
        string? model = Environment.GetEnvironmentVariable("LITERTLM_TEST_MODEL");
        Skip.If(string.IsNullOrEmpty(model) || !File.Exists(model), "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");
        LiteRtEngine.SetMinLogLevel(3);
        return LiteRtEngine.Load(new LiteRtEngineOptions
        {
            ModelPath = model!, Backend = LiteRtBackend.Cpu, MaxNumTokens = maxNumTokens, EnableBenchmark = benchmark,
        });
    }

    /// <summary>A send that the runtime rejects before its prefill records no prefill turn, so
    /// <see cref="LiteRtBenchmarkInfo.LastSendPrefillTokenCount"/> reads 0 after it rather than the previous
    /// send's message. Reporting the last recorded turn instead would give the earlier send's value. The rejected
    /// send is a JSON array, which the runtime rejects (pinned by
    /// <see cref="SendRaw_WithAJsonArray_FailsWithInvalidArgument_AndTheConversationStaysUsable"/>); if the runtime
    /// starts to accept arrays, this test fails at the <c>Assert.Throws</c>, not at the prefill count.</summary>
    [SkippableFact]
    public void LastSendPrefillTokenCount_AfterASendRejectedBeforeItsPrefill_IsZero()
    {
        using LiteRtEngine engine = LoadEngine(2048, benchmark: true);
        using var chat = engine.CreateConversation(new LiteRtConversationOptions { Sampler = Argmax, MaxOutputTokens = 16 });

        Assert.False(string.IsNullOrWhiteSpace(chat.Send("Reply with one word: hello").Text));
        LiteRtBenchmarkInfo before = chat.GetBenchmarkInfo()!;
        Assert.Throws<LiteRtException>(() => chat.SendRaw(
            """[{"role":"user","content":[{"type":"text","text":"Reply with one word: hello"}]}]"""));
        LiteRtBenchmarkInfo after = chat.GetBenchmarkInfo()!;
        output.WriteLine($"before: prefill turns {before.NumPrefillTurns}, send prefill {before.LastSendPrefillTokenCount}; " +
                         $"after: prefill turns {after.NumPrefillTurns}, send prefill {after.LastSendPrefillTokenCount}, " +
                         $"last prefill {after.LastPrefillTokenCount}");
        Assert.True(before.LastSendPrefillTokenCount > 0);
        Assert.Equal(before.NumPrefillTurns, after.NumPrefillTurns);
        Assert.Equal(0, after.LastSendPrefillTokenCount);
    }

    /// <summary>The runtime takes one message per send: a JSON array, which LiteRT-LM v0.18.0 accepted through
    /// <see cref="LiteRtConversation.SendRaw"/>, is rejected before anything is generated, and the conversation
    /// stays usable.</summary>
    [SkippableFact]
    public void SendRaw_WithAJsonArray_FailsWithInvalidArgument_AndTheConversationStaysUsable()
    {
        using LiteRtEngine engine = LoadEngine(2048);
        using var chat = engine.CreateConversation(new LiteRtConversationOptions { Sampler = Argmax, MaxOutputTokens = 16 });

        var ex = Assert.Throws<LiteRtException>(() => chat.SendRaw(
            """[{"role":"user","content":[{"type":"text","text":"Reply with one word: hello"}]}]"""));
        output.WriteLine($"[{ex.StatusCode}] {ex.Message}");
        Assert.Equal(LiteRtStatusCode.InvalidArgument, ex.StatusCode);
        Assert.Equal("litert_lm_conversation_send_message failed: INVALID_ARGUMENT: Message must be a JSON object.", ex.Message);

        Assert.False(string.IsNullOrWhiteSpace(chat.Send("Reply with one word: hello").Text));
    }

    /// <summary>The runtime removes the thinking channel from the KV cache right after the reply (LiteRT-LM
    /// v0.18.0 did it at the next send), so the token count read after a reply with thinking leaves the thinking
    /// out. After a stream, the teardown waits for that background refill, so the cached count read during the
    /// teardown is already the settled one.</summary>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TokenCount_AfterAReplyWithThinking_LeavesTheThinkingOut(bool stream)
    {
        using LiteRtEngine engine = LoadEngine(2048);
        using var chat = engine.CreateConversation(new LiteRtConversationOptions
        {
            Sampler = Argmax, MaxOutputTokens = 1024, EnableThinking = true,
        });
        const string Prompt = "Think about bridges first. Then write a 60-word paragraph about bridges.";
        int promptTokens = engine.CountTokens(chat.RenderMessage(Prompt));

        string answer = "", thinking = "";
        if (stream)
        {
            await foreach (LiteRtStreamChunk chunk in chat.SendStreamingAsync(Prompt))
            {
                if (chunk.Kind == LiteRtStreamChunkKind.Answer) answer += chunk.Text;
                else if (chunk.Kind == LiteRtStreamChunkKind.Thinking) thinking += chunk.Text;
            }
        }
        else
        {
            LiteRtResponse reply = chat.Send(Prompt);
            answer = reply.Text ?? "";
            thinking = reply.Thinking ?? "";
        }

        int cached = chat.TokenCountCached;
        int count = chat.TokenCount;
        int answerTokens = engine.CountTokens(answer), thinkingTokens = engine.CountTokens(thinking);
        output.WriteLine($"prompt {promptTokens}, answer {answerTokens}, thinking {thinkingTokens}; count {count}, cached {cached}");
        Assert.True(thinkingTokens > 32, $"the reply must carry thinking ({thinkingTokens} tokens)");
        Assert.Equal(count, cached);
        // Template tokens around the reply are few; the thinking alone is far more than this margin.
        Assert.True(count <= promptTokens + answerTokens + 16, $"count {count} must leave the thinking out");
        Assert.True(count >= promptTokens + answerTokens - 16, $"count {count} must include the reply");
    }

    /// <summary>A reply with thinking that the overflow guard's decode clamp cuts does not mark the context
    /// full. The runtime removes the thinking from the KV cache right after the reply (LiteRT-LM v0.18.0 kept it
    /// until the next send, so the count stayed at the ceiling, <see cref="LiteRtConversation.IsContextFull"/> read
    /// <c>true</c> and the next send threw <see cref="LiteRtContextOverflowException"/>), so the context has room
    /// again and the next send passes the guard. <see cref="LiteRtConversation.LastReplyReachedContextLimit"/>
    /// still reports the cut (here from the tokenized reply, as benchmarking is off); a clone does not inherit it,
    /// a send the binding rejects while it builds the per-send options leaves it, and the next send, which ends on
    /// its own under the caller's cap, clears it.</summary>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClampedReplyWithThinking_LeavesRoomInTheContext(bool stream)
    {
        const int Limit = 1024;
        using LiteRtEngine engine = LoadEngine(Limit);
        using var chat = engine.CreateConversation(new LiteRtConversationOptions { Sampler = Argmax, EnableThinking = true });
        const string Prompt = "Think very carefully and at great length about the history of bridges, then write an essay " +
                              "of at least 900 words about bridges.";
        int promptTokens = engine.CountTokens(chat.RenderMessage(Prompt));

        string answer = "", thinking = "";
        if (stream)
        {
            await foreach (LiteRtStreamChunk chunk in chat.SendStreamingAsync(Prompt))
            {
                if (chunk.Kind == LiteRtStreamChunkKind.Answer) answer += chunk.Text;
                else if (chunk.Kind == LiteRtStreamChunkKind.Thinking) thinking += chunk.Text;
            }
        }
        else
        {
            LiteRtResponse reply = chat.Send(Prompt);
            answer = reply.Text ?? "";
            thinking = reply.Thinking ?? "";
        }

        int answerTokens = engine.CountTokens(answer), thinkingTokens = engine.CountTokens(thinking);
        int count = chat.TokenCount;
        output.WriteLine($"prompt {promptTokens}, answer {answerTokens}, thinking {thinkingTokens}; count {count}, " +
                         $"IsContextFull {chat.IsContextFull}, LastReplyReachedContextLimit {chat.LastReplyReachedContextLimit}");
        // The reply ran into the clamp: with its thinking it reaches the band the guard treats as full.
        Assert.True(LiteRtContextGuard.IsContextFull(promptTokens + answerTokens + thinkingTokens, Limit),
            $"prompt {promptTokens} + answer {answerTokens} + thinking {thinkingTokens} must reach the clamp");
        Assert.False(chat.IsContextFull);
        Assert.True(chat.LastReplyReachedContextLimit);
        Assert.True(count <= promptTokens + answerTokens + 16, $"count {count} must leave the thinking out");

        using (LiteRtConversation clone = chat.Clone())
            Assert.False(clone.LastReplyReachedContextLimit);

        // A send the binding rejects after the guard, while it builds the per-send options (a constraint on a
        // conversation without a constraint provider), leaves the flag as it was.
        var noProvider = new LiteRtSendOptions { Constraint = LiteRtConstraint.FromRegex("[a-z]+") };
        if (stream)
            await Assert.ThrowsAsync<LiteRtException>(async () =>
            {
                await foreach (LiteRtStreamChunk _ in chat.SendStreamingAsync("Reply with one word: OK", null, noProvider)) { }
            });
        else
            Assert.Throws<LiteRtException>(() => chat.Send("Reply with one word: OK", attachments: null, noProvider));
        Assert.True(chat.LastReplyReachedContextLimit, "a send the binding rejected cleared the flag");

        // A caller cap under the guard's budget arms no truncation check, so only the reset at the start of the
        // send can clear the flag. The cap is above what this reply decodes (about 150 tokens, mostly thinking).
        // If the cut reply's answer left the guard a budget of 400 or less, the guard would cap the follow-up
        // itself and its truncation check could clear the flag without the reset.
        int followUpBudget = Limit - chat.TokenCount - engine.CountTokens(chat.RenderMessage("Reply with one word: OK"))
                             - LiteRtContextGuard.SafetyMargin;
        output.WriteLine($"follow-up budget {followUpBudget}");
        Skip.If(followUpBudget <= 400,
            $"The guard's budget for the follow-up is {followUpBudget}, not above the 400-token cap, so the " +
            "follow-up cannot show that the start of a send clears the flag.");
        Assert.False(string.IsNullOrWhiteSpace(
            chat.Send("Reply with one word: OK", attachments: null, new LiteRtSendOptions { MaxOutputTokens = 400 }).Text));
        Assert.False(chat.LastReplyReachedContextLimit);
    }

    /// <summary>A send that the binding rejects before calling the runtime leaves the values of the last send that
    /// reached it: <see cref="LiteRtConversation.LastReplyReachedContextLimit"/> stays <c>true</c> and
    /// <see cref="LiteRtBenchmarkInfo.LastSendPrefillTokenCount"/> keeps its value. A long message leaves the guard a
    /// small budget, the reply (a count that never stops) runs to it and fills the context, and the next send throws
    /// <see cref="LiteRtContextOverflowException"/>. Clearing the flag at the start of every send, before the guard,
    /// would read <c>false</c> here.</summary>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASendTheBindingRejects_KeepsTheLastSendsValues(bool stream)
    {
        const int Limit = 1024;
        using LiteRtEngine engine = LoadEngine(Limit, benchmark: true);
        using var chat = engine.CreateConversation(new LiteRtConversationOptions { Sampler = Argmax });
        string Block(int words) => string.Join(' ', Enumerable.Range(0, words).Select(i => $"w{i}"));

        // Grow the list until the message leaves a budget of about 150 tokens.
        string prompt;
        int words = 100, input;
        do
        {
            prompt = "Here is a list: " + Block(words) +
                     ". Ignore the list. Count upward from 1, one number per line, and never stop.";
            input = engine.CountTokens(chat.RenderMessage(prompt));
            words += 10;
        }
        while (input < Limit - LiteRtContextGuard.SafetyMargin - 160);

        string cut = "";
        if (stream)
        {
            await foreach (LiteRtStreamChunk chunk in chat.SendStreamingAsync(prompt))
                if (chunk.Kind == LiteRtStreamChunkKind.Answer) cut += chunk.Text;
        }
        else
        {
            cut = chat.Send(prompt).Text ?? "";
        }
        LiteRtBenchmarkInfo before = chat.GetBenchmarkInfo()!;
        output.WriteLine($"input {input}, count {chat.TokenCount}, decoded {before.LastDecodeTokenCount}, send prefill " +
                         $"{before.LastSendPrefillTokenCount}, cut {chat.LastReplyReachedContextLimit}, full {chat.IsContextFull}");
        Assert.True(chat.LastReplyReachedContextLimit, $"the reply must run to the guard's budget: '{cut}'");
        Assert.True(chat.IsContextFull);
        Assert.True(before.LastSendPrefillTokenCount > 0);

        if (stream)
            await Assert.ThrowsAsync<LiteRtContextOverflowException>(async () =>
            {
                await foreach (LiteRtStreamChunk _ in chat.SendStreamingAsync("Reply with one word: OK")) { }
            });
        else
            Assert.Throws<LiteRtContextOverflowException>(() => chat.Send("Reply with one word: OK"));

        LiteRtBenchmarkInfo after = chat.GetBenchmarkInfo()!;
        Assert.True(chat.LastReplyReachedContextLimit, "a send the binding rejected cleared the flag");
        Assert.Equal(before.LastSendPrefillTokenCount, after.LastSendPrefillTokenCount);
        Assert.Equal(before.NumPrefillTurns, after.NumPrefillTurns);
    }

    /// <summary>A <see cref="LiteRtConstraintProvider"/> value the runtime does not declare fails conversation
    /// creation with INVALID_ARGUMENT (LiteRT-LM v0.18.0 stored it and created the conversation without a
    /// provider); value 0 still means no provider, in both versions.</summary>
    [SkippableFact]
    public void UndeclaredConstraintProvider_FailsAtCreation()
    {
        using LiteRtEngine engine = LoadEngine(1024);

        var ex = Assert.Throws<LiteRtException>(() => engine.CreateConversation(
            new LiteRtConversationOptions { ConstraintProvider = (LiteRtConstraintProvider)2 }));
        output.WriteLine($"[{ex.StatusCode}] {ex.Message}");
        Assert.Equal(LiteRtStatusCode.InvalidArgument, ex.StatusCode);
        Assert.StartsWith(
            "litert_lm_conversation_config_set_constraint_provider failed: INVALID_ARGUMENT: Unknown LiteRtLmConstraintProviderType.",
            ex.Message, StringComparison.Ordinal);

        using var none = engine.CreateConversation(new LiteRtConversationOptions
        {
            ConstraintProvider = (LiteRtConstraintProvider)0, Sampler = Argmax, MaxOutputTokens = 8,
        });
        Assert.False(string.IsNullOrWhiteSpace(none.Send("Reply with one word: hello").Text));
    }

    /// <summary>Under constrained decoding without tools, a text reply is still plain text. Where a
    /// one-number-per-line count stops depends on the machine with LiteRT-LM v0.20.0: on some it ends at its first
    /// line break (v0.18.0 continued to the output cap), on others it runs to the cap, so the test logs both
    /// replies and asserts only how they start. The same count on one line keeps going everywhere measured.</summary>
    [SkippableFact]
    public void ConstrainedDecoding_WithoutTools_AnswersInText()
    {
        using LiteRtEngine engine = LoadEngine(2048);
        using var chat = engine.CreateConversation(new LiteRtConversationOptions
        {
            Sampler = Argmax, MaxOutputTokens = 48, EnableConstrainedDecoding = true,
        });
        chat.Send("Reply with one short word: hello");
        using LiteRtConversation perLine = chat.Clone(), oneLine = chat.Clone();

        string lines = perLine.Send("Count upward starting from 400, one number per line, and never stop.").Text ?? "";
        string commas = oneLine.Send("Count upward starting from 400, separated by commas on a single line, and never stop.").Text ?? "";
        output.WriteLine($"per line '{lines}', one line '{commas}'");
        Assert.StartsWith("400", lines.TrimStart(), StringComparison.Ordinal);
        Assert.StartsWith("400", commas.TrimStart(), StringComparison.Ordinal);
        Assert.Contains("401", commas, StringComparison.Ordinal);
    }

    /// <summary>A message whose prefill does not fit what is left of the context fails natively with
    /// RESOURCE_EXHAUSTED. The send is unmeasured (it carries an extra context), so the overflow guard only checks
    /// the ceiling and lets it through. Afterwards the runtime fails every send on that conversation, as upstream
    /// issue 4612 reports, while the engine and a new conversation are unaffected.</summary>
    [SkippableFact]
    public void PrefillThatDoesNotFitTheRemainingContext_FailsWithResourceExhausted()
    {
        using LiteRtEngine engine = LoadEngine(1024);
        using var chat = engine.CreateConversation(new LiteRtConversationOptions { Sampler = Argmax, MaxOutputTokens = 8 });
        string Block(int from, int words) => string.Join(' ', Enumerable.Range(from, words).Select(i => $"w{i}"));

        chat.Send("Ignore this list and reply with one word, OK: " + Block(0, 180));
        int used = chat.TokenCount;
        string message = LiteRtJson.UserMessage("Ignore this list and reply with one word, OK: " + Block(1000, 100));
        int input = engine.CountTokens(chat.RenderMessageRaw(message));
        output.WriteLine($"used {used}, input {input}, limit 1024");
        Assert.True(used + input >= 1024 && input < 1024 && used < 1024 - 128, $"used {used}, input {input}");

        var ex = Assert.Throws<LiteRtException>(() => chat.SendRaw(message, extraContext: "{}"));
        output.WriteLine($"[{ex.StatusCode}] {ex.Message}");
        Assert.Equal(LiteRtStatusCode.ResourceExhausted, ex.StatusCode);
        Assert.Contains("Not enough space left in the context window for the input", ex.Message, StringComparison.Ordinal);
        Assert.Equal(used, chat.TokenCount);

        var after = Assert.Throws<LiteRtException>(() => chat.Send("Reply with one word: hello"));
        output.WriteLine($"next send: [{after.StatusCode}] {after.Message}");

        using var fresh = engine.CreateConversation(new LiteRtConversationOptions { Sampler = Argmax, MaxOutputTokens = 8 });
        Assert.False(string.IsNullOrWhiteSpace(fresh.Send("Reply with one word: hello").Text));
    }
}

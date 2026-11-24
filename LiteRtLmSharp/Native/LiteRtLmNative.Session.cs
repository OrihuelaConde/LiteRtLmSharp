using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LiteRtLmSharp.Native;

// Session API (c/engine.h, C API 1.1.0): a session prefills and decodes raw text on an engine, without the
// conversation layer's template, history, tool parsing or reasoning channels. The session config functions
// that conversations also use (create, max output tokens, sampler params, LoRA paths, speculative decoding,
// delete) are declared in LiteRtLmNative.cs; the input-data functions in LiteRtLmNative.Embeddings.cs.
//
// engine.h "Threading model": calls that use the same session must not overlap; a stream callback may call
// only litert_lm_session_cancel_process, and only on a chunk that is not final; after cancel_process the
// session must not be reused, only deleted. Every session must be deleted before its engine.
internal static unsafe partial class LiteRtLmNative
{
    // --- Session config ---------------------------------------------------

    /// <summary>Sets whether the session wraps its input in the model's prompt template. With the template
    /// off, the session sends the text as given. Fails with <c>INVALID_ARGUMENT</c> for a null
    /// config.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_session_config_set_apply_prompt_template(
        nint config, [MarshalAs(UnmanagedType.U1)] bool apply_prompt_template);

    // --- Session lifecycle ------------------------------------------------

    /// <summary>Creates a session on <paramref name="engine"/>. A null <paramref name="config"/> uses the
    /// default session config; the config is copied, so it can be deleted right after the call. The caller
    /// owns <paramref name="out_session"/> and frees it with <see cref="litert_lm_session_delete"/>, before
    /// the engine; it is set to NULL on failure. Fails with <c>INVALID_ARGUMENT</c> for a null engine or a
    /// null out pointer, otherwise with the error the runtime met while creating the session.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_engine_create_session(nint engine, nint config, nint* out_session);

    /// <summary>Deletes the session. It waits for the session's tasks to end, so a decode still in progress
    /// keeps it blocked until that decode stops. Never call it from a stream callback.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_session_delete(nint session);

    /// <summary>Requests the cancellation of the session's work and returns without waiting: the stream
    /// callback can still run afterwards. After this call the session must not be reused, only deleted. Fails
    /// with <c>INVALID_ARGUMENT</c> for a null session.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_session_cancel_process(nint session);

    // --- Checkpoints and rewind -------------------------------------------

    /// <summary>Saves the session's current point under <paramref name="label"/>, a NUL-terminated string
    /// lent for the call; a label that exists already is overwritten. Fails with <c>INVALID_ARGUMENT</c> for
    /// a null session or label, otherwise with the runtime's error.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_session_save_checkpoint(nint session, byte* label);

    /// <summary>Returns the session to the checkpoint saved under <paramref name="label"/>, a NUL-terminated
    /// string lent for the call. Fails with <c>INVALID_ARGUMENT</c> for a null session or label, otherwise
    /// with the runtime's error, for example when no checkpoint has that label.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_session_rewind_to_checkpoint(nint session, byte* label);

    /// <summary>Returns the session to step <paramref name="step"/> (a C <c>int</c>, not a
    /// <c>size_t</c>). Fails with <c>INVALID_ARGUMENT</c> for a null session, otherwise with the runtime's
    /// error, for example for a step past the current one. The C API reports no current step.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_session_rewind_to_step(nint session, int step);

    // --- Prefill, decode and scoring --------------------------------------

    /// <summary>Adds <paramref name="num_inputs"/> input items to the session's context and blocks until the
    /// prefill ends. <paramref name="inputs"/> points at an array of <c>LiteRtLmInputData*</c> built with
    /// <see cref="litert_lm_input_data_create"/>; the items stay owned by the caller and can be deleted right
    /// after the call. Fails with <c>INVALID_ARGUMENT</c> for a null session, null inputs or a zero count,
    /// otherwise with the runtime's error.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_session_run_prefill(nint session, nint* inputs, nuint num_inputs);

    /// <summary>Decodes a reply to the prefilled input and blocks until the decode ends. The caller owns
    /// <paramref name="out_responses"/> and frees it with <see cref="litert_lm_responses_delete"/>; it is set
    /// to NULL on failure. Fails with <c>INVALID_ARGUMENT</c> for a null session or out pointer, otherwise
    /// with the runtime's error. The binding does not call it: the declaration lets a test compare it with
    /// <see cref="litert_lm_session_run_decode_async"/> and pin the export.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_session_run_decode(nint session, nint* out_responses);

    /// <summary>Starts decoding a reply to the prefilled input and returns after queuing it; the reply arrives
    /// as chunks through <paramref name="callback"/>, an unmanaged function pointer with the
    /// <c>LiteRtLmStreamCallback</c> shape <c>(void* callback_data, const LiteRtLmStreamChunk* chunk)</c>,
    /// which receives <paramref name="callback_data"/> unchanged. The callback can run on the calling thread
    /// before this function returns. Each chunk is valid only during its callback. A non-OK status is the
    /// error that kept the stream from starting (<c>INVALID_ARGUMENT</c> for a null session or callback);
    /// errors after the start arrive as the error of a final chunk.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_session_run_decode_async(
        nint session, delegate* unmanaged[Cdecl]<nint, nint, void> callback, nint callback_data);

    /// <summary>Scores the target texts after the prefill and blocks until scoring ends.
    /// <paramref name="target_text"/> points at <paramref name="num_targets"/> NUL-terminated UTF-8 strings,
    /// lent for the call: one text, or several texts that are each a single token. Each score is the log
    /// likelihood of its target given the prefilled context. The call changes the session's memory (it
    /// consumes the pending input token and writes the target's tokens except the last), so rewind the
    /// session before any further prefill or decode. The caller owns <paramref name="out_responses"/> and
    /// frees it with <see cref="litert_lm_responses_delete"/>; it is set to NULL on failure. Fails with
    /// <c>INVALID_ARGUMENT</c> for a null session, targets or out pointer, a zero count, or several targets
    /// where one is not a single token; otherwise with the runtime's error.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_session_run_text_scoring(
        nint session, byte** target_text, nuint num_targets,
        [MarshalAs(UnmanagedType.U1)] bool store_token_lengths, nint* out_responses);

    // --- Responses --------------------------------------------------------
    // A responses object is owned by the caller and independent of the session once returned. The strings
    // it lends are valid until it is deleted. Each getter fails with INVALID_ARGUMENT for a null responses
    // object or out pointer, OUT_OF_RANGE for an index past the candidates, and NOT_FOUND when the value is
    // absent at that index.

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_responses_delete(nint responses);

    /// <summary>The number of candidates: the number of response texts or, when there are none (text
    /// scoring), the number of scores or token lengths. <paramref name="out_num_candidates"/> is written only
    /// on success.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_responses_get_num_candidates(nint responses, nuint* out_num_candidates);

    /// <summary>The response text at <paramref name="index"/>, lent by the responses object until it is
    /// deleted; <paramref name="out_text"/> is set to NULL on failure. The binding does not call it: it reads
    /// the reply of the blocking decode in a test.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_responses_get_response_text_at(nint responses, nuint index, nint* out_text);

    /// <summary>The score at <paramref name="index"/>. <paramref name="out_score"/> is written only on
    /// success.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_responses_get_score_at(nint responses, nuint index, float* out_score);

    /// <summary>The token length at <paramref name="index"/>, present when scoring was asked to store token
    /// lengths. <paramref name="out_token_length"/> is written only on success.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_responses_get_token_length_at(nint responses, nuint index, nuint* out_token_length);
}

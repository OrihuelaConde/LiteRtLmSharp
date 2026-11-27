using System.Diagnostics;
using LiteRtLmSharp;
using Xunit;
using Xunit.Abstractions;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// The stream teardown on a model: how a stream that ends, or that the consumer leaves, decides whether the
/// conversation is consumed (<see cref="LiteRtConversation.IsCancelled"/>), including the background KV-cache
/// refill the runtime runs after a reply with thinking.
/// Set LITERTLM_TEST_MODEL to a .litertlm file to run (CPU backend; skipped without it, and when
/// LITERTLM_TEST_BACKEND names another backend).
/// </summary>
public sealed class StreamTeardownModelTests(ITestOutputHelper output)
{
    /// <summary>Exact argmax over the TopP sampler (TopK = 1), so the replies repeat from run to run.</summary>
    private static readonly LiteRtSamplerParams Argmax = new() { Strategy = LiteRtSamplerType.TopP, TopK = 1 };

    private const string ShortThinkingPrompt = "Think about bridges first. Then write a 60-word paragraph about bridges.";

    private static LiteRtEngine LoadEngine()
    {
        string? model = Environment.GetEnvironmentVariable("LITERTLM_TEST_MODEL");
        Skip.If(string.IsNullOrEmpty(model) || !File.Exists(model), "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");
        // The engine is CPU whatever the pass: a pass on another backend would only repeat the CPU pass.
        Skip.If(LiteRtBackend.Parse(Environment.GetEnvironmentVariable("LITERTLM_TEST_BACKEND") ?? "cpu") != LiteRtBackend.Cpu,
            "These tests load a CPU engine; the CPU pass runs them.");
        LiteRtEngine.SetMinLogLevel(3);
        return LiteRtEngine.Load(new LiteRtEngineOptions { ModelPath = model!, Backend = LiteRtBackend.Cpu, MaxNumTokens = 4096 });
    }

    /// <summary>
    /// A <see cref="LiteRtConversation.CancelProcess"/> from another thread after the last chunk of a reply with
    /// thinking, while the enumerator has not finished yet (the teardown waits for the runtime's background
    /// KV-cache refill, about 0.7 s for this reply on CPU), either consumes the conversation or leaves one that
    /// sends again. A cancel that reaches the refill makes the runtime fail every later send, so
    /// <see cref="LiteRtConversation.IsCancelled"/> must not read <c>false</c> then.
    /// </summary>
    [SkippableFact]
    public async Task CancelProcess_WhileTheStreamTeardownWaits_ConsumesTheConversation_OrLeavesItUsable()
    {
        using LiteRtEngine engine = LoadEngine();
        using var chat = engine.CreateConversation(new LiteRtConversationOptions
        {
            Sampler = Argmax, EnableThinking = true, MaxOutputTokens = 1500,
        });

        long lastChunk = 0, cancelAt = 0;
        int finished = 0;
        Exception? watcherFault = null;
        // Cancels once no chunk has arrived for 200 ms while the read loop is still running.
        var watcher = new Thread(() =>
        {
            try
            {
                while (Volatile.Read(ref finished) == 0)
                {
                    long last = Volatile.Read(ref lastChunk);
                    if (last != 0 && Stopwatch.GetElapsedTime(last).TotalMilliseconds > 200)
                    {
                        Volatile.Write(ref cancelAt, Stopwatch.GetTimestamp());
                        chat.CancelProcess();
                        return;
                    }
                    Thread.Sleep(1);
                }
            }
            catch (Exception e)
            {
                watcherFault = e;
            }
        }) { IsBackground = true };
        watcher.Start();

        int chunks = 0;
        string outcome = "clean";
        long loopEnd;
        try
        {
            await foreach (LiteRtStreamChunk _ in chat.SendStreamingAsync(
                               "Think about bridges first. Then write a 250-word paragraph about bridges."))
            {
                chunks++;
                Volatile.Write(ref lastChunk, Stopwatch.GetTimestamp());
            }
        }
        catch (LiteRtException e) when (e.StatusCode == LiteRtStatusCode.Cancelled)
        {
            // The cancel landed before the final chunk (a long gap between two chunks): a cut stream.
            outcome = "cancelled";
        }
        finally
        {
            // Stop the watcher before anything else uses or disposes the conversation.
            loopEnd = Stopwatch.GetTimestamp();
            Volatile.Write(ref finished, 1);
            watcher.Join();
        }
        Assert.Null(watcherFault);

        long cancel = Volatile.Read(ref cancelAt), last = Volatile.Read(ref lastChunk);
        string timing = cancel == 0
            ? "no cancel"
            : $"cancel {Stopwatch.GetElapsedTime(last, cancel).TotalMilliseconds:F0} ms after the last chunk, " +
              $"loop end {Stopwatch.GetElapsedTime(last, loopEnd).TotalMilliseconds:F0} ms after it";
        output.WriteLine($"chunks {chunks}, stream {outcome}, {timing}; IsCancelled {chat.IsCancelled}");

        if (outcome == "cancelled")
            Assert.True(chat.IsCancelled);
        if (!chat.IsCancelled)
            Assert.False(string.IsNullOrWhiteSpace(chat.Send("Reply with one word: OK").Text));
        // Checked after the asserts so a failure still fails: a run where no cancel landed while the
        // teardown waited did not reach the scenario.
        Skip.If(outcome == "clean" && (cancel == 0 || cancel > loopEnd), "The cancel did not land while the teardown waited.");
    }

    /// <summary>
    /// A stream with thinking that the consumer leaves after its final chunk arrived, with chunks still unread,
    /// is a completed turn: the conversation is not consumed, its cached token count is the settled one, and it
    /// sends again.
    /// </summary>
    [SkippableFact]
    public async Task StreamWithThinking_LeftAfterItsFinalChunk_KeepsTheConversationUsable()
    {
        using LiteRtEngine engine = LoadEngine();
        using var chat = engine.CreateConversation(new LiteRtConversationOptions
        {
            Sampler = Argmax, EnableThinking = true, MaxOutputTokens = 1024,
        });

        LiteRtStreamChunkKind? first = null;
        await foreach (LiteRtStreamChunk chunk in chat.SendStreamingAsync(ShortThinkingPrompt))
        {
            first = chunk.Kind;
            // Creating a conversation waits for the generation in flight; the pause lets the final callback run.
            using LiteRtConversation waited = engine.CreateConversation();
            await Task.Delay(200);
            break;
        }

        int cached = chat.TokenCountCached;
        int count = chat.TokenCount;
        output.WriteLine($"first chunk {first}; IsCancelled {chat.IsCancelled}; cached {cached}, count {count}");
        Assert.Equal(LiteRtStreamChunkKind.Thinking, first);
        Assert.False(chat.IsCancelled);
        Assert.Equal(count, cached);
        Assert.False(string.IsNullOrWhiteSpace(chat.Send("Reply with one word: OK").Text));
    }

    /// <summary>
    /// A <see cref="LiteRtConversation.CancelProcess"/> during a stream consumes the conversation even when the
    /// reply had already ended natively, with chunks still unread, as <see cref="LiteRtConversation.IsCancelled"/>
    /// documents.
    /// </summary>
    [SkippableFact]
    public async Task CancelProcess_AfterTheReplyEnded_WithChunksUnread_ConsumesTheConversation()
    {
        using LiteRtEngine engine = LoadEngine();
        using var chat = engine.CreateConversation(new LiteRtConversationOptions { Sampler = Argmax, MaxOutputTokens = 48 });

        int chunks = 0;
        await foreach (LiteRtStreamChunk _ in chat.SendStreamingAsync("Write one short sentence about the sea."))
        {
            chunks++;
            // Creating a conversation waits for the generation in flight; the pause lets the final callback run.
            using LiteRtConversation waited = engine.CreateConversation();
            await Task.Delay(200);
            chat.CancelProcess();
            break;
        }

        Assert.Equal(1, chunks);
        Assert.True(chat.IsCancelled);
        Assert.Throws<InvalidOperationException>(() => chat.Clone());
    }
}

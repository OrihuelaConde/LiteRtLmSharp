# Sessions: raw prefill and decode

A session runs the model on text that you write. You prefill text into the model's context, and then you
decode a reply. Everything you prefill and every reply stays in the context, so you can add turns, but the
session keeps no list of messages that you can read back, restore, or clone. The experimental
[checkpoints](#experimental-checkpoints-rewind-and-reset) return the context to a point that you saved earlier on
the same session.

A session applies no chat template, so you write the prompt with the markup the model expects. It takes text
only, and it doesn't parse tool calls or reasoning. Create a session with `LiteRtEngine.CreateSession`.
Sessions and conversations can share one engine.

## When to use a session

Use a conversation for chat. Use a session when you need to control the exact text in the context, for
example to continue a reply that was cut.

| | Conversation | Session |
|---|---|---|
| Prompt template | Applied by the runtime | None: you write the text |
| Message history | Kept; restore it with `History`, fork it with `Clone()` | The context holds every prefill and reply; no list of messages, clone, or restore. Experimental: rewind to a saved checkpoint, or reset |
| Tools, reasoning, constrained decoding | Supported | Not available |
| Images and audio | Supported | Text only |
| Reply | `LiteRtResponse`, or chunks tagged as answer, thinking, or tool call | A string, or a stream of text pieces |
| Token count | `TokenCount` | None |
| Output cap | Per conversation, and per send with `LiteRtSendOptions` | Per session, fixed at creation |
| Input before a reply | One message per send | Any number of prefills before one decode |
| A reply that was cut | Ask again on a clone taken before the send ([Continue after a cancellation](conversation-state.md#continue-after-a-cancellation)) | Prefill the partial reply and decode the rest ([Continue a reply that was cut](#continue-a-reply-that-was-cut)) |

## Requirements

- LiteRtLmSharp 1.4.0 or later. The native packages of 1.4.0 carry LiteRT-LM v0.20.0.
- A chat model that you can load with `LiteRtEngine.Load`.

## Create a session and generate

The following example loads an engine, prefills one user turn, and decodes the reply. The later examples
reuse this `engine`.

```csharp
using LiteRtLmSharp;

using var engine = LiteRtEngine.Load(new LiteRtEngineOptions
{
    ModelPath = "gemma-4-E2B-it.litertlm",
    MaxNumTokens = 4096,
});

using LiteRtSession session = engine.CreateSession(new LiteRtSessionOptions
{
    Sampler = new LiteRtSamplerParams { Strategy = LiteRtSamplerType.Greedy },
    MaxOutputTokens = 256,
});

session.Prefill("<|turn>user\nName three primary colors.<turn|>\n<|turn>model\n");
string reply = session.Decode();
Console.WriteLine(reply);
```

The text is one user turn in Gemma 4's markup, followed by the start of the model's turn.
[Build the prompt text](#build-the-prompt-text) shows how to get this text from a conversation instead of
writing it by hand.

`Prefill` adds the text to the context and returns when the runtime has processed it. `Decode` generates the
reply and returns its text. After a reply that ends normally, `session.State` is `LiteRtSessionState.Decoded`,
and the session accepts the next prefill.

`PrefillAsync` runs the prefill on the thread pool. `DecodeAsync` generates the reply without blocking a
thread. `Prefill` and `PrefillAsync` throw `ArgumentNullException` for a `null` text and `ArgumentException`
for an empty one, and the state doesn't change.

`LiteRtSessionOptions` holds two settings, fixed for the session's lifetime:

- `Sampler`: the sampling settings. For greedy replies, set `Strategy = LiteRtSamplerType.Greedy`. With
  `null`, the engine's default sampling applies; on Gemma 4 E2B, in the one prompt measured, that default
  gave the greedy reply. `new LiteRtSamplerParams()` is not the same as `null`: it selects top-p sampling, and
  its reply differed from the greedy one.
- `MaxOutputTokens`: the most tokens one decode generates. See [Cap the reply length](#cap-the-reply-length).

## Build the prompt text

A session sends your text to the model as you give it. It has no template, so the markup of each turn is your
part of the work. A conversation can render that markup for you without sending anything.

### The first turn

For the first turn, create a conversation with the options you would give a conversation for this chat,
system message included, and send nothing on it. This guide calls it an *unsent conversation*.
`RenderMessage(text)` on it returns the whole first prompt: the system message, any `History`, the user's
turn, and the start of the model's turn.

```csharp
var conversationOptions = new LiteRtConversationOptions { SystemMessage = "Be brief." };

string firstPrompt;
using (LiteRtConversation unsent = engine.CreateConversation(conversationOptions))
    firstPrompt = unsent.RenderMessage("What is the capital of France?");

using LiteRtSession session = engine.CreateSession();
session.Prefill(firstPrompt);
Console.WriteLine(session.Decode());
```

The conversation only renders: it sends nothing, and you can dispose it right away. Don't add
`RenderPreface()` to the text: `RenderMessage` on an unsent conversation already includes the system message
and any `History`, so the preface would be prefilled twice.

### Later turns on Gemma 4

For each later turn on Gemma 4, prefill exactly this text, with the user's message in place of `{text}`:

```text
<|turn>user
{text}<turn|>
<|turn>model

```

In C#, that is `$"<|turn>user\n{text}<turn|>\n<|turn>model\n"`. Put nothing before it: no `<turn|>` and no
line break. When a reply ends by itself, the context already holds one more position than the reply's tokens,
taken to be the end of the model's turn, although the reply text doesn't show it
([Context limits and the output cap](#context-limits-and-the-output-cap) explains positions). A conversation
that renders a `History` puts `<turn|>` and a line break after each model reply; don't copy that shape into a
session.

With this text, a three-turn session gave the same replies as a conversation, byte for byte, in 10 of 10
dialogs on the CPU and on the GPU, with and without a system message. Texts that closed the previous turn
first, or started with a line break, matched in only 2 to 4 of 10. Use the same text after a reply that
`MaxOutputTokens` cut: it matched the conversation there too, in the one dialog measured.

```csharp
var conversationOptions = new LiteRtConversationOptions { SystemMessage = "Be brief." };

string firstPrompt;
using (LiteRtConversation unsent = engine.CreateConversation(conversationOptions))
    firstPrompt = unsent.RenderMessage("I am planning a trip to the mountains.");

static string NextTurn(string text) => $"<|turn>user\n{text}<turn|>\n<|turn>model\n";

var greedy = new LiteRtSamplerParams { Strategy = LiteRtSamplerType.Greedy };
using LiteRtSession session = engine.CreateSession(new LiteRtSessionOptions { Sampler = greedy });

session.Prefill(firstPrompt);
Console.WriteLine(session.Decode());

session.Prefill(NextTurn("What should I pack?"));
Console.WriteLine(session.Decode());

session.Prefill(NextTurn("And for a rainy day?"));
Console.WriteLine(session.Decode());
```

### The start token

Before your first prefill, the runtime adds one position at the start of the context, for the model's start
token. Leave the start token's text out of every prefill. On Gemma 4 E2B, `RenderMessage` output doesn't
contain it.

On Gemma 4 and Ministral 3, whose start token is one token ID, the session refuses a first prefill whose text
starts with that token's text: `<bos>` on Gemma 4 E2B and E4B, `<s>` on Ministral 3. `Prefill` or
`PrefillAsync` throws `ArgumentException`, and the session doesn't change. That text becomes a second start
token (on Gemma 4 this is inferred from the positions it takes; on Ministral 3 the tokenizer itself maps `<s>`
to the start token ID), and it changed the reply in 1 or 2 of 3 prompts on each model.

The session checks only the first prefill of a `Fresh` session. A session is `Fresh` again after the
experimental `Reset`, and after a rewind to a checkpoint saved while it was `Fresh`: the runtime then adds the
start position again, and the check applies again (see
[The start token after a reset](#the-start-token-after-a-reset)). Keep the start token's text out of later
prefills too: on Gemma 4 a later `<bos>` also takes the one position of a start token, and on Ministral 3 the
tokenizer turns a later `<s>` into the start token ID.

### Other models

- **First turn.** The unsent-conversation approach worked on every model tested: Gemma 4 E2B and E4B,
  Ministral 3, and Phi-4-mini, on the CPU and on the GPU. These checks used 3 short prompts per model, with no
  system message, and compared the first 32 tokens of each reply.
- **Multi-token start token.** On Phi-4-mini, the start token is several token IDs, and its text
  (`<|endoftext|>`) is ordinary tokens to the tokenizer, so the session doesn't refuse it. The text is not
  needed, and adding it changed the reply (3 of 3 prompts on the CPU, 1 of 3 on the GPU).
- **Later turns.** This guide has a measured later-turn text for Gemma 4 E2B only. Each model's chat template
  defines its own. To check a candidate text on another model, compare a session's replies with a
  conversation's, with greedy sampling on both.

## Stream the reply

`DecodeStreamingAsync` returns the reply as text pieces while the model generates it:

```csharp
session.Prefill(prompt);   // prompt holds the text built as in "Build the prompt text".

await foreach (string piece in session.DecodeStreamingAsync())
    Console.Write(piece);

if (session.State == LiteRtSessionState.ContextFull)
    Console.WriteLine("\n[The context is full.]");
```

- The decode starts when the loop asks for the first piece, and the session is busy until the decode ends.
- Each piece holds whole characters. The pieces joined are the text that `Decode` returns.
- While the loop runs, another call on the session throws `InvalidOperationException`, including a `Prefill`
  from inside the loop body.
- Leaving the loop early, with `break` or an exception, cancels the decode. See
  [Cancel a generation](#cancel-a-generation).
- An enumerator that you drop without disposing it doesn't cancel: the decode runs to its end. An
  `await foreach` loop always disposes it.

## Continue a reply that was cut

When a reply stops before its end, the model can finish it on a session. Prefill the prompt that the reply
answered, followed by the partial reply, and decode. The model continues from the end of the partial text as
if it had written that text itself.

Whether the result matches the reply that was never cut depends on the backend:

- **CPU:** the partial reply followed by the continuation was the same text, byte for byte, as the reply that
  was never cut, in 45 of 45 cases, with no system message, with a system message, and with a history.
  Splitting the prefill into several calls made no difference.
- **GPU:** the continuation starts at the cut and ends like any reply, but its text differed from the uncut
  reply in 45 of 45 cases, in some of them from the first token after the cut. Splitting the prefill into
  several calls can change the continuation too: two calls gave the one-call continuation in 3 of 45 cases.
  Treat a GPU continuation as another reply that starts with the partial text.

To continue a cut reply, follow these steps:

1. Keep the partial reply as the stream gave it: the text of every `Answer` chunk, in order and untrimmed, and
   stop between two chunks. A partial reply that ends inside a chunk didn't continue the same way, even on
   the CPU (0 of 13 cases).
1. Render the prompt with `RenderMessage(text)` on an unsent conversation. Create it with the same options as
   the conversation whose reply was cut, plus a `History` that holds every turn that conversation had already
   completed. The measured cases used the same `SystemMessage`, or a `History` with one earlier turn.
1. Create a session with the same sampler as the conversation. With `null` on both, both use the engine's
   default sampling. Prefill the prompt and the partial reply in one `Prefill` call.
1. Decode. The partial reply followed by the decoded text is the whole reply.

The following example cuts a conversation's stream and finishes the reply on a session:

```csharp
using System.Text;
using LiteRtLmSharp;

var greedy = new LiteRtSamplerParams { Strategy = LiteRtSamplerType.Greedy };
var options = new LiteRtConversationOptions { SystemMessage = "Be brief.", Sampler = greedy };
const string Question = "Explain in three sentences why the sky is blue.";

var partial = new StringBuilder();
using (LiteRtConversation chat = engine.CreateConversation(options))
{
    await foreach (LiteRtStreamChunk chunk in chat.SendStreamingAsync(Question))
    {
        if (chunk.Kind != LiteRtStreamChunkKind.Answer)
            continue;
        partial.Append(chunk.Text);
        if (partial.Length > 80)   // Stands for whatever cut the reply.
            break;
    }
}

string prompt;
using (LiteRtConversation unsent = engine.CreateConversation(options))
    prompt = unsent.RenderMessage(Question);

using LiteRtSession session = engine.CreateSession(new LiteRtSessionOptions { Sampler = greedy });
session.Prefill(prompt + partial);
string rest = session.Decode();
Console.WriteLine(partial + rest);
```

The session's `MaxOutputTokens` applies to the continuation alone, so the whole reply can run longer than one
capped reply. The recipe was not measured with reasoning or tool calls on.

To go on with the chat after the continuation, you have two paths. Neither was measured after a continued
reply.

- **Stay on the session.** Prefill the next turn as in [Later turns on Gemma 4](#later-turns-on-gemma-4). The
  context already holds the whole reply, so nothing is replayed.
- **Go back to a conversation.** Create a conversation whose `History` holds the earlier turns, the user's
  message, and the whole reply as a model message (`LiteRtMessage.Model`), as in
  [Restore chat history](conversation-state.md#restore-chat-history). Its first send replays the history.

Stay on the session when you only need text. Go back to a conversation when you need its features, such as
`TokenCount`, `Clone()`, or tools.

### Finish a session's own reply

A cancelled session accepts only `Dispose`. When that session held one prompt, you can try the same recipe to
finish its reply: on a new session, prefill the same prompt followed by the partial reply, and decode. This was
not measured: every measured partial reply came from a conversation's stream. For a session that held several
turns, this guide has no recipe.

## Session states

`LiteRtSession.State` says which calls the session accepts next. The last column lists experimental members,
which need an opt-in (see [Turn on the experimental members](#turn-on-the-experimental-members)). Without the
opt-in, only the **Accepts** column applies.

| State | The session holds | Accepts | Also accepts, experimental |
|---|---|---|---|
| `Fresh` | Nothing yet | `Prefill`, `PrefillAsync`, `Dispose` | `SaveCheckpoint`, `RewindTo`, `Reset` |
| `Prefilled` | Input that waits for a decode | `Prefill`, `PrefillAsync`, `Decode`, `DecodeAsync`, `DecodeStreamingAsync`, `Dispose` | `SaveCheckpoint`, `RewindTo`, `Reset` |
| `Decoded` | A reply that ended by itself or at `MaxOutputTokens` | `Prefill`, `PrefillAsync`, `Dispose` | `SaveCheckpoint`, `RewindTo`, `Reset` |
| `ContextFull` | A reply that stopped at the end of the context | `Dispose` | `RewindTo`, `Reset` |
| `Faulted` | A call that the runtime failed: a prefill, a decode, or an experimental member | `Dispose` | After a failed prefill: `RewindTo`, `Reset`. After any other failure: nothing |
| `Cancelled` | A reply that a cancel stopped | `Dispose` | Nothing |

A call that the state doesn't accept throws `InvalidOperationException` before it reaches the runtime, and the
state doesn't change. A decode on a `Fresh` or `Decoded` session is refused that way: prefill first.

Without the experimental members, `ContextFull`, `Faulted`, and `Cancelled` accept only `Dispose`. After a
cancel, a failed prefill, or a full context, the runtime skipped later prefills on the session without an error,
and a decode gave no reply. The session refuses that work instead, and it refuses the same work after a failed
decode as a precaution. Dispose the session, create another one, and prefill the context again. With the
experimental members, `RewindTo` and `Reset` also accept a `ContextFull` session and a session that a failed
prefill left `Faulted`, and return it to a state that accepts a prefill (see
[Recover a full or failed session](#recover-a-full-or-failed-session)). `SaveCheckpoint` needs `Fresh`,
`Prefilled`, or `Decoded`, so save the checkpoint before the call that can fail.

`State` doesn't say which call left a session `Faulted`. The exception does: a `LiteRtException` from `Prefill`
or `PrefillAsync` leaves a session that `RewindTo` and `Reset` accept. Any other call that leaves the session
`Faulted`, including a decode that throws `OperationCanceledException`, leaves one that accepts only `Dispose`.

When `Decode` returns, the state is `Decoded` or `ContextFull`:

```csharp
session.Prefill(prompt);
string reply = session.Decode();

switch (session.State)
{
    case LiteRtSessionState.Decoded:
        // The reply ended. Prefill the next turn to go on.
        break;
    case LiteRtSessionState.ContextFull:
        // reply holds the text up to the end of the context. Of the stable members, the session
        // accepts only Dispose.
        break;
}
```

`State` can be read from any thread. After `Dispose`, it keeps its value, except that a call still running (a
prefill, a decode, or an experimental `SaveCheckpoint`, `RewindTo`, or `Reset`) moves it to its outcome when it
ends (`Cancelled` for a decode that the dispose stopped).

### Exceptions and the state they leave

| Exception | When | State afterwards |
|---|---|---|
| `ArgumentNullException`, `ArgumentException` | A prefill with a `null` or empty text, or a first prefill that starts with the start token's text. Experimental: `RewindTo` with `null` or with a checkpoint saved on another session | Unchanged |
| `InvalidOperationException` | Another call on the session is running, or the state doesn't accept the call | Unchanged |
| `InvalidOperationException` from the experimental `RewindTo` | The runtime has dropped the checkpoint. `InnerException` is a `LiteRtException` whose `StatusCode` is `NotFound` | Unchanged |
| `LiteRtException` from `Prefill` or `PrefillAsync` | The runtime rejected the prefill | `Faulted`; also accepts the experimental `RewindTo` and `Reset` |
| `LiteRtException` from any other member | A decode didn't start or ended with an error, or the experimental `SaveCheckpoint`, `RewindTo`, or `Reset` failed | `Faulted`; accepts only `Dispose` |
| `OperationCanceledException` from `DecodeAsync` or `DecodeStreamingAsync` | The token fired | `Cancelled`; or `Decoded`, `ContextFull`, or `Faulted` when the token fired after the last piece; unchanged (`Prefilled`) when the token was cancelled before the decode started |
| `OperationCanceledException` from `PrefillAsync` | The token was cancelled before the prefill started | Unchanged |
| `ObjectDisposedException` from a decode that was running | A `Dispose` stopped the decode | `Cancelled` |
| `ObjectDisposedException` from any other call | The session was disposed before the call reached the runtime | Unchanged |

Three cases need a closer look:

- **`Dispose` during a blocking `Prefill`.** A prefill that has reached the runtime runs to its end, and
  `Prefill` returns, or throws `LiteRtException` if the runtime rejects the text. A `Dispose` that comes before
  the prefill reaches the runtime makes `Prefill` throw `ObjectDisposedException`.
- **`Dispose` after the last piece of a blocking `Decode`.** Nothing is cancelled: `Decode` returns the reply,
  and the state follows how the reply ended.
- **`Dispose` during an experimental call.** A `SaveCheckpoint`, `RewindTo`, or `Reset` that has reached the
  runtime runs to its end the same way, and the state follows its outcome. A `Reset` can wait for seconds
  behind another session's generation (see [Cost and waiting](#cost-and-waiting)). After a `Dispose` in that
  time, a `Reset` that succeeds still leaves the session `Fresh`.

## Cancel a generation

There are three ways to stop a decode:

- **A token.** Pass a `CancellationToken` to `DecodeAsync` or `DecodeStreamingAsync`. The call throws
  `OperationCanceledException`.
- **Leave the stream early.** A `break` or an exception in the `await foreach` body cancels the decode. The
  loop exits after the runtime has stopped, so no call is running on the session, and the session is already
  `Cancelled` unless the reply had already reached its last piece.
- **Dispose the session** from another thread. The running decode throws `ObjectDisposedException`. This is
  the only way to stop a blocking `Decode`, which takes no token.

The cancel reaches the runtime at the next piece of the reply, and the runtime stops there. After you request
the cancel, the loop can still receive the piece that carries it and any pieces already waiting, and then it
ends. In the measurements, the runtime stopped within about the time of one piece after the request. The
session is then `Cancelled` and accepts only `Dispose`. Every piece you received is part of the reply: keep the
pieces if you want to [continue the reply](#continue-a-reply-that-was-cut).

```csharp
using System.Text;

using LiteRtSession session = engine.CreateSession();
session.Prefill(prompt);   // prompt holds the text built as in "Build the prompt text".

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
var partial = new StringBuilder();
try
{
    await foreach (string piece in session.DecodeStreamingAsync(cts.Token))
        partial.Append(piece);
}
catch (OperationCanceledException)
{
    // Cancelled; or Decoded, ContextFull, or Faulted when the token fired after the last piece;
    // or Prefilled when it fired before the decode started.
    Console.WriteLine($"Stopped in state {session.State}: {partial}");
}
```

If the cancel arrives after the last piece but before the call returns, nothing is cancelled. The state is
`Decoded` or `ContextFull`, or `Faulted` if the decode failed, and the session accepts what that state accepts.
A token still makes `DecodeAsync` or the stream throw `OperationCanceledException`, so always read `State` in
the catch block. A `Dispose` that arrives after the last piece doesn't make the call throw.

A prefill can't be cancelled once it starts. The token of `PrefillAsync` works only before the prefill starts:
the call then throws `OperationCanceledException` and leaves the session as it was. A `Dispose` during a
prefill doesn't wait for it: the native session is released when the prefill ends.

## Context limits and the output cap

The context is counted in positions. Most tokens take one position each;
[Count the context yourself](#count-the-context-yourself) lists the measured exceptions. A session's context
holds the start position, everything you prefilled, and every reply. A reply that ends by itself takes one more
position than its tokens. A reply cut by `MaxOutputTokens` takes only its tokens.

`LiteRtEngineOptions.MaxNumTokens` sets the size of the context when it is below the model's built-in context
length. With 0, the engine's default applies, and a value at or above the model's length falls back to the
model's own size. The remarks of that property in the API reference give the details.

### A prefill that doesn't fit

A prefill that doesn't fit throws `LiteRtException`, and the session is `Faulted`. Don't rely on `StatusCode`
alone to tell this from another failure: it can be `ResourceExhausted`, `InvalidArgument`, or
`FailedPrecondition`, depending on how the input doesn't fit. The message says that the input doesn't fit the
remaining context. For `InvalidArgument` and `FailedPrecondition`, it says so only when the runtime gives its
reason, and the runtime drops the reason when its log is silenced with `LiteRtEngine.SetMinLogLevel` above 5.
Whatever the cause, the session is `Faulted`, and of the stable members it accepts only `Dispose`.

```csharp
string? Generate(LiteRtEngine engine, string prompt)
{
    using LiteRtSession session = engine.CreateSession();
    try
    {
        session.Prefill(prompt);
    }
    catch (LiteRtException e)
    {
        // The runtime rejected the prompt. The session is Faulted: of the stable members, it
        // accepts only Dispose.
        Console.WriteLine($"{e.StatusCode}: {e.Message}");
        return null;
    }
    return session.Decode();
}
```

To go on, prefill a shorter context on a new session, or load the engine with a larger `MaxNumTokens`. With the
experimental members, you can also keep this session (see
[Recover a full or failed session](#recover-a-full-or-failed-session)).

### A decode that fills the context

A decode that fills the context doesn't throw. It returns the text up to the end of the context and leaves the
session `ContextFull`. Of the stable members, `ContextFull` accepts only `Dispose` (for the experimental ones, see
[Recover a full or failed session](#recover-a-full-or-failed-session)).

### Count the context yourself

A session has no token count. To keep track of the context, count with `LiteRtEngine.Tokenize`: one start
position, plus the tokens of every prefilled text and every reply, plus one for each reply that ended by
itself.

`State` is `Decoded` both after a reply that ended by itself and after one that `MaxOutputTokens` cut, so the
session doesn't tell you which one you have. A reply whose `Tokenize` count equals `MaxOutputTokens` most
likely reached the cap: in the measurements, a capped reply of N tokens re-tokenized to exactly N. To stay on
the safe side, count one extra position for every reply.

With the experimental members, store your position count with each checkpoint. After a `RewindTo`, the context
holds the positions it held at the save. After a `Reset`, the count starts again at the one start position.

Leave a margin, because a prefill can be refused before its tokens fill the space left, and some text takes
more positions than `Tokenize` counts:

- On Gemma 4 E2B with `MaxNumTokens` 1024, with 423 positions left after an earlier prefill, a prefill of 384
  tokens was accepted and prefills of 390 to 422 tokens were refused. With 123 positions left, a prefill of 16
  tokens was refused. This fits a prefill rounded up to a multiple of 128, but the rounding is inferred.
- On Gemma 4, `<bos>` takes one position although `Tokenize` counts 3 tokens for it. Keep it out of the text
  anyway (see [The start token](#the-start-token)).
- On Phi-4-mini, a prefill whose text starts with a template marker such as `<|user|>` took 3 to 4 positions
  more than `Tokenize` counts.

### Cap the reply length

`LiteRtSessionOptions.MaxOutputTokens` caps every decode of the session. With a cap of N, a reply that runs
that long has exactly N tokens. It ends without an error, and the state is `Decoded`, as for a reply that
ended by itself. The next turn takes the same text as after any reply (see
[Later turns on Gemma 4](#later-turns-on-gemma-4)).

A session can't decode on from a capped reply: `Decoded` refuses a decode, and `Prefill` refuses an empty text.
To get the rest of a capped reply, you can try the recipe in
[Continue a reply that was cut](#continue-a-reply-that-was-cut) on a new session. It was not measured with a
capped reply from a session.

With 0, the default, the session sends no cap to the runtime. A negative value throws
`ArgumentOutOfRangeException`.

## Experimental: checkpoints, rewind, and reset

A checkpoint records a point in a session's context. `RewindTo` returns the session to that point, and `Reset`
clears the context. With them you can try a turn, drop it, and try another one. You can also go on with a
session whose context filled or whose prefill failed, without creating a new session and prefilling the context
again.

### Turn on the experimental members

`LiteRtSession.SaveCheckpoint`, `LiteRtSession.RewindTo`, `LiteRtSession.Reset`, and the
`LiteRtSessionCheckpoint` type carry `[Experimental("LITERTLM001")]`. The C# compiler reports every use of them as
error LITERTLM001, and the error's help link opens this section. They're experimental because their behavior was
measured on one platform and one model only (see [Limitations](#limitations)), so they can change or be removed
in a minor version. The stable members don't depend on them.

To use them, suppress the diagnostic in the files that call them:

```csharp
#pragma warning disable LITERTLM001
```

Or suppress it for the whole project:

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);LITERTLM001</NoWarn>
</PropertyGroup>
```

In a check of the same attribute with another diagnostic ID, Visual Basic reported a warning, not an error, with
the ID and the link. F# reported its generic warning FS0057, with no ID and no link.

### Try a turn and go back

`SaveCheckpoint` returns a `LiteRtSessionCheckpoint` for the session's current point, and the state doesn't
change. `RewindTo` returns the context and `State` to the ones the checkpoint recorded: `Fresh`, `Prefilled`, or
`Decoded`. After a rewind to a checkpoint saved after a reply, as in the following example, the session is
`Decoded` and accepts a prefill. After a rewind to one saved after a prefill, it is `Prefilled` and accepts a
decode or another prefill.

The following example saves the point after the first reply, tries a turn, and, when the reply doesn't fit what
the app needs, drops that turn and asks again with other wording:

```csharp
#pragma warning disable LITERTLM001   // SaveCheckpoint, RewindTo, and Reset are experimental.

static string NextTurn(string text) => $"<|turn>user\n{text}<turn|>\n<|turn>model\n";

var greedy = new LiteRtSamplerParams { Strategy = LiteRtSamplerType.Greedy };
using LiteRtSession session = engine.CreateSession(new LiteRtSessionOptions { Sampler = greedy });

session.Prefill(firstPrompt);   // firstPrompt holds the text built as in "The first turn".
Console.WriteLine(session.Decode());

// The session is Decoded. Save the point after the first reply.
LiteRtSessionCheckpoint afterFirstReply = session.SaveCheckpoint();

session.Prefill(NextTurn("Reply with one word: which city is the Eiffel Tower in?"));
string answer = session.Decode().Trim();

if (answer.Contains(' '))
{
    // Not one word. Drop the attempt from the context, and ask the same turn another way.
    session.RewindTo(afterFirstReply);   // Decoded again, with only the first turn in the context.
    session.Prefill(NextTurn("Which city is the Eiffel Tower in? Answer with the city name only."));
    answer = session.Decode().Trim();
}

Console.WriteLine(answer);
```

In the measurements, the same work run again after a rewind gave the same reply, byte for byte, as the first
pass from that point and as a new session that reached the point the same way. This held on the CPU and on the
GPU, and also when another session or a conversation used the engine between the save and the rewind. On the
GPU this differs from re-prefilling a reply on a new session (see
[Continue a reply that was cut](#continue-a-reply-that-was-cut)).

The sampler is fixed for the session's lifetime, so a rewind can't give you a second reply with other sampling
settings. With greedy sampling the same turn gives the same reply again, which is why the example rewords it.
Every measurement used argmax sampling; whether another strategy gives a different reply after a rewind was not
measured.

### What drops a checkpoint

The runtime keeps the saved points inside the session, and it drops them in two cases:

- **A rewind** drops every checkpoint saved at a later point than the one it returns to. Checkpoints saved at
  that same point stay, the one you rewound to included.
- **A reset** drops every checkpoint except those saved while the session was `Fresh`.

A rewind to a dropped checkpoint throws `InvalidOperationException`, whose `InnerException` is a
`LiteRtException` with `StatusCode` `NotFound`. The session doesn't change: in the measurements, neither its
position nor its next reply changed. The state didn't change either on a `ContextFull` session or on one that a
failed prefill left `Faulted`, and a rewind to a checkpoint the session still held then recovered it.

A checkpoint is valid only on the session that saved it. `RewindTo` with a checkpoint of another session throws
`ArgumentException`, and with `null`, `ArgumentNullException`. The three members check in this order: disposal
(`ObjectDisposedException`), then the argument, then another call in progress, then the state
(`InvalidOperationException` for both).

A checkpoint has nothing to dispose, and the C API has no call that drops one checkpoint. The memory that saved
points take was not measured.

### Reset a session

`Reset` clears the context and returns the session to `Fresh`. It changes only the context: the
`LiteRtSessionOptions` stay the ones the session was created with. In the measurements, after one turn and after
three, the next prompt took the same positions and gave the same reply as on a new session. The output cap after
a reset wasn't measured.

`Reset` is not faster than a new session, and it can wait for another session's generation (see
[Cost and waiting](#cost-and-waiting)). What it keeps is the session object and the checkpoints saved while the
session was `Fresh`.

### Recover a full or failed session

`RewindTo` and `Reset` accept these states, besides `Fresh`, `Prefilled`, and `Decoded`:

- `ContextFull`.
- `Faulted` after a `LiteRtException` from `Prefill` or `PrefillAsync`, whatever its `StatusCode`.

They refuse, with `InvalidOperationException`, a session that any other failure left `Faulted`: a decode that
didn't start or ended with an error, or a failed `SaveCheckpoint`, `RewindTo`, or `Reset`. They also refuse a
`Cancelled` session, because after a cancel the C API allows only deleting the session. Those sessions accept
only `Dispose`.

A rewind needs a checkpoint saved before the call that filled the context or failed. `Reset` needs none.

```csharp
#pragma warning disable LITERTLM001

// session is Decoded after a reply. NextTurn is the function from the previous example, and longText and
// shortText hold two versions of the next user message.
LiteRtSessionCheckpoint beforeTurn = session.SaveCheckpoint();
try
{
    session.Prefill(NextTurn(longText));
}
catch (LiteRtException)
{
    // The prefill failed, and the session is Faulted. Return to the point before it.
    session.RewindTo(beforeTurn);
    session.Prefill(NextTurn(shortText));
}

string reply = session.Decode();
if (session.State == LiteRtSessionState.ContextFull)
{
    // The reply filled the context. Drop this turn, and keep the session for another one.
    session.RewindTo(beforeTurn);
}
```

In the measurements, after a full context and after a failed later prefill, both a rewind and a reset were
followed by a decode with the same reply as a new session that reached the same point without the failure, on
the CPU and on the GPU. After a failed first prefill, both a reset and a rewind to a checkpoint saved while the
session was `Fresh` gave the same reply as a new session. Every failed prefill measured was input that didn't fit:
the `InvalidArgument` "too long" form, `ResourceExhausted`, and the input-length form of `FailedPrecondition`. The
session also accepts a rewind or a reset after any other prefill failure, but that case wasn't measured.

A rewind to a point from which the reply fills the context fills it again. In the measurements, after a rewind to
a checkpoint saved right after a long prefill, the next decode filled the context again with the same reply. Rewind
to a checkpoint saved before the long input instead.

### The start token after a reset

After a `Reset`, or a rewind to a checkpoint saved while the session was `Fresh`, the session is `Fresh`. The
runtime adds the start position again on the next prefill, so prefill the first-turn text, as in
[The first turn](#the-first-turn), and not a later-turn text. The session refuses a first prefill that starts with
the start token's text again, as described in [The start token](#the-start-token).

### Cost and waiting

In the measurements, on an idle engine, a `SaveCheckpoint` or a `RewindTo` took at most 0.1 ms, and a save on a
`Fresh` session about 1 ms. A `Reset` took about 1 ms, and 12 to 16 ms on the GPU after a prefill. The first
`SaveCheckpoint` or `RewindTo` on a session after another session or a conversation used the engine took 5 to
14 ms on the CPU and 285 to 392 ms on the GPU.

Like `Prefill` and `CreateSession`, `Reset` waits while another session of the engine generates: behind a
160-token decode, it took 4.4 to 4.7 s on the CPU and 1.4 to 1.5 s on the GPU. Whether `SaveCheckpoint` and
`RewindTo` wait the same way was not measured. On an idle engine, `CreateSession` took at most 0.4 ms after the
engine's first session.

### What the binding doesn't wrap

- **Text scoring.** The C API can score a target text: the log likelihood of the text after the prefilled
  context. Scoring writes to the context, so the session has to rewind after each score. The binding doesn't wrap
  it, because in the measurements it failed in two ways. After a save, a score of a one-token target,
  and a rewind, the next decode failed with `INVALID_ARGUMENT: Unprocessed token is null.` and left the session
  unusable, on the CPU and on the GPU. A score taken after a rewind that undid a prefill returned a different
  value than the same score on a new session.
- **Rewind to a position.** The C API can rewind to a numbered position, but it reports no current position, so
  a caller can't choose a valid one. `Reset` is the rewind to position 0.

## Threads, lifetime, and the engine

- **One call at a time.** A session runs one call at a time. A second call while one runs throws
  `InvalidOperationException` at once; it doesn't wait. A decode, streamed or not, counts as running until it
  ends. The experimental `SaveCheckpoint`, `RewindTo`, and `Reset` follow the same rule. `State` and `Dispose`
  can be called from any thread at any time.
- **Other sessions and conversations on the engine.** In the measurements, the runtime ran overlapping calls
  on one engine one after the other, and every reply was the same as when it ran alone. `CreateSession`
  waited until a running session decode or conversation send ended, and so did a `Prefill` on another session
  while a session decoded. A conversation's stream that started first gave its last piece, but in all but one
  run its loop ended only after a session decode that started during it. The experimental `Reset` waited the
  same way for another session's decode; behind a conversation send it wasn't measured (see
  [Cost and waiting](#cost-and-waiting)). `Dispose` of an idle session didn't wait. These results come from one
  model, one pair of prompts, and 5 to 10 runs per case, so treat them as observations, not as a guarantee.
  Create the sessions you need before you start a long generation, and don't call `CreateSession` or `Prefill`
  on a UI thread while something generates on the engine.
- **Session lifetime.** Keep one session per dialog while you add turns, and dispose it when the dialog ends.
  Without the experimental members, dispose it as soon as it is `ContextFull`, `Faulted`, or `Cancelled`. With
  them, you can keep a `ContextFull` session, or one that a failed prefill left `Faulted` (see
  [Recover a full or failed session](#recover-a-full-or-failed-session)). The memory that an idle session holds
  was not measured.
- **Dispose.** `Dispose` doesn't wait for a call in progress: it cancels a running decode, and the native
  session is released when that call ends. Calls after `Dispose` throw `ObjectDisposedException`. Calling
  `Dispose` again does nothing.
- **The session keeps the engine alive.** You can dispose the engine before its sessions: each session holds
  the native engine, which is released after its last conversation or session is disposed. Until then,
  `LiteRtEngine.LiveEngineCount` counts it, so `LiteRtEngine.Load` can refuse a new engine.

```csharp
LiteRtEngine engine = LiteRtEngine.Load(new LiteRtEngineOptions { ModelPath = "gemma-4-E2B-it.litertlm" });
using LiteRtSession session = engine.CreateSession();
engine.Dispose();   // The session still holds the native engine.

session.Prefill("<|turn>user\nName three primary colors.<turn|>\n<|turn>model\n");
Console.WriteLine(session.Decode());
// The native engine is released when the session is disposed.
```

## Limitations

- **What conversations have and sessions don't.** Besides the differences in the table in
  [When to use a session](#when-to-use-a-session), a session has no LoRA adapter and no `GetBenchmarkInfo()`.
- **Where it was measured.** Unless a section says otherwise, the results in this guide come from Gemma 4 E2B
  with LiteRT-LM v0.20.0 on Windows x64, on the CPU and on an NVIDIA GPU. Apart from the comparison of sampler
  settings, every comparison of two replies used argmax sampling (top-k 1), which gave the same replies as
  `Greedy`. Linux, macOS, Android, and other GPU vendors were not measured.
- **Models.** The later-turn text, the continuation of a cut reply, the cancel, the context limits, and the
  experimental checkpoints, rewind, and reset were measured on Gemma 4 E2B only. The start-token rule and the
  first-turn text were also measured on Gemma 4 E4B, Ministral 3, and Phi-4-mini. Prompts with reasoning or tool
  markup were not measured.
- **Continuing a cut reply.** It was measured with partial replies from a conversation's stream, cut after one
  prompt, with no system message, with a system message, or with a one-turn history. Continuing a cut reply in
  a session that already held earlier turns was not measured, and neither was going on with the chat after a
  continuation. Whether a continuation on the GPU reads as one coherent reply was not measured either.
- **Concurrency.** Two conversations generating at the same time, and overlapping calls on other models, were
  not measured.
- **Checkpoints, rewind, and reset.** Not measured: a save or a rewind while another session generates, many
  checkpoints held at the same time, and any rewind or reset with
  `LiteRtEngineOptions.UseRingbuffersLocalAttention` on. The recovery was measured only after prefills whose
  input didn't fit and after full contexts; a rewind or a reset after another prefill failure is accepted but
  wasn't measured (see [Recover a full or failed session](#recover-a-full-or-failed-session)).

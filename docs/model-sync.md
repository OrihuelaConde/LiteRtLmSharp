# Download and update models

`LiteRtModelSync` downloads a `.litertlm` model file from an HTTP server and brings a local copy up to date
with a newer remote file. The calls load no model and need no engine.

A `.litertlm` file starts with a header that lists the file's sections, the parts of the model stored one after
another, and the header can record a SHA-256 hash of each section. The runtime compares the section hashes
that the headers of the local and the remote file record. It copies the sections whose hashes match from the
local file and downloads the others. When 80% or more of the section bytes changed, or when either file has
no section hashes, it downloads the whole file.

Three members do the work:

- `LiteRtModelSyncSource.FromHttp` names the remote file.
- `LiteRtModelSync.CreatePlanAsync` reports what an update would download and reuse, without writing
  anything.
- `LiteRtModelSync.UpdateAsync` writes the remote file to a path, reusing the unchanged sections of a local
  file.

## Files without section hashes

Differential updates need section hashes, and none of the published files that were checked has them. On
November 25, 2026, the headers of these four `.litertlm` files on Hugging Face recorded no section hashes:

- `gemma-4-E2B-it.litertlm` and `gemma-4-E2B-it-gpu.litertlm` in `litert-community/gemma-4-E2B-it-litert-lm`
- `gemma-4-E4B-it.litertlm` in `litert-community/gemma-4-E4B-it-litert-lm`
- `embeddinggemma-2-text-270m.litertlm` in `litert-community/embeddinggemma-2-text-270m-litert-lm`

With test files that have no section hashes, the runtime reused nothing: every update downloaded the whole
file, and `CreatePlanAsync` reported a local file as not up to date even when it was identical to the remote
file. Expect the same with the files above. For `gemma-4-E2B-it.litertlm` the whole file is 2,588,147,712
bytes.

A section is reused only when both headers record its SHA-256 hash:

- **The remote file.** For differential updates to work, the publisher has to publish `.litertlm` files whose
  header records a SHA-256 hash for each section.
- **The local file.** The runtime takes the hashes of the sections it reuses from the local header, and it
  doesn't compute them for a local file whose header has none. So a local file without hashes reuses
  nothing, even against a remote file that has them. The first update after a publisher adds hashes still
  downloads the whole file; later updates can reuse sections.

Section hashes also decide what the runtime verifies. It checks the SHA-256 hash of each downloaded section
that the remote header records, so the bytes of a remote file without section hashes are not verified. For
such files, the `ETag` check that `FromHttp` makes is the only protection against a file assembled from two
versions; see [Pin the model to a revision](#pin-the-model-to-a-revision).

Two rules follow:

- Check the plan before you update. With an existing local file, `BytesToReuse` equal to 0 means that the
  update downloads the whole file.
- Don't call `UpdateAsync` at every start of your app. With files without section hashes, that downloads the
  whole model every time. [Keep a model up to date at startup](#keep-a-model-up-to-date-at-startup) shows
  how to avoid it.

## Experimental status

`LiteRtModelSync`, `LiteRtModelSyncSource`, `LiteRtModelSyncPlan`, `LiteRtModelSyncOptions`, and
`LiteRtModelUpdateResult` carry `[Experimental("LITERTLM003")]`. These types and their members can change or
be removed in a minor version. They're experimental for three reasons:

- The runtime's model sync functions first appear in C API version 1.1.0, which LiteRT-LM v0.20.0 carries,
  and the binding was tested against that version only.
- The calls were measured only on Windows x64 and, in part, on an Android x86_64 emulator. On Linux and macOS,
  only the repository's CI tests run them (see [Limitations](#limitations)).
- Differential updates depend on files with section hashes, and none of the files that were checked has them.

The C# compiler reports each use of these types as error LITERTLM003, and the error's help link opens this
section. To use them, suppress the diagnostic in the files that call them:

```csharp
#pragma warning disable LITERTLM003
```

Or suppress it for the whole project:

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);LITERTLM003</NoWarn>
</PropertyGroup>
```

The examples in this guide start with the `#pragma` line, so they compile in a project without the `NoWarn`
setting.

Other settings and languages behave as follows. These results come from a test library that marks its types
the same way, built with .NET SDK 10.0.403:

- An `.editorconfig` line `dotnet_diagnostic.LITERTLM003.severity = warning` lowers the C# error to a
  warning.
- Visual Basic reports warning LITERTLM003, with the help link.
- F# reports its generic warning FS0057, without the ID or the link. `--nowarn:57` in `<OtherFlags>`,
  `<NoWarn>57</NoWarn>`, or `#nowarn "57"` in a file removes it. Suppressing FS0057 also hides the warnings of
  any other experimental API that the project uses.

## Requirements

- LiteRtLmSharp 1.4.0 or later. The native packages of 1.4.0 carry LiteRT-LM v0.20.0.
- An HTTP or HTTPS server that serves the file with byte ranges. On November 25, 2026, the `resolve`
  addresses of the files listed in [Files without section hashes](#files-without-section-hashes) redirected
  to a CDN that answered range requests with `206 Partial Content`. For other servers, see
  [Servers without range support](#servers-without-range-support).
- Free space for a full copy of the remote file next to the output file, in addition to the old file. See
  [Limitations](#limitations).

## Download a model

To download a model the first time, pass `null` as the local path. The following example downloads Gemma 4
E2B into the app's data folder and prints the running total of bytes received:

```csharp
#pragma warning disable LITERTLM003

using LiteRtLmSharp;

var uri = new Uri(
    "https://huggingface.co/litert-community/gemma-4-E2B-it-litert-lm/resolve/REVISION/gemma-4-E2B-it.litertlm");
string folder = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyApp");
Directory.CreateDirectory(folder);
string modelPath = Path.Combine(folder, "gemma-4-E2B-it.litertlm");

var options = new LiteRtModelSyncOptions
{
    Progress = new Progress<long>(received => Console.WriteLine($"{received / (1024 * 1024)} MiB")),
};

LiteRtModelUpdateResult result = await LiteRtModelSync.UpdateAsync(
    localModelPath: null, modelPath, LiteRtModelSyncSource.FromHttp(uri), options);
Console.WriteLine($"Received {result.BytesReceived:N0} bytes.");
```

Replace `REVISION` with the commit of the repository that you want, or with `main`. Every example in this
guide uses this placeholder. A commit is safer; see
[Pin the model to a revision](#pin-the-model-to-a-revision). The example in
[Keep a model up to date at startup](#keep-a-model-up-to-date-at-startup) needs a commit.

The output's folder must exist: the runtime doesn't create it, and the call throws `ArgumentException` when
it's missing. The runtime writes the new file to `OUTPUT.tmp_updating` next to the output, where `OUTPUT` is
the output path, and renames it over the output at the end.

The progress receiver gets the running total of bytes received from the server, at most once per MiB and at
the end of each range the runtime asks for. `Report` runs on the thread that runs the native update, so the
update waits while it runs, and an `IProgress<long>` whose `Report` blocks stalls the download.
`Progress<T>` posts each report to the synchronization context it was created on and returns at once. A
console app has no such context, so the reports go to the thread pool and can print out of order. A receiver
that throws fails the update with `LiteRtStatusCode.Unknown`.

`BytesReceived` counts the bytes that the call received from the server and passed to the runtime, header
fetches included. On a first download it's a little more than the file size: the runtime reads the header
with a fetch of the first 16 KiB (16,384 bytes) of the file, and then fetches the file from the end of the
header. For a 129,024-byte test file with a 1,184-byte header, a first download received 144,224 bytes. To
show a percentage, divide by `BytesToDownload` from a plan (see
[Check for an update before downloading](#check-for-an-update-before-downloading)), not by `TotalRemoteSize`,
which also counts the sections that an update copies from the local file. Cap the value at 100%:
`BytesReceived` counts the whole 16 KiB fetch of the header, and `BytesToDownload` counts only the header's own
bytes, so the total can pass `BytesToDownload`. In an update that downloads little, the header fetch alone can
pass it. When the plan reports `BytesToDownload` as 0, the local file is up to date: show no percentage.

## Pin the model to a revision

Prefer an address that names one revision of the file, such as `resolve/COMMIT/FILE`, over one that names a
branch such as `main`. A branch can move to a new version during a call.

`FromHttp` remembers the first strong `ETag` of a call, read from the final response after any redirect, and
fails the call with `LiteRtStatusCode.Aborted` when a later response of the same call carries another one. A
weak or missing `ETag` turns that check off, and for a remote file without section hashes the check is the
only protection against a file assembled from two versions. On November 25, 2026, the `ETag` that a client
saw after the Hugging Face redirect was strong and the same across requests, for the two files checked
(`gemma-4-E2B-it.litertlm` and `embeddinggemma-2-text-270m.litertlm`).

Each call checks only its own requests. If the remote file changes between a plan and an update, the update
downloads the new version, and the plan's numbers no longer apply.

## Check for an update before downloading

`CreatePlanAsync` fetches the remote header, reads the local file, and reports what an update would do. It
writes nothing. Use it to tell the user what an update costs before it starts, for example on a metered
connection:

```csharp
#pragma warning disable LITERTLM003

using LiteRtLmSharp;

var uri = new Uri(
    "https://huggingface.co/litert-community/gemma-4-E2B-it-litert-lm/resolve/REVISION/gemma-4-E2B-it.litertlm");
string folder = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyApp");
Directory.CreateDirectory(folder);
string modelPath = Path.Combine(folder, "gemma-4-E2B-it.litertlm");
LiteRtModelSyncSource remote = LiteRtModelSyncSource.FromHttp(uri);

string? local = File.Exists(modelPath) ? modelPath : null;
LiteRtModelSyncPlan plan = await LiteRtModelSync.CreatePlanAsync(local, remote);

Console.WriteLine($"Up to date: {plan.IsUpToDate}");
Console.WriteLine($"To download: {plan.BytesToDownload:N0} of {plan.TotalRemoteSize:N0} bytes");
Console.WriteLine($"To reuse: {plan.BytesToReuse:N0} bytes");
Console.WriteLine(plan);

if (!plan.IsUpToDate)
{
    Console.Write("Download the update? [y/N] ");
    if (string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
        await LiteRtModelSync.UpdateAsync(modelPath, modelPath, remote);
}
```

Pass `null` when there is no local file. A local path that doesn't exist throws `ArgumentException`, because
the runtime would ignore it and plan a full download. `UpdateAsync` accepts the same path twice when the file
doesn't exist yet, and then downloads the whole file.

The plan reports these values:

| Member | What it means |
|---|---|
| `IsUpToDate` | `true` when the local file matches the remote file, section by section. It's `false` when there is no local file, when either file has no section hashes, and when a local section's bytes don't match the hash its header records. |
| `BytesToDownload` | The bytes an update would download, the remote header included. It's 0 when the local file is up to date. It's the size of the whole file when there is no local file, when either file has no section hashes, or when 80% or more of the section bytes changed. |
| `BytesToReuse` | The section bytes an update would copy from the local file. 0 with an existing local file means that the remote file has no section hashes, the local file has none, or at least 80% of the section bytes changed. The plan doesn't tell these cases apart. |
| `TotalRemoteSize` | The size of the remote file in bytes. |
| `ToString()` | The runtime's summary of the plan, for logs. Its format is not stable. |

For example, for a 129,024-byte test file with a 1,184-byte header in which one 8,192-byte section changed,
the plan reported 9,376 bytes to download (the section and the header) and 72,704 bytes to reuse.

`BytesToDownload` is an estimate:

- An update also downloads a section whose local bytes don't match their hash, even when the plan counted the
  section as reused.
- The bytes an update receives include the header fetches, so they differ from it; see `BytesReceived` in
  [Download a model](#download-a-model).
- `BytesToDownload` plus `BytesToReuse` can be less than `TotalRemoteSize`: the alignment padding between
  sections is neither downloaded nor counted as reused.

A plan fetches the first 16 KiB of the remote file, and the rest of the header when the header is longer. What
it costs after that depends on the files:

- **Both files have section hashes and their headers match.** The verdict depends on the bytes of the local
  sections, and the runtime works on the local file with no further fetch. For a 1 GiB test file that the
  operating system had cached, that took about 3.3 seconds on a desktop computer. An update of a local file
  that is already up to date takes the same time.
- **Either file has no section hashes.** A plan of a 1 GiB test file ended about 1 millisecond after the
  header fetch.

## Keep a model up to date at startup

An app that calls `UpdateAsync` at every start downloads the whole model every time when the files have no
section hashes. A plan can't tell a new version from the same file published without hashes either: both
report `IsUpToDate` as `false` and `BytesToReuse` as 0.

The following example pins the model to a revision and records the revision it downloaded in a file next to
the model. It goes to the network only when the model file is missing, when the revision file is missing, or
when the app asks for another revision, so a new version of your app decides when the model changes. When the
files have section hashes, the update reuses the sections that didn't change. When the server can't be
reached, the app keeps the file it has.

Set `Revision` to a commit. With a branch such as `main`, the revision file matches `Revision` after the first
download, so the example never checks the branch again.

```csharp
#pragma warning disable LITERTLM003

using LiteRtLmSharp;

const string Revision = "REVISION";
var uri = new Uri(
    $"https://huggingface.co/litert-community/gemma-4-E2B-it-litert-lm/resolve/{Revision}/gemma-4-E2B-it.litertlm");
string folder = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyApp");
Directory.CreateDirectory(folder);
string modelPath = Path.Combine(folder, "gemma-4-E2B-it.litertlm");
string revisionPath = modelPath + ".revision";

bool haveModel = File.Exists(modelPath);
string? haveRevision = File.Exists(revisionPath) ? File.ReadAllText(revisionPath) : null;

if (!haveModel || haveRevision != Revision)
{
    try
    {
        LiteRtModelSyncSource remote = LiteRtModelSyncSource.FromHttp(uri);
        LiteRtModelSyncPlan plan = await LiteRtModelSync.CreatePlanAsync(haveModel ? modelPath : null, remote);
        if (!plan.IsUpToDate)
        {
            Console.WriteLine($"Downloading {plan.BytesToDownload:N0} of {plan.TotalRemoteSize:N0} bytes.");
            await LiteRtModelSync.UpdateAsync(modelPath, modelPath, remote);
        }
        File.WriteAllText(revisionPath, Revision);
    }
    catch (LiteRtException e) when (haveModel
        && e.StatusCode is LiteRtStatusCode.Unavailable or LiteRtStatusCode.DeadlineExceeded)
    {
        // Offline: keep the file that is there. An update that fails during a fetch doesn't replace it.
    }
}

Console.WriteLine($"Model: {modelPath}");
```

The plan decides what to do:

- **No local file:** the update downloads the whole file.
- **Up to date:** nothing to do. This needs section hashes on both files.
- **Not up to date, `BytesToReuse` greater than 0:** the update downloads the changed sections and copies the
  others.
- **Not up to date, local file present, `BytesToReuse` equal to 0:** the update downloads the whole file. In
  this example that happens when the revision changed, even when that commit left the model file unchanged,
  so change `Revision` only to a commit that changed the model file. To follow a branch such as `main`
  instead, make a plan at every start in place of the revision check. The plan can't tell a new version from
  the same file without hashes, so when it reports `IsUpToDate` as `false` and `BytesToReuse` as 0, ask the
  user before you download `BytesToDownload` bytes.

A model file without the revision file is downloaded again in full when it has no section hashes. That
happens with a file downloaded before you added this code, for example with the code in
[Download a model](#download-a-model), and when the process ends between the update and the line that writes
the revision file. A first launch without a network fails, because there is no file to fall back on.

The example updates the file before the process loads it. For a process that loaded the file before it was
replaced, see
[Delete the compiled cache after you replace a model](#delete-the-compiled-cache-after-you-replace-a-model).

## Update a model that an engine is using

`UpdateAsync` refuses an output that a live `LiteRtEngine` or `LiteRtEmbeddingEngine` of the same process
loaded, and throws `InvalidOperationException` before it fetches anything. Without that check, on Windows the
runtime downloads what the update needs, writes the temporary file, and then fails to replace the file with
`LiteRtStatusCode.Internal`, while the engine keeps working. This was measured with a chat engine on CPU and
an embedding engine on CPU and GPU, on files without section hashes, so each of those updates downloaded the
whole file before it failed. An engine stays alive until it and every conversation and session created from
it are disposed.

To update a model while the app uses it, write the new version to a second path, dispose the engine, replace
the file, delete the compiled cache files of the old version, and load the engine again. The following
example does this with an embedding engine:

```csharp
#pragma warning disable LITERTLM003

using LiteRtLmSharp;

var uri = new Uri(
    "https://huggingface.co/litert-community/embeddinggemma-2-text-270m-litert-lm/resolve/REVISION/embeddinggemma-2-text-270m.litertlm");
string folder = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyApp");
string modelPath = Path.Combine(folder, "embeddinggemma-2-text-270m.litertlm");
string nextPath = Path.Combine(folder, "embeddinggemma-2-text-270m.next.litertlm");

var options = new LiteRtEmbeddingEngineOptions { ModelPath = modelPath };
LiteRtEmbeddingEngine engine = LiteRtEmbeddingEngine.Load(options);

// Write the new version to another path while the engine runs.
await LiteRtModelSync.UpdateAsync(modelPath, nextPath, LiteRtModelSyncSource.FromHttp(uri));

// Dispose the engine before you replace the file.
engine.Dispose();
File.Move(nextPath, modelPath, overwrite: true);

// This process loaded the old version, so delete its compiled cache files before you load the new one.
string modelName = Path.GetFileName(modelPath);
foreach (string file in Directory.GetFiles(folder))
{
    string name = Path.GetFileName(file);
    if (name.StartsWith(modelName, StringComparison.Ordinal)
        && (name.EndsWith("xnnpack_cache", StringComparison.Ordinal)
            || name.EndsWith("_cache.bin", StringComparison.Ordinal)))
        File.Delete(file);
}

engine = LiteRtEmbeddingEngine.Load(options);
engine.Dispose();
```

On Windows, the update to the second path read the model file while a live embedding engine held it, on CPU
and on GPU. With a `LiteRtEngine`, take the same steps, and dispose its conversations and sessions with it.
[Limitations](#limitations) says what was measured with each kind of engine.

Keep these points in mind:

- The check covers engines of this process that loaded the file under the same path. It runs when the update
  starts. An engine that loads the output while the update runs isn't detected, so don't load the output
  until the update completes. Engines that loaded the file under another name, such as a hard link or a short
  8.3 name, aren't detected either.
- `LiteRtModelInfo.Read` isn't checked. On Windows, a `Read` of the output that runs when the runtime replaces
  the file makes the update fail with `Internal` after the download, and the `Read` itself can fail with
  `Internal`. Don't read the output during an update.

### Delete the compiled cache after you replace a model

With the default `LiteRtCache.Default`, an engine writes compiled cache files next to the model file. When a
process loaded a model and the file is then replaced, by an in-place update or by moving an update's output
over it, an engine that the same process loads from that path afterwards can use the old cache and compute
with the old model. On Windows, an embedding engine on CPU and on GPU returned the old model's embeddings
after both kinds of replacement.

Before such a process loads the file again, do one of the following:

- Delete the cache files next to the model. Their names start with the model file's name and end with
  `xnnpack_cache` or `_cache.bin`, as the example above does.
- Load the engine with `Cache = LiteRtCache.Disabled`.
- Load the engine with `Cache = LiteRtCache.Directory(PATH)`, where `PATH` is an existing folder that holds no
  cache of the file. This was measured after a move over the file, not after an in-place update.

In a process that hadn't loaded the file before an in-place update, such as an app that updates the model at
startup and then loads it, the embedding engine built a new cache and computed with the new model. On GPU, the old cache files
stayed next to the model; delete them to recover their disk space.

## Cancel a download or an update

Pass a `CancellationToken` to stop a plan or an update. The following example cancels when the user presses
`Ctrl+C`:

```csharp
#pragma warning disable LITERTLM003

using LiteRtLmSharp;

var uri = new Uri(
    "https://huggingface.co/litert-community/gemma-4-E2B-it-litert-lm/resolve/REVISION/gemma-4-E2B-it.litertlm");
string folder = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyApp");
Directory.CreateDirectory(folder);
string modelPath = Path.Combine(folder, "gemma-4-E2B-it.litertlm");

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

try
{
    LiteRtModelUpdateResult result = await LiteRtModelSync.UpdateAsync(
        modelPath, modelPath, LiteRtModelSyncSource.FromHttp(uri), cancellationToken: cancel.Token);
    Console.WriteLine($"Done: {result.BytesReceived:N0} bytes received.");
}
catch (OperationCanceledException)
{
    Console.WriteLine("Cancelled.");
}
```

What a cancel interrupts:

- **Before the call:** after the checks of the arguments, the paths, the engines, and the calls in flight, a
  token that is already cancelled throws `OperationCanceledException`, and the runtime isn't called. A call
  with a cancelled token that fails one of those checks throws the exception of that check.
- **During a fetch:** the cancel interrupts a fetch that waits for the server or copies its bytes.
- **Between fetches:** the runtime's own work, such as reading the local file, copying reused sections, or
  renaming the temporary file, has no callback. A cancel during that work takes effect at the next fetch, or
  not at all when no fetch follows. For files with section hashes whose headers match, that work can take
  seconds; see the costs at the end of
  [Check for an update before downloading](#check-for-an-update-before-downloading).

The task completes only when the native call has returned. When the call fails while the token is cancelled,
whatever the cause of the failure, it throws `OperationCanceledException`, and the `LiteRtException` of the
failure is its `InnerException`. When the runtime completes the update before it sees the cancel, the call
returns the result instead of throwing, because the file was replaced.

## Use a gated or private model

`FromHttp` sends its requests through the `HttpClient` that you pass, with that client's handler, redirect,
proxy, and header settings. Without one, it uses a client that the binding shares between sources, with no
overall timeout. The source never disposes a client that you pass, so keep it alive while a call uses the
source.

The following example sends an access token in the `Authorization` header:

```csharp
#pragma warning disable LITERTLM003

using System.Net.Http.Headers;
using LiteRtLmSharp;

string token = Environment.GetEnvironmentVariable("HF_TOKEN")
    ?? throw new InvalidOperationException("Set HF_TOKEN to an access token.");
var uri = new Uri("https://huggingface.co/OWNER/REPOSITORY/resolve/REVISION/FILE_NAME.litertlm");
string folder = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyApp");
Directory.CreateDirectory(folder);
string modelPath = Path.Combine(folder, "FILE_NAME.litertlm");

using var client = new HttpClient();
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

await LiteRtModelSync.UpdateAsync(modelPath, modelPath, LiteRtModelSyncSource.FromHttp(uri, client));
```

Replace the following:

- `OWNER/REPOSITORY`: the repository that holds the model.
- `FILE_NAME`: the name of the `.litertlm` file.

How `SocketsHttpHandler` treats headers on a redirect, measured on .NET 10.0.15 against a server on the
loopback address:

- An `Authorization` header set on `DefaultRequestHeaders` is not sent again after a redirect, to the same
  host or to another one. The address that a redirect leads to doesn't receive it.
- Other request headers, such as an API key header or a `Cookie` header set by hand, are sent again to the
  address a redirect leads to. Send credentials in `Authorization`, or use a handler that removes them on a
  redirect.

A download from a real gated repository was not measured, and neither were other handlers, such as the
Android one.

The source fails a fetch with `LiteRtStatusCode.DeadlineExceeded` when the server sends no response headers,
or no body bytes, for 60 seconds. The client's own `HttpClient.Timeout` applies too. With
`SocketsHttpHandler` it bounds the wait for the response headers and not the copy of the body, so it doesn't
cut a long download.

## Handle errors

A failed call throws `LiteRtException`. Its `StatusCode` says why, its message carries the runtime's reason
and, for some codes, a hint, and its `InnerException` holds the source's exception when a fetch failed with
one, such as the `HttpRequestException` of an HTTP error. The runtime doesn't retry a failed fetch. When an
update fails or is cancelled during a fetch, the runtime deletes the temporary file and leaves the old file in
place.

```csharp
#pragma warning disable LITERTLM003

using LiteRtLmSharp;

var uri = new Uri(
    "https://huggingface.co/litert-community/gemma-4-E2B-it-litert-lm/resolve/REVISION/gemma-4-E2B-it.litertlm");
string folder = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyApp");
Directory.CreateDirectory(folder);
string modelPath = Path.Combine(folder, "gemma-4-E2B-it.litertlm");

try
{
    await LiteRtModelSync.UpdateAsync(modelPath, modelPath, LiteRtModelSyncSource.FromHttp(uri));
}
catch (LiteRtException e) when (e.StatusCode is LiteRtStatusCode.Unavailable or LiteRtStatusCode.DeadlineExceeded)
{
    Console.WriteLine("The server can't be reached. Try again later.");
}
catch (LiteRtException e) when ((e.StatusCode is LiteRtStatusCode.Unauthenticated or LiteRtStatusCode.PermissionDenied)
    && e.InnerException is HttpRequestException)
{
    Console.WriteLine("The server refused the request. Check the access token and the repository's access.");
}
catch (LiteRtException e)
{
    Console.WriteLine($"The update failed ({e.StatusCode}): {e.Message}");
}
```

| `StatusCode` | Cause | What the app can do |
|---|---|---|
| `Unavailable` | The request got no response, for example because of a DNS or connection failure; the server answered 408, 429, or a 5xx status; or a response body ended early. | Keep the file you have and try later. |
| `DeadlineExceeded` | The server sent no response headers, or no body bytes, for 60 seconds, or the client's `HttpClient.Timeout` expired. | Keep the file you have and try later. |
| `NotFound` | The server answered 404 or 410. | Check the address and the revision. |
| `Unauthenticated` | The server answered 401. | Send a valid access token; see [Use a gated or private model](#use-a-gated-or-private-model). |
| `PermissionDenied` | With an `HttpRequestException` as `InnerException`: the server answered 403. Without one: the runtime couldn't create or open a file, such as the temporary file next to the output, whatever the cause. | For a 403, check that the account can access the file. Otherwise, check that the output's folder is writable. |
| `Aborted` | The remote file changed during the call: a later response carried another strong `ETag`. The output isn't replaced. | Run the update again, and use an address pinned to a revision. |
| `DataLoss` | The server answered with other bytes than the ones requested; the file on the server ended before a range that the runtime asked for, as happens when the file is shorter than its header declares (see [Servers without range support](#servers-without-range-support)); a downloaded section doesn't match the hash in the remote header; or the server returned a different number of bytes than the remote header declares. The runtime also returns `DataLoss` when it can't write the temporary file next to the output, which is not a fault of the server (not measured). | Run the update again. If it fails again, the remote file is damaged or changed during the call, or the runtime can't write the temporary file: check the output's volume, for example its free space. |
| `InvalidArgument` | The remote file isn't a `.litertlm` file. | Check that the address points to the `.litertlm` file. |
| `Unimplemented` | The server doesn't serve byte ranges, and the remote header is longer than 16 KiB. | Use a server that serves byte ranges; see [Servers without range support](#servers-without-range-support). |
| `Internal` | The runtime couldn't replace or write the output. When the reason says "Failed to rename temporary model", the runtime downloaded what the update needs and wrote the temporary file, and the output or its temporary file `OUTPUT.tmp_updating` was open: for example by a `LiteRtModelInfo.Read` of the output, or by a second update of the same output in another process (measured on Windows). For an up-to-date local file, another cause is an output that is another name of the local file, such as a hard link or a short 8.3 name (not measured; another spelling of the path failed this way). | Close what holds the file and update again, or write the update to another path and replace the file later. For another name of the local file, pass the same path for both arguments. |
| `ResourceExhausted` | The runtime's free-space check found less free space on the output's volume than the remote file's size. The check runs after the header fetch, before anything is written, and it counts the space of an `OUTPUT.tmp_updating` left by an update that didn't finish as used (not measured). | Free disk space on the output's volume. If an `OUTPUT.tmp_updating` is next to the output and no update of that output is running, delete it. |
| `Unknown` | The server answered another status, such as 400 or 416; a progress receiver threw (its exception is the `InnerException`); or another failure. | Read the message and the `InnerException`. |

Match on `StatusCode`, not on the message text. The runtime's reason can contain operating-system text in the
system's language.

The calls also throw these exceptions before any download:

| Exception | When |
|---|---|
| `ArgumentNullException` | `remote` or `outputModelPath` is `null`, or `FromHttp` gets a `null` address. |
| `ArgumentException` | `outputModelPath` is empty or a folder, or its folder doesn't exist. `localModelPath` doesn't exist (unless it's the same path as the output), is a folder, or can't be opened for reading. A path can't be passed to the runtime (see [Windows paths](#windows-paths)). `FromHttp` gets an address that isn't an absolute `http` or `https` address. |
| `InvalidOperationException` | Another model sync call of this process writes the file that the call reads or writes, or reads the file that an update writes. A live engine of this process loaded the output (see [Update a model that an engine is using](#update-a-model-that-an-engine-is-using)). Calls that only read one file run together. |
| `OperationCanceledException` | The token was cancelled before the call, or the call failed while the token was cancelled (see [Cancel a download or an update](#cancel-a-download-or-an-update)). |

`LiteRtException` is also thrown when the native library's C API version doesn't match the binding.

## Servers without range support

A server that ignores the `Range` header answers each request with `200 OK` and the whole file. `FromHttp`
serves a request at byte 0 from the first bytes of that answer, and reports any other request to the runtime
as a remote that can't serve byte ranges. The runtime then downloads the whole file from byte 0:

- A first download makes one refused request before the full one, and an update that reuses sections makes
  two (measured against a server on the loopback address).
- Each refused request is still a `GET` of the whole file. The source stops reading it at once, but the
  server can already have sent part or all of it.
- A plan or an update fails with `LiteRtStatusCode.Unimplemented` when the remote header is longer than
  16 KiB (16,384 bytes), because the runtime doesn't fall back for the fetches of the header. The headers of
  the four published files that were checked measured 880 to 1,872 bytes.

Every request that `FromHttp` sends is a `GET` with a closed `Range: bytes=FIRST-LAST` header. A `206 Partial
Content` answer must carry a `Content-Range` header that starts at the requested byte, or the call fails with
`LiteRtStatusCode.DataLoss`. When a 206 carries fewer bytes than requested and doesn't reach the end of the
file, the source asks for the rest of the range with another `GET`. A server that answers with a part of each
range costs one request per part. When the `Content-Range` of a short 206 doesn't give the file size, such as
`bytes 0-1023/*`, the source also asks for the rest, and a `416 Range Not Satisfiable` answer to that request
means that the file ended there.

Only the first range of a call, the first 16 KiB of the file, can come back shorter than requested, as it does
for a file smaller than 16 KiB. Every later range asks for an exact number of bytes. A later range that comes
back short fails the call with `LiteRtStatusCode.DataLoss`, and the output isn't replaced. That covers a 206
that ends at the end of the file before the end of the range, a 416 after a 206 that doesn't give the file
size, and a 200 whose body is shorter than the range. The check catches a file on the server that is shorter
than its header declares: the runtime doesn't count the bytes of a whole-file download (not measured), and it
verifies only the sections that the header hashes, so without the check the output would be a truncated file.

## Windows paths

The rules in this section apply on Windows. They were measured on Windows x64 with code page 1252.

- **Code page.** Unless the process runs under the UTF-8 code page (65001), the runtime decodes paths in the
  active ANSI code page. The binding refuses a path with characters outside that code page before any native
  call, with an `ArgumentException` that names the code page. On code page 1252, a folder named `José` works,
  and folders with Japanese or emoji names are refused.
- **UTF-8 code page.** With an application manifest that sets the UTF-8 code page, the runtime accepted
  folder names in Latin script, Japanese, and emoji. That was measured on the runtime directly; the binding's
  calls under that code page were not run.

  ```xml
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <activeCodePage xmlns="http://schemas.microsoft.com/SMI/2019/WindowsSettings">UTF-8</activeCodePage>
    </windowsSettings>
  </application>
  ```

- **Error messages.** In an error message, the runtime's reason can show the non-ASCII characters of a path
  wrongly. The path at the start of the message is the one the call used.
- **Long paths.** In a process that isn't long-path aware, the runtime fails on plain paths of 260 characters
  or more, even with long paths enabled in Windows, and paths of 259 characters work. When a path of the
  call, or the temporary file next to the output (the output path plus 13 characters), reaches 260
  characters, the binding passes every path of the call in its extended-length form (`\\?\`).
- **UNC paths.** A UNC path such as `\\server\share\model.litertlm` works. A UNC path in a call that needs
  the extended-length form is refused with `ArgumentException`.
- **Device paths.** A path that starts with `\\?\` or `\\.\` is compared and passed in its plain form when
  that form names the same file. A `\\?\` path that only the extended form can name, such as a folder whose
  name ends in a dot, is passed as it is. The same path with `\\.\`, and a UNC path in extended form whose
  plain form names another file, are refused. A device path that names no drive letter and no UNC share, such
  as a volume GUID path, is refused.
- **The same file under two names.** To update a file in place, pass the same path for both arguments. Paths
  that `Path.GetFullPath` makes equal count as the same file, with or without a `\\?\` prefix, and so do paths
  that differ only in case in a folder that ignores case. Another name of the file, such as a hard link or a
  short 8.3 name, counts as another file.

Non-ASCII file names in an ASCII folder were not measured; the measured non-ASCII names were folder names.

## Limitations

- **Where it was measured.** Unless a section says otherwise, the results in this guide come from LiteRT-LM
  v0.20.0 on Windows x64, with test files served from memory or from an HTTP server on the loopback address.
  The Hugging Face results come from requests to the four files listed in
  [Files without section hashes](#files-without-section-hashes). The native libraries of every packaged
  platform export the model sync functions. The measurements were made on Windows x64, and some of the calls
  also ran on an Android x86_64 emulator (see **Android** in this list). The repository's CI also runs the
  model sync tests on linux-x64, linux-arm64, and osx-arm64, including the tests that need no model and the
  compiled cache tests with an embedding engine on the CPU, and on osx-arm64 also on the GPU. Beyond what those
  tests check, Linux and macOS were not measured.
- **Engines.** The read of a model file that a live engine holds, and the stale compiled cache, were measured
  with an embedding engine only. A chat engine was measured in two cases, both on CPU: the failed replacement,
  and a replacement by another model of another size. In the second case, with LiteRT-LM v0.20.0 and two small
  test models (a Qwen3 0.6B and the LiteRT-LM LoRA test model), a chat engine that the same process loaded from
  that path afterwards computed with the new model. A chat model replaced in the same process by a file of the
  same size was not measured, and on the GPU this case was not measured either, because the LoRA test model
  doesn't load there.
- **Android.** Each of the following gave the expected result in 3 of 3 runs on an Android x86_64 emulator
  (API 35) with LiteRT-LM v0.20.0. The runs used test files of 129,024 bytes and the default `HttpClient` over
  cleartext HTTP, and every update wrote a separate output file:
  - `CreatePlanAsync`, for a file with section hashes and for a file that is up to date. Each plan read the
    header with one range request and gave the same values as on Windows x64.
  - `UpdateAsync` with section hashes, which received the same number of bytes as on Windows x64.
  - `UpdateAsync` without section hashes.
  - `UpdateAsync` against a server that ignores range requests, which fell back to whole-file answers and
    received the same number of bytes as the Windows x64 test, which updates in place.

  In each update, the output matched the remote file, and the local file didn't change.

  Not measured on Android: in-place updates, HTTPS, redirects, cancellation, files of real model sizes, the
  runtime's free-space check in app storage, and arm64 devices.
- **iOS.** The iOS package is not published.
- **Servers.** Apart from the loopback server, only Hugging Face was measured. Other CDNs and object stores
  were not.
- **No resume and no retry.** An update that fails or is cancelled during a fetch deletes its temporary file,
  so the next update starts over. The runtime doesn't retry a failed fetch. When the process ends during an
  update, `OUTPUT.tmp_updating` stays next to the output and the old output is intact. The temporary file
  takes disk space until the next update of that output, which overwrites it. The runtime's free-space check
  runs before that overwrite and counts the file's space as used (not measured), so on a volume with little
  room the next updates can fail with `ResourceExhausted` until you delete `OUTPUT.tmp_updating`. The binding
  doesn't delete it; delete it only while no update of that output is running.
- **Free space.** The temporary file is a full copy of the remote file, and it sits next to the old file until
  the rename, so the output's volume needs room for both. The binding turns on the runtime's free-space check,
  which fails the call with `ResourceExhausted` before it writes anything. A volume that fills up during the
  download was not measured.
- **Other processes.** The calls check other model sync calls and engines of the same process only. The
  binding doesn't check, or lock against, calls or engines of other processes. Two processes that update the
  same output at the same time write the same temporary file: one of them fails at the end with `Internal`,
  and the other one succeeds. That was measured with both processes updating from the same remote file; two
  different remote files were not measured.
- **Timing.** The times in this guide come from a Windows 11 computer with an Intel Core i7-12700F and an
  NVIDIA RTX 3080, with LiteRT-LM v0.20.0 and the file in the operating system's cache. A file that isn't
  cached, a file of several GB, mobile devices, and the time an update of a file without section hashes spends
  on the local file were not timed.
- **Cache manager.** The runtime's cache manager, which keeps models in a folder by ID and falls back to the
  cached file when offline, is not part of the binding. Catch `Unavailable` and keep the existing file
  instead, as in [Keep a model up to date at startup](#keep-a-model-up-to-date-at-startup).

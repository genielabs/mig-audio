# MIG.Audio

Local speech recognition and synthesis interfaces for [MIG](https://github.com/genielabs/mig-service-dotnet), intended for HomeGenie and other MIG clients.

Requires .NET 10. Recognition uses Whisper.net; synthesis uses Piper VITS via sherpa-onnx. Models run on the host; microphone recording and audio playback belong to the client.

## HTTP API

All examples use HTTP POST. Path parameters are positional; omitted values or the literal `default` use the interface configuration. Overrides apply only to the current request.

| Domain / command | Parameters after command | POST body | Result |
| --- | --- | --- | --- |
| `Audio.VoiceRecognizer/{address}/Voice.Transcribe` | `language/model` | WAV or raw PCM | MIG `ResponseText` (`ResponseValue`) |
| `Audio.VoiceSynthesizer/{address}/Voice.Synthesize` | `speed/voice/speaker` | UTF-8 text | Binary WAV |

`Voice.Speak` is an alias of `Voice.Synthesize`, with the same positional parameters and POST text body. The former GET URL containing the text as the first option is no longer supported.

Examples:

```sh
curl --data-binary @recording.wav -H 'Content-Type: application/octet-stream' \
  http://localhost:8088/api/Audio.VoiceRecognizer/chat-1/Voice.Transcribe/it/base

curl --data-binary 'Ciao, questa è una prova.' -H 'Content-Type: text/plain; charset=utf-8' \
  http://localhost:8088/api/Audio.VoiceSynthesizer/chat-1/Voice.Synthesize/1.15/it_IT-paola-medium/0 \
  --output response.wav
```

Recognition input must be PCM mono, 16 kHz, 16-bit signed samples, little endian, either raw or inside a WAV container. Compressed browser recording formats are not accepted. Model identifiers can be `base` or `ggml-base.bin`; arbitrary filesystem paths are rejected. Synthesis speed must be finite and positive, using a decimal point. The speaker must exist in the selected voice.

MIG errors return a JSON `ResponseStatus`, potentially with HTTP 200 according to gateway behavior. Clients requesting a WAV must distinguish JSON errors from binary success. The current MIG gateway does not explicitly set an audio MIME type for an interface's binary result; browser clients can create an `audio/wav` Blob.

## Channels and events

`0` is the default module. Each new address creates an interface module before publishing request events, then raises `InterfaceModulesChanged`. Reuse addresses for chat channels. Modules remain for the lifetime of the interface; there is currently no release command or disk persistence of the channel registry.

Engines are shared across addresses. Requests are serialized per interface, including model selection and event emission; recognition and synthesis use separate engines. The currently loaded model/voice is reused until a different one is requested. Defaults are never changed by an override.

Recognition publishes `Voice.Duration`, `Voice.Language`, `Voice.Model`, `Voice.Text`, then `Voice.Recognized` (inference seconds). Synthesis publishes `Voice.Text`, `Voice.Duration`, `Voice.Speed`, `Voice.Voice`, then `Voice.Synthesized` (generation seconds). Events are emitted synchronously in this order by MIG.Audio. Downstream consumers can schedule their own asynchronous processing.

Engine readiness/errors use module `0`; request results use the requested address. Duration and timing events use invariant decimal formatting. No request correlation ID, streaming inference or cancellation command is implemented yet.

## Interface configuration

Recognizer defaults: `ModelName=ggml-base.bin`, `Language=auto`, `Threads=6`, `Acceleration=cpu`.

Synthesizer defaults: `Voice=it_IT-paola-medium`, `Speed=1.0`, `Threads=2`.

`Voice.SetLanguage` and `Voice.SetSpeed` change defaults explicitly, accepting a UTF-8 POST body or their first path option. Disabled interfaces reject requests. Whisper runtime selection is process-wide: changing CPU/GPU mode after initialization requires a process restart. CPU is the tested baseline; GPU runtime variants need separate validation on suitable hardware.

## Models

Recognition models reside under `<AppBase>/data/models/asr/`. Missing tiny, base, small and medium models (including `.en`) are downloaded automatically. Downloads are written to a temporary file before replacing the final file. Other model names require an installed file.

Piper voices must be installed under `<AppBase>/data/models/tts/vits-piper-<voice>/` or `<AppBase>/data/models/tts/<voice>/`. Each voice requires `<voice>.onnx`, `tokens.txt`, and the voice's `espeak-ng-data` when applicable. Use sherpa-onnx-compatible Piper bundles. Missing voices are downloaded automatically from the official sherpa-onnx `tts-models` release, extracted into a staging directory, and installed only after validating the model, tokens and espeak data. Existing compatible installations are reused.

## Build and testing

```sh
dotnet build MIG.Audio.sln -c Release
dotnet test MIG.Audio.Tests/MIG.Audio.Tests.csproj -c Release
dotnet run --project Test.WebService -c Release -- --port=18088 --self-test
dotnet run --project Test.WebService -c Release -- --port=18088 --native-test
```

`--self-test` checks HTTP validation without loading models. `--native-test` additionally requires installed `ggml-base.bin` and `it_IT-paola-medium`, synthesizes audio, resamples it to 16 kHz, transcribes it, and checks separate channels, event order and concurrent synthesis. Generated WAVs are saved beside the test host binaries.

The host loads engines on demand and listens on localhost by default. Use `--host=<hostname>` to choose a different binding; Windows HttpListener may require an appropriate URL reservation. No system permissions are changed by the host. Stop with Ctrl+C.

## Publishing

GitHub Actions (`.github/workflows/publish.yml`) builds, runs unit and HTTP tests, and packs on pushes to main and version tags. Only tags of the form `v<version>` publish to NuGet.org and create a GitHub release. For example, `v1.0.0-preview.3` publishes a preview and marks the GitHub release as a prerelease. Untagged builds produce downloadable artifacts only.

NuGet publishing uses `NuGet/login@v1` and GitHub OIDC, without a stored API key. Configure a NuGet trusted publishing policy for repository owner `genielabs`, repository `mig-audio`, workflow file `publish.yml` (file name only), no environment, push-new-packages-and-versions scope, and package pattern `MIG.Audio`. The workflow uses NuGet profile `G-Labs`. GitHub releases use the workflow's temporary `GITHUB_TOKEN`. Re-running a tag build skips package versions already uploaded and retries symbol/release uploads.

AppVeyor remains an independent build, test and packaging check; its deployments are disabled. No long-lived publishing credentials are needed in either CI configuration.

Native runtime assets must be verified in restore/publish output for each supported RID. CUDA/Vulkan packaging is not validated by the default CPU build. Package license metadata follows the repository Apache-2.0 LICENSE and source headers.

## References

- [HomeGenie](https://github.com/genielabs/HomeGenie)
- [Whisper.net](https://github.com/nikstra/whisper.net)
- [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx)

## License

Apache-2.0. See LICENSE. Models and dependencies retain their own licenses.


### Model service events

Each audio interface exposes a `service` module, separate from chat addresses. Model preparation emits `Download.Model`, `Download.State` (`Idle`, `Downloading`, `Extracting`, `Completed`, `Error`), `Download.Progress` (0–100, or -1 for unknown length), `Download.Bytes`, `Download.TotalBytes` and `Download.Error`. `Voice.Status` on this module reports `Loading`, `Ready` or `Error`; download completion does not imply engine readiness. Downloads are automatic when the interface connects or a requested model is first used. Failed downloads never replace an installed model.

### Model download controls

Enabling an interface starts model preparation in the background and returns immediately. Disabling cancels preparation and retains partial downloads. Service commands use `/api/<domain>/service/`:

- `Models.List`: known model identifiers (Whisper models or Piper voices from the official sherpa-onnx catalog).
- `Model.Status`: selected model, full file path, installed flag, file and partial download sizes, readiness and last service events.
- `Download.Stop`: pause preparation, retaining downloaded data.
- `Download.Resume`: resume preparation, using HTTP Range and If-Range validators where supported; otherwise restart the transfer safely.
- `Download.Restart`: download again from the beginning, replacing the installed model only after the new download has completed and been validated.
- `Model.Load`: prepare the configured model after an option change.

Download states also include `Stopping` and `Paused`. Cancelling extraction preserves the completed archive so it can be extracted again. Paused downloads do not resume until the interface is enabled again or Resume is requested. All controls and status queries remain responsive during preparation.

### Native ONNX runtime coexistence

On glibc Linux, MIG.Audio loads Sherpa and its bundled ONNX runtime in an isolated loader namespace. The package copies these libraries to `runtimes/mig-audio/<rid>/native`, separate from the host application's ONNX runtime. Keep that directory when deploying or publishing. A native load failure is reported before creating the Sherpa TTS object, avoiding the upstream finalizer crash on missing libraries. Linux requires glibc (`dlmopen`); musl is not supported by this loader.

Native lifecycle regression test (Linux with HomeGenie's ONNX version):

```sh
OMP_WAIT_POLICY=PASSIVE dotnet run --project Test.WebService -c Release -p:TestHostOnnxConflict=true -- \
  --native-lifecycle-test \
  --onnx-runtime="$PWD/Test.WebService/bin/Release/net10.0/runtimes/host-onnx/linux-x64/native/libonnxruntime.so"
```

This preloads ONNX 1.23.2 in the host namespace, then performs five enable/synthesize/disable cycles on separate threads. The isolated glibc locale tables are initialized on each calling thread before creating, using or disposing Sherpa objects (glibc bug 32483).

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

Piper voices must be installed under `<AppBase>/data/models/tts/vits-piper-<voice>/` or `<AppBase>/data/models/tts/<voice>/`. Each voice requires `<voice>.onnx`, `tokens.txt`, and the voice's `espeak-ng-data` when applicable. Use sherpa-onnx-compatible Piper bundles. TTS does not download models automatically.

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

AppVeyor builds and tests before packing. Release tags use `v<version>` or `<version>`; development packages have a preview version. Configure the repository-specific GitHub and NuGet credentials before enabling deployment. Credentials in appveyor.yml are encrypted with the AppVeyor account that runs the project. Untagged builds produce artifacts only; tags on main publish the NuGet package and symbols to NuGet.org, then create a GitHub release. Tags containing a prerelease suffix (for example v1.0.0-preview.1) are marked as GitHub prereleases.

Native runtime assets must be verified in restore/publish output for each supported RID. CUDA/Vulkan packaging is not validated by the default CPU build. Package license metadata follows the repository Apache-2.0 LICENSE and source headers.

## References

- [HomeGenie](https://github.com/genielabs/HomeGenie)
- [Whisper.net](https://github.com/nikstra/whisper.net)
- [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx)

## License

Apache-2.0. See LICENSE. Models and dependencies retain their own licenses.


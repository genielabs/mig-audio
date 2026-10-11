/*
  This file is part of MIG (https://github.com/genielabs/mig-service-dotnet)

  Copyright (2012-2026) G-Labs (https://github.com/genielabs)

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

  Unless required by applicable law or agreed to in writing, software
  distributed under the License is distributed on an "AS IS" BASIS,
  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
  See the License for the specific language governing permissions and
  limitations under the License.
*/

namespace MIG.Interfaces.Audio;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using MIG;
using MIG.Config;
using SherpaOnnx;

/// <summary>
/// MIG Interface for neural Text-to-Speech synthesis powered by Piper VITS via sherpa-onnx.
/// </summary>
public class VoiceSynthesizer : MigInterface
{
    private OfflineTts? _tts;
    private List<Option> _options = CreateDefaultOptions();

    private static List<Option> CreateDefaultOptions() => new()
    {
        new Option("Voice", "it_IT-paola-medium"),
        new Option("Speed", "1.0"),
        new Option("Threads", "2")
    };
    private readonly List<InterfaceModule> _modules = new();
    private readonly object _syncLock = new();
    private readonly object _moduleLock = new();

    #region MigInterface Properties & Events

    public string Domain
    {
        get
        {
            string ifacedomain = GetType().Namespace ?? string.Empty;
            ifacedomain = ifacedomain.Substring(ifacedomain.LastIndexOf(".") + 1) + "." + GetType().Name;
            return ifacedomain;
        }
    }

    private volatile bool _isEnabled = true;
    private readonly AudioModelJob _models;
    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; if (!value) _models.Stop(); }
    }

    private volatile bool _isConnected;
    public bool IsConnected { get => _isConnected; private set => _isConnected = value; }

    public List<Option> Options
    {
        get => _options;
        set
        {
            lock (_syncLock)
            {
                _options = value ?? new List<Option>();
                foreach (var fallback in CreateDefaultOptions())
                {
                    var configured = _options.Find(o => o.Name.Equals(fallback.Name, StringComparison.OrdinalIgnoreCase));
                    if (configured == null) _options.Add(fallback);
                    else if (string.IsNullOrWhiteSpace(configured.Value)) configured.Value = fallback.Value;
                }
            }
        }
    }

#pragma warning disable CS0067
    public event InterfaceModulesChangedEventHandler? InterfaceModulesChanged;
#pragma warning restore CS0067

    public event InterfacePropertyChangedEventHandler? InterfacePropertyChanged;

    #endregion

    public VoiceSynthesizer()
    {
        _models = new AudioModelJob((property, value) => OnInterfacePropertyChanged(Domain, "service", "Audio model service", property, value));
        // High-level MIG options (Paths are automatically handled by HomeGenie conventions)

        _modules.Add(new InterfaceModule { Domain = Domain, Address = "service", Description = "Audio model service" });
        // Default module (Node "0" represents the local synthesis engine)
        _modules.Add(new InterfaceModule
        {
            Domain = Domain,
            Address = "0",
            Description = "Local Voice Synthesizer"
        });
    }

    public bool IsSystemInterface() => false;

    public bool IsDevicePresent() => true;

    public List<InterfaceModule> GetModules() { lock (_moduleLock) return new List<InterfaceModule>(_modules); }

    #region Options Handling

    public void OnSetOption(Option option)
    {
        if (option.Name.Equals("Voice", StringComparison.OrdinalIgnoreCase)) _models.Stop();
        lock (_syncLock)
        {
            var opt = _options.Find(o => o.Name.Equals(option.Name, StringComparison.OrdinalIgnoreCase));
            if (opt != null)
            {
                if (option.Name.Equals("Threads", StringComparison.OrdinalIgnoreCase) ) _loadedModel = null;
                opt.Value = option.Value;
            }
            else
            {
                _options.Add(option);
            }
        }
    }

    #endregion

    #region Lifecycle (Connect / Disconnect)

    public bool Connect()
    {
        if (!IsEnabled) return false;
        _models.Start((token, restart) => ConnectEngine(null, token, restart));
        return true;
    }

    private string? _loadedModel;

    private bool ConnectEngine(string? selectedModel, CancellationToken token = default, bool restart = false)
    {
        lock (_syncLock)
        {
            try
            {
                OnInterfacePropertyChanged(Domain, "0", "Engine Error", "Voice.Error", "");
                var voiceName = AudioRequest.Identifier(selectedModel ?? GetOption("Voice").Value);
                if (string.IsNullOrWhiteSpace(voiceName)) voiceName = "it_IT-paola-medium";

                if (!restart && IsConnected && _tts != null && _loadedModel == voiceName) return true;
                token.ThrowIfCancellationRequested();
                IsConnected = false;
                if (restart) { DisposeEngine(); _tts = null; _loadedModel = null; }
                _models.Publish("Download.Error", "");
                _models.Publish("Download.State", "Idle");
                _models.Publish("Voice.Status", "Loading");
                // Resolve model directory under <AppBase>/data/models/tts/
                var ttsBaseDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "models", "tts");
                
                var voiceDirectory = AudioModelDownload.Voice(ttsBaseDirectory, voiceName,
                    (property, value) => _models.Publish(property, value), token, restart);
                var modelFile = Path.Combine(voiceDirectory, $"{voiceName}.onnx");
                var tokensFile = Path.Combine(voiceDirectory, "tokens.txt");
                var espeakDataDir = Path.Combine(voiceDirectory, "espeak-ng-data");

                int numThreads = 2;
                if (int.TryParse(GetOption("Threads").Value, out int configuredThreads) && configuredThreads > 0)
                {
                    numThreads = configuredThreads;
                }

                var sw = Stopwatch.StartNew();

                var config = new OfflineTtsConfig();
                config.Model.Vits.Model = modelFile;
                config.Model.Vits.Tokens = tokensFile;
                config.Model.Vits.DataDir = Directory.Exists(espeakDataDir) ? espeakDataDir : string.Empty;
                config.Model.NumThreads = numThreads;
                config.Model.Provider = "cpu";

                DisposeEngine();
                AudioNativeRuntime.EnsureLoaded();
                _tts = new OfflineTts(config);

                sw.Stop();
                _loadedModel = voiceName;
                token.ThrowIfCancellationRequested();
                IsConnected = true;
                _models.Publish("Voice.Status", "Ready");

                OnInterfacePropertyChanged(Domain, "0", "Engine Status", "Voice.Status", "Ready");
                MigService.Log.Info("VoiceSynthesizer initialized in {0}ms with voice '{1}'.", sw.ElapsedMilliseconds, voiceName);
                return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                IsConnected = false;
                _models.Publish("Download.State", "Paused");
                _models.Publish("Voice.Status", "Paused");
                return false;
            }
            catch (Exception ex)
            {
                IsConnected = false;
                _models.Publish("Voice.Status", "Error");
                MigService.Log.Error("VoiceSynthesizer connect failed: {0}", ex.Message);
                OnInterfacePropertyChanged(Domain, "0", "Engine Error", "Voice.Error", ex.Message);
                return false;
            }
        }
    }

    private void DisposeEngine()
    {
        if (_tts == null) return;
        AudioNativeRuntime.EnsureLoaded();
        _tts.Dispose();
        _tts = null;
    }

    public void Disconnect()
    {
        _models.Stop();
        lock (_syncLock)
        {
            DisposeEngine();
            _tts = null;
            _loadedModel = null;
            IsConnected = false;
            _models.Publish("Voice.Status", "Disconnected");
            OnInterfacePropertyChanged(Domain, "0", "Engine Status", "Voice.Status", "Disconnected");
            MigService.Log.Info("VoiceSynthesizer disconnected.");
        }
    }

    #endregion

    #region Command Processing

    public object InterfaceControl(MigInterfaceCommand request)
    {
        if (request != null && request.Address == "service")
        {
            if (request.Command == "Models.List") return AudioModels.Voices;
            if (request.Command == "Model.Status") return ModelStatus();
            if (request.Command == "Download.Stop") { _models.Stop(); return new ResponseStatus(Status.Ok); }
            if (request.Command is "Download.Resume" or "Download.Restart" or "Model.Load")
            {
                if (!IsEnabled) return new ResponseStatus(Status.Error, "Interface disabled.");
                _models.Start((token, restart) => ConnectEngine(null, token, restart), request.Command == "Download.Restart");
                return new ResponseStatus(Status.Ok);
            }
        }
        lock (_syncLock)
        {
            if (request == null || !IsEnabled) return new ResponseStatus(Status.Error, "Interface disabled or invalid request.");
            var address = string.IsNullOrWhiteSpace(request.Address) ? "0" : request.Address;
            try
            {
                if (request.Command == "Voice.SetSpeed")
                {
                    GetOption("Speed").Value = AudioRequest.Speed(AudioRequest.Text(request)).ToString(CultureInfo.InvariantCulture);
                    return new ResponseStatus(Status.Ok);
                }
                if (request.Command != "Voice.Synthesize" && request.Command != "Voice.Speak") return new ResponseStatus(Status.Error, "Unknown command.");
                var speed = AudioRequest.Speed(AudioRequest.Option(request, 0, GetOption("Speed").Value));
                var voice = AudioRequest.Identifier(AudioRequest.Option(request, 1, GetOption("Voice").Value));
                var speakerText = AudioRequest.Option(request, 2, "0");
                if (!int.TryParse(speakerText, out var speaker) || speaker < 0) throw new ArgumentException("Speaker must be a non-negative integer.");
                var text = AudioRequest.BodyText(request.Data);
                if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Text payload is required.");
                EnsureModule(address);
                if (!ConnectEngine(voice, _models.Token)) return new ResponseStatus(Status.Error, "Synthesis engine unavailable.");
                return ProcessSpeechSynthesis(request, address, text, speed, speaker);
            }
            catch (Exception ex) { return new ResponseStatus(Status.Error, ex.Message); }
        }
    }
    private object ProcessSpeechSynthesis(MigInterfaceCommand request, string address, string textToSpeak, float speed, int speaker)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            byte[] wavBytes;
            double audioDuration;

            lock (_syncLock)
            {
                AudioNativeRuntime.EnsureLoaded();
                if (speaker >= _tts!.NumSpeakers) throw new ArgumentException("Speaker is not available in this voice.");
                var generatedAudio = _tts.Generate(textToSpeak, speakerId: speaker, speed: speed);

                stopwatch.Stop();

                if (generatedAudio == null || generatedAudio.Samples == null || generatedAudio.Samples.Length == 0)
                {
                    MigService.Log.Error("TTS generation returned empty audio samples.");
                    return new ResponseStatus(Status.Error);
                }

                audioDuration = Math.Round((double)generatedAudio.Samples.Length / generatedAudio.SampleRate, 2);
                wavBytes = ConvertSamplesToWav(generatedAudio.Samples, generatedAudio.SampleRate);
            }

            double elapsedSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 3);
            MigService.Log.Debug("Synthesized {0}s audio in {1}s: \"{2}\"", audioDuration, elapsedSeconds, textToSpeak);

            // =========================================================================
            // EVENT CHAIN:
            // 1. Update text, duration and speed metadata first.
            // 2. Raise 'Voice.Synthesized' as the final trigger event with generation time.
            // =========================================================================
            OnInterfacePropertyChanged(Domain, address, "Spoken Text", "Voice.Text", textToSpeak);
            OnInterfacePropertyChanged(Domain, address, "Generated Audio Duration", "Voice.Duration", audioDuration.ToString("0.00", CultureInfo.InvariantCulture));

            OnInterfacePropertyChanged(Domain, address, "Speech Speed", "Voice.Speed", speed.ToString(CultureInfo.InvariantCulture));
            OnInterfacePropertyChanged(Domain, address, "Speech Voice", "Voice.Voice", _loadedModel!);
            // FINAL TRIGGER: Value contains elapsed generation time in seconds
            OnInterfacePropertyChanged(Domain, address, "Voice Synthesis Trigger", "Voice.Synthesized", elapsedSeconds.ToString("0.000", CultureInfo.InvariantCulture));

            return wavBytes;
        }
        catch (Exception ex)
        {
            MigService.Log.Error("Speech synthesis failed for node '{0}': {1}", address, ex.Message);
            OnInterfacePropertyChanged(Domain, address, "Synthesis Error", "Voice.Error", ex.Message);
            return new ResponseStatus(Status.Error, ex.Message);
        }
    }

    #endregion

    #region Audio Processing Helpers

    private static byte[] ConvertSamplesToWav(float[] samples, int sampleRate)
    {
        short channels = 1;
        short bitsPerSample = 16;
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        short blockAlign = (short)(channels * bitsPerSample / 8);

        byte[] pcmData = new byte[samples.Length * sizeof(short)];
        for (int i = 0; i < samples.Length; i++)
        {
            float clamped = Math.Clamp(samples[i], -1.0f, 1.0f);
            short sample16 = (short)(clamped * short.MaxValue);
            byte[] bytes = BitConverter.GetBytes(sample16);
            pcmData[i * 2] = bytes[0];
            pcmData[i * 2 + 1] = bytes[1];
        }

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        int chunkSize = 36 + pcmData.Length;

        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(chunkSize);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1);
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write(bitsPerSample);
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(pcmData.Length);
        bw.Write(pcmData);

        return ms.ToArray();
    }

    protected virtual void OnInterfacePropertyChanged(string domain, string source, string description, string propertyPath, object propertyValue)
    {
        if (InterfacePropertyChanged != null)
        {
            var args = new InterfacePropertyChangedEventArgs(domain, source, description, propertyPath, propertyValue);
            InterfacePropertyChanged(this, args);
        }
    }

    private void EnsureModule(string address)
    {
        lock (_moduleLock)
        {
            if (_modules.Exists(m => m.Address == address)) return;
            _modules.Add(new InterfaceModule { Domain = Domain, Address = address, Description = "Voice channel " + address });
            InterfaceModulesChanged?.Invoke(this, new InterfaceModulesChangedEventArgs(Domain));
        }
    }
    private Dictionary<string, object> ModelStatus()
    {
        var model = AudioRequest.Identifier(_options.Find(o => o.Name.Equals("Voice", StringComparison.OrdinalIgnoreCase))?.Value ?? "it_IT-paola-medium");
        var root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "models", "tts");
        var directory = Path.Combine(root, "vits-piper-" + model);
        if (!AudioModelDownload.VoiceInstalled(directory, model) && AudioModelDownload.VoiceInstalled(Path.Combine(root, model), model)) directory = Path.Combine(root, model);
        var file = Path.Combine(directory, model + ".onnx");
        var archive = Path.Combine(root, ".vits-piper-" + model + "-download", "voice.tar.bz2");
        var partial = File.Exists(archive) ? archive : archive + ".download";
        var installed = AudioModelDownload.VoiceInstalled(directory, model);
        var result = _models.Snapshot();
        result["Model.Name"] = model;
        result["Model.File"] = file;
        result["Model.Installed"] = installed;
        result["Model.Bytes"] = AudioModelDownload.Size(file);
        result["Model.PartialBytes"] = AudioModelDownload.Size(partial);
        result["Model.Ready"] = IsConnected && _loadedModel == model;
        return result;
    }

    private Option GetOption(string name)
    {
        var opt = _options.Find(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (opt == null)
        {
            string defaultValue = name switch
            {
                "Voice" => "it_IT-paola-medium",
                "Speed" => "1.0",
                "Threads" => "2",
                _ => string.Empty
            };
            opt = new Option(name, defaultValue);
            _options.Add(opt);
        }
        else if (string.IsNullOrWhiteSpace(opt.Value))
        {
            opt.Value = name switch
            {
                "Voice" => "it_IT-paola-medium",
                "Speed" => "1.0",
                "Threads" => "2",
                _ => opt.Value
            };
        }
        return opt;
    }

    #endregion
}




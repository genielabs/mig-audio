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
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MIG;
using MIG.Config;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

/// <summary>
/// MIG Interface for Speech-to-Text processing powered by Whisper.net.
/// </summary>
public class VoiceRecognizer : MigInterface
{
    private static readonly object RuntimeLock = new();
    private static bool? RuntimeUsesGpu;
    private static readonly List<RuntimeLibrary> DefaultRuntimeOrder = new(RuntimeOptions.RuntimeLibraryOrder);
    private WhisperFactory? _factory;
    private List<Option> _options = CreateDefaultOptions();

    private static List<Option> CreateDefaultOptions() => new()
    {
        new Option("ModelName", "ggml-base.bin"),
        new Option("Language", "auto"),
        new Option("Threads", "6"),
        new Option("Acceleration", "cpu")
    };
    private readonly List<InterfaceModule> _modules = new();
    private readonly object _syncLock = new();

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

    public bool IsEnabled { get; set; } = true;

    public bool IsConnected { get; private set; }

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

    public VoiceRecognizer()
    {

        // Default module (Node "0" represents the local engine)
        _modules.Add(new InterfaceModule
        {
            Domain = Domain,
            Address = "0",
            Description = "Local Voice Engine"
        });
    }

    public bool IsSystemInterface() => false;

    public bool IsDevicePresent() => true;

    public List<InterfaceModule> GetModules() { lock (_syncLock) return new List<InterfaceModule>(_modules); }

    #region Options Handling

    public void OnSetOption(Option option)
    {
        lock (_syncLock)
        {
            var opt = _options.Find(o => o.Name.Equals(option.Name, StringComparison.OrdinalIgnoreCase));
            if (opt != null)
            {
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

    public bool Connect() => ConnectEngine(null);

    private string? _loadedModel;

    private bool ConnectEngine(string? selectedModel)
    {
        lock (_syncLock)
        {
            try
            {
                var modelFile = AudioRequest.Model(selectedModel ?? GetOption("ModelName").Value);
                if (string.IsNullOrWhiteSpace(modelFile)) modelFile = "ggml-base.bin";

                if (IsConnected && _factory != null && _loadedModel == modelFile) return true;
                // Model path anchored to HomeGenie application base directory
                var asrDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "models", "asr");
                var fullModelPath = Path.Combine(asrDirectory, modelFile);

                Directory.CreateDirectory(asrDirectory);

                // Automatically download model if missing locally
                if (!File.Exists(fullModelPath))
                {
                    MigService.Log.Info("Model not found locally. Downloading '{0}' to '{1}'...", modelFile, fullModelPath);
                    DownloadModelAsync(modelFile, fullModelPath).GetAwaiter().GetResult();
                    MigService.Log.Info("Model download completed successfully.");
                }
                else
                {
                    MigService.Log.Info("Loading Whisper model from local cache: '{0}'", fullModelPath);
                }

                var sw = Stopwatch.StartNew();
                _factory?.Dispose();

                var acceleration = GetOption("Acceleration").Value;
                bool useGpu = acceleration.Equals("gpu", StringComparison.OrdinalIgnoreCase);
                if (!useGpu && !acceleration.Equals("cpu", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Acceleration must be cpu or gpu.");
                lock (RuntimeLock)
                {
                    if (RuntimeUsesGpu.HasValue && RuntimeUsesGpu.Value != useGpu)
                        throw new InvalidOperationException("Changing Whisper acceleration requires restarting the host process.");
                    if (!RuntimeUsesGpu.HasValue)
                    {
                        RuntimeOptions.RuntimeLibraryOrder = useGpu ? new List<RuntimeLibrary>(DefaultRuntimeOrder) : new List<RuntimeLibrary> { RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx };
                        RuntimeUsesGpu = useGpu;
                    }
                }
                // Native runtime selection is process-wide; configure before first load.
                try
                {
                    _factory = WhisperFactory.FromPath(fullModelPath, new WhisperFactoryOptions { UseGpu = useGpu });
                    MigService.Log.Info("Whisper factory initialized ({0}).", useGpu ? "GPU" : "CPU");
                }
                catch (Exception ex)
                {
                    MigService.Log.Warn("GPU initialization failed ({0}). Falling back to CPU.", ex.Message);
                    _factory = WhisperFactory.FromPath(fullModelPath, new WhisperFactoryOptions { UseGpu = false });
                    MigService.Log.Info("Whisper factory initialized with CPU fallback.");
                }

                sw.Stop();
                _loadedModel = modelFile;
                IsConnected = true;

                OnInterfacePropertyChanged(Domain, "0", "Engine Status", "Voice.Status", "Ready");
                MigService.Log.Info("VoiceRecognizer engine ready in {0}ms.", sw.ElapsedMilliseconds);
                return true;
            }
            catch (Exception ex)
            {
                IsConnected = false;
                MigService.Log.Error("VoiceRecognizer connect failed: {0}", ex.Message);
                OnInterfacePropertyChanged(Domain, "0", "Engine Error", "Voice.Error", ex.Message);
                return false;
            }
        }
    }

    public void Disconnect()
    {
        lock (_syncLock)
        {
            _factory?.Dispose();
            _factory = null;
            _loadedModel = null;
            IsConnected = false;
            OnInterfacePropertyChanged(Domain, "0", "Engine Status", "Voice.Status", "Disconnected");
            MigService.Log.Info("VoiceRecognizer disconnected.");
        }
    }

    #endregion

    #region Command Processing

    public object InterfaceControl(MigInterfaceCommand request)
    {
        lock (_syncLock)
        {
            if (request == null || !IsEnabled) return new ResponseStatus(Status.Error, "Interface disabled or invalid request.");
            var address = string.IsNullOrWhiteSpace(request.Address) ? "0" : request.Address;
            try
            {
                if (request.Command == "Voice.SetLanguage")
                {
                    var language = AudioRequest.Text(request);
                    if (string.IsNullOrWhiteSpace(language)) throw new ArgumentException("Language is required.");
                    GetOption("Language").Value = language;
                    return new ResponseStatus(Status.Ok);
                }
                if (request.Command != "Voice.Transcribe") return new ResponseStatus(Status.Error, "Unknown command.");
                var languageOption = AudioRequest.Option(request, 0, GetOption("Language").Value);
                var rawAudio = ExtractAudioBytes(request);
                AudioRequest.Duration(rawAudio);
                var model = AudioRequest.Model(AudioRequest.Option(request, 1, GetOption("ModelName").Value));
                EnsureModule(address);
                if (!ConnectEngine(model)) return new ResponseStatus(Status.Error, "Recognition engine unavailable.");
                return ProcessTranscription(rawAudio, address, languageOption);
            }
            catch (Exception ex) { return new ResponseStatus(Status.Error, ex.Message); }
        }
    }
    private object ProcessTranscription(byte[] rawAudio, string address, string language)
    {
        try
        {

            if (rawAudio.Length == 0)
            {
                string errorMsg = "Audio payload is empty.";
                MigService.Log.Error(errorMsg);
                OnInterfacePropertyChanged(Domain, address, "Transcription Error", "Voice.Error", errorMsg);
                return new ResponseStatus(Status.Error, errorMsg);
            }

            double audioDuration = AudioRequest.Duration(rawAudio);
            using var wavStream = EnsureWavStream(rawAudio);



            int threads = 6;
            if (int.TryParse(GetOption("Threads").Value, out int configuredThreads) && configuredThreads > 0)
            {
                threads = configuredThreads;
            }

            var detectedLanguage = language;
            var transcribedText = new StringBuilder();
            var stopwatch = Stopwatch.StartNew();

            lock (_syncLock)
            {
                using var processor = _factory!.CreateBuilder()
                    .WithLanguage(language)
                    .WithThreads(threads)
                    .WithSegmentEventHandler(segment => { transcribedText.Append(segment.Text).Append(' '); detectedLanguage = segment.Language; })
                    .Build();

                processor.Process(wavStream);
            }

            stopwatch.Stop();
            double processingTime = Math.Round(stopwatch.Elapsed.TotalSeconds, 2);
            string finalText = transcribedText.ToString().Trim();

            MigService.Log.Debug("Audio transcribed in {0}s ({1}s audio): \"{2}\"", processingTime, audioDuration, finalText);

            // =========================================================================
            // EVENT CHAIN:
            // 1. Update metadata and transcription properties first.
            // 2. Raise 'Voice.Recognized' as the final trigger event with elapsed time.
            // =========================================================================
            OnInterfacePropertyChanged(Domain, address, "Audio Duration", "Voice.Duration", audioDuration.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            OnInterfacePropertyChanged(Domain, address, "Detected Language", "Voice.Language", detectedLanguage);
            OnInterfacePropertyChanged(Domain, address, "Recognition Model", "Voice.Model", _loadedModel!);
            OnInterfacePropertyChanged(Domain, address, "Transcribed Text", "Voice.Text", finalText);

            // FINAL TRIGGER: Value contains total processing time in seconds
            OnInterfacePropertyChanged(Domain, address, "Voice Recognition Trigger", "Voice.Recognized", processingTime.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));

            return new ResponseText(finalText);
        }
        catch (Exception ex)
        {
            MigService.Log.Error("Transcription failed for node '{0}': {1}", address, ex.Message);
            OnInterfacePropertyChanged(Domain, address, "Transcription Error", "Voice.Error", ex.Message);
            return new ResponseStatus(Status.Error, ex.Message);
        }
    }

    #endregion

    #region Audio Processing & Helpers

    private static byte[] ExtractAudioBytes(MigInterfaceCommand request)
    {
        if (request.Data is byte[] bytes && bytes.Length > 0)
            return bytes;

        if (request.Data is Stream stream)
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        string? payloadString = request.Data as string;
        if (string.IsNullOrEmpty(payloadString))
        {
            payloadString = request.GetOption(0);
        }

        if (!string.IsNullOrWhiteSpace(payloadString))
        {
            if (File.Exists(payloadString))
                return File.ReadAllBytes(payloadString);

            try
            {
                return Convert.FromBase64String(payloadString);
            }
            catch
            {
                return Encoding.UTF8.GetBytes(payloadString);
            }
        }

        var prop = request.GetType().GetProperty("PostData");
        if (prop != null)
        {
            var postDataVal = prop.GetValue(request);
            if (postDataVal is byte[] pBytes && pBytes.Length > 0)
                return pBytes;
            if (postDataVal is string pStr && !string.IsNullOrEmpty(pStr))
            {
                if (File.Exists(pStr)) return File.ReadAllBytes(pStr);
                try { return Convert.FromBase64String(pStr); } catch { }
            }
        }

        return Array.Empty<byte>();
    }

    private static Stream EnsureWavStream(byte[] rawData, int sampleRate = 16000, short bitsPerSample = 16, short channels = 1)
    {
        if (rawData.Length >= 4 &&
            rawData[0] == 'R' && rawData[1] == 'I' && rawData[2] == 'F' && rawData[3] == 'F')
        {
            return new MemoryStream(rawData);
        }

        var wavStream = new MemoryStream();
        using (var bw = new BinaryWriter(wavStream, Encoding.UTF8, leaveOpen: true))
        {
            int byteRate = sampleRate * channels * bitsPerSample / 8;
            short blockAlign = (short)(channels * bitsPerSample / 8);
            int chunkSize = 36 + rawData.Length;

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
            bw.Write(rawData.Length);
            bw.Write(rawData);
        }

        wavStream.Position = 0;
        return wavStream;
    }

    private static async Task DownloadModelAsync(string modelFileName, string destinationPath)
    {
        var type = modelFileName.ToLowerInvariant() switch
        {
            "ggml-tiny.bin" => GgmlType.Tiny,
            "ggml-tiny.en.bin" => GgmlType.TinyEn,
            "ggml-base.bin" => GgmlType.Base,
            "ggml-base.en.bin" => GgmlType.BaseEn,
            "ggml-small.bin" => GgmlType.Small,
            "ggml-small.en.bin" => GgmlType.SmallEn,
            "ggml-medium.bin" => GgmlType.Medium,
            "ggml-medium.en.bin" => GgmlType.MediumEn,
            _ => throw new ArgumentException("Model is not installed and has no supported automatic download.")
        };
        using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(type);
        using var fileWriter = new FileStream(destinationPath + ".download", FileMode.Create, FileAccess.Write);
        await modelStream.CopyToAsync(fileWriter);
        fileWriter.Close();
        File.Move(destinationPath + ".download", destinationPath, overwrite: true);
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
        if (_modules.Exists(m => m.Address == address)) return;
        _modules.Add(new InterfaceModule { Domain = Domain, Address = address, Description = "Voice channel " + address });
        InterfaceModulesChanged?.Invoke(this, new InterfaceModulesChangedEventArgs(Domain));
    }
    private Option GetOption(string name)
    {
        var opt = _options.Find(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (opt == null)
        {
            string defaultValue = name switch
            {
                "ModelName" => "ggml-base.bin",
                "Language" => "auto",
                "Threads" => "6",
                "Acceleration" => "cpu",
                _ => string.Empty
            };
            opt = new Option(name, defaultValue);
            _options.Add(opt);
        }
        else if (string.IsNullOrWhiteSpace(opt.Value))
        {
            opt.Value = name switch
            {
                "ModelName" => "ggml-base.bin",
                "Language" => "auto",
                "Threads" => "6",
                "Acceleration" => "cpu",
                _ => opt.Value
            };
        }
        return opt;
    }

    #endregion
}







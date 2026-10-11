using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Test.WebService;

internal static class ApiSmokeTests
{
    internal static async Task Run(string root, bool native, MIG.MigService service)
    {
        using var client = new HttpClient { BaseAddress = new Uri(root), Timeout = TimeSpan.FromMinutes(3) };
        async Task<byte[]> Post(string path, byte[] payload)
        {
            using var body = new ByteArrayContent(payload);
            body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var response = await client.PostAsync("api/" + path, body);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync();
        }
        async Task Error(string path, byte[] body)
        {
            var result = await Post(path, body);
            using var json = JsonDocument.Parse(result);
            var status = json.RootElement.GetProperty("Status").ToString();
            if (!status.Equals("Error", StringComparison.OrdinalIgnoreCase) && status != "1")
                throw new InvalidOperationException("Expected error: " + Encoding.UTF8.GetString(result));
            Console.WriteLine("PASS " + path);
        }
        foreach (var expected in new[]
        {
            ("Audio.VoiceSynthesizer", "Speed", "1.0"),
            ("Audio.VoiceSynthesizer", "Voice", "it_IT-paola-medium"),
            ("Audio.VoiceRecognizer", "Language", "auto"),
            ("Audio.VoiceRecognizer", "ModelName", "ggml-base.bin"),
            ("Audio.VoiceRecognizer", "Acceleration", "cpu")
        })
        {
            var response = await client.GetStringAsync($"api/MIGService.Interfaces/{expected.Item1}/Options.Get/{expected.Item2}");
            using var parsed = JsonDocument.Parse(response);
            if (!parsed.RootElement.TryGetProperty("ResponseValue", out var actual) || actual.GetString() != expected.Item3)
                throw new InvalidOperationException($"Missing startup default {expected.Item1}/{expected.Item2}: {response}");
        }
        foreach (var domain in new[] { "Audio.VoiceRecognizer", "Audio.VoiceSynthesizer" })
            if (!service.GetInterface(domain).GetModules().Any(m => m.Address == "service"))
                throw new InvalidOperationException("Missing audio model service module.");
        foreach (var domain in new[] { "Audio.VoiceRecognizer", "Audio.VoiceSynthesizer" })
        {
            using var catalog = JsonDocument.Parse(await client.GetStringAsync($"api/{domain}/service/Models.List"));
            if (catalog.RootElement.ValueKind != JsonValueKind.Array || catalog.RootElement.GetArrayLength() == 0)
                throw new InvalidOperationException("Missing model catalog.");
            using var model = JsonDocument.Parse(await client.GetStringAsync($"api/{domain}/service/Model.Status"));
            if (!model.RootElement.TryGetProperty("Model.File", out _) || !model.RootElement.TryGetProperty("Model.Installed", out _))
                throw new InvalidOperationException("Missing model file status.");
        }
        Console.WriteLine("PASS defaults and model service modules available before audio requests");
        var text = Encoding.UTF8.GetBytes("Ciao, questa è una prova della voce locale.");
        await Error("Audio.VoiceSynthesizer/chat-a/Voice.Synthesize/0", text);
        await Error("Audio.VoiceSynthesizer/chat-a/Voice.Synthesize/NaN", text);
        await Error("Audio.VoiceSynthesizer/chat-a/Voice.Synthesize/1/default/-1", text);
        await Error("Audio.VoiceSynthesizer/chat-a/Voice.Synthesize", Array.Empty<byte>());
        await Error("Audio.VoiceRecognizer/chat-a/Voice.Transcribe", new byte[3]);
        if (!native) return;
        var events = new List<(string Domain, string Address, string Property)>();
        service.InterfacePropertyChanged += (_, args) =>
        {
            lock (events) events.Add((args.EventData.Domain, args.EventData.Source, args.EventData.Property));
        };
        foreach (var address in new[] { "chat-a", "chat-b" })
        {
            foreach (var speed in new[] { "0.9", "1.15" })
            {
                var wav = await Post($"Audio.VoiceSynthesizer/{address}/Voice.Synthesize/{speed}/it_IT-paola-medium/0", text);
                if (wav.Length < 44 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF") throw new InvalidOperationException("Expected WAV.");
                var output = Path.Combine(AppContext.BaseDirectory, $"test-{address}-{speed}.wav");
                await File.WriteAllBytesAsync(output, wav);
                Console.WriteLine("PASS WAV " + output);
                // Recognition expects 16 kHz PCM; resample the synthesized mono PCM output.
                int rate = BitConverter.ToInt32(wav, 24);
                int count = (wav.Length - 44) / 2;
                int targetCount = (int)((long)count * 16000 / rate);
                var pcm = new byte[targetCount * 2];
                for (int i = 0; i < targetCount; i++)
                {
                    double pos = i * (double)rate / 16000;
                    int left = Math.Min((int)pos, count - 1), right = Math.Min(left + 1, count - 1);
                    var sample = (short)(BitConverter.ToInt16(wav, 44 + left * 2) * (1 - (pos - left)) + BitConverter.ToInt16(wav, 44 + right * 2) * (pos - left));
                    BitConverter.TryWriteBytes(pcm.AsSpan(i * 2, 2), sample);
                }
                var language = speed == "0.9" ? "auto" : "it";
                var transcript = await Post($"Audio.VoiceRecognizer/{address}/Voice.Transcribe/{language}/base", pcm);
                using var parsed = JsonDocument.Parse(transcript);
                var recognized = parsed.RootElement.GetProperty("ResponseValue").GetString();
                if (string.IsNullOrWhiteSpace(recognized) || new[] { "ciao", "questa", "una", "prova", "della", "voce", "locale" }.Count(word => recognized.Contains(word, StringComparison.OrdinalIgnoreCase)) < 5) throw new InvalidOperationException("Unexpected transcription: " + recognized);
                Console.WriteLine("PASS ASR " + recognized);
            }
        }
        var concurrent = await Task.WhenAll(
            Post("Audio.VoiceSynthesizer/chat-a/Voice.Speak/default", text),
            Post("Audio.VoiceSynthesizer/chat-b/Voice.Synthesize/1.1", text));
        if (concurrent.Any(w => w.Length < 44 || Encoding.ASCII.GetString(w, 0, 4) != "RIFF")) throw new InvalidOperationException("Concurrent synthesis failed.");
        foreach (var domain in new[] { "Audio.VoiceRecognizer", "Audio.VoiceSynthesizer" })
        {
            var iface = service.GetInterface(domain);
            if (!new[] { "chat-a", "chat-b" }.All(a => iface.GetModules().Any(m => m.Address == a))) throw new InvalidOperationException("Missing channel module.");
            if (domain.EndsWith("VoiceSynthesizer") && iface.Options.Find(o => o.Name == "Speed")?.Value != "1.0") throw new InvalidOperationException("Request changed default speed.");
        }
        lock (events)
        {
            foreach (var domain in new[] { "Audio.VoiceRecognizer", "Audio.VoiceSynthesizer" })
                foreach (var address in new[] { "chat-a", "chat-b" })
                {
                    var properties = events.Where(e => e.Domain == domain && e.Address == address).Select(e => e.Property).ToList();
                    var trigger = domain.EndsWith("VoiceRecognizer") ? "Voice.Recognized" : "Voice.Synthesized";
                    if (properties.IndexOf("Voice.Text") < 0 || properties.IndexOf(trigger) <= properties.IndexOf("Voice.Text")) throw new InvalidOperationException("Unexpected event ordering.");
                }
        }
        Console.WriteLine("PASS concurrent channels, alias, defaults and ordered events");
    }
}




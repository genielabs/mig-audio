using System.Globalization;
using System.Text;
using MIG;

namespace MIG.Interfaces.Audio;

internal static class AudioRequest
{
    internal static string Option(MigInterfaceCommand request, int index, string fallback)
    {
        var value = request.GetOption(index);
        return string.IsNullOrWhiteSpace(value) || value.Equals("default", StringComparison.OrdinalIgnoreCase) ? fallback : value;
    }

    internal static string BodyText(object? data) => data switch
    {
        byte[] bytes => Encoding.UTF8.GetString(bytes).Trim(),
        string text => text.Trim(),
        Stream stream => ReadText(stream),
        _ => string.Empty
    };

    private static string ReadText(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, leaveOpen: true);
        return reader.ReadToEnd().Trim();
    }

    internal static string Text(MigInterfaceCommand request)
    {
        var text = BodyText(request.Data);
        return string.IsNullOrWhiteSpace(text) ? request.GetOption(0) : text;
    }

    internal static float Speed(string value)
    {
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) || !float.IsFinite(speed) || speed <= 0)
            throw new ArgumentException("Speed must be a finite positive number using a decimal point.");
        return speed;
    }

    internal static string Identifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_' && c != '.'))
            throw new ArgumentException("Invalid model or voice identifier.");
        if (value.Contains("..")) throw new ArgumentException("Invalid model or voice identifier.");
        return value;
    }

    internal static string Model(string value)
    {
        value = Identifier(value);
        return value.StartsWith("ggml-") && value.EndsWith(".bin") ? value : "ggml-" + value + ".bin";
    }

    internal static double Duration(byte[] audio)
    {
        if (audio.Length >= 12 && Encoding.ASCII.GetString(audio, 0, 4) == "RIFF")
        {
            if (Encoding.ASCII.GetString(audio, 8, 4) != "WAVE") throw new ArgumentException("Expected WAV audio.");
            int byteRate = 0, dataLength = -1;
            for (int offset = 12; offset + 8 <= audio.Length;)
            {
                var chunk = Encoding.ASCII.GetString(audio, offset, 4);
                var size = BitConverter.ToUInt32(audio, offset + 4);
                var start = offset + 8;
                if (size > int.MaxValue || size > audio.Length - start) throw new ArgumentException("Truncated WAV audio.");
                if (chunk == "fmt ")
                {
                    if (size < 16 || BitConverter.ToInt16(audio, start) != 1 || BitConverter.ToInt16(audio, start + 2) != 1 || BitConverter.ToInt32(audio, start + 4) != 16000 || BitConverter.ToInt16(audio, start + 14) != 16)
                        throw new ArgumentException("WAV must be PCM, mono, 16 kHz, 16 bit.");
                    byteRate = BitConverter.ToInt32(audio, start + 8);
                    if (byteRate != 32000 || BitConverter.ToInt16(audio, start + 12) != 2) throw new ArgumentException("Invalid WAV format.");
                }
                if (chunk == "data") dataLength = (int)size;
                var next = (long)start + size + (size & 1);
                if (next > audio.Length) throw new ArgumentException("Truncated WAV chunk.");
                offset = (int)next;
            }
            if (byteRate == 0 || dataLength <= 0 || dataLength % 2 != 0) throw new ArgumentException("Invalid WAV audio data.");
            return Math.Round((double)dataLength / byteRate, 2);
        }
        if (audio.Length == 0 || audio.Length % 2 != 0) throw new ArgumentException("PCM audio must contain complete 16-bit samples.");
        return Math.Round(audio.Length / 32000.0, 2);
    }
}

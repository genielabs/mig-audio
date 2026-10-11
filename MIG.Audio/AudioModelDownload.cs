namespace MIG.Interfaces.Audio;

using System.Globalization;
using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using SharpCompress.Archives;
using SharpCompress.Common;

/// <summary>Downloads models into staging files and publishes only complete installations.</summary>
internal static class AudioModelDownload
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(30) };

    internal static async Task FileAsync(string url, string destination, Action<string, object> emit, CancellationToken token = default, bool restart = false)
    {
        emit("Download.Error", "");
        emit("Download.Progress", -1);
        emit("Download.State", "Downloading");
        var temporary = destination + ".download";
        var metadata = temporary + ".etag";
        try
        {
            token.ThrowIfCancellationRequested();
            long offset = !restart && File.Exists(temporary) && File.Exists(metadata) ? new FileInfo(temporary).Length : 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
                request.Headers.TryAddWithoutValidation("If-Range", File.ReadAllText(metadata));
            }
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && offset > 0)
            {
                var finalValidator = response.Headers.ETag?.ToString() ?? response.Content.Headers.LastModified?.ToString("R", CultureInfo.InvariantCulture);
                if (response.Content.Headers.ContentRange?.Length == offset && finalValidator == File.ReadAllText(metadata))
                {
                    token.ThrowIfCancellationRequested();
                    File.Move(temporary, destination, true);
                    File.Delete(metadata);
                    emit("Download.Bytes", offset);
                    emit("Download.TotalBytes", offset);
                    emit("Download.Progress", 100);
                    return;
                }
                await FileAsync(url, destination, emit, token, restart: true);
                return;
            }
            response.EnsureSuccessStatusCode();
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                if (response.Content.Headers.ContentRange?.From != offset) throw new IOException("Invalid model download range.");
            }
            else offset = 0;
            var total = response.Content.Headers.ContentRange?.Length ?? (response.Content.Headers.ContentLength + offset);
            var validator = response.Headers.ETag?.ToString() ?? response.Content.Headers.LastModified?.ToString("R", CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(validator)) File.WriteAllText(metadata, validator);
            else if (File.Exists(metadata)) File.Delete(metadata);
            emit("Download.TotalBytes", total ?? 0);
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using (var output = new FileStream(temporary, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                var buffer = new byte[81920];
                long received = offset;
                var last = DateTime.MinValue;
                int count;
                while ((count = await input.ReadAsync(buffer, token)) != 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    received += count;
                    if (DateTime.UtcNow - last >= TimeSpan.FromMilliseconds(250))
                    {
                        emit("Download.Bytes", received);
                        emit("Download.Progress", total > 0 ? Math.Round(received * 100.0 / total.Value, 1).ToString("0.0", CultureInfo.InvariantCulture) : "-1");
                        last = DateTime.UtcNow;
                    }
                }
                if (received == 0 || (total.HasValue && received != total.Value))
                    throw new IOException("Incomplete model download.");
                emit("Download.Bytes", received);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
            if (File.Exists(metadata)) File.Delete(metadata);
            emit("Download.Progress", 100);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            emit("Download.State", "Paused");
            throw;
        }
        catch (Exception ex)
        {
            emit("Download.Error", ex.Message);
            emit("Download.State", "Error");
            throw;
        }
    }

    internal static long Size(string file)
    {
        try { return File.Exists(file) ? new FileInfo(file).Length : 0; }
        catch (IOException) { return 0; }
    }

    internal static bool VoiceInstalled(string directory, string voice) =>
        File.Exists(Path.Combine(directory, voice + ".onnx")) &&
        File.Exists(Path.Combine(directory, "tokens.txt")) &&
        Directory.Exists(Path.Combine(directory, "espeak-ng-data"));

    internal static string Voice(string root, string voice, Action<string, object> emit, CancellationToken token = default, bool restart = false)
    {
        AudioRequest.Identifier(voice);
        var name = "vits-piper-" + voice;
        var destination = Path.Combine(root, name);
        var alternative = Path.Combine(root, voice);
        var staging = Path.Combine(root, "." + name + "-download");
        var archivePath = Path.Combine(staging, "voice.tar.bz2");
        var pending = File.Exists(archivePath) || File.Exists(archivePath + ".download");
        if (!restart && !pending && VoiceInstalled(destination, voice)) return destination;
        if (!restart && !pending && VoiceInstalled(alternative, voice)) return alternative;
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(staging);
        try
        {
            emit("Download.Model", voice);
            if (restart || !File.Exists(archivePath)) FileAsync("https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/" + name + ".tar.bz2", archivePath, emit, token, restart).GetAwaiter().GetResult();
            token.ThrowIfCancellationRequested();
            emit("Download.State", "Extracting");
            var extracted = Path.Combine(staging, name);
            if (Directory.Exists(extracted)) Directory.Delete(extracted, true);
            Extract(archivePath, staging, name, token);
            if (!VoiceInstalled(extracted, voice)) throw new IOException("The voice archive is missing model, tokens or espeak data.");
            token.ThrowIfCancellationRequested();
            // Preserve an incomplete previous installation until the replacement has been validated.
            var backup = destination + ".previous-" + Guid.NewGuid().ToString("N");
            if (Directory.Exists(destination)) Directory.Move(destination, backup);
            try { Directory.Move(extracted, destination); }
            catch { if (Directory.Exists(backup)) Directory.Move(backup, destination); throw; }
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            Directory.Delete(staging, true);
            emit("Download.State", "Completed");
            return destination;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            emit("Download.State", "Paused");
            throw;
        }
        catch (Exception ex)
        {
            emit("Download.Error", ex.Message);
            emit("Download.State", "Error");
            throw;
        }
    }

    internal static void Extract(string archivePath, string staging, string expectedRoot, CancellationToken token = default)
    {
        if (archivePath.EndsWith(".bz2", StringComparison.OrdinalIgnoreCase))
        {
            var tarPath = Path.Combine(staging, "voice.tar");
            using (var input = File.OpenRead(archivePath))
            using (var decompressed = SharpCompress.Compressors.BZip2.BZip2Stream.Create(input,
                SharpCompress.Compressors.CompressionMode.Decompress, false))
            using (var output = File.Create(tarPath))
            {
                var buffer = new byte[81920]; int count;
                while ((count = decompressed.Read(buffer)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, count);
                }
            }
            archivePath = tarPath;
        }
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var key = (entry.Key ?? "").Replace('\\', '/');
            while (key.StartsWith("./", StringComparison.Ordinal)) key = key[2..];
            var parts = key.TrimEnd('/').Split('/');
            if (parts.Length == 0 || parts[0] != expectedRoot || parts.Any(p => p is ".." or "" || p.Contains(':')) || !string.IsNullOrEmpty(entry.LinkTarget))
                throw new IOException("Unsafe path or link in voice archive.");
            var path = Path.Combine(staging, Path.Combine(parts));
            if (entry.IsDirectory) Directory.CreateDirectory(path);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.WriteToFile(path, new ExtractionOptions { Overwrite = false });
            }
        }
    }
}

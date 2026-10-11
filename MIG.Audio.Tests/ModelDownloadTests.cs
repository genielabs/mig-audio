using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using MIG;
using MIG.Interfaces.Audio;
using NUnit.Framework;

namespace MIG.Audio.Tests;

public class ModelDownloadTests
{
    [Test]
    public async Task DownloadReportsProgressAndInstallsOnlyCompleteFile()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var directory = Path.Combine(Path.GetTempPath(), "mig-audio-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var server = Task.Run(async () => {
                using var connection = await listener.AcceptTcpClientAsync();
                await using var stream = connection.GetStream();
                var request = new byte[4096]; _ = await stream.ReadAsync(request);
                await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\nhello"));
            });
            var events = new List<(string Name, object Value)>();
            var file = Path.Combine(directory, "model");
            await AudioModelDownload.FileAsync($"http://127.0.0.1:{port}/", file, (name, value) => events.Add((name, value)));
            await server;
            Assert.That(File.ReadAllText(file), Is.EqualTo("hello"));
            Assert.That(File.Exists(file + ".download"), Is.False);
            Assert.That(events.Any(e => e.Name == "Download.State" && (string)e.Value == "Downloading"), Is.True);
            Assert.That(events.Last(e => e.Name == "Download.Progress").Value, Is.EqualTo(100));
        }
        finally { listener.Stop(); Directory.Delete(directory, true); }
    }

    [Test]
    public async Task FailedDownloadPreservesInstalledFileAndPartialForResume()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var directory = Path.Combine(Path.GetTempPath(), "mig-audio-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var server = Task.Run(async () => {
                using var connection = await listener.AcceptTcpClientAsync();
                await using var stream = connection.GetStream();
                var request = new byte[4096]; _ = await stream.ReadAsync(request);
                await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 20\r\nConnection: close\r\n\r\nshort"));
            });
            var file = Path.Combine(directory, "model"); File.WriteAllText(file, "installed");
            var events = new List<(string Name, object Value)>();
            Assert.That(async () => await AudioModelDownload.FileAsync($"http://127.0.0.1:{port}/", file, (name, value) => events.Add((name, value))), Throws.Exception);
            await server;
            Assert.That(File.ReadAllText(file), Is.EqualTo("installed"));
            Assert.That(File.ReadAllText(file + ".download"), Is.EqualTo("short"));
            Assert.That(events.Last(e => e.Name == "Download.State").Value, Is.EqualTo("Error"));
        }
        finally { listener.Stop(); Directory.Delete(directory, true); }
    }

    [TestCase("voice/../../escape")]
    [TestCase("/escape")]
    public void ArchiveRejectsPathsOutsideVoiceDirectory(string key)
    {
        var directory = Path.Combine(Path.GetTempPath(), "mig-audio-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "voice.zip");
            using (var archive = ZipFile.Open(file, ZipArchiveMode.Create)) archive.CreateEntry(key);
            Assert.Throws<IOException>(() => AudioModelDownload.Extract(file, directory, "voice"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test, Explicit("Downloads the real Piper voice and loads the native engine")]
    public void PiperDownloadAndNativeSynthesis()
    {
        var synth = new VoiceSynthesizer();
        var events = new List<(string Property, string Value)>();
        synth.InterfacePropertyChanged += (_, args) => events.Add((args.EventData.Property, Convert.ToString(args.EventData.Value) ?? ""));
        try
        {
            Assert.That(synth.Connect(), Is.True);
            Assert.That(SpinWait.SpinUntil(() => synth.IsConnected, TimeSpan.FromMinutes(2)), Is.True);
            Assert.That(events.Any(e => e.Property == "Voice.Status" && e.Value == "Ready"), Is.True);
            if (events.Any(e => e.Property == "Download.State" && e.Value == "Downloading"))
                Assert.That(events.Last(e => e.Property == "Download.State").Value, Is.EqualTo("Completed"));
            synth.InterfaceControl(new MigInterfaceCommand("Audio.VoiceSynthesizer/service/Download.Stop"));
            var response = synth.InterfaceControl(new MigInterfaceCommand("Audio.VoiceSynthesizer/download-test/Voice.Synthesize", "Prova del download automatico."));
            Assert.That(response, Is.TypeOf<byte[]>());
            Assert.That(((byte[])response).Length, Is.GreaterThan(44));
        }
        finally { synth.Disconnect(); }
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text;
using MIG.Interfaces.Audio;
using NUnit.Framework;

namespace MIG.Audio.Tests;

public class ResumableDownloadTests
{
    [Test]
    public void DisablingInterfaceCancelsPreparationAndModulesRemainResponsive()
    {
        var recognizer = new VoiceRecognizer();
        using var downloading = new ManualResetEventSlim();
        using var paused = new ManualResetEventSlim();
        recognizer.InterfacePropertyChanged += (_, args) => {
            if (args.EventData.Property != "Download.State") return;
            if (Convert.ToString(args.EventData.Value) == "Downloading") downloading.Set();
            if (Convert.ToString(args.EventData.Value) == "Paused") paused.Set();
        };
        try
        {
            recognizer.InterfaceControl(new MIG.MigInterfaceCommand("Audio.VoiceRecognizer/service/Download.Restart"));
            Assert.That(downloading.Wait(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(Task.Run(() => recognizer.GetModules()).Wait(TimeSpan.FromSeconds(1)), Is.True);
            var status = recognizer.InterfaceControl(new MIG.MigInterfaceCommand("Audio.VoiceRecognizer/service/Model.Status"));
            Assert.That(status, Is.TypeOf<Dictionary<string, object>>());
            recognizer.IsEnabled = false;
            Assert.That(paused.Wait(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(recognizer.IsConnected, Is.False);
        }
        finally { recognizer.Disconnect(); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ResumeUsesRangeAndRestartDiscardsPartial(bool restart)
    {
        var directory = Path.Combine(Path.GetTempPath(), "mig-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var file = Path.Combine(directory, "model");
        File.WriteAllText(file + ".download", "hello");
        File.WriteAllText(file + ".download.etag", "\"version-one\"");
        try
        {
            string request = "";
            var server = Task.Run(async () => {
                using var connection = await listener.AcceptTcpClientAsync();
                await using var stream = connection.GetStream();
                var buffer = new byte[4096]; var read = await stream.ReadAsync(buffer);
                request = Encoding.ASCII.GetString(buffer, 0, read);
                var response = restart ? "HTTP/1.1 200 OK\r\nContent-Length: 11\r\nETag: \"version-one\"\r\nConnection: close\r\n\r\nhello world"
                    : "HTTP/1.1 206 Partial Content\r\nContent-Length: 6\r\nContent-Range: bytes 5-10/11\r\nETag: \"version-one\"\r\nConnection: close\r\n\r\n world";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            });
            await AudioModelDownload.FileAsync($"http://127.0.0.1:{port}/", file, (_, _) => {}, restart: restart);
            await server;
            Assert.That(File.ReadAllText(file), Is.EqualTo("hello world"));
            Assert.That(request.Contains("Range: bytes=5-"), Is.EqualTo(!restart));
            Assert.That(File.Exists(file + ".download"), Is.False);
        }
        finally { listener.Stop(); Directory.Delete(directory, true); }
    }

    [Test]
    public async Task StopPreservesPartialAndDoesNotReplaceInstalledModel()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mig-stop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cancellation = new CancellationTokenSource();
        var file = Path.Combine(directory, "model"); File.WriteAllText(file, "installed");
        var states = new List<string>();
        try
        {
            var server = Task.Run(async () => {
                using var connection = await listener.AcceptTcpClientAsync();
                await using var stream = connection.GetStream();
                var buffer = new byte[4096]; _ = await stream.ReadAsync(buffer);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 100\r\nETag: \"one\"\r\nConnection: close\r\n\r\npartial"));
                try { await Task.Delay(TimeSpan.FromSeconds(10), cancellation.Token); } catch (OperationCanceledException) {}
            });
            Assert.That(async () => await AudioModelDownload.FileAsync($"http://127.0.0.1:{port}/", file, (name, value) => {
                if (name == "Download.State") states.Add(value.ToString()!);
                if (name == "Download.Bytes" && Convert.ToInt64(value) > 0) cancellation.Cancel();
            }, cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
            await server;
            Assert.That(states.Last(), Is.EqualTo("Paused"));
            Assert.That(File.ReadAllText(file), Is.EqualTo("installed"));
            Assert.That(File.ReadAllText(file + ".download"), Is.EqualTo("partial"));
        }
        finally { cancellation.Cancel(); listener.Stop(); Directory.Delete(directory, true); }
    }

    [Test]
    public void BackgroundJobCanBeStoppedWithoutWaitingForEngineLock()
    {
        var job = new AudioModelJob((_, _) => {});
        using var entered = new ManualResetEventSlim();
        using var exited = new ManualResetEventSlim();
        job.Start((token, restart) => { entered.Set(); token.WaitHandle.WaitOne(); exited.Set(); return false; });
        Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
        job.Stop();
        Assert.That(exited.Wait(TimeSpan.FromSeconds(5)), Is.True);
        using var resumed = new ManualResetEventSlim();
        job.Start((token, restart) => { if (!token.IsCancellationRequested && restart) resumed.Set(); return true; }, true);
        Assert.That(resumed.Wait(TimeSpan.FromSeconds(5)), Is.True);
        job.Stop();
    }
}

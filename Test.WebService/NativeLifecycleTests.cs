using System.Runtime.InteropServices;
using MIG;
using MIG.Interfaces.Audio;

namespace Test.WebService;

internal static class NativeLifecycleTests
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetApiBase();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetVersionString();
    public static void Run(string[] args)
    {
        var onnx = args.FirstOrDefault(a => a.StartsWith("--onnx-runtime="))?.Substring(15);
        var expectRejection = args.Contains("--expect-unsupported-host-runtime");
        if (expectRejection && (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(onnx)))
            throw new ArgumentException("Expected host rejection requires Windows and an ONNX runtime path.");
        IntPtr hostRuntime = IntPtr.Zero;
        var synth = new VoiceSynthesizer();
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        synth.InterfacePropertyChanged += (_, e) =>
        {
            if (e.EventData.Property == "Voice.Error" && !string.IsNullOrEmpty(Convert.ToString(e.EventData.Value)))
                errors.Enqueue(Convert.ToString(e.EventData.Value)!);
        };
        try
        {
            if (!string.IsNullOrEmpty(onnx))
            {
                hostRuntime = NativeLibrary.Load(Path.GetFullPath(onnx));
                Console.WriteLine("Preloaded host ONNX: " + onnx);
                var api = Marshal.GetDelegateForFunctionPointer<GetApiBase>(NativeLibrary.GetExport(hostRuntime, "OrtGetApiBase"))();
                var version = Marshal.PtrToStringUTF8(Marshal.GetDelegateForFunctionPointer<GetVersionString>(Marshal.ReadIntPtr(api, IntPtr.Size))());
                Console.WriteLine("Host ONNX version: " + version);
                if (version != "1.23.2") throw new InvalidOperationException("This conflict regression requires the host ONNX 1.23.2 library.");
            }
            for (int cycle = 1; cycle <= 5; cycle++)
            {
                Exception? failure = null;
                int current = cycle;
                var thread = new Thread(() =>
                {
                    try
                    {
                        Console.WriteLine($"Native lifecycle {current}/5, thread {Environment.CurrentManagedThreadId}: enable and synthesize");
                        synth.IsEnabled = true;
                        if (!synth.Connect() || !SpinWait.SpinUntil(() => synth.IsConnected || !errors.IsEmpty, TimeSpan.FromMinutes(2)) || !synth.IsConnected)
                            throw new InvalidOperationException("Engine did not become ready after enable: " + string.Join("; ", errors));
                        var result = synth.InterfaceControl(new MigInterfaceCommand(
                            "Audio.VoiceSynthesizer/lifecycle-test/Voice.Synthesize", "Prova di riattivazione della sintesi vocale."));
                        if (result is not byte[] wav || wav.Length <= 44 || !synth.IsConnected)
                            throw new InvalidOperationException("Native synthesis failed: " + System.Text.Json.JsonSerializer.Serialize(result));
                        Console.WriteLine($"Native lifecycle {current}/5: WAV {wav.Length} bytes; disable and dispose");
                        synth.IsEnabled = false;
                        synth.Disconnect();
                        if (synth.IsConnected) throw new InvalidOperationException("Engine remained connected after disable.");
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    }
                    catch (Exception ex) { failure = ex; }
                }) { IsBackground = true };
                thread.Start();
                if (!thread.Join(TimeSpan.FromMinutes(3))) throw new TimeoutException("Native lifecycle test timed out.");
                if (expectRejection && failure != null && errors.Any(e => e.Contains("requires ONNX API 28")))
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    Console.WriteLine("Native preflight PASS: incompatible host ONNX rejected without creating TTS or crashing.");
                    return;
                }
                if (failure != null) throw new InvalidOperationException($"Native lifecycle {cycle} failed.", failure);
                if (expectRejection) throw new InvalidOperationException("Expected the incompatible host runtime to be rejected.");
            }
            Console.WriteLine("Native lifecycle PASS: 5 create/synthesize/dispose cycles on separate threads.");
        }
        finally
        {
            synth.Disconnect();
            if (hostRuntime != IntPtr.Zero) NativeLibrary.Free(hostRuntime);
        }
    }
}

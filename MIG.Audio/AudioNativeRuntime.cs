namespace MIG.Interfaces.Audio;

using System.Reflection;
using System.Runtime.InteropServices;
using SherpaOnnx;

// Keep the handle for the process lifetime: Sherpa objects have native finalizers.
internal static class AudioNativeRuntime
{
    private static readonly object Sync = new();
    private static IntPtr handle;
    private static bool resolverInstalled;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void InitializeCtype();
    private static InitializeCtype? initializeCtype;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetApiBase();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetApi(uint version);

    internal static void EnsureLoaded()
    {
        lock (Sync)
        {
            if (handle != IntPtr.Zero)
            {
                initializeCtype?.Invoke();
                return;
            }
            if (!resolverInstalled)
            {
                NativeLibrary.SetDllImportResolver(typeof(OfflineTts).Assembly, Resolve);
                resolverInstalled = true;
            }
            if (OperatingSystem.IsLinux())
            {
                var rid = RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.X64 => "linux-x64",
                    Architecture.Arm64 => "linux-arm64",
                    _ => throw new PlatformNotSupportedException("Unsupported Sherpa Linux architecture.")
                };
                var path = Path.Combine(AppContext.BaseDirectory, "runtimes", "mig-audio", rid, "native", "libsherpa-onnx-c-api.so");
                if (!File.Exists(path)) throw new DllNotFoundException($"Missing isolated Sherpa runtime: {path}. Rebuild with the updated MIG.Audio package.");
                // A new glibc namespace prevents the host's ONNX SONAME from being reused.
                try { handle = Dlmopen(new IntPtr(-1), path, 2); }
                catch (EntryPointNotFoundException) { handle = DlmopenLegacy(new IntPtr(-1), path, 2); }
                if (handle == IntPtr.Zero)
                {
                    IntPtr error;
                    try { error = Dlerror(); }
                    catch (EntryPointNotFoundException) { error = DlerrorLegacy(); }
                    throw new DllNotFoundException("Unable to load isolated Sherpa runtime: " + Marshal.PtrToStringUTF8(error));
                }
            }
            else
            {
                handle = NativeLibrary.Load("sherpa-onnx-c-api", typeof(OfflineTts).Assembly, null);
            }
            try
            {
                Validate(handle);
                if (OperatingSystem.IsWindows())
                {
                    // The Windows loader reuses an already loaded DLL by basename.
                    // Check the dependency before Sherpa dereferences an unsupported API.
                    var onnx = GetModuleHandle("onnxruntime.dll");
                    if (onnx != IntPtr.Zero) ValidateOnnx(onnx);
                }
                if (OperatingSystem.IsLinux())
                {
                    // glibc bug 32483: pthread_create initializes ctype TLS only in
                    // the base namespace. Every caller thread needs the isolated
                    // libc's initialization before any Sherpa operation.
                    var entry = NativeLibrary.GetExport(handle, "__ctype_init");
                    initializeCtype = Marshal.GetDelegateForFunctionPointer<InitializeCtype>(entry);
                    initializeCtype();
                }
            }
            catch { handle = IntPtr.Zero; throw; }
        }
    }

    internal static void Validate(IntPtr library)
    {
        foreach (var name in new[] { "SherpaOnnxCreateOfflineTts", "SherpaOnnxDestroyOfflineTts" })
            if (!NativeLibrary.TryGetExport(library, name, out _))
                throw new EntryPointNotFoundException($"Sherpa runtime is missing {name}.");
    }

    internal static void ValidateOnnx(IntPtr library)
    {
        var apiBase = Marshal.GetDelegateForFunctionPointer<GetApiBase>(NativeLibrary.GetExport(library, "OrtGetApiBase"))();
        var getApi = Marshal.GetDelegateForFunctionPointer<GetApi>(Marshal.ReadIntPtr(apiBase));
        if (getApi(28) == IntPtr.Zero)
            throw new NotSupportedException("Sherpa 1.13.8 requires ONNX API 28, but an incompatible onnxruntime.dll is already loaded by the host.");
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
        => name == "sherpa-onnx-c-api" ? handle : IntPtr.Zero;

    [DllImport("libc.so.6", EntryPoint = "dlmopen")]
    private static extern IntPtr Dlmopen(IntPtr namespaceId, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc.so.6", EntryPoint = "dlerror")]
    private static extern IntPtr Dlerror();
    [DllImport("libdl.so.2", EntryPoint = "dlmopen")]
    private static extern IntPtr DlmopenLegacy(IntPtr namespaceId, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libdl.so.2", EntryPoint = "dlerror")]
    private static extern IntPtr DlerrorLegacy();
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string name);
}

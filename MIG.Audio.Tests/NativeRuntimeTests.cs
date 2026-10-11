using System.Runtime.InteropServices;
using MIG.Interfaces.Audio;
using NUnit.Framework;

public class NativeRuntimeTests
{
    [Test]
    public void RejectsLibraryWithoutSherpaExportsBeforeCreatingTts()
    {
        var library = NativeLibrary.Load(OperatingSystem.IsWindows() ? "kernel32.dll" : OperatingSystem.IsLinux() ? "libc.so.6" : "/usr/lib/libSystem.B.dylib");
        try { Assert.Throws<EntryPointNotFoundException>(() => AudioNativeRuntime.Validate(library)); }
        finally { NativeLibrary.Free(library); }
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}

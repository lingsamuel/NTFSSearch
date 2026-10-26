using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NtfsSearch.Core;

internal static class VolumeNative
{
    internal static SafeFileHandle Open(char drive, uint access) => CreateFileW($"\\\\.\\{drive}:", access, 7, 0, 3, 0, 0);
    internal static bool Control(SafeFileHandle handle, uint code, byte[]? input, byte[] output, out int bytes) =>
        DeviceIoControl(handle, code, input, input?.Length ?? 0, output, output.Length, out bytes, 0);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, int inputSize, [Out] byte[] output, int outputSize, out int returned, nint overlapped);
}

using System.Runtime.InteropServices;

namespace ExtendedScreen.Linux.Input;

/// <summary>linux/uinput.h and linux/input-event-codes.h, x86_64 layout.</summary>
internal static unsafe partial class UinputNative
{
    public const int O_WRONLY = 0x1;
    public const int O_NONBLOCK = 0x800;

    // _IOW('U', nr, size) = (1 << 30) | (size << 16) | ('U' << 8) | nr
    public const ulong UI_SET_EVBIT = 0x40045564;
    public const ulong UI_SET_KEYBIT = 0x40045565;
    public const ulong UI_SET_ABSBIT = 0x40045567;
    public const ulong UI_SET_PROPBIT = 0x4004556e;
    public const ulong UI_DEV_SETUP = 0x405c5503;
    public const ulong UI_ABS_SETUP = 0x401c5504;
    public const ulong UI_DEV_CREATE = 0x5501;
    public const ulong UI_DEV_DESTROY = 0x5502;

    public const ushort EV_SYN = 0x00, EV_KEY = 0x01, EV_ABS = 0x03;
    public const ushort SYN_REPORT = 0;
    public const ushort ABS_X = 0x00, ABS_Y = 0x01, ABS_PRESSURE = 0x18, ABS_TILT_X = 0x1a, ABS_TILT_Y = 0x1b;
    public const ushort BTN_TOOL_PEN = 0x140, BTN_TOOL_RUBBER = 0x141, BTN_TOUCH = 0x14a, BTN_STYLUS = 0x14b;
    public const ushort BUS_VIRTUAL = 0x06;

    [StructLayout(LayoutKind.Sequential)]
    public struct InputId
    {
        public ushort BusType, Vendor, Product, Version;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UinputSetup
    {
        public InputId Id;
        public fixed byte Name[80];
        public uint FfEffectsMax;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UinputAbsSetup
    {
        public ushort Code;
        public int Value, Minimum, Maximum, Fuzz, Flat, Resolution; // struct input_absinfo, 4-byte aligned after code
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct InputEvent
    {
        public long TvSec, TvUsec;
        public ushort Type, Code;
        public int Value;
    }

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int open(string path, int flags);

    [LibraryImport("libc", SetLastError = true)]
    public static partial int close(int fd);

    [LibraryImport("libc", SetLastError = true)]
    public static partial int ioctl(int fd, ulong request, int value);

    [LibraryImport("libc", SetLastError = true)]
    public static partial int ioctl(int fd, ulong request, void* arg);

    [LibraryImport("libc", SetLastError = true)]
    public static partial nint write(int fd, void* buf, nuint count);
}

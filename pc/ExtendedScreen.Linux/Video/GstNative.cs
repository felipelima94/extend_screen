using System.Runtime.InteropServices;

namespace ExtendedScreen.Linux.Video;

/// <summary>The handful of GStreamer/GLib entry points the pipeline needs.</summary>
internal static partial class GstNative
{
    private const string Gst = "libgstreamer-1.0.so.0";
    private const string GstApp = "libgstapp-1.0.so.0";
    private const string GstVideo = "libgstvideo-1.0.so.0";
    private const string GLib = "libglib-2.0.so.0";

    public const int StateNull = 1;
    public const int StatePlaying = 4;
    public const int StateChangeFailure = 0;

    public const int MessageEos = 1 << 0;
    public const int MessageError = 1 << 1;

    public const int MapRead = 1;
    public const ulong ClockTimeNone = ulong.MaxValue;

    [StructLayout(LayoutKind.Sequential)]
    public struct MapInfo
    {
        public IntPtr Memory;
        public int Flags;
        public IntPtr Data;
        public nuint Size;
        public nuint MaxSize;
        private IntPtr _user0, _user1, _user2, _user3;
        private IntPtr _reserved0, _reserved1, _reserved2, _reserved3;
    }

    [LibraryImport(Gst)]
    public static partial void gst_init(IntPtr argc, IntPtr argv);

    [LibraryImport(Gst, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr gst_parse_launch(string description, out IntPtr error);

    [LibraryImport(Gst, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr gst_element_factory_find(string name);

    [LibraryImport(Gst, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr gst_bin_get_by_name(IntPtr bin, string name);

    [LibraryImport(Gst)]
    public static partial int gst_element_set_state(IntPtr element, int state);

    [LibraryImport(Gst)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool gst_element_send_event(IntPtr element, IntPtr evt);

    [LibraryImport(Gst)]
    public static partial IntPtr gst_element_get_bus(IntPtr element);

    [LibraryImport(Gst)]
    public static partial IntPtr gst_bus_pop_filtered(IntPtr bus, int types);

    [LibraryImport(Gst)]
    public static partial void gst_message_parse_error(IntPtr message, out IntPtr error, out IntPtr debug);

    [LibraryImport(Gst)]
    public static partial void gst_object_unref(IntPtr obj);

    [LibraryImport(Gst)]
    public static partial void gst_mini_object_unref(IntPtr obj);

    [LibraryImport(Gst)]
    public static partial IntPtr gst_sample_get_buffer(IntPtr sample);

    [LibraryImport(Gst)]
    public static partial IntPtr gst_sample_get_caps(IntPtr sample);

    [LibraryImport(Gst)]
    public static partial IntPtr gst_caps_get_structure(IntPtr caps, uint index);

    [LibraryImport(Gst, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool gst_structure_get_int(IntPtr structure, string field, out int value);

    [LibraryImport(Gst)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool gst_buffer_map(IntPtr buffer, out MapInfo info, int flags);

    [LibraryImport(Gst)]
    public static partial void gst_buffer_unmap(IntPtr buffer, ref MapInfo info);

    [LibraryImport(GstApp)]
    public static partial IntPtr gst_app_sink_try_pull_sample(IntPtr sink, ulong timeoutNs);

    [LibraryImport(GstVideo)]
    public static partial IntPtr gst_video_event_new_upstream_force_key_unit(ulong runningTime,
        [MarshalAs(UnmanagedType.Bool)] bool allHeaders, uint count);

    [LibraryImport(GLib)]
    public static partial void g_error_free(IntPtr error);

    [LibraryImport(GLib)]
    public static partial void g_free(IntPtr mem);

    /// <summary>Reads GError.message (the struct is { GQuark domain; gint code; gchar* message }) and frees the error.</summary>
    public static string TakeError(IntPtr error)
    {
        if (error == IntPtr.Zero) return "erro desconhecido";
        var message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)) ?? "erro desconhecido";
        g_error_free(error);
        return message;
    }

    private static int _initialized;

    public static void EnsureInitialized()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 0) gst_init(IntPtr.Zero, IntPtr.Zero);
    }

    public static bool HasElement(string name)
    {
        EnsureInitialized();
        var factory = gst_element_factory_find(name);
        if (factory == IntPtr.Zero) return false;
        gst_object_unref(factory);
        return true;
    }
}

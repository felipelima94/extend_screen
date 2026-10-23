using System.Runtime.InteropServices;
using SharpGen.Runtime;

namespace ExtendedScreen.Host.Encoding;

/// <summary>Minimal ICodecAPI wrapper (Vortice does not project it). Only SetValue is needed.</summary>
internal sealed unsafe class CodecApi : IDisposable
{
    private static readonly Guid IID_ICodecAPI = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    public static readonly Guid AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid AVEncCommonQualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
    public static readonly Guid AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid AVEncMPVDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static readonly Guid AVEncVideoForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    public const uint RateControlCbr = 0;
    public const uint RateControlLowDelayVbr = 4;

    private const ushort VT_BOOL = 11;
    private const ushort VT_UI4 = 19;

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint ui4;
        [FieldOffset(8)] public short boolVal;
    }

    private IntPtr _ptr;

    private CodecApi(IntPtr ptr) => _ptr = ptr;

    public static CodecApi? From(ComObject obj) =>
        Marshal.QueryInterface(obj.NativePointer, in IID_ICodecAPI, out var ptr) == 0 ? new CodecApi(ptr) : null;

    public bool SetUInt32(Guid api, uint value) => SetValue(api, new Variant { vt = VT_UI4, ui4 = value });

    public bool SetBool(Guid api, bool value) => SetValue(api, new Variant { vt = VT_BOOL, boolVal = (short)(value ? -1 : 0) });

    private bool SetValue(Guid api, Variant value)
    {
        // ICodecAPI vtable: IUnknown (0-2), IsSupported, IsModifiable, GetParameterRange,
        // GetParameterValues, GetDefaultValue, GetValue, SetValue (9).
        var setValue = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, Variant*, int>)(*(*(void***)_ptr + 9));
        return setValue(_ptr, &api, &value) >= 0;
    }

    public void Dispose()
    {
        if (_ptr == IntPtr.Zero) return;
        Marshal.Release(_ptr);
        _ptr = IntPtr.Zero;
    }
}

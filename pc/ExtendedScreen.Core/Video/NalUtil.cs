namespace ExtendedScreen.Core.Video;

/// <summary>Helpers for H.264 Annex-B byte streams.</summary>
public static class NalUtil
{
    public const int NalSps = 7;
    public const int NalPps = 8;
    public const int NalIdr = 5;

    /// <summary>Returns (offset, length) of each NAL unit payload, start codes excluded.</summary>
    public static List<(int Offset, int Length)> SplitNals(ReadOnlySpan<byte> data)
    {
        var result = new List<(int, int)>();
        var start = -1;
        var i = 0;
        while (i + 2 < data.Length)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                // A 4-byte start code (00 00 00 01) leaves a trailing zero on the previous NAL.
                var end = i > 0 && data[i - 1] == 0 ? i - 1 : i;
                if (start >= 0) result.Add((start, end - start));
                i += 3;
                start = i;
            }
            else
            {
                i++;
            }
        }
        if (start >= 0 && start < data.Length) result.Add((start, data.Length - start));
        return result;
    }

    public static int NalType(byte firstByte) => firstByte & 0x1F;

    /// <summary>Extracts SPS and PPS as an Annex-B blob, or null when the access unit has neither.</summary>
    public static byte[]? ExtractParameterSets(ReadOnlySpan<byte> accessUnit)
    {
        using var ms = new MemoryStream();
        bool sps = false, pps = false;
        foreach (var (offset, length) in SplitNals(accessUnit))
        {
            if (length == 0) continue;
            var type = NalType(accessUnit[offset]);
            if (type is not (NalSps or NalPps)) continue;
            sps |= type == NalSps;
            pps |= type == NalPps;
            ms.Write([0, 0, 0, 1]);
            ms.Write(accessUnit.Slice(offset, length));
        }
        return sps && pps ? ms.ToArray() : null;
    }

    public static bool ContainsIdr(ReadOnlySpan<byte> accessUnit)
    {
        foreach (var (offset, length) in SplitNals(accessUnit))
        {
            if (length > 0 && NalType(accessUnit[offset]) == NalIdr) return true;
        }
        return false;
    }
}

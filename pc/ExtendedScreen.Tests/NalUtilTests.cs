using ExtendedScreen.Core.Video;

namespace ExtendedScreen.Tests;

public class NalUtilTests
{
    private static readonly byte[] Sample =
    [
        0, 0, 0, 1, 0x67, 0xAA, 0xBB,   // SPS (4-byte start code)
        0, 0, 1, 0x68, 0xCC,            // PPS (3-byte start code)
        0, 0, 0, 1, 0x65, 0x01, 0x02,   // IDR slice
    ];

    [Fact]
    public void SplitNals_HandlesBothStartCodes()
    {
        var nals = NalUtil.SplitNals(Sample);
        Assert.Equal([(4, 3), (10, 2), (16, 3)], nals);
    }

    [Fact]
    public void ExtractParameterSets_ReturnsSpsAndPps()
    {
        var csd = NalUtil.ExtractParameterSets(Sample);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x67, 0xAA, 0xBB, 0, 0, 0, 1, 0x68, 0xCC }, csd);
    }

    [Fact]
    public void ExtractParameterSets_NullWithoutPps()
    {
        Assert.Null(NalUtil.ExtractParameterSets(new byte[] { 0, 0, 0, 1, 0x67, 0xAA, 0, 0, 0, 1, 0x41, 0x00 }));
    }

    [Fact]
    public void ContainsIdr()
    {
        Assert.True(NalUtil.ContainsIdr(Sample));
        Assert.False(NalUtil.ContainsIdr(new byte[] { 0, 0, 0, 1, 0x41, 0x9A }));
    }
}

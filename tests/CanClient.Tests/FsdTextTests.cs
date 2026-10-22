using System.Reflection;
using System.Text;
using CanClient.Core.Fsd;

namespace CanClient.Tests;

public class FsdTextTests
{
    [Theory]
    [InlineData(65001)]
    [InlineData(936)]
    [InlineData(54936)]
    public void ChineseAtcInfoPreservesBodyAndColons(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        const string text = "上海塔台，欢迎连飞。频率:118.100，请报告通播已收悉。";
        var bytes = Encoding.GetEncoding(codePage).GetBytes($"$CRZSSS_TWR:CCA123:ATIS:T:{text}");
        var packet = FsdProtocol.DecodePacket(bytes);
        using var fsd = new FsdClient("localhost", 6809, "CCA123", "1", "", "Test", 1, "A320", 10, null);
        string? received = null;
        fsd.AtisReceived += (_, line) => received = line;
        typeof(FsdClient).GetMethod("HandlePacket", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fsd, [packet]);
        Assert.Equal(text, received);
        Assert.DoesNotContain('\uFFFD', packet);
    }
}

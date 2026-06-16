using UnityEditorMCP.Roslyn;
using Xunit;

public class ProtocolTests
{
    [Fact]
    public void Dispatch_UnknownMethod_ReturnsError()
    {
        var rpc = new RpcServer();
        var resp = rpc.Handle("{\"id\":\"1\",\"method\":\"no_such\",\"params\":{}}");
        Assert.Contains("\"error\"", resp);
        Assert.Contains("\"1\"", resp);
    }

    [Fact]
    public void Dispatch_Ping_ReturnsResult()
    {
        var rpc = new RpcServer();
        var resp = rpc.Handle("{\"id\":\"2\",\"method\":\"ping\",\"params\":{}}");
        Assert.Contains("\"result\"", resp);
        Assert.DoesNotContain("\"error\"", resp);
    }
}

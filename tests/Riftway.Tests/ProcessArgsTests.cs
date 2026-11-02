using System.Runtime.InteropServices;
using System.Text;
using Riftway.Cef.Platform;

namespace Riftway.Tests;

public sealed unsafe class ProcessArgsTests
{
    [Fact]
    public void FromCmdline_SplitsOnNul_KeepingSpacesInsideArguments()
    {
        var args = ProcessArgs.FromCmdline("/app/Riftway Sample\0--type=renderer\0https://example.com\0"u8);

        Assert.Equal(3, args.argc);
        Assert.Equal("/app/Riftway Sample", Read(args.argv[0]));
        Assert.Equal("--type=renderer", Read(args.argv[1]));
        Assert.Equal("https://example.com", Read(args.argv[2]));
    }

    [Fact]
    public void FromCmdline_NullTerminatesArgv()
    {
        var args = ProcessArgs.FromCmdline("a\0b\0"u8);

        Assert.True(args.argv[args.argc] == null);
    }

    [Fact]
    public void FromCmdline_KeepsEmptyArguments()
    {
        var args = ProcessArgs.FromCmdline("exe\0\0last\0"u8);

        Assert.Equal(3, args.argc);
        Assert.Equal("", Read(args.argv[1]));
        Assert.Equal("last", Read(args.argv[2]));
    }

    private static string Read(byte* value) => Encoding.UTF8.GetString(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(value));
}

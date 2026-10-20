using System.Runtime.InteropServices;
using Oriel.Cef.Platform;

namespace Oriel.Tests;

// @encode(BOOL) differs by architecture: B on arm64, c on x86-64.
public sealed class ObjCEncodingTests
{
    private static readonly char Expected = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 'B' : 'c';

    [Fact]
    public void BoolIsTheArchitecturesEncoding() => Assert.Equal(Expected, ObjCEncoding.Bool);

    [Fact]
    public void EveryBStandsForBool_AndTheRestIsKept()
        => Assert.Equal($"{Expected}@:@{Expected}{{CGSize=dd}}", ObjCEncoding.Types("B@:@B{CGSize=dd}"));

    [Fact]
    public void OfIsNulTerminatedUtf8()
        => Assert.Equal([(byte)Expected, (byte)'@', (byte)':', 0], ObjCEncoding.Of("B@:"));
}

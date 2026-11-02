using System.Text;
using Riftway.Blazor;
using Xunit;

namespace Riftway.Tests;

public class BlazorConcatStreamTests
{
    private static string ReadAll(Stream stream, int bufferSize)
    {
        var output = new MemoryStream();
        var buffer = new byte[bufferSize];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) output.Write(buffer, 0, read);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static MemoryStream Of(string text) => new(Encoding.UTF8.GetBytes(text));

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4096)]
    public void Reads_the_first_stream_then_the_second(int bufferSize)
    {
        using var stream = new ConcatStream(Of("shim;"), Of("blazor"));
        Assert.Equal("shim;blazor", ReadAll(stream, bufferSize));
    }

    [Fact]
    public void An_empty_first_stream_reads_as_the_second()
    {
        using var stream = new ConcatStream(Of(""), Of("blazor"));
        Assert.Equal("blazor", ReadAll(stream, 16));
    }

    [Fact]
    public void Disposing_disposes_both_streams()
    {
        var first = Of("a");
        var second = Of("b");
        new ConcatStream(first, second).Dispose();
        Assert.False(first.CanRead);
        Assert.False(second.CanRead);
    }

    [Fact]
    public void Is_read_only_and_not_seekable()
    {
        using var stream = new ConcatStream(Of("a"), Of("b"));
        Assert.True(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.Throws<NotSupportedException>(() => stream.Write([1], 0, 1));
    }
}

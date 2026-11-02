using System.Runtime.InteropServices;
using System.Text;

namespace Riftway.Cef.Platform;

/// <summary>Objective-C type encodings for the running architecture.</summary>
public static class ObjCEncoding
{
    /// <summary><c>@encode(BOOL)</c>: C <c>bool</c> (<c>B</c>) on arm64, <c>signed char</c> (<c>c</c>) on x86-64.</summary>
    public static char Bool { get; } = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 'B' : 'c';

    /// <summary><paramref name="types"/> with each <c>B</c> standing for BOOL; only NSInvocation and method signatures
    /// read the difference, since both are one byte.</summary>
    public static string Types(string types) => types.Replace('B', Bool);

    /// <summary><see cref="Types"/> as a NUL-terminated UTF-8 string for <c>class_addMethod</c>.</summary>
    public static byte[] Of(string types) => Encoding.UTF8.GetBytes(Types(types) + "\0");
}

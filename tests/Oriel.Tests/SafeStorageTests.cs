using System.Runtime.Versioning;
using Oriel.Cef.Platform;

namespace Oriel.Tests;

/// <summary>A fact that only runs on Windows (DPAPI) and shows as skipped elsewhere, rather than passing vacuously.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows DPAPI";
    }
}

/// <summary>A theory that only runs on Windows (DPAPI) and shows as skipped elsewhere.</summary>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows DPAPI";
    }
}

[SupportedOSPlatform("windows")]
public sealed class SafeStorageTests
{
    [WindowsTheory]
    [InlineData("hello")]
    [InlineData("héllo wörld ✓ 日本語 😀")]
    [InlineData("")]
    public void RoundTrip_ReturnsTheText(string text)
    {
        Assert.Equal(text, SafeStorage.DecryptString(SafeStorage.EncryptString(text)));
    }

    [WindowsFact]
    public void IsEncryptionAvailable_IsTrueOnWindows() => Assert.True(SafeStorage.IsEncryptionAvailable());

    [WindowsFact]
    public void EncryptString_IsNotThePlainTextBytes()
    {
        var plain = System.Text.Encoding.UTF8.GetBytes("hunter2");
        Assert.NotEqual(plain, SafeStorage.EncryptString("hunter2"));
    }

    [WindowsFact]
    public void EncryptString_DiffersBetweenCalls()
    {
        Assert.NotEqual(SafeStorage.EncryptString("same"), SafeStorage.EncryptString("same"));
    }

    [WindowsFact]
    public void DecryptString_ThrowsOnATamperedCiphertext()
    {
        var encrypted = SafeStorage.EncryptString("secret");
        encrypted[^1] ^= 0xFF;
        var ex = Assert.Throws<InvalidOperationException>(() => SafeStorage.DecryptString(encrypted));
        Assert.Equal("Error while decrypting the ciphertext provided to safeStorage.decryptString.", ex.Message);
    }

    [WindowsFact]
    public void DecryptString_ThrowsOnGarbage()
    {
        Assert.Throws<InvalidOperationException>(() => SafeStorage.DecryptString([1, 2, 3]));
    }

    [WindowsFact]
    public void Unprotect_ReturnsNullForOtherEntropy()
    {
        var blob = WindowsDpapi.Protect("x"u8, "a"u8);
        Assert.Null(WindowsDpapi.Unprotect(blob, "b"u8));
        Assert.Equal("x"u8.ToArray(), WindowsDpapi.Unprotect(blob, "a"u8)!);
    }

    [WindowsFact]
    public void SafeStorage_BindsItsCiphertextToItsOwnEntropy()
    {
        var encrypted = SafeStorage.EncryptString("secret");
        Assert.Null(WindowsDpapi.Unprotect(encrypted, default));
    }

    [WindowsFact]
    public void Unprotect_ReturnsEmptyForEmptyData()
    {
        var plain = WindowsDpapi.Unprotect(WindowsDpapi.Protect(default, "a"u8), "a"u8);
        Assert.NotNull(plain);
        Assert.Empty(plain);
    }

    [Fact]
    public void NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => SafeStorage.EncryptString(null!));
        Assert.Throws<ArgumentNullException>(() => SafeStorage.DecryptString(null!));
    }

    [Fact]
    public void LinuxOnlyMembers_AreInertElsewhere()
    {
        SafeStorage.SetUsePlainTextEncryption(true);
        Assert.Equal("unknown", SafeStorage.GetSelectedStorageBackend());
    }

    [Fact]
    public void UnsupportedBackend_ReportsUnavailableAndThrows()
    {
        var backend = new UnsupportedSafeStorageBackend();
        Assert.False(backend.IsEncryptionAvailable);
        Assert.Throws<PlatformNotSupportedException>(() => backend.Encrypt([1]));
        Assert.Throws<PlatformNotSupportedException>(() => backend.Decrypt([1]));
    }
}



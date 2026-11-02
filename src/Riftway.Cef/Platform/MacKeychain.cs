using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Riftway.Cef.Platform;

/// <summary>Generic passwords in the user's Keychain through <c>SecItem</c>, built as Chromium's <c>KeychainV2</c> builds
/// them (<c>crypto/apple/keychain_v2.mm</c>), so os_crypt items are shared with Chromium-based apps of the same name.</summary>
[SupportedOSPlatform("macos")]
public static unsafe class MacKeychain
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8 = 0x08000100; // kCFStringEncodingUTF8
    private const int ErrSecItemNotFound = -25300;

    private static readonly nint s_security = NativeLibrary.Load(Security);
    private static readonly nint s_coreFoundation = NativeLibrary.Load(CoreFoundation);

    private static nint SecConstant(string name) => *(nint*)NativeLibrary.GetExport(s_security, name);

    private static nint CfConstant(string name) => NativeLibrary.GetExport(s_coreFoundation, name);

    [DllImport(Security)] private static extern int SecItemCopyMatching(nint query, nint* result);
    [DllImport(Security)] private static extern int SecItemAdd(nint attributes, nint* result);
    [DllImport(Security)] private static extern int SecItemUpdate(nint query, nint attributesToUpdate);
    [DllImport(CoreFoundation)] private static extern nint CFDictionaryCreate(nint allocator, nint* keys, nint* values, nint count, nint keyCallBacks, nint valueCallBacks);
    [DllImport(CoreFoundation)] private static extern nint CFDictionaryGetValue(nint dictionary, nint key);
    [DllImport(CoreFoundation)] private static extern nint CFStringCreateWithBytes(nint allocator, byte* bytes, nint length, uint encoding, byte isExternal);
    [DllImport(CoreFoundation)] private static extern nint CFDataCreate(nint allocator, byte* bytes, nint length);
    [DllImport(CoreFoundation)] private static extern nint CFDataGetLength(nint data);
    [DllImport(CoreFoundation)] private static extern byte* CFDataGetBytePtr(nint data);
    [DllImport(CoreFoundation)] private static extern nint CFGetTypeID(nint value);
    [DllImport(CoreFoundation)] private static extern nint CFDictionaryGetTypeID();
    [DllImport(CoreFoundation)] private static extern nint CFDataGetTypeID();
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint value);

    /// <summary><c>KeychainV2::FindGenericPassword()</c>: the password, or the <c>OSStatus</c> that stopped it.</summary>
    public static (byte[]? Password, int Status) FindGenericPassword(string service, string account)
    {
        var query = PasswordQuery(service, account);
        try
        {
            nint result = 0;
            var status = SecItemCopyMatching(query, &result);
            if (status != 0) return (null, status);
            try
            {
                if (result == 0 || CFGetTypeID(result) != CFDictionaryGetTypeID()) return ([], 0);
                var data = CFDictionaryGetValue(result, SecConstant("kSecValueData"));
                if (data == 0 || CFGetTypeID(data) != CFDataGetTypeID()) return ([], 0);
                return (new ReadOnlySpan<byte>(CFDataGetBytePtr(data), (int)CFDataGetLength(data)).ToArray(), 0);
            }
            finally
            {
                if (result != 0) CFRelease(result);
            }
        }
        finally
        {
            CFRelease(query);
        }
    }

    /// <summary><c>KeychainV2::AddGenericPassword()</c>: adds the item, accessible when unlocked, or updates its password if
    /// it already exists. The <c>OSStatus</c>.</summary>
    public static int AddGenericPassword(string service, string account, byte[] password)
    {
        var query = PasswordQuery(service, account);
        var serviceString = CfString(service);
        var accountString = CfString(account);
        nint data;
        fixed (byte* bytes = password) data = CFDataCreate(0, bytes, password.Length);
        try
        {
            var status = SecItemCopyMatching(query, null);
            if (status == ErrSecItemNotFound)
            {
                var attributes = Dictionary(
                    (SecConstant("kSecValueData"), data),
                    (SecConstant("kSecClass"), SecConstant("kSecClassGenericPassword")),
                    (SecConstant("kSecAttrAccessible"), SecConstant("kSecAttrAccessibleWhenUnlocked")),
                    (SecConstant("kSecAttrService"), serviceString),
                    (SecConstant("kSecAttrAccount"), accountString));
                status = SecItemAdd(attributes, null);
                CFRelease(attributes);
            }
            else if (status == 0)
            {
                var update = Dictionary((SecConstant("kSecValueData"), data));
                status = SecItemUpdate(query, update);
                CFRelease(update);
            }
            return status;
        }
        finally
        {
            CFRelease(data);
            CFRelease(serviceString);
            CFRelease(accountString);
            CFRelease(query);
        }
    }

    // MakeGenericPasswordQuery: the first match's data and attributes.
    private static nint PasswordQuery(string service, string account)
    {
        var serviceString = CfString(service);
        var accountString = CfString(account);
        var trueValue = *(nint*)CfConstant("kCFBooleanTrue");
        var query = Dictionary(
            (SecConstant("kSecClass"), SecConstant("kSecClassGenericPassword")),
            (SecConstant("kSecAttrService"), serviceString),
            (SecConstant("kSecAttrAccount"), accountString),
            (SecConstant("kSecMatchLimit"), SecConstant("kSecMatchLimitOne")),
            (SecConstant("kSecReturnData"), trueValue),
            (SecConstant("kSecReturnAttributes"), trueValue));
        CFRelease(serviceString);
        CFRelease(accountString);
        return query;
    }

    private static nint Dictionary(params ReadOnlySpan<(nint Key, nint Value)> entries)
    {
        var keys = stackalloc nint[entries.Length];
        var values = stackalloc nint[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            keys[i] = entries[i].Key;
            values[i] = entries[i].Value;
        }
        return CFDictionaryCreate(0, keys, values, entries.Length,
            CfConstant("kCFTypeDictionaryKeyCallBacks"), CfConstant("kCFTypeDictionaryValueCallBacks"));
    }

    private static nint CfString(string text)
    {
        var utf8 = Encoding.UTF8.GetBytes(text);
        fixed (byte* bytes = utf8) return CFStringCreateWithBytes(0, bytes, utf8.Length, Utf8, 0);
    }
}

using System.Runtime.Versioning;
using System.Text;
using Oriel;

// End-to-end safeStorage check on Linux: runs Electron's API against the real store selection, libsecret and CEF's
// app lifecycle. ORIEL_SS_EXPECT names the store the environment should select ("basic_text" by default,
// "gnome_libsecret" with a running Secret Service). ORIEL_SS_OUT, if set, receives the v11 ciphertexts in hex so the
// harness can decrypt them independently; ORIEL_SS_ASYNC_FIRST=1 starts with the asynchronous API. The exit code is the number of failures.
[assembly: SupportedOSPlatform("linux")]

var expected = Environment.GetEnvironmentVariable("ORIEL_SS_EXPECT") ?? "basic_text";
var outFile = Environment.GetEnvironmentVariable("ORIEL_SS_OUT");
const string Text = "Oriel ✓ safeStorage";
const string NotReady = "safeStorage cannot be used before app is ready";
const string EncryptError = "Error while encrypting the text provided to safeStorage.encryptString.";
const string DecryptError = "Error while decrypting the ciphertext provided to safeStorage.decryptString.";
// openssl: PBKDF2-SHA1("peanuts", "saltysalt", 1), then AES-128-CBC of Text with an IV of 16 spaces.
byte[] v10Text = [.. "v10"u8, .. Convert.FromHexString("46189fb2ec86b3f6b3e4f5c082f9431f804270f56dc9988d0d6b7c807fdce6ee")];

var failures = 0;
void Check(string name, bool pass, string detail = "")
{
    if (!pass) failures++;
    Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? $"  [{detail}]" : "")}");
}

string? Throws(Func<object?> action)
{
    try
    {
        action();
        return null;
    }
    catch (InvalidOperationException e)
    {
        return e.Message;
    }
}

async Task<string?> Rejects(Func<Task> action)
{
    try
    {
        await action();
        return null;
    }
    catch (InvalidOperationException e)
    {
        return e.Message;
    }
}

var app = OrielApp.Create("Oriel.SafeStorageTest");

Check("before ready: unavailable", !SafeStorage.IsEncryptionAvailable());
Check("before ready: encryptString throws", Throws(() => SafeStorage.EncryptString("x")) == NotReady);
Check("before ready: backend unknown", SafeStorage.GetSelectedStorageBackend() == "unknown");
Check("before ready: async unavailable", !SafeStorage.IsAsyncEncryptionAvailableAsync().Result);

app.Ready += async (_, _) =>
{
    try
    {
        var backend = SafeStorage.GetSelectedStorageBackend();
        Check("selected backend", backend == expected, backend);
        var lines = new List<string>();
        // ORIEL_SS_ASYNC_FIRST: the asynchronous provider meets an empty keyring and creates the item itself.
        if (Environment.GetEnvironmentVariable("ORIEL_SS_ASYNC_FIRST") == "1")
        {
            var first = await SafeStorage.EncryptStringAsync(Text);
            Check("async first: v11", first.AsSpan().StartsWith("v11"u8));
            lines.Add("async-first " + Convert.ToHexString(first));
        }

        if (expected == "basic_text")
        {
            Check("basic_text: unavailable without plain text", !SafeStorage.IsEncryptionAvailable());
            Check("basic_text: encryptString refuses", Throws(() => SafeStorage.EncryptString("x")) == EncryptError + " Encryption is not available.");
            SafeStorage.SetUsePlainTextEncryption(true);
            Check("basic_text: available with plain text", SafeStorage.IsEncryptionAvailable());
            var encrypted = SafeStorage.EncryptString(Text);
            Check("basic_text: v10 matches OpenSSL", encrypted.SequenceEqual(v10Text), Convert.ToHexString(encrypted));
            Check("basic_text: decrypts OpenSSL's", SafeStorage.DecryptString(v10Text) == Text);
        }
        else
        {
            Check("secret service: available", SafeStorage.IsEncryptionAvailable());
            var encrypted = SafeStorage.EncryptString(Text);
            Check("secret service: v11", encrypted.AsSpan().StartsWith("v11"u8), Encoding.ASCII.GetString(encrypted, 0, 3));
            Check("secret service: round trip", SafeStorage.DecryptString(encrypted) == Text);
            Check("secret service: still decrypts v10", SafeStorage.DecryptString(v10Text) == Text);
            lines.Add("sync " + Convert.ToHexString(encrypted));
        }

        Check("decrypt: unversioned", Throws(() => SafeStorage.DecryptString("abc"u8.ToArray())) == DecryptError + " Ciphertext does not appear to be encrypted.");
        var tampered = v10Text.ToArray();
        tampered[^1] ^= 1;
        Check("decrypt: tampered", Throws(() => SafeStorage.DecryptString(tampered)) == DecryptError);
        Check("decrypt: empty", SafeStorage.DecryptString([]) == "");

        Check("async: available", await SafeStorage.IsAsyncEncryptionAvailableAsync());
        var asyncEncrypted = await SafeStorage.EncryptStringAsync(Text);
        var asyncTag = expected == "basic_text" ? "v10" : "v11";
        Check("async: tag", Encoding.ASCII.GetString(asyncEncrypted, 0, 3) == asyncTag, Encoding.ASCII.GetString(asyncEncrypted, 0, 3));
        Check("async: round trip", await SafeStorage.DecryptStringAsync(asyncEncrypted) == new SafeStorageDecryptResult(false, Text));
        Check("async: v10 re-encrypt flag", await SafeStorage.DecryptStringAsync(v10Text) == new SafeStorageDecryptResult(asyncTag != "v10", Text));
        Check("async: garbage", await Rejects(() => SafeStorage.DecryptStringAsync([1, 2, 3])) == "Error while decrypting the ciphertext provided to safeStorage.decryptStringAsync.");
        if (asyncTag == "v11") lines.Add("async " + Convert.ToHexString(asyncEncrypted));
        if (outFile is not null) File.WriteAllLines(outFile, lines);
    }
    catch (Exception e)
    {
        Check("no exception", false, e.ToString());
    }
    app.Quit();
};

app.Run();
Console.WriteLine($"RESULT: {failures} failure(s)");
return failures;

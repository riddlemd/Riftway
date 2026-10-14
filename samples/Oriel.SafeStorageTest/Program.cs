using System.Runtime.Versioning;
using System.Text;
using Oriel;

// End-to-end safeStorage check on Linux: runs Electron's API against the real store selection, libsecret and CEF's
// app lifecycle. ORIEL_SS_EXPECT names the store the environment should select ("basic_text" by default,
// "gnome_libsecret" with a running Secret Service). ORIEL_SS_OUT, if set, receives the v11 ciphertexts in hex so the
// harness can decrypt them independently; ORIEL_SS_ASYNC_FIRST=1 starts with the asynchronous API; ORIEL_SS_SILENT=1 means the
// Secret Service's default collection never answers (eng/docker/with-secret-service.sh silent). The exit code is the number of failures.
[assembly: SupportedOSPlatform("linux")]

if (!OperatingSystem.IsLinux())
{
    Console.WriteLine("RESULT: Linux only");
    return 0;
}

var expected = Environment.GetEnvironmentVariable("ORIEL_SS_EXPECT") ?? "basic_text";
var silent = Environment.GetEnvironmentVariable("ORIEL_SS_SILENT") == "1";
// Chromium's D-Bus default timeout, 25 s, plus room for the libsecret calls the guard skips.
var guardLimit = TimeSpan.FromSeconds(40);
var callLog = Environment.GetEnvironmentVariable("ORIEL_SS_CALL_LOG");
// The fake service's calls since the last look; the guard asks only ReadAlias and the Label.
var seenCalls = 0;
string[] NewCalls()
{
    var all = callLog is null || !File.Exists(callLog) ? [] : File.ReadAllLines(callLog);
    var fresh = all[seenCalls..];
    seenCalls = all.Length;
    return fresh;
}
bool OnlyGuardCalls(string[] calls) =>
    calls.Length > 0 && calls.All(c => c is "org.freedesktop.Secret.Service.ReadAlias" or "org.freedesktop.DBus.Properties.Get");
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
        if (Environment.GetEnvironmentVariable("ORIEL_SS_ASYNC_FIRST") == "1" && !silent)
        {
            var first = await SafeStorage.EncryptStringAsync(Text);
            Check("async first: v11", first.AsSpan().StartsWith("v11"u8));
            lines.Add("async-first " + Convert.ToHexString(first));
        }

        if (silent)
        {
            // Chromium's CanUseLibsecret: a default collection that does not answer means no libsecret, so no key.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var available = SafeStorage.IsEncryptionAvailable();
            Check("silent keyring: the synchronous API is unavailable, within the D-Bus timeout", !available && clock.Elapsed < guardLimit, $"{available} after {clock.Elapsed.TotalSeconds:F1} s");
            Check("silent keyring: encryptString refuses", Throws(() => SafeStorage.EncryptString("x")) == EncryptError + " Encryption is not available.");
            var syncCalls = NewCalls();
            Check("silent keyring: the synchronous guard stops before libsecret asks the service anything", OnlyGuardCalls(syncCalls), string.Join(",", syncCalls));
        }
        else if (expected == "basic_text")
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

        if (silent)
            Check("silent keyring: decryptString refuses", Throws(() => SafeStorage.DecryptString(v10Text)) == DecryptError + " Decryption is not available.");
        else
        {
            Check("decrypt: unversioned", Throws(() => SafeStorage.DecryptString("abc"u8.ToArray())) == DecryptError + " Ciphertext does not appear to be encrypted.");
            var tampered = v10Text.ToArray();
            tampered[^1] ^= 1;
            Check("decrypt: tampered", Throws(() => SafeStorage.DecryptString(tampered)) == DecryptError);
            Check("decrypt: empty", SafeStorage.DecryptString([]) == "");
        }

        // The first asynchronous call loads the key, the guard included.
        var asyncClock = System.Diagnostics.Stopwatch.StartNew();
        Check("async: available", await SafeStorage.IsAsyncEncryptionAvailableAsync());
        var asyncEncrypted = await SafeStorage.EncryptStringAsync(Text);
        // FreedesktopSecretKeyProvider's kGnomeKeyringDeadlock leaves only the "v10" key.
        var asyncTag = expected == "basic_text" || silent ? "v10" : "v11";
        Check("async: tag", Encoding.ASCII.GetString(asyncEncrypted, 0, 3) == asyncTag, Encoding.ASCII.GetString(asyncEncrypted, 0, 3));
        if (silent)
        {
            Check("silent keyring: the asynchronous API falls back within the D-Bus timeout", asyncClock.Elapsed < guardLimit, $"{asyncClock.Elapsed.TotalSeconds:F1} s");
            var asyncCalls = NewCalls();
            Check("silent keyring: the asynchronous guard stops before libsecret asks the service anything", OnlyGuardCalls(asyncCalls), string.Join(",", asyncCalls));
        }
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

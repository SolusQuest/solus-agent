using System.Diagnostics;
using System.Text;

namespace PackageConsumers.LiveSmoke;

internal sealed record Selection(bool Live, string? KeyFile = null);
internal static class LiveGate
{
    internal static Selection Select(string[] args, Func<string, string?> environment)
    {
        if (args.Length == 0) return new(false);
        Check.That(args[0] == "smoke-live" && args.Contains("--live", StringComparer.Ordinal), Failure.OptIn);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index++)
        {
            var key = args[index];
            Check.That(key is "--live" or "--key-file" or "--profile" && !values.ContainsKey(key), Failure.Arguments);
            if (key == "--live") values.Add(key, "true");
            else
            {
                Check.That(++index < args.Length && !args[index].StartsWith("--", StringComparison.Ordinal), Failure.Arguments);
                values.Add(key, args[index]);
            }
        }
        Check.That(values.GetValueOrDefault("--profile", Fixture.Profile) == Fixture.Profile, Failure.Profile);
        Check.That(string.IsNullOrEmpty(environment("CI")) && string.IsNullOrEmpty(environment("GITHUB_ACTIONS")), Failure.Ci);
        Check.That(values.TryGetValue("--key-file", out var path) && Path.IsPathFullyQualified(path), Failure.CredentialSource);
        return new(true, path);
    }

    internal static string ReadCredential(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) throw new SmokeFailure(Failure.CredentialSource);
            Check.That(Path.IsPathFullyQualified(path), Failure.CredentialSource);
            var info = new FileInfo(path);
            Check.That(info.Exists && info.Length is > 0 and <= 4096 && (info.Attributes & FileAttributes.ReparsePoint) == 0, Failure.CredentialSource);
            Check.That(info.LinkTarget is null, Failure.CredentialSource);
            var mode = File.GetUnixFileMode(path);
            Check.That((mode & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) == 0 && mode.HasFlag(UnixFileMode.UserRead), Failure.CredentialSource);
            CredentialSource.Admit(info.FullName);
            // Recheck using the open handle before any allocation/read. The explicitly selected source passed private-file and Git/external-source admission.
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Check.That(input.Length is > 0 and <= 4096, Failure.CredentialSource);
            var bytes = new byte[input.Length]; input.ReadExactly(bytes);
            return ValidateCredential(bytes);
        }
        catch (SmokeFailure) { throw; }
        catch { throw new SmokeFailure(Failure.CredentialSource); }
    }

    internal static string ValidateCredential(byte[] bytes)
    {
        Check.That(bytes.Length is > 0 and <= 4096, Failure.Credential);
        string credential;
        try { credential = new UTF8Encoding(false, true).GetString(bytes).TrimEnd('\r', '\n'); }
        catch { throw new SmokeFailure(Failure.Credential); }
        Check.That(credential.Length is > 0 and <= 4096 && credential.All(value => value is >= '!' and <= '~'), Failure.Credential);
        return credential;
    }

    // The same entry policy is exercised with loader/transport spies; offline cannot reach either dependency.
    internal static Send ChooseTransport(string[] args, Func<string, string?> environment, Func<string, string> load,
        Func<string, Send> live, Send offline)
    {
        var selection = Select(args, environment);
        return selection.Live ? live(load(selection.KeyFile!)) : offline;
    }
}

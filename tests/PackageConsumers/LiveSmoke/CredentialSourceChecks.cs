using System.Diagnostics;

namespace PackageConsumers.LiveSmoke;

internal static class CredentialSourceChecks
{
    internal static void Run()
    {
        if (OperatingSystem.IsWindows()) throw new SmokeFailure(Failure.CredentialSource);
        var root = Path.Combine(Path.GetTempPath(), "solus-key-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var key = Path.Combine(root, "synthetic.key");
        void Denied(string path)
        {
            var factories = 0;
            try
            {
                _ = LiveGate.ChooseTransport(["smoke-live", "--live", "--key-file", path], _ => null, LiveGate.ReadCredential,
                    _ => { factories++; throw new SmokeFailure(Failure.Transport); }, new FakeTransport().SendAsync);
            }
            catch (SmokeFailure error) when (error.Code == Failure.CredentialSource) { Check.That(factories == 0); return; }
            throw new SmokeFailure(Failure.CredentialSource);
        }
        try
        {
            File.WriteAllText(key, Fixture.LiveCredential); File.SetUnixFileMode(key, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Check.That(LiveGate.ReadCredential(key) == Fixture.LiveCredential);
            var bin = Path.Combine(root, "bin"); Directory.CreateDirectory(bin);
            var executable = Path.Combine(bin, "git"); var originalPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("PATH", Path.Combine(root, "absent")); Denied(key);
                Environment.SetEnvironmentVariable("PATH", bin);
                foreach (var script in new[] { "echo 'fatal: permission denied' >&2; exit 128", "printf '%09000d' 0; exit 128", "exec /bin/sleep 30" })
                {
                    File.WriteAllText(executable, "#!/bin/sh\n" + script + "\n");
                    File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); Denied(key);
                }
            }
            finally { Environment.SetEnvironmentVariable("PATH", originalPath); }
            var trace = Path.Combine(root, "trace"); var priorTrace = Environment.GetEnvironmentVariable("GIT_TRACE2_EVENT");
            try
            {
                Environment.SetEnvironmentVariable("GIT_TRACE2_EVENT", trace);
                Check.That(LiveGate.ReadCredential(key) == Fixture.LiveCredential && !File.Exists(trace));
            }
            finally { Environment.SetEnvironmentVariable("GIT_TRACE2_EVENT", priorTrace); }
            var priorOverride = Environment.GetEnvironmentVariable("GIT_DIR");
            try { Environment.SetEnvironmentVariable("GIT_DIR", Path.Combine(root, "absent")); Denied(key); }
            finally { Environment.SetEnvironmentVariable("GIT_DIR", priorOverride); }
            Denied("relative.key"); Denied(root);
            var link = Path.Combine(root, "link"); File.CreateSymbolicLink(link, key); Denied(link);
            File.SetUnixFileMode(key, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead); Denied(key);
            File.SetUnixFileMode(key, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.WriteAllText(key, ""); Denied(key); File.WriteAllText(key, new string('x', 4097)); Denied(key);
            File.WriteAllText(key, Fixture.LiveCredential);
            using (var git = Process.Start(new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, ArgumentList = { "init", "--quiet", "--template=", root } })!)
            { git.WaitForExit(); Check.That(git.ExitCode == 0); }
            Denied(key);
            File.WriteAllText(Path.Combine(root, ".gitignore"), "synthetic.key\n");
            Check.That(LiveGate.ReadCredential(key) == Fixture.LiveCredential);
            File.WriteAllText(Path.Combine(root, ".git", "config"), "[broken\n"); Denied(key);
            Directory.Delete(Path.Combine(root, ".git"), true);
            File.WriteAllText(Path.Combine(root, ".git"), "gitdir: absent-directory\n"); Denied(key);
        }
        finally { Directory.Delete(root, true); }
    }
}

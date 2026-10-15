using System.Diagnostics;

namespace PackageConsumers.LiveSmoke;

internal static class CredentialSource
{
    internal static void Admit(string path)
    {
        string[] overrides = ["GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE", "GIT_CEILING_DIRECTORIES",
            "GIT_DISCOVERY_ACROSS_FILESYSTEM", "GIT_CONFIG", "GIT_CONFIG_GLOBAL", "GIT_CONFIG_SYSTEM", "GIT_CONFIG_PARAMETERS", "GIT_CONFIG_COUNT"];
        Check.That(overrides.All(name => Environment.GetEnvironmentVariable(name) is null), Failure.CredentialSource);
        var parent = Path.GetPathRoot(path)!;
        foreach (var component in Path.GetDirectoryName(path)![parent.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = new DirectoryInfo(Path.Combine(parent, component));
            Check.That((directory.Attributes & FileAttributes.Directory) != 0, Failure.CredentialSource);
            parent = directory.ResolveLinkTarget(true)?.FullName ?? directory.FullName;
        }
        var canonical = Path.Combine(parent, Path.GetFileName(path));
        var root = Git(parent, "rev-parse", "--show-toplevel");
        if (root.Exit == 0)
        {
            Check.That(root.Error.Length == 0 && Path.IsPathFullyQualified(root.Output.TrimEnd('\r', '\n'))
                && Directory.Exists(root.Output.TrimEnd('\r', '\n')), Failure.CredentialSource);
            var ignored = Git(parent, "check-ignore", "--quiet", "--", canonical);
            Check.That(ignored.Exit == 0 && ignored.Output.Length == 0 && ignored.Error.Length == 0, Failure.CredentialSource);
            return;
        }
        Check.That(root.Exit == 128 && root.Output.Length == 0
            && root.Error == "fatal: not a git repository (or any of the parent directories): .git\n", Failure.CredentialSource);
        for (var directory = new DirectoryInfo(parent); directory is not null; directory = directory.Parent)
        {
            try { _ = File.GetAttributes(Path.Combine(directory.FullName, ".git")); throw new SmokeFailure(Failure.CredentialSource); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static (int Exit, string Output, string Error) Git(string parent, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-C"); start.ArgumentList.Add(parent);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("GIT_TRACE", StringComparison.Ordinal)).ToArray()) start.Environment.Remove(name);
        foreach (var name in new[] { "GIT_TRACE", "GIT_TRACE2", "GIT_TRACE2_EVENT", "GIT_TRACE2_PERF" }) start.Environment[name] = "0";
        start.Environment["LC_ALL"] = "C"; start.Environment["LANG"] = "C"; start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var process = Process.Start(start) ?? throw new SmokeFailure(Failure.CredentialSource);
        var output = ReadAsync(process.StandardOutput, deadline.Token); var error = ReadAsync(process.StandardError, deadline.Token);
        try
        {
            Task.WhenAll(output, error, process.WaitForExitAsync(deadline.Token)).GetAwaiter().GetResult();
            return (process.ExitCode, output.Result, error.Result);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
        }
    }
    private static async Task<string> ReadAsync(TextReader reader, CancellationToken token)
    {
        var result = new System.Text.StringBuilder(); var buffer = new char[512]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            Check.That(result.Length + count <= 8192, Failure.CredentialSource); result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}

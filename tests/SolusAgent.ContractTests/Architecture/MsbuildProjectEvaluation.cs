using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SolusAgent.ContractTests.Architecture;

/// <summary>Runs native dotnet msbuild evaluation with a bounded timeout and useful failure diagnostics.</summary>
internal static class MsbuildProjectEvaluation
{
    private static readonly TimeSpan EvaluationTimeout = TimeSpan.FromMinutes(2);

    public static ProjectEvaluation Evaluate(string projectPath)
    {
        var canonicalProjectPath = ProjectBoundaries.Canonicalize(projectPath);
        if (!File.Exists(canonicalProjectPath))
        {
            throw new InvalidOperationException($"Cannot evaluate missing project '{canonicalProjectPath}'.");
        }

        return ProjectEvaluation.FromJson(canonicalProjectPath, RunEvaluation(canonicalProjectPath));
    }

    private static string RunEvaluation(string canonicalProjectPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = RepositoryLayout.Root,
        };

        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(canonicalProjectPath);
        startInfo.ArgumentList.Add("-nologo");
        startInfo.ArgumentList.Add("-property:Configuration=Release");
        startInfo.ArgumentList.Add("-getItem:ProjectReference,PackageReference,Compile");
        startInfo.ArgumentList.Add("-getProperty:TargetFramework,PublishAot,PublishTrimmed");

        using var process = new Process { StartInfo = startInfo };
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        process.OutputDataReceived += (_, args) => AppendLine(standardOutput, args.Data);
        process.ErrorDataReceived += (_, args) => AppendLine(standardError, args.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit((int)EvaluationTimeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new InvalidOperationException(
                $"MSBuild evaluation timed out after {EvaluationTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds "
                + $"for '{canonicalProjectPath}'. Standard error: '{standardError}' Standard output: '{standardOutput}'");
        }

        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"MSBuild evaluation exited with code {process.ExitCode.ToString(CultureInfo.InvariantCulture)} "
                + $"for '{canonicalProjectPath}'. Standard error: '{standardError}' Standard output: '{standardOutput}'");
        }

        return standardOutput.ToString();
    }

    private static void AppendLine(StringBuilder builder, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (builder)
        {
            builder.AppendLine(line);
        }
    }
}

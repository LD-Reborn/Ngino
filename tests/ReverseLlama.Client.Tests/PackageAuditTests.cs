using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace ReverseLlama.Client.Tests;

public sealed class PackageAuditTests
{
    [Fact]
    public async Task Solution_HasNoKnownVulnerablePackages()
    {
        var solutionPath = FindSolutionPath();
        var result = await RunDotnetPackageAuditAsync(solutionPath);

        Assert.True(
            result.ExitCode == 0,
            $"Package audit command failed with exit code {result.ExitCode}.{Environment.NewLine}{result.Output}{result.Error}");

        using var document = JsonDocument.Parse(result.Output);
        var findings = new List<string>();
        CollectVulnerablePackages(document.RootElement, findings);

        Assert.True(
            findings.Count == 0,
            "Known vulnerable packages were found:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, findings));
    }

    private static string FindSolutionPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var solutionPath = Path.Combine(directory.FullName, "ReverseLlama.sln");
            if (File.Exists(solutionPath))
            {
                return solutionPath;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find ReverseLlama.sln from the test output directory.");
    }

    private static async Task<CommandResult> RunDotnetPackageAuditAsync(string solutionPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(solutionPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add("list");
        startInfo.ArgumentList.Add(solutionPath);
        startInfo.ArgumentList.Add("package");
        startInfo.ArgumentList.Add("--vulnerable");
        startInfo.ArgumentList.Add("--include-transitive");
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("json");
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start dotnet package audit.");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort; the assertion below will fail with the timeout message.
            }

            throw new TimeoutException("dotnet package audit did not finish within 2 minutes.");
        }

        return new CommandResult(
            process.ExitCode,
            await outputTask,
            await errorTask);
    }

    private static void CollectVulnerablePackages(JsonElement element, List<string> findings)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("vulnerabilities", out var vulnerabilities)
                && vulnerabilities.ValueKind == JsonValueKind.Array
                && vulnerabilities.GetArrayLength() > 0)
            {
                findings.Add(DescribePackageFinding(element, vulnerabilities));
            }

            foreach (var property in element.EnumerateObject())
            {
                CollectVulnerablePackages(property.Value, findings);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectVulnerablePackages(item, findings);
            }
        }
    }

    private static string DescribePackageFinding(JsonElement package, JsonElement vulnerabilities)
    {
        var id = package.TryGetProperty("id", out var idElement)
            ? idElement.GetString()
            : "<unknown package>";
        var version = package.TryGetProperty("resolvedVersion", out var versionElement)
            ? versionElement.GetString()
            : "<unknown version>";

        var advisories = vulnerabilities
            .EnumerateArray()
            .Select(vulnerability => DescribeVulnerability(vulnerability))
            .ToArray();

        return $"- {id} {version}: {string.Join(", ", advisories)}";
    }

    private static string DescribeVulnerability(JsonElement vulnerability)
    {
        var severity = vulnerability.TryGetProperty("severity", out var severityElement)
            ? severityElement.GetString()
            : "unknown severity";
        var advisoryUrl = vulnerability.TryGetProperty("advisoryUrl", out var advisoryElement)
            ? advisoryElement.GetString()
            : "unknown advisory";

        return $"{severity} {advisoryUrl}";
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error);
}

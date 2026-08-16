using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ngino.Client;

internal sealed partial class LlamaCppManager : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int DefaultBasePort = 8081;
    private const string NginoContainerLabel = "ngino-llamacpp";
    private static readonly TimeSpan DockerTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ContainerStartTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultFallbackCooldown = TimeSpan.FromMinutes(3);

    private readonly string _blobsPath;
    private readonly string _manifestsPath;
    private readonly string _dockerImage;
    private readonly int _basePort;
    private readonly int? _parallel;
    private readonly TimeSpan _fallbackCooldown;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, int> _modelPorts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, byte> _reservedPorts = new();
    private readonly ConcurrentDictionary<string, DateTime> _fallbackModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _modelStartLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _portAllocationLock = new();

    public LlamaCppManager(
        string ollamaModelsPath,
        string? dockerImage,
        int? basePort,
        ILogger? logger = null,
        TimeSpan fallbackCooldown = default,
        int? parallel = null)
    {
        _manifestsPath = Path.Combine(ollamaModelsPath, "manifests");
        _blobsPath = Path.Combine(ollamaModelsPath, "blobs");
        _dockerImage = dockerImage ?? GetDefaultDockerImage();
        _basePort = basePort ?? DefaultBasePort;
        _fallbackCooldown = fallbackCooldown > TimeSpan.Zero ? fallbackCooldown : DefaultFallbackCooldown;
        _parallel = parallel is > 0 ? parallel : null;
        _logger = logger ?? NullLogger<LlamaCppManager>.Instance;
    }

    public string DockerImage => _dockerImage;

    public List<LlamaCppModel> DiscoverModels()
    {
        var models = new List<LlamaCppModel>();

        if (!Directory.Exists(_manifestsPath))
        {
            _logger.LogWarning("Ollama manifests path not found: {ManifestsPath}", _manifestsPath);
            return models;
        }

        foreach (var manifestPath in Directory.EnumerateFiles(_manifestsPath, "*", SearchOption.AllDirectories))
        {
            try
            {
                var model = ParseManifest(manifestPath);
                if (model is not null)
                {
                    models.Add(model);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to parse manifest: {ManifestPath}", manifestPath);
            }
        }

        return models;
    }

    public List<LlamaCppModel> DiscoverModelsWithBlob()
    {
        return DiscoverModels().Where(m => File.Exists(m.BlobPath)).ToList();
    }

    public bool IsModelActive(string ollamaModelName)
    {
        return _modelPorts.ContainsKey(ollamaModelName);
    }

    public void MarkModelAsFallback(string ollamaModelName)
    {
        _fallbackModels[ollamaModelName] = DateTime.UtcNow.Add(_fallbackCooldown);
        _logger.LogWarning(
            "Model {Model} will fall back to the Ollama upstream for {Cooldown} before llama.cpp is retried.",
            ollamaModelName, _fallbackCooldown);
    }

    public void MarkModelAsPermanentFallback(string ollamaModelName)
    {
        _fallbackModels[ollamaModelName] = DateTime.MaxValue;
        _logger.LogError(
            "Model {Model} exited its llama.cpp container before becoming ready. It will fall back to the Ollama upstream until it is unloaded.",
            ollamaModelName);
    }

    public bool IsModelOnFallback(string ollamaModelName)
    {
        if (_fallbackModels.TryGetValue(ollamaModelName, out var expiresAt))
        {
            if (expiresAt > DateTime.UtcNow)
            {
                return true;
            }

            _fallbackModels.TryRemove(ollamaModelName, out _);
        }

        return false;
    }

    public void ClearModelFallback(string ollamaModelName)
    {
        if (_fallbackModels.TryRemove(ollamaModelName, out _))
        {
            _logger.LogInformation("Cleared llama.cpp fallback marker for model {Model}.", ollamaModelName);
        }
    }

    public async Task<bool> IsContainerRunningAsync(string ollamaModelName)
    {
        if (!_modelPorts.ContainsKey(ollamaModelName))
        {
            return false;
        }

        var containerName = SanitizeContainerName($"ngino-llamacpp-{ollamaModelName}");
        var (exitCode, output) = await RunDockerWithOutputAsync(
            ["ps", "--filter", $"name=^{containerName}$", "--format", "{{.ID}}"],
            CancellationToken.None);

        var running = exitCode == 0 && !string.IsNullOrWhiteSpace(output);
        if (!running)
        {
            _logger.LogWarning(
                "llama.cpp container {ContainerName} is no longer running. Invalidating cached port for {Model}.",
                containerName, ollamaModelName);
            RemoveModelPort(ollamaModelName);
        }

        return running;
    }

    public bool RemoveModelMapping(string ollamaModelName)
    {
        if (RemoveModelPort(ollamaModelName))
        {
            _logger.LogWarning(
                "Removed stale llama.cpp port mapping for {Model}.",
                ollamaModelName);
            return true;
        }

        return false;
    }

    public Uri? GetUpstream(string ollamaModelName)
    {
        if (_modelPorts.TryGetValue(ollamaModelName, out var port))
        {
            return new Uri($"http://localhost:{port}");
        }

        return null;
    }

    public async Task<bool> StartModelContainerAsync(LlamaCppModel model, CancellationToken cancellationToken)
    {
        var ollamaName = model.OllamaName;
        if (string.IsNullOrWhiteSpace(ollamaName))
        {
            _logger.LogWarning("Cannot start container: model has no Ollama name");
            return false;
        }

        var startLock = _modelStartLocks.GetOrAdd(ollamaName, static _ => new SemaphoreSlim(1, 1));
        await startLock.WaitAsync(cancellationToken);
        try
        {
            return await StartModelContainerCoreAsync(model, cancellationToken);
        }
        finally
        {
            startLock.Release();
        }
    }

    private async Task<bool> StartModelContainerCoreAsync(LlamaCppModel model, CancellationToken cancellationToken)
    {
        var ollamaName = model.OllamaName;
        if (string.IsNullOrWhiteSpace(ollamaName))
        {
            return false;
        }

        if (_modelPorts.ContainsKey(ollamaName))
        {
            _logger.LogInformation("Model {Model} already has a running container", ollamaName);
            return true;
        }

        if (IsModelOnFallback(ollamaName))
        {
            _logger.LogInformation(
                "Model {Model} previously failed to load via llama.cpp. Skipping container start.",
                ollamaName);
            return false;
        }

        if (!File.Exists(model.BlobPath))
        {
            _logger.LogError("Model blob not found: {BlobPath}", model.BlobPath);
            MarkModelAsFallback(ollamaName);
            return false;
        }

        var port = FindAvailablePort();
        var containerName = SanitizeContainerName($"ngino-llamacpp-{ollamaName}");

        try
        {
            var existingPort = await FindExistingContainerPortAsync(containerName);
            if (existingPort.HasValue)
            {
                _logger.LogInformation(
                    "Reusing existing container for {Model} on port {Port}", ollamaName, existingPort.Value);
                _modelPorts[ollamaName] = existingPort.Value;
                return true;
            }

            await RunDockerAsync(["rm", "-f", containerName], CancellationToken.None);

            var args = BuildDockerRunArgs(containerName, model, port);
            _logger.LogInformation(
                "Starting llama.cpp container for {Model} on port {Port}: docker {Args}",
                ollamaName, port, string.Join(" ", args));

            var (exitCode, output) = await RunDockerWithOutputAsync(args, cancellationToken);
            if (exitCode != 0)
            {
                _logger.LogError(
                    "Failed to start llama.cpp container for {Model}, exit code: {ExitCode}, output: {Output}",
                    ollamaName, exitCode, output);
                MarkModelAsFallback(ollamaName);
                return false;
            }

            _logger.LogInformation(
                "llama.cpp container for {Model} started on port {Port}. Waiting for it to become ready...",
                ollamaName, port);

            var result = await WaitForServerReadyAsync("localhost", port, containerName, cancellationToken);
            if (result != ContainerStartResult.Ready)
            {
                if (result == ContainerStartResult.ContainerExited)
                {
                    MarkModelAsPermanentFallback(ollamaName);
                }
                else
                {
                    MarkModelAsFallback(ollamaName);
                }

                _logger.LogError(
                    "llama.cpp container for {Model} did not become ready on port {Port} within {Timeout} ({Result}). Stopping it.",
                    ollamaName, port, ContainerStartTimeout, result);

                try
                {
                    await RunDockerAsync(["stop", "--time", "10", containerName], CancellationToken.None);
                    await RunDockerAsync(["rm", "-f", containerName], CancellationToken.None);
                }
                catch (Exception cleanupException)
                {
                    _logger.LogWarning(cleanupException, "Failed to clean up container {ContainerName}", containerName);
                }

                return false;
            }

            _modelPorts[ollamaName] = port;
            _logger.LogInformation("llama.cpp container for {Model} is ready on port {Port}.", ollamaName, port);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start llama.cpp container for {Model}", ollamaName);
            MarkModelAsFallback(ollamaName);
            return false;
        }
        finally
        {
            ReleaseReservedPort(port);
        }
    }

    public async Task<bool> StopModelContainerAsync(string ollamaModelName, CancellationToken cancellationToken)
    {
        if (!RemoveModelPort(ollamaModelName))
        {
            _logger.LogWarning("No running container found for model {Model}", ollamaModelName);
            return false;
        }

        var containerName = SanitizeContainerName($"ngino-llamacpp-{ollamaModelName}");

        _logger.LogInformation("Stopping llama.cpp container {ContainerName}", containerName);

        try
        {
            await RunDockerAsync(["stop", "--time", "10", containerName], cancellationToken);
            await RunDockerAsync(["rm", "-f", containerName], cancellationToken);
            ClearModelFallback(ollamaModelName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop container {ContainerName}", containerName);
            return false;
        }
    }

    public async Task StopAllContainersAsync()
    {
        _logger.LogInformation("Stopping all llama.cpp containers...");

        try
        {
            var (exitCode, output) = await RunDockerWithOutputAsync(
                ["ps", "-q", "--filter", $"label={NginoContainerLabel}"],
                CancellationToken.None);

            if (exitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
                var containerIds = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var id in containerIds)
                {
                    await RunDockerAsync(["stop", "--time", "10", id], CancellationToken.None);
                    await RunDockerAsync(["rm", "-f", id], CancellationToken.None);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop all llama.cpp containers");
        }

        _modelPorts.Clear();
        _reservedPorts.Clear();
        _fallbackModels.Clear();
    }

    public async Task<bool> TestDockerAsync()
    {
        try
        {
            var (exitCode, _) = await RunDockerWithOutputAsync(["info", "--format", "{{.ServerVersion}}"], CancellationToken.None);
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAllContainersAsync();
    }

    private LlamaCppModel? ParseManifest(string manifestPath)
    {
        var json = File.ReadAllText(manifestPath);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("layers", out var layers) || layers.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? modelDigest = null;
        foreach (var layer in layers.EnumerateArray())
        {
            if (layer.TryGetProperty("mediaType", out var mediaType)
                && mediaType.GetString() == "application/vnd.ollama.image.model"
                && layer.TryGetProperty("digest", out var digest))
            {
                modelDigest = digest.GetString();
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(modelDigest))
        {
            return null;
        }

        var modelName = ResolveModelName(manifestPath);
        if (modelName is null)
        {
            return null;
        }

        var blobName = modelDigest.Replace(":", "-", StringComparison.Ordinal);
        var blobPath = Path.GetFullPath(Path.Combine(_blobsPath, blobName));

        return new LlamaCppModel
        {
            OllamaName = modelName,
            BlobDigest = blobName,
            BlobPath = blobPath,
            ManifestPath = manifestPath
        };
    }

    private static string? ResolveModelName(string manifestPath)
    {
        var normalizedPath = manifestPath.Replace('\\', '/');
        var parts = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        var manifestIndex = Array.FindLastIndex(parts, p =>
            p.Equals("manifests", StringComparison.OrdinalIgnoreCase));

        if (manifestIndex < 0 || manifestIndex >= parts.Length - 1)
        {
            return null;
        }

        var relativeParts = parts[(manifestIndex + 1)..];

        if (relativeParts.Length < 2)
        {
            return null;
        }

        var registry = relativeParts[0];

        if (registry.Equals("registry.ollama.ai", StringComparison.OrdinalIgnoreCase))
        {
            if (relativeParts.Length < 3)
            {
                return null;
            }

            if (relativeParts[1].Equals("library", StringComparison.OrdinalIgnoreCase) && relativeParts.Length >= 4)
            {
                return $"{relativeParts[2]}:{relativeParts[3]}";
            }

            if (relativeParts.Length == 3)
            {
                return $"{relativeParts[1]}:{relativeParts[2]}";
            }

            return null;
        }

        if (relativeParts.Length >= 3)
        {
            var tag = relativeParts[^1];
            var modelPath = string.Join("/", relativeParts.Take(relativeParts.Length - 1));
            return $"{modelPath}:{tag}";
        }

        return null;
    }

    internal string[] BuildDockerRunArgs(string containerName, LlamaCppModel model, int port)
    {
        var blobsDir = Path.GetDirectoryName(Path.GetFullPath(model.BlobPath))!;
        var blobFile = Path.GetFileName(model.BlobPath);

        var args = new List<string>
        {
            "run",
            "-d",
            "--label", $"{NginoContainerLabel}=true",
            "--name", containerName,
            "-p", $"{port}:{port}",
            "-v", $"{blobsDir}:/models/blobs:ro",
        };

        if (HasRocmDevices())
        {
            args.Add("--device=/dev/kfd");
            args.Add("--device=/dev/dri");
            args.Add("--group-add=video");
        }

        if ((HasNvidiaGpu() || IsCudaDockerImage(_dockerImage)) && !HasRocmDevices())
        {
            args.Add("--gpus=all");
        }

        args.Add(_dockerImage);
        args.Add("--embeddings");
        args.Add("-m");
        args.Add($"/models/blobs/{blobFile}");
        args.Add("-ngl");
        args.Add("auto");
        if (_parallel.HasValue)
        {
            args.Add("--parallel");
            args.Add(_parallel.Value.ToString());
        }
        args.Add("--host");
        args.Add("0.0.0.0");
        args.Add("--port");
        args.Add(port.ToString());

        return [.. args];
    }

    private async Task<int?> FindExistingContainerPortAsync(string containerName)
    {
        var (exitCode, output) = await RunDockerWithOutputAsync(
            ["ps", "--filter", $"name=^{containerName}$", "--format", "{{.Ports}}"],
            CancellationToken.None);

        if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var port = ParseHostPort(line);
            if (port.HasValue)
            {
                return port;
            }
        }

        return null;
    }

    private static int? ParseHostPort(string ports)
    {
        foreach (var mapping in ports.Split(','))
        {
            var trimmed = mapping.Trim();
            var arrowIndex = trimmed.IndexOf("->", StringComparison.Ordinal);
            if (arrowIndex < 0)
            {
                continue;
            }

            var hostPart = trimmed[..arrowIndex].Trim();
            var colonIndex = hostPart.LastIndexOf(':');
            if (colonIndex < 0)
            {
                continue;
            }

            if (int.TryParse(hostPart[(colonIndex + 1)..], out var port))
            {
                return port;
            }
        }

        return null;
    }

    private int FindAvailablePort()
    {
        lock (_portAllocationLock)
        {
            var usedPorts = new HashSet<int>(_modelPorts.Values);
            foreach (var reservedPort in _reservedPorts.Keys)
            {
                usedPorts.Add(reservedPort);
            }

            var port = _basePort;

            while (usedPorts.Contains(port))
            {
                port++;
            }

            _reservedPorts[port] = 0;
            return port;
        }
    }

    private void ReleaseReservedPort(int port)
    {
        _reservedPorts.TryRemove(port, out _);
    }

    private bool RemoveModelPort(string ollamaModelName)
    {
        if (_modelPorts.TryRemove(ollamaModelName, out var port))
        {
            ReleaseReservedPort(port);
            return true;
        }

        return false;
    }

    private async Task<ContainerStartResult> WaitForServerReadyAsync(
        string host, int port, string containerName, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + ContainerStartTimeout;

        var tcpResult = await WaitForTcpPortAsync(host, port, containerName, deadline, cancellationToken);
        if (tcpResult != ContainerStartResult.Ready)
        {
            return tcpResult;
        }

        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(3),
            UseProxy = false
        };
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!await IsDockerContainerRunningAsync(containerName))
            {
                await LogContainerOutputAsync(containerName);
                return ContainerStartResult.ContainerExited;
            }

            try
            {
                using var response = await httpClient.GetAsync(
                    $"http://{host}:{port}/health", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return ContainerStartResult.Ready;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (HttpRequestException ex)
            {
                _logger.LogDebug(ex, "Health probe of llama.cpp container {ContainerName} failed; retrying.", containerName);
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Health probe of llama.cpp container {ContainerName} failed; retrying.", containerName);
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        return ContainerStartResult.TimedOut;
    }

    private async Task<ContainerStartResult> WaitForTcpPortAsync(
        string host, int port, string containerName, DateTime deadline, CancellationToken cancellationToken)
    {
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!await IsDockerContainerRunningAsync(containerName))
            {
                await LogContainerOutputAsync(containerName);
                return ContainerStartResult.ContainerExited;
            }

            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(host, port, cancellationToken);
                return ContainerStartResult.Ready;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SocketException)
            {
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        return ContainerStartResult.TimedOut;
    }

    private async Task<bool> IsDockerContainerRunningAsync(string containerName)
    {
        try
        {
            var (exitCode, output) = await RunDockerWithOutputAsync(
                ["inspect", "-f", "{{.State.Running}}", containerName],
                CancellationToken.None);
            return exitCode == 0 && string.Equals(output.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private async Task LogContainerOutputAsync(string containerName)
    {
        try
        {
            var (_, output) = await RunDockerWithOutputAsync(
                ["logs", "--tail", "100", containerName],
                CancellationToken.None);

            if (!string.IsNullOrWhiteSpace(output))
            {
                _logger.LogError(
                    "llama.cpp container {ContainerName} exited before becoming ready. Last output:\n{Output}",
                    containerName, output);
            }
            else
            {
                _logger.LogError(
                    "llama.cpp container {ContainerName} exited before becoming ready, but produced no output.",
                    containerName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read logs of container {ContainerName}", containerName);
        }
    }

    private static string SanitizeContainerName(string name)
    {
        var sanitized = InvalidContainerNameChars().Replace(name, "_");
        return sanitized.Trim('_').ToLowerInvariant();
    }

    private async Task<int> RunDockerAsync(string[] args, CancellationToken cancellationToken)
    {
        var (exitCode, _) = await RunDockerWithOutputAsync(args, cancellationToken);
        return exitCode;
    }

    private async Task<(int ExitCode, string Output)> RunDockerWithOutputAsync(
        string[] args, CancellationToken cancellationToken)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        var readOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var readError = process.StandardError.ReadToEndAsync(cancellationToken);
        var waitTask = process.WaitForExitAsync(cancellationToken);

        var completed = await Task.WhenAny(waitTask, Task.Delay(DockerTimeout, cancellationToken));

        string output;
        string error;

        try
        {
            output = await readOutput;
            error = await readError;
        }
        catch
        {
            output = "";
            error = "timed out";
        }

        if (completed != waitTask)
        {
            _logger.LogWarning("Docker command timed out: docker {Args}", string.Join(" ", args));
            try { process.Kill(entireProcessTree: true); } catch { }
            return (-1, error);
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            output = $"{output}\n{error}".Trim();
        }

        return (process.ExitCode, output);
    }

    private static string GetDefaultDockerImage()
    {
        if (HasRocmDevices())
        {
            return "ghcr.io/ggml-org/llama.cpp:server-rocm";
        }

        if (HasNvidiaGpu())
        {
            return "ghcr.io/ggml-org/llama.cpp:server-cuda";
        }

        return "ghcr.io/ggml-org/llama.cpp:server";
    }

    private static bool HasRocmDevices() => File.Exists("/dev/kfd") && Directory.Exists("/dev/dri");

    private static bool HasNvidiaGpu()
    {
        try
        {
            if (File.Exists("/proc/driver/nvidia/version")
                || Directory.Exists("/proc/driver/nvidia/gpus"))
            {
                return true;
            }

            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            var systemNvidiaSmi = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "nvidia-smi.exe");
            var programFilesNvidiaSmi = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NVIDIA Corporation",
                "NVSMI",
                "nvidia-smi.exe");

            return File.Exists(systemNvidiaSmi) || File.Exists(programFilesNvidiaSmi);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCudaDockerImage(string image) =>
        image.Contains("cuda", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"[^a-zA-Z0-9_.-]")]
    private static partial Regex InvalidContainerNameChars();

    private enum ContainerStartResult
    {
        Ready,
        ContainerExited,
        TimedOut
    }
}

internal sealed record LlamaCppModel
{
    public required string OllamaName { get; init; }
    public required string BlobDigest { get; init; }
    public required string BlobPath { get; init; }
    public required string ManifestPath { get; init; }
}

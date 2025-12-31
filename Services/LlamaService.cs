using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace TinyGenerator.Services
{
    public class LlamaService
    {
        private readonly string _llamaRoot;
        private readonly string _llamaModels;
        private readonly string _llamaServerExe;
        private readonly string _llamaHost;
        private readonly int _llamaPort;
        private readonly int _llamaGpuLayers;
        private readonly string? _llamaDevice;
        private readonly int _llamaRestartDelayMs;
        // Can be a single path or a semicolon-separated list of paths.
        // On Windows, llama.cpp CUDA DLLs are typically in ...\CUDA\vXX.Y\bin\x64
        private readonly string? _cudaBinPath;
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        private readonly ICustomLogger? _logger;

        private Process? _llamaServer;
        private string? _currentModelPath;
        private int _currentContextSize;
        private readonly object _lock = new();
        private bool _loggedCudaPreflight;

        public LlamaService(IConfiguration configuration, ICustomLogger? logger = null)
        {
            _logger = logger;

            _llamaRoot = configuration["LlamaCpp:Root"] ?? @"C:\llama.cpp";
            _llamaModels = configuration["LlamaCpp:Models"]
                ?? configuration["LlamaCpp:ModelsDir"]
                ?? Path.Combine(_llamaRoot, "models");
            _llamaServerExe = configuration["LlamaCpp:ServerExe"] ?? Path.Combine(_llamaRoot, "llama-server.exe");
            _llamaHost = configuration["LlamaCpp:Host"] ?? "127.0.0.1";
            _llamaPort = int.TryParse(configuration["LlamaCpp:Port"], out var port) ? port : 11436;
            _llamaGpuLayers = int.TryParse(configuration["LlamaCpp:GpuLayers"], out var ngl) ? ngl : 99;
            _llamaDevice = configuration["LlamaCpp:Device"];
            _cudaBinPath = configuration["LlamaCpp:CudaBinPath"];
            _llamaRestartDelayMs = int.TryParse(configuration["LlamaCpp:RestartDelayMs"], out var delayMs) ? delayMs : 500;
        }

        public void StartServer(string modelName, int contextSize)
        {
            if (string.IsNullOrWhiteSpace(modelName)) throw new ArgumentException("Model name is required.", nameof(modelName));
            if (contextSize <= 0) contextSize = 32768;

            var fileName = modelName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
                ? modelName
                : modelName + ".gguf";
            var modelPath = Path.Combine(_llamaModels, fileName);

            if (!File.Exists(modelPath))
            {
                throw new FileNotFoundException($"Llama model not found at '{modelPath}'.", modelPath);
            }

            lock (_lock)
            {
                if (_llamaServer != null && !_llamaServer.HasExited)
                {
                    if (string.Equals(_currentModelPath, modelPath, StringComparison.OrdinalIgnoreCase) &&
                        _currentContextSize == contextSize)
                    {
                        return;
                    }

                    StopServer();
                    if (_llamaRestartDelayMs > 0)
                    {
                        Thread.Sleep(_llamaRestartDelayMs);
                    }
                }

                var startMsg = $"Starting llama.cpp server (model={modelPath}, ctx={contextSize}, host={_llamaHost}, port={_llamaPort})";
                _logger?.Log("Info", "llama.cpp", startMsg);
                Console.WriteLine("[LlamaService] " + startMsg);

                LogCudaPreflightOnce();

                var args = $"--model \"{modelPath}\"";
                if (_llamaGpuLayers != 0)
                {
                    args += $" --n-gpu-layers {_llamaGpuLayers}";
                }
                args += $" --ctx-size {contextSize} --port {_llamaPort} --host {_llamaHost}";

                // llama-server's --device expects a device identifier (often indices), and does NOT accept the string "cuda".
                // If the config contains "cuda" (common in older setups), omit the flag and let llama.cpp pick the default GPU.
                var device = _llamaDevice?.Trim();
                if (!string.IsNullOrWhiteSpace(device))
                {
                    if (device.Equals("cuda", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger?.Log("Warning", "llama.cpp",
                            "Ignoring LlamaCpp:Device='cuda' (invalid for llama-server --device). Leave it empty or set a valid value from --list-devices.");
                    }
                    else
                    {
                        args += $" --device {device}";
                    }
                }

                _llamaServer = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = _llamaServerExe,
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        WorkingDirectory = _llamaRoot
                    }
                };

                ConfigureCudaPath(_llamaServer.StartInfo);

                _llamaServer.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        _logger?.Log("Info", "llama.cpp", $"llama.cpp: {e.Data}");
                    }
                };
                _llamaServer.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        _logger?.Log("Warning", "llama.cpp", $"llama.cpp stderr: {e.Data}");
                    }
                };

                _llamaServer.Start();
                _llamaServer.BeginOutputReadLine();
                _llamaServer.BeginErrorReadLine();
                _currentModelPath = modelPath;
                _currentContextSize = contextSize;
                WaitForReady();
                _logger?.Log("Info", "llama.cpp", "llama.cpp server is ready");
                Console.WriteLine("[LlamaService] llama.cpp server is ready");
            }
        }

        public void StopServer()
        {
            lock (_lock)
            {
                if (_llamaServer != null && !_llamaServer.HasExited)
                {
                    _logger?.Log("Info", "llama.cpp", "Stopping llama.cpp server");
                    Console.WriteLine("[LlamaService] Stopping llama.cpp server");
                    _llamaServer.Kill(entireProcessTree: true);
                    _llamaServer.WaitForExit();
                }

                _llamaServer = null;
                _currentModelPath = null;
                _currentContextSize = 0;
            }
        }

        private void WaitForReady()
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            var healthUrl = $"http://{_llamaHost}:{_llamaPort}/health";
            var modelsUrl = $"http://{_llamaHost}:{_llamaPort}/v1/models";

            while (DateTime.UtcNow < deadline)
            {
                if (_llamaServer != null && _llamaServer.HasExited)
                {
                    throw new InvalidOperationException("Llama server exited before it became ready.");
                }

                try
                {
                    var healthOk = _httpClient.GetAsync(healthUrl, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    if (healthOk.IsSuccessStatusCode) return;
                }
                catch { }

                try
                {
                    var modelsOk = _httpClient.GetAsync(modelsUrl, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    if (modelsOk.IsSuccessStatusCode) return;
                }
                catch { }

                Thread.Sleep(200);
            }

            throw new TimeoutException("Llama server did not become ready in time.");
        }

        private void ConfigureCudaPath(ProcessStartInfo psi)
        {
            var cudaBins = ResolveCudaBinPaths();
            if (cudaBins.Count == 0) return;

            var existing = psi.Environment.ContainsKey("PATH")
                ? psi.Environment["PATH"]
                : Environment.GetEnvironmentVariable("PATH");
            existing ??= string.Empty;

            // Prepend all CUDA paths (avoid duplicates)
            foreach (var cudaBin in cudaBins)
            {
                if (existing.IndexOf(cudaBin, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }
                existing = $"{cudaBin};{existing}";
            }

            psi.Environment["PATH"] = existing;
            _logger?.Log("Info", "llama.cpp", $"CUDA PATH injected for llama-server: {string.Join(";", cudaBins)}");
        }

        private List<string> ResolveCudaBinPaths()
        {
            var bins = new List<string>();

            // If ggml-cuda.dll was built against a specific CUDA major (e.g. imports cublas64_12.dll),
            // prefer matching Toolkit folders to avoid LoadLibrary(126) due to missing dependencies.
            var preferredCudaMajor = TryDetectPreferredCudaMajor();

            // 1) Config override(s)
            if (!string.IsNullOrWhiteSpace(_cudaBinPath))
            {
                foreach (var raw in _cudaBinPath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var p = raw;
                    // Allow pointing to CUDA version root; normalize to bin\x64 when present
                    if (Directory.Exists(p))
                    {
                        var x64 = Path.Combine(p, "bin", "x64");
                        if (Directory.Exists(x64))
                        {
                            p = x64;
                        }
                        else
                        {
                            var bin = Path.Combine(p, "bin");
                            if (Directory.Exists(bin)) p = bin;
                        }

                        if (!bins.Contains(p, StringComparer.OrdinalIgnoreCase))
                        {
                            bins.Add(p);
                        }
                    }
                }
            }

            // 2) Auto-discover
            var auto = TryFindCudaToolkitBin(preferredCudaMajor);
            if (!string.IsNullOrWhiteSpace(auto) && Directory.Exists(auto) &&
                !bins.Contains(auto, StringComparer.OrdinalIgnoreCase))
            {
                bins.Add(auto);
            }

            return bins;
        }

        private string? TryDetectPreferredCudaMajor()
        {
            try
            {
                var ggmlCuda = Path.Combine(_llamaRoot, "ggml-cuda.dll");
                if (!File.Exists(ggmlCuda)) return null;

                // Cheap heuristic: search the binary for well-known import names.
                var bytes = File.ReadAllBytes(ggmlCuda);
                var text = Encoding.ASCII.GetString(bytes);

                if (text.IndexOf("cublas64_12.dll", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.IndexOf("cudart64_12.dll", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return "12";
                }
                if (text.IndexOf("cublas64_13.dll", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.IndexOf("cudart64_13.dll", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return "13";
                }
            }
            catch
            {
                // ignore
            }

            return null;
        }

        private static string? TryFindCudaToolkitBin(string? preferredCudaMajor)
        {
            var cudaRoot = @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA";
            if (!Directory.Exists(cudaRoot)) return null;

            Version? bestVersion = null;
            string? bestDir = null;

            foreach (var dir in Directory.GetDirectories(cudaRoot, "v*"))
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(name) || !name.StartsWith("v", StringComparison.OrdinalIgnoreCase)) continue;
                var verText = name.Substring(1);
                if (!Version.TryParse(verText, out var v)) continue;

                // If we know ggml-cuda expects a major (e.g. 12), prefer that major.
                if (!string.IsNullOrWhiteSpace(preferredCudaMajor))
                {
                    if (!verText.StartsWith(preferredCudaMajor + ".", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                if (bestVersion == null || v > bestVersion)
                {
                    // DLLs typically live in bin\x64; prefer that when available
                    var bin = Path.Combine(dir, "bin", "x64");
                    if (!Directory.Exists(bin))
                    {
                        bin = Path.Combine(dir, "bin");
                    }
                    if (!Directory.Exists(bin)) continue;
                    bestVersion = v;
                    bestDir = bin;
                }
            }

            return bestDir;
        }

        private void LogCudaPreflightOnce()
        {
            if (_loggedCudaPreflight) return;
            _loggedCudaPreflight = true;

            try
            {
                var ggmlCuda = Path.Combine(_llamaRoot, "ggml-cuda.dll");
                if (!File.Exists(ggmlCuda))
                {
                    _logger?.Log("Warning", "llama.cpp", $"CUDA backend dll not found: {ggmlCuda}. GPU offload will not work.");
                    return;
                }

                // Try to load as-is first; if it fails due to missing dependent DLLs (126),
                // retry after temporarily prepending the same CUDA paths we inject into llama-server.
                var handle = LoadLibraryW(ggmlCuda);
                if (handle == IntPtr.Zero)
                {
                    var err = Marshal.GetLastWin32Error();

                    if (err == 126)
                    {
                        var cudaBins = ResolveCudaBinPaths();
                        if (cudaBins.Count > 0)
                        {
                            var originalPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                            try
                            {
                                var updatedPath = originalPath;
                                foreach (var cudaBin in cudaBins)
                                {
                                    if (updatedPath.IndexOf(cudaBin, StringComparison.OrdinalIgnoreCase) < 0)
                                    {
                                        updatedPath = $"{cudaBin};{updatedPath}";
                                    }
                                }

                                Environment.SetEnvironmentVariable("PATH", updatedPath);
                                handle = LoadLibraryW(ggmlCuda);
                                if (handle != IntPtr.Zero)
                                {
                                    _logger?.Log("Info", "llama.cpp",
                                        $"CUDA backend dll loaded successfully (ggml-cuda.dll) after applying configured/auto CUDA paths: {string.Join(";", cudaBins)}");
                                    return;
                                }

                                err = Marshal.GetLastWin32Error();
                            }
                            finally
                            {
                                Environment.SetEnvironmentVariable("PATH", originalPath);
                            }
                        }
                    }

                    // 126 = missing dependent module (usually CUDA runtime DLLs like cublas/cudart)
                    var hint = "Install NVIDIA CUDA Toolkit/Runtime and set LlamaCpp:CudaBinPath (prefer ...\\CUDA\\vXX.Y\\bin or ...\\bin\\x64).";

                    var expected = TryExtractLikelyCudaDllDependencies(ggmlCuda);
                    if (expected.Count > 0)
                    {
                        hint += " Expected CUDA DLLs (place on PATH or next to llama-server.exe): " + string.Join(", ", expected);
                    }

                    // Common real-world case: ggml-cuda.dll built against CUDA 12 but only CUDA 13 is installed
                    try
                    {
                        var bytes = File.ReadAllBytes(ggmlCuda);
                        var text = Encoding.ASCII.GetString(bytes);
                        if (text.IndexOf("cublas64_12.dll", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            text.IndexOf("cudart64_12.dll", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            hint = "Detected ggml-cuda.dll built for CUDA 12 (expects cublas64_12.dll/cudart64_12.dll). " +
                                   "Install CUDA Toolkit 12.x (or provide CUDA 12 runtime DLLs) and point LlamaCpp:CudaBinPath to its bin (or bin\\x64), OR rebuild llama.cpp against your installed CUDA.";

                            if (expected.Count > 0)
                            {
                                hint += " Expected CUDA DLLs (place on PATH or next to llama-server.exe): " + string.Join(", ", expected);
                            }
                        }
                    }
                    catch
                    {
                        // ignore
                    }

                    _logger?.Log("Warning", "llama.cpp",
                        $"CUDA backend present but could not be loaded (LoadLibrary error {err}). {hint}");
                }
                else
                {
                    _logger?.Log("Info", "llama.cpp", "CUDA backend dll loaded successfully (ggml-cuda.dll)." );
                }
            }
            catch (Exception ex)
            {
                _logger?.Log("Warning", "llama.cpp", $"CUDA preflight check failed: {ex.Message}");
            }
        }

        private static List<string> TryExtractLikelyCudaDllDependencies(string ggmlCudaPath)
        {
            var result = new List<string>();
            try
            {
                var bytes = File.ReadAllBytes(ggmlCudaPath);
                var text = Encoding.ASCII.GetString(bytes);

                // Extract all occurrences like "something.dll" and filter to CUDA-ish names.
                var matches = Regex.Matches(text, @"[A-Za-z0-9_.-]+\\.dll", RegexOptions.CultureInvariant);
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (Match m in matches)
                {
                    var dll = m.Value;
                    if (dll.Length < 6 || dll.Length > 64) continue;

                    // Keep only DLLs that are likely part of CUDA 12 runtime stack.
                    if (dll.Contains("_12", StringComparison.OrdinalIgnoreCase) ||
                        dll.StartsWith("nvrtc", StringComparison.OrdinalIgnoreCase) ||
                        dll.StartsWith("cublas", StringComparison.OrdinalIgnoreCase) ||
                        dll.StartsWith("cudart", StringComparison.OrdinalIgnoreCase) ||
                        dll.StartsWith("cufft", StringComparison.OrdinalIgnoreCase) ||
                        dll.StartsWith("curand", StringComparison.OrdinalIgnoreCase) ||
                        dll.StartsWith("cusolver", StringComparison.OrdinalIgnoreCase) ||
                        dll.StartsWith("cusparse", StringComparison.OrdinalIgnoreCase))
                    {
                        set.Add(dll);
                    }
                }

                // Keep output stable and short.
                foreach (var dll in set.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).Take(12))
                {
                    result.Add(dll);
                }
            }
            catch
            {
                // ignore
            }

            return result;
        }

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryW(string lpFileName);
    }
}

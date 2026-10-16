using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Local_Port_Utility;

/// Starts Bore and forwards its output and events to the UI
internal sealed class BoreTunnelService : IDisposable
{
    private static readonly Regex AnsiEscapeRegex = new(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1B\\))", RegexOptions.Compiled);
    private volatile Process? _process;
    private bool _disposed;
    private int _endpointFound;

    public event Action<string>? OutputReceived;
    public event Action<string>? EndpointDetected;
    public event Action<int>? Exited;

    public bool IsRunning => _process is not null;

    public static string GetExecutablePath()
    {
        // Bore ships x86 and x64 windows builds and x86 also runs under Windows on ARM
        var executableName = RuntimeInformation.ProcessArchitecture is Architecture.X86 or Architecture.Arm64
            ? "bore-x86.exe"
            : "bore-x64.exe";
        return Path.Combine(AppContext.BaseDirectory, "Tools", executableName);
    }

    public static bool IsValidServer(string server) =>
        !string.IsNullOrWhiteSpace(server) && server.Length <= 253 && !server.Contains("://", StringComparison.Ordinal) &&
        !server.Any(char.IsWhiteSpace) && !server.Contains('/') && !server.Contains('\\') && !server.Contains('\'');

    public void Start(string localHost, int localPort, string server, int remotePort)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process is not null) throw new InvalidOperationException("Bore is already running or stopping.");

        var executablePath = GetExecutablePath();
        if (!File.Exists(executablePath)) throw new FileNotFoundException("Bore executable was not found.", executablePath);

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath)!
            }
        };
        process.StartInfo.Environment["NO_COLOR"] = "1";
        process.StartInfo.Environment["CLICOLOR"] = "0";

        // ArgumentList keeps user input separate from PowerShell or commandline syntax
        foreach (var argument in new[] { "local", localPort.ToString(), "--local-host", localHost, "--to", server, "--port", remotePort.ToString() })
            process.StartInfo.ArgumentList.Add(argument);

        try
        {
            Interlocked.Exchange(ref _endpointFound, 0);
            if (!process.Start()) throw new InvalidOperationException("Bore could not be started.");
            _process = process;
            var standardOutputTask = ReadOutputAsync(process, process.StandardOutput, server);
            var standardErrorTask = ReadOutputAsync(process, process.StandardError, server);
            _ = WatchProcessAsync(process, standardOutputTask, standardErrorTask);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    public void Stop()
    {
        var process = _process;
        if (process is not null && !process.HasExited) process.Kill(entireProcessTree: true);
    }

    private async Task ReadOutputAsync(Process process, StreamReader output, string server)
    {
        try
        {
            // stdout and stderr are drained together so either pipe cannot fill and block bore.
            while (await output.ReadLineAsync() is { } line)
            {
                line = AnsiEscapeRegex.Replace(line, string.Empty).Replace("\u001b", string.Empty);
                OutputReceived?.Invoke(line);

                // Bore prints the relay host and assigned port when the tunnel is ready
                var match = Regex.Match(line, $@"(?i)(?<![\w.-]){Regex.Escape(server)}:(?<port>\d{{1,5}})(?!\d)");
                if (match.Success && int.TryParse(match.Groups["port"].Value, out var port) && port is >= 1 and <= 65535 && Interlocked.Exchange(ref _endpointFound, 1) == 0)
                    EndpointDetected?.Invoke($"{server}:{port}");
            }
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        catch (IOException) { }
    }

    private async Task WatchProcessAsync(Process process, Task standardOutputTask, Task standardErrorTask)
    {
        int exitCode;
        try
        {
            await process.WaitForExitAsync();
            await Task.WhenAll(standardOutputTask, standardErrorTask);
            exitCode = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_process, process)) _process = null;
            process.Dispose();
        }

        Exited?.Invoke(exitCode);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var process = _process;
        _process = null;
        if (process is null) return;

        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
        // WatchProcessAsync owns disposal after the redirected output pipes have drained
    }
}

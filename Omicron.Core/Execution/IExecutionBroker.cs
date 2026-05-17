using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Omicron.Core.Events;

namespace Omicron.Core.Execution;

/// <summary>
/// Request to execute a command scoped to a session.
/// </summary>
public sealed record ExecutionRequest(
    SessionId SessionId,
    string Command,
    string? ShellId,
    string? WorkingDirectory,
    int TimeoutSeconds = 120,
    ToolCallId? ToolCallId = null);

/// <summary>
/// Result of a command execution.
/// </summary>
public sealed record ExecutionResult(
    string Output,
    int ExitCode,
    long DurationMs,
    bool TimedOut,
    bool Cancelled,
    string? Error);

/// <summary>
/// Abstraction for executing shell commands.
/// </summary>
public interface IExecutionBroker
{
    /// <summary>
    /// Execute a command and return the result.
    /// </summary>
    Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken ct = default);
}

/// <summary>
/// Default execution broker that runs commands on the local machine.
/// Implementation adapted from ShellTools with timeout, truncation, and output merging.
/// </summary>
public sealed class LocalExecutionBroker : IExecutionBroker
{
    /// <summary>Default command timeout.</summary>
    public const int DefaultTimeoutSeconds = 120;

    /// <summary>Output truncation limit.</summary>
    public const int MaxOutputBytes = 50 * 1024;

    /// <summary>Maximum output lines.</summary>
    public const int MaxOutputLines = 2000;

    private readonly IEventSink? _eventSink;

    public LocalExecutionBroker(IEventSink? eventSink = null)
    {
        _eventSink = eventSink;
    }

    // -----------------------------------------------------------
    // Shell definitions
    // -----------------------------------------------------------

    private static readonly ShellDef[] KnownShells =
    [
        new() { Id = "bash",       Exe = "bash",       Args = "-c", IsUnix = true  },
        new() { Id = "sh",         Exe = "sh",         Args = "-c", IsUnix = true  },
        new() { Id = "zsh",        Exe = "zsh",        Args = "-c", IsUnix = true  },
        new() { Id = "fish",       Exe = "fish",       Args = "-c", IsUnix = true  },
        new() { Id = "dash",       Exe = "dash",       Args = "-c", IsUnix = true  },
        new() { Id = "pwsh",       Exe = "pwsh",       Args = "-NoProfile -Command", IsUnix = false, WinArgs = "-NoProfile -Command" },
        new() { Id = "powershell", Exe = "powershell", Args = "-NoProfile -Command", IsUnix = false, WinArgs = "-NoProfile -Command" },
        new() { Id = "cmd",        Exe = "cmd",        Args = "/c", IsUnix = false, WinArgs = "/c" },
    ];

    public async Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken ct = default)
    {
        var shellId = request.ShellId ?? DetectDefaultShell();
        var sessionId = request.SessionId;
        var toolCallId = request.ToolCallId;
        var startTime = DateTimeOffset.UtcNow;

        // Helper to emit completion event and return result
        ExecutionResult Complete(int exitCode, long durationMs, bool timedOut, bool cancelled,
            string? output, string? error)
        {
            _eventSink?.Emit(new ExecutionCompletedEvent(
                new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, sessionId),
                request.Command, exitCode, durationMs, timedOut, toolCallId, cancelled, error));
            return new ExecutionResult(output ?? "", exitCode, durationMs, timedOut, cancelled, error);
        }

        // Emit execution started event
        _eventSink?.Emit(new ExecutionStartedEvent(
            new EventEnvelope(EventId.New(), 0, startTime, sessionId),
            request.Command, request.WorkingDirectory ?? Environment.CurrentDirectory, toolCallId));
        var shellDef = KnownShells.FirstOrDefault(s => s.Id == shellId);

        if (shellDef is null)
        {
            return Complete(-1, 0, false, false,
                $"Error: unknown shell '{shellId}'.", $"Unknown shell: {shellId}");
        }

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var shellExe = FindExe(shellDef.Exe);
        if (shellExe is null)
        {
            return Complete(-1, 0, false, false,
                $"Error: shell '{shellId}' not found on this system.",
                $"Shell not found: {shellId}");
        }

        var shellArgs = isWindows && shellDef.WinArgs is not null ? shellDef.WinArgs : shellDef.Args;
        var cwd = request.WorkingDirectory ?? Environment.CurrentDirectory;
        var timeoutSec = Math.Clamp(request.TimeoutSeconds, 1, 600);

        var psi = new ProcessStartInfo
        {
            FileName = shellExe,
            Arguments = $"{shellArgs} \"{EscapeCommand(request.Command)}\"",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        psi.Environment["DEBIAN_FRONTEND"] = "noninteractive";
        psi.Environment["CI"] = "true";

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outputLock = new object();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                lock (outputLock) stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                lock (outputLock) stderr.AppendLine(e.Data);
        };

        var stopwatch = Stopwatch.StartNew();
        int exitCode = -1;
        bool timedOut = false;
        bool cancelled = false;
        string? errorMessage = null;

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested)
                    cancelled = true;
                else
                    timedOut = true;
                KillProcessTree(process);
            }

            if (!timedOut && !cancelled)
                process.WaitForExit(1000);

            exitCode = timedOut ? -1 : process.ExitCode;
        }
        catch (Exception ex)
        {
            cancelled = true;
            errorMessage = ex.Message;
            try { process.Kill(true); } catch { }
        }

        stopwatch.Stop();

        // Build output
        var output = stdout.ToString();
        var error = stderr.ToString();

        // Merge stderr into output if stdout is empty
        if (output.Length == 0 && error.Length > 0)
        {
            output = error;
            error = "";
        }

        // Truncate — use StringReader to avoid Replace+Split allocations
        var truncated = false;
        var outputReader = new StringReader(output);
        var lines = new List<string>();
        string? lineStr;
        while ((lineStr = outputReader.ReadLine()) is not null)
            lines.Add(lineStr);
        var totalLines = lines.Count;

        if (totalLines > MaxOutputLines)
        {
            lines = lines.GetRange(0, MaxOutputLines);
            truncated = true;
        }

        var joined = string.Join("\n", lines);
        var joinedByteCount = Encoding.UTF8.GetByteCount(joined);
        if (joinedByteCount > MaxOutputBytes)
        {
            var trimmed = new StringBuilder();
            int trimmedBytes = 0;
            foreach (var line in lines)
            {
                var lineBytes = Encoding.UTF8.GetByteCount(line) + 1; // +1 for newline
                if (trimmedBytes + lineBytes > MaxOutputBytes)
                {
                    truncated = true;
                    break;
                }
                trimmed.Append(line);
                trimmed.Append('\n');
                trimmedBytes += lineBytes;
            }
            joined = trimmed.ToString().TrimEnd();
        }

        var sb = new StringBuilder();
        sb.Append(joined.TrimEnd());

        if (truncated)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine($"[Output truncated. Full: {totalLines:N0} lines, {Encoding.UTF8.GetByteCount(output):N0} bytes.]");
        }

        // Append stderr
        if (error.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("--- stderr ---");
            int stderrLineCount = 0;
            using var stderrReader = new StringReader(error);
            string? stderrLine;
            while ((stderrLine = stderrReader.ReadLine()) is not null)
            {
                stderrLineCount++;
                if (stderrLineCount <= 50)
                {
                    sb.Append(stderrLine);
                    sb.Append('\n');
                }
            }
            if (stderrLineCount > 50)
                sb.Append($"... and {stderrLineCount - 50} more stderr lines\n");
        }

        // Status line
        sb.AppendLine();
        if (timedOut)
            sb.AppendLine($"--- (timed out after {timeoutSec}s) ---");
        else if (cancelled)
            sb.AppendLine("--- (cancelled) ---");
        else
            sb.AppendLine($"--- (exit {exitCode}, {stopwatch.Elapsed.TotalSeconds:F1}s) ---");

        return Complete(exitCode, (long)stopwatch.ElapsedMilliseconds, timedOut, cancelled,
            sb?.ToString().TrimEnd() ?? output.TrimEnd(), errorMessage);
    }

    private static string DetectDefaultShell()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "powershell";
        return "bash";
    }

    private static string EscapeCommand(string command)
    {
        // Single-pass escaping: backslash first, then double-quote.
        // Use StringBuilder sized to input length to avoid reallocation.
        int backslashCount = 0;
        for (int i = 0; i < command.Length; i++)
        {
            if (command[i] == '\\') backslashCount++;
        }
        int quoteCount = 0;
        for (int i = 0; i < command.Length; i++)
        {
            if (command[i] == '"') quoteCount++;
        }
        if (backslashCount == 0 && quoteCount == 0)
            return command;

        var sb = new StringBuilder(command.Length + backslashCount + quoteCount);
        foreach (var c in command)
        {
            if (c == '\\') sb.Append("\\\\");
            else if (c == '"') sb.Append("\\\"");
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string? FindExe(string name)
    {
        var paths = new List<string>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name + ".exe"));
            paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name));
            paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"));
            paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", name + ".exe"));
            paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "usr", "bin", name + ".exe"));
            paths.Add(@"C:\Program Files\Git\bin\" + name + ".exe");
            paths.Add(@"C:\Program Files\Git\usr\bin\" + name + ".exe");
        }
        else
        {
            paths.Add($"/bin/{name}");
            paths.Add($"/usr/bin/{name}");
            paths.Add($"/usr/local/bin/{name}");
            paths.Add($"/opt/homebrew/bin/{name}");
        }

        foreach (var p in paths)
        {
            if (File.Exists(p))
                return p;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which",
                Arguments = name,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            var result = proc.StandardOutput.ReadLine()?.Trim();
            proc.WaitForExit(1000);

            if (!string.IsNullOrEmpty(result) && File.Exists(result))
                return result;
        }
        catch { }

        return null;
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/T /F /PID {process.Id}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var killer = Process.Start(psi);
                killer?.WaitForExit(3000);
            }
            else
            {
                process.Kill(true);
            }
        }
        catch
        {
            try { process.Kill(); } catch { }
        }
    }

    private class ShellDef
    {
        public string Id { get; init; } = "";
        public string Exe { get; init; } = "";
        public string Args { get; init; } = "";
        public bool IsUnix { get; init; }
        public string? WinArgs { get; init; }
    }
}



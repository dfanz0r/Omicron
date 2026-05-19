using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Cysharp.Text;
using Omicron.Core.Events;
using Omicron.Core.Text;

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
    string? Error,
    ReadOnlyMemory<byte>? Utf8Output = null);

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
/// Executes commands via direct process spawn with timeout, truncation, and output merging.
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
            string? output, string? error, ReadOnlyMemory<byte>? utf8Output = null)
        {
            _eventSink?.Emit(new ExecutionCompletedEvent(
                new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, sessionId),
                request.Command, exitCode, durationMs, timedOut, toolCallId, cancelled, error));
            var outputStr = output ?? "";
            return new ExecutionResult(outputStr, exitCode, durationMs, timedOut, cancelled, error,
                Utf8Output: utf8Output ?? (outputStr.Length > 0 ? Encoding.UTF8.GetBytes(outputStr) : null));
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

        // Byte-oriented output capture: read from base streams instead of
        // using BeginOutputReadLine (which always produces strings).
        var stdoutBytes = new ArrayBufferWriter<byte>();
        var stderrBytes = new ArrayBufferWriter<byte>();

        var stopwatch = Stopwatch.StartNew();
        int exitCode = -1;
        bool timedOut = false;
        bool cancelled = false;
        string? errorMessage = null;

        try
        {
            process.Start();

            // Drain a process stream into an ArrayBufferWriter<byte>
            async Task DrainStreamAsync(Stream stream, ArrayBufferWriter<byte> writer, CancellationToken token)
            {
                byte[] buf = ArrayPool<byte>.Shared.Rent(8192);
                try
                {
                    while (true)
                    {
                        var read = await stream.ReadAsync(buf.AsMemory(0, buf.Length), token);
                        if (read == 0) break;
                        writer.Write(buf.AsSpan(0, read));
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buf);
                }
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

            var drainToken = cts.Token;
            var stdoutTask = DrainStreamAsync(process.StandardOutput.BaseStream, stdoutBytes, drainToken);
            var stderrTask = DrainStreamAsync(process.StandardError.BaseStream, stderrBytes, drainToken);

            try
            {
                await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync(drainToken));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                timedOut = true;
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            if (timedOut || cancelled)
                KillProcessTree(process);
            else
            {
                // Drain any remaining data after WaitForExit (process may close streams before fully exiting)
                process.WaitForExit(1000);
            }

            exitCode = timedOut ? -1 : process.ExitCode;
        }
        catch (Exception ex)
        {
            cancelled = true;
            errorMessage = ex.Message;
            try { process.Kill(true); } catch { }
        }

        stopwatch.Stop();

        // Build output using Utf8ValueStringBuilder with byte-oriented line handling.
        var builder = ZString.CreateUtf8StringBuilder();
        try
        {
            var stdoutSpan = stdoutBytes.WrittenSpan;
            var stderrSpan = stderrBytes.WrittenSpan;

            // Helper: count \n occurrences in a byte span
            static int CountLines(ReadOnlySpan<byte> span)
            {
                int count = 0;
                for (int i = 0; i < span.Length; i++)
                    if (span[i] == (byte)'\n') count++;
                return count;
            }

            // Merge stderr into stdout if stdout is empty
            if (stdoutSpan.IsEmpty && !stderrSpan.IsEmpty)
            {
                stdoutSpan = stderrSpan;
                stderrSpan = default;
            }

            int totalLines = CountLines(stdoutSpan);
            int writtenBytes = 0;
            int linesWritten = 0;
            bool truncated = false;

            // Scan and copy stdout lines up to the limits
            int lineStart = 0;
            for (int i = 0; i <= stdoutSpan.Length && linesWritten < MaxOutputLines && writtenBytes < MaxOutputBytes; i++)
            {
                if (i == stdoutSpan.Length || stdoutSpan[i] == (byte)'\n')
                {
                    int lineLen = i - lineStart;
                    // Strip trailing \r
                    if (lineLen > 0 && stdoutSpan[lineStart + lineLen - 1] == (byte)'\r')
                        lineLen--;

                    int lineCost = lineLen + 1; // +1 for \n we'll append
                    if (writtenBytes + lineCost > MaxOutputBytes)
                    {
                        truncated = true;
                        break;
                    }

                    if (lineLen > 0)
                        builder.AppendLiteral(stdoutSpan.Slice(lineStart, lineLen));
                    builder.AppendLine();
                    writtenBytes += lineCost;
                    linesWritten++;
                    lineStart = i + 1;
                }
            }

            // If we didn't reach the end of stdoutSpan, we truncated
            if (lineStart < stdoutSpan.Length)
                truncated = true;

            if (truncated)
            {
                builder.AppendLine();
                Utf8CompositeFormat.AppendFormatUtf8(
                    ref builder,
                    "[Output truncated. Full: {0:N0} lines, {1:N0} bytes.]"u8,
                    totalLines, stdoutBytes.WrittenCount);
                builder.AppendLine();
            }

            // Append stderr (first 50 lines)
            if (stderrSpan.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLiteral("--- stderr ---"u8);
                builder.AppendLine();
                int stderrLineCount = 0;
                int stderrLineStart = 0;
                for (int i = 0; i <= stderrSpan.Length && stderrLineCount < 50; i++)
                {
                    if (i == stderrSpan.Length || stderrSpan[i] == (byte)'\n')
                    {
                        int lineLen = i - stderrLineStart;
                        if (lineLen > 0 && stderrSpan[stderrLineStart + lineLen - 1] == (byte)'\r')
                            lineLen--;
                        stderrLineCount++;
                        if (lineLen > 0)
                            builder.AppendLiteral(stderrSpan.Slice(stderrLineStart, lineLen));
                        builder.AppendLine();
                        stderrLineStart = i + 1;
                    }
                }
                int totalStderrLines = CountLines(stderrSpan);
                if (totalStderrLines > 50)
                {
                    Utf8CompositeFormat.AppendFormatUtf8(
                        ref builder,
                        "... and {0} more stderr lines"u8,
                        totalStderrLines - 50);
                    builder.AppendLine();
                }
            }

            // Status line
            builder.AppendLine();
            if (timedOut)
                Utf8CompositeFormat.AppendFormatUtf8(ref builder, "--- (timed out after {0}s) ---"u8, timeoutSec);
            else if (cancelled)
                builder.AppendLiteral("--- (cancelled) ---"u8);
            else
                Utf8CompositeFormat.AppendFormatUtf8(ref builder, "--- (exit {0}, {1:F1}s) ---"u8, exitCode, stopwatch.Elapsed.TotalSeconds);
            builder.AppendLine();

            var resultBytes = builder.AsSpan().ToArray();
            return Complete(exitCode, (long)stopwatch.ElapsedMilliseconds, timedOut, cancelled,
                Encoding.UTF8.GetString(resultBytes).TrimEnd(), errorMessage,
                utf8Output: resultBytes);
        }
        finally { builder.Dispose(); }
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



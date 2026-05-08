using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Omicron.Core.Tools;

/// <summary>
/// Shell execution tool with dynamic detection of available shells.
/// Gives the LLM a list of usable shells and handles cross-platform
/// command execution with truncation and timeouts.
/// </summary>
public static class ShellTools
{
    /// <summary>Default command timeout.</summary>
    public const int DefaultTimeoutSeconds = 120;

    /// <summary>Output truncation limit.</summary>
    public const int MaxOutputBytes = 50 * 1024;

    /// <summary>Maximum output lines.</summary>
    public const int MaxOutputLines = 2000;

    // -----------------------------------------------------------
    // Shell definitions — ordered by detection priority
    // -----------------------------------------------------------

    private static readonly ShellDef[] KnownShells =
    [
        // Unix-first shells
        new() { Id = "bash",       Exe = "bash",       Args = "-c", IsUnix = true  },
        new() { Id = "sh",         Exe = "sh",         Args = "-c", IsUnix = true  },
        new() { Id = "zsh",        Exe = "zsh",        Args = "-c", IsUnix = true  },
        new() { Id = "fish",       Exe = "fish",       Args = "-c", IsUnix = true  },
        new() { Id = "dash",       Exe = "dash",       Args = "-c", IsUnix = true  },

        // PowerShell Core (cross-platform)
        new() { Id = "pwsh",       Exe = "pwsh",       Args = "-NoProfile -Command", IsUnix = false, WinArgs = "-NoProfile -Command" },

        // Windows shells
        new() { Id = "powershell", Exe = "powershell", Args = "-NoProfile -Command", IsUnix = false, WinArgs = "-NoProfile -Command" },
        new() { Id = "cmd",        Exe = "cmd",        Args = "/c", IsUnix = false, WinArgs = "/c" },
    ];

    private static List<ShellInfo>? _detected;

    // -----------------------------------------------------------
    // Public API
    // -----------------------------------------------------------

    /// <summary>
    /// Create the shell tool with dynamically-detected available shells.
    /// </summary>
    public static Tool Create(string workspaceRoot)
    {
        var shells = DetectShells();

        // Build description enumerating available shells
        var desc = new StringBuilder();
        desc.Append("Execute a command in the specified shell. Available shells on this system: ");
        desc.AppendJoin(", ", shells.Select(s => $"`{s.Id}` ({s.Name})"));
        desc.Append(". Use the shell best suited for the target platform. ");
        desc.Append("Commands run in the workspace directory by default; override with `cwd`. ");
        desc.Append($"Default timeout is {DefaultTimeoutSeconds}s. ");
        desc.Append($"Output is truncated at {MaxOutputBytes / 1024} KB / {MaxOutputLines} lines. ");
        desc.Append("Prefer short, focused commands.");

        return new Tool
        {
            Name = "shell",
            Description = desc.ToString(),

            Parameters = ToolSchema.Object(
                new Dictionary<string, JsonElement>
                {
                    ["shell"] = ToolSchema.EnumProperty(
                        $"Shell to use. Available: {string.Join(", ", shells.Select(s => s.Id))}",
                        shells.Select(s => s.Id).ToArray()),

                    ["command"] = ToolSchema.StringProperty(
                        "The command to execute. Will be passed to the shell's command interpreter."),

                    ["timeout"] = ToolSchema.IntegerProperty(
                        $"Timeout in seconds (default: {DefaultTimeoutSeconds}, max: 600)."),

                    ["cwd"] = ToolSchema.StringProperty(
                        "Working directory relative to workspace root. Default: workspace root.")
                },
                required: new[] { "shell", "command" }
            ),

            ExecuteAsync = async (id, args) =>
            {
                var shellId = args?.GetValueOrDefault("shell")?.ToString() ?? "";
                var command = args?.GetValueOrDefault("command")?.ToString() ?? "";
                var timeout = TryGetInt(args, "timeout") ?? DefaultTimeoutSeconds;
                var cwd = args?.GetValueOrDefault("cwd")?.ToString();

                // Validate shell
                var shell = shells.FirstOrDefault(s => s.Id == shellId);
                if (shell is null)
                    return $"Error: unknown shell '{shellId}'. Available: {string.Join(", ", shells.Select(s => s.Id))}";

                if (string.IsNullOrWhiteSpace(command))
                    return "Error: command is required.";

                // Resolve working directory
                var root = Path.GetFullPath(workspaceRoot);
                var workDir = root;
                if (!string.IsNullOrWhiteSpace(cwd))
                {
                    var resolved = ResolvePath(root, cwd);
                    if (resolved is null || !Directory.Exists(resolved))
                    {
                        resolved = ResolvePath(root, cwd);
                        return resolved is null
                            ? $"Error: cwd escapes workspace root: {cwd}"
                            : $"Error: cwd not found or not a directory: {cwd}";
                    }
                    workDir = resolved;
                }

                // Clamp timeout
                timeout = Math.Clamp(timeout, 1, 600);

                return await ExecuteAsync(shell, command, workDir, timeout);
            }
        };
    }

    /// <summary>
    /// Detect available shells on this system. Results are cached.
    /// </summary>
    public static IReadOnlyList<ShellInfo> DetectShells()
    {
        if (_detected is not null) return _detected;

        _detected = [];
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        foreach (var def in KnownShells)
        {
            // Skip Unix-only shells on Windows
            if (def.IsUnix && isWindows)
                continue;

            // Try to locate the executable
            var exe = FindExe(def.Exe);
            if (exe is null) continue;

            _detected.Add(new ShellInfo
            {
                Id = def.Id,
                Name = def.Exe,
                Path = exe,
                Args = isWindows && def.WinArgs is not null ? def.WinArgs : def.Args,
            });
        }

        // Ensure at least one shell exists
        if (_detected.Count == 0)
        {
            var fallback = isWindows
                ? new ShellInfo { Id = "cmd", Name = "cmd.exe", Path = "cmd.exe", Args = "/c" }
                : new ShellInfo { Id = "sh",   Name = "/bin/sh",   Path = "/bin/sh",   Args = "-c" };

            _detected.Add(fallback);
        }

        return _detected;
    }

    // -----------------------------------------------------------
    // Execution
    // -----------------------------------------------------------

    private static async Task<string> ExecuteAsync(ShellInfo shell, string command, string cwd, int timeoutSec)
    {
        var psi = new ProcessStartInfo
        {
            FileName = shell.Path,
            Arguments = $"{shell.Args} \"{EscapeCommand(command)}\"",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Set environment variables
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

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
                KillProcessTree(process);
            }

            // Give buffers a moment to flush
            if (!timedOut)
                process.WaitForExit(1000);

            exitCode = timedOut ? -1 : process.ExitCode;
        }
        catch (Exception ex)
        {
            cancelled = true;
            try { process.Kill(true); } catch { /* ignore */ }
            return $"[shell: {shell.Id}] [cwd: {cwd}]\n$ {command}\n\nError: {ex.Message}";
        }

        stopwatch.Stop();

        // Build output
        var sb = new StringBuilder();
        sb.AppendLine($"[shell: {shell.Id}] [cwd: {cwd}]");
        sb.AppendLine($"$ {command}");
        sb.AppendLine();

        var output = stdout.ToString();
        var error = stderr.ToString();

        // Merge stderr into output if stdout is empty (common for build tools)
        if (output.Length == 0 && error.Length > 0)
        {
            output = error;
            error = "";
        }

        // Truncate
        var truncated = false;
        var lines = output.Replace("\r\n", "\n").Split('\n').ToList();
        var totalLines = lines.Count;
        var totalBytes = Encoding.UTF8.GetByteCount(output);

        if (totalLines > MaxOutputLines)
        {
            lines = lines.Take(MaxOutputLines).ToList();
            truncated = true;
        }

        var joined = string.Join("\n", lines);
        if (Encoding.UTF8.GetByteCount(joined) > MaxOutputBytes)
        {
            // Trim bytes while keeping lines intact
            var trimmed = new StringBuilder();
            foreach (var line in lines)
            {
                var withNewline = line + "\n";
                if (Encoding.UTF8.GetByteCount(trimmed.ToString() + withNewline) > MaxOutputBytes)
                {
                    truncated = true;
                    break;
                }
                trimmed.Append(withNewline);
            }
            joined = trimmed.ToString().TrimEnd();
        }

        sb.Append(joined.TrimEnd());

        if (truncated)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine($"[Output truncated. Full: {totalLines:N0} lines, {totalBytes:N0} bytes.]");
        }

        // Stderr (if there is any after merge logic)
        if (error.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("--- stderr ---");
            var errLines = error.Replace("\r\n", "\n").Split('\n');
            sb.Append(string.Join("\n", errLines.Take(50)));
            if (errLines.Length > 50)
                sb.AppendLine($"\n... and {errLines.Length - 50} more stderr lines");
        }

        // Exit status
        sb.AppendLine();
        if (timedOut)
            sb.AppendLine($"--- (timed out after {timeoutSec}s) ---");
        else if (cancelled)
            sb.AppendLine($"--- (cancelled) ---");
        else
            sb.AppendLine($"--- (exit {exitCode}, {stopwatch.Elapsed.TotalSeconds:F1}s) ---");

        return sb.ToString().TrimEnd();
    }

    // -----------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------

    private static string EscapeCommand(string command)
    {
        // Escape double quotes for passing through the shell's -c argument
        return command.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static string? FindExe(string name)
    {
        // Check common paths first
        var paths = new List<string>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name + ".exe"));
            paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name));
            paths.Add(Path.Combine(Environment.SpecialFolder.ProgramFiles.ToString(), "PowerShell", "7", "pwsh.exe"));
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

        // Fall back to PATH lookup
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
        catch
        {
            // PATH lookup failed
        }

        return null;
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Use taskkill /T to kill the process tree
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
                process.Kill(true); // true = entire process tree on .NET
            }
        }
        catch
        {
            try { process.Kill(); } catch { /* best effort */ }
        }
    }

    private static string? ResolvePath(string root, string raw)
    {
        raw = raw.Replace('\\', '/').TrimStart('/');
        var full = Path.GetFullPath(Path.Combine(root, raw));
        return full.StartsWith(root + Path.DirectorySeparatorChar) || full == root
            ? full
            : null;
    }

    private static int? TryGetInt(Dictionary<string, object?>? args, string key)
    {
        if (args?.TryGetValue(key, out var val) != true || val is null) return null;
        if (val is int i) return i;
        if (val is JsonElement je && je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var ji))
            return ji;
        if (val is long l) return (int)l;
        if (val is double d) return (int)d;
        if (int.TryParse(val.ToString(), out var parsed)) return parsed;
        return null;
    }

    // -----------------------------------------------------------
    // Types
    // -----------------------------------------------------------

    private class ShellDef
    {
        public string Id { get; init; } = "";
        public string Exe { get; init; } = "";
        public string Args { get; init; } = "";
        public bool IsUnix { get; init; }
        public string? WinArgs { get; init; }
    }

    public class ShellInfo
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Path { get; init; } = "";
        public string Args { get; init; } = "";
    }
}

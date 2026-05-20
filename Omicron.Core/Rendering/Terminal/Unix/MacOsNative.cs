using System.Runtime.InteropServices;

namespace Omicron.Core.Rendering.Terminal.Unix;

/// <summary>
///     macOS (Darwin) specific P/Invoke definitions for terminal I/O.
///     Do not use these on Linux — the termios layout and ioctl constants differ.
/// </summary>
internal static class MacOsNative
{
    // ── File descriptors ──
    public const int STDIN_FILENO = 0;
    public const int STDOUT_FILENO = 1;

    // ── termios actions ──
    public const int TCSANOW = 0;
    public const int TCSAFLUSH = 2;

    // ── macOS termios flags ──
    // Darwin uses unsigned long (nuint) sized flags, but in practice
    // the lower 32 bits contain the same bit layout as Linux for these.
    public const ulong IGNBRK = 0x00000001;
    public const ulong BRKINT = 0x00000002;
    public const ulong IGNPAR = 0x00000004;
    public const ulong PARMRK = 0x00000008;
    public const ulong INPCK = 0x00000010;
    public const ulong ISTRIP = 0x00000020;
    public const ulong INLCR = 0x00000040;
    public const ulong IGNCR = 0x00000080;
    public const ulong ICRNL = 0x00000100;
    public const ulong IXON = 0x00000200;
    public const ulong IXANY = 0x00000800;
    public const ulong IXOFF = 0x00001000;
    public const ulong IMAXBEL = 0x00002000;
    public const ulong IUTF8 = 0x00004000;

    public const ulong OPOST = 0x00000001;
    public const ulong ONLCR = 0x00000002;
    public const ulong OCRNL = 0x00000010;
    public const ulong ONOCR = 0x00000020;
    public const ulong ONLRET = 0x00000040;

    public const ulong ECHO = 0x00000008;
    public const ulong ECHOE = 0x00000002;
    public const ulong ECHOK = 0x00000004;
    public const ulong ECHONL = 0x00000010;
    public const ulong ICANON = 0x00000100;
    public const ulong ISIG = 0x00000080;
    public const ulong IEXTEN = 0x00000400;

    public const ulong CS8 = 0x00000300;
    public const ulong CREAD = 0x00000800;

    // ── c_cc indices (macOS) ──
    public const int VEOF = 0;
    public const int VEOL = 1;
    public const int VERASE = 3;
    public const int VKILL = 5;
    public const int VINTR = 8;
    public const int VQUIT = 9;
    public const int VSTART = 12;
    public const int VSTOP = 13;
    public const int VSUSP = 14;
    public const int VMIN = 16;
    public const int VTIME = 17;
    public const int VLNEXT = 15;
    public const int VWERASE = 4;
    public const int VREPRINT = 6;

    // ── ioctl (macOS uses different constants) ──
    // TIOCGWINSZ on macOS is 0x40087468
    public const ulong TIOCGWINSZ = 0x40087468;

    // ── poll ──
    public const short POLLIN = 1;
    public const short POLLPRI = 2;
    public const short POLLOUT = 4;
    public const short POLLERR = 8;
    public const short POLLHUP = 0x10;
    public const short POLLNVAL = 0x20;

    // ── errno ──
    public const int EAGAIN = 35;
    public const int EINTR = 4;
    public const int EIO = 5;

    // ── fcntl ──
    public const int F_GETFL = 3;
    public const int F_SETFL = 4;
    public const int O_NONBLOCK = 0x0004;

    // ── P/Invoke ──

    [DllImport("libSystem", SetLastError = true)]
    public static extern int tcgetattr(int fd, out Termios termios);

    [DllImport("libSystem", SetLastError = true)]
    public static extern int tcsetattr(int fd, int optional_actions, ref Termios termios);

    /// <summary>
    ///     Regular ioctl for macOS x64. On ARM64, ioctl is variadic and requires
    ///     register padding — use <see cref="ioctl_arm64" /> instead.
    /// </summary>
    [DllImport("libSystem", SetLastError = true)]
    public static extern int ioctl(int fd, ulong request, ref Winsize wsz);

    /// <summary>
    ///     macOS ARM64 ioctl workaround: pad registers x2–x7 with dummy ulong args
    ///     so the real variadic argument (ref Winsize) lands on the stack where
    ///     the ARM64 variadic ABI expects it.
    ///     See https://github.com/dotnet/runtime/issues/48752
    /// </summary>
    [DllImport("libSystem", SetLastError = true, EntryPoint = "ioctl")]
    public static extern int ioctl_arm64(
        int fd,
        ulong request,
        ulong x2,
        ulong x3,
        ulong x4,
        ulong x5,
        ulong x6,
        ulong x7,
        ref Winsize wsz);

    [DllImport("libSystem", SetLastError = true)]
    public static extern int poll(ref PollFd fds, nuint nfds, int timeout);

    [DllImport("libSystem", SetLastError = true)]
    public static extern nint read(int fd, byte[] buffer, UIntPtr count);

    [DllImport("libSystem", SetLastError = true)]
    public static extern int fcntl(int fd, int cmd, int arg);

    [DllImport("libSystem", SetLastError = true)]
    public static extern int fcntl(int fd, int cmd);

    [DllImport("libSystem", SetLastError = true)]
    public static extern nint write(int fd, byte[] buf, UIntPtr count);

    // ── Structs ──

    /// <summary>
    ///     macOS (Darwin/XNU) termios layout:
    ///     4× nuint (ulong) flags, no c_line, byte[20] c_cc, nuint c_ispeed, nuint c_ospeed.
    ///     NCCS = 20 on macOS.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Termios
    {
        public ulong c_iflag;
        public ulong c_oflag;
        public ulong c_cflag;
        public ulong c_lflag;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
        public byte[] c_cc;

        public ulong c_ispeed;
        public ulong c_ospeed;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Winsize
    {
        public ushort ws_row;
        public ushort ws_col;
        public ushort ws_xpixel;
        public ushort ws_ypixel;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int fd;
        public short events;
        public short revents;
    }
}

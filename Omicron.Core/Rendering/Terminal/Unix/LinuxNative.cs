using System.Runtime.InteropServices;

namespace Omicron.Core.Rendering.Terminal.Unix;

/// <summary>
/// Linux-specific P/Invoke definitions for terminal I/O.
/// Do not use these on macOS — the termios layout and ioctl constants differ.
/// </summary>
internal static class LinuxNative
{
    // ── File descriptors ──
    public const int STDIN_FILENO = 0;
    public const int STDOUT_FILENO = 1;

    // ── termios actions ──
    public const int TCSANOW = 0;
    public const int TCSAFLUSH = 2;

    // ── Linux termios flags ──
    public const uint IGNBRK = 0x0001;
    public const uint BRKINT = 0x0002;
    public const uint IGNPAR = 0x0004;
    public const uint PARMRK = 0x0008;
    public const uint INPCK = 0x0010;
    public const uint ISTRIP = 0x0020;
    public const uint INLCR = 0x0040;
    public const uint IGNCR = 0x0080;
    public const uint ICRNL = 0x0100;
    public const uint IUCLC = 0x0200;
    public const uint IXON = 0x0400;
    public const uint IXANY = 0x0800;
    public const uint IXOFF = 0x1000;
    public const uint IMAXBEL = 0x2000;
    public const uint IUTF8 = 0x4000;

    public const uint OPOST = 0x0001;
    public const uint ONLCR = 0x0004;
    public const uint OCRNL = 0x0008;
    public const uint ONOCR = 0x0010;
    public const uint ONLRET = 0x0020;

    public const uint ECHO = 0x0008;
    public const uint ECHOE = 0x0010;
    public const uint ECHOK = 0x0020;
    public const uint ECHONL = 0x0040;
    public const uint ICANON = 0x0002;
    public const uint ISIG = 0x0001;
    public const uint IEXTEN = 0x8000;

    public const uint CS8 = 0x0030;
    public const uint CREAD = 0x0080;

    // ── c_cc indices (Linux) ──
    public const int VINTR = 0;
    public const int VQUIT = 1;
    public const int VERASE = 2;
    public const int VKILL = 3;
    public const int VEOF = 4;
    public const int VTIME = 5;
    public const int VMIN = 6;
    public const int VSTART = 8;
    public const int VSTOP = 9;
    public const int VSUSP = 10;
    public const int VEOL = 11;
    public const int VREPRINT = 12;
    public const int VDISCARD = 13;
    public const int VWERASE = 14;
    public const int VLNEXT = 15;

    // ── ioctl ──
    public const int TIOCGWINSZ = 0x5413;

    // ── poll ──
    public const short POLLIN = 1;
    public const short POLLPRI = 2;
    public const short POLLOUT = 4;
    public const short POLLERR = 8;
    public const short POLLHUP = 0x10;
    public const short POLLNVAL = 0x20;

    // ── errno ──
    public const int EAGAIN = 11;
    public const int EINTR = 4;
    public const int EIO = 5;

    // ── fcntl (used only for save/restore) ──
    public const int F_GETFL = 3;
    public const int F_SETFL = 4;
    public const int O_NONBLOCK = 0x800;

    // ── Structs ──

    /// <summary>
    /// Linux (glibc) termios layout:
    /// 4× uint flags, byte c_line, byte[32] c_cc, uint c_ispeed, uint c_ospeed.
    /// NCCS = 32 on Linux.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Termios
    {
        public uint c_iflag;
        public uint c_oflag;
        public uint c_cflag;
        public uint c_lflag;
        public byte c_line;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] c_cc;

        public uint c_ispeed;
        public uint c_ospeed;
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

    // ── P/Invoke ──

    [DllImport("libc", SetLastError = true)]
    public static extern int tcgetattr(int fd, out Termios termios);

    [DllImport("libc", SetLastError = true)]
    public static extern int tcsetattr(int fd, int optional_actions, ref Termios termios);

    [DllImport("libc", SetLastError = true)]
    public static extern int ioctl(int fd, int request, ref Winsize wsz);

    [DllImport("libc", SetLastError = true)]
    public static extern int poll(ref PollFd fds, nuint nfds, int timeout);

    [DllImport("libc", SetLastError = true)]
    public static extern nint read(int fd, byte[] buffer, UIntPtr count);

    [DllImport("libc", SetLastError = true)]
    public static extern int fcntl(int fd, int cmd, int arg);

    [DllImport("libc", SetLastError = true)]
    public static extern int fcntl(int fd, int cmd);

    [DllImport("libc", SetLastError = true)]
    public static extern nint write(int fd, byte[] buf, UIntPtr count);
}

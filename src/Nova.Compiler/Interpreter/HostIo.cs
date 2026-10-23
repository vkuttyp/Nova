using System.Reflection;
using System.Runtime.InteropServices;

namespace Nova.Compiler.Interpreter;

/// <summary>
/// The host C library for the interpreter's files and sockets (intermediate-representation.md §5.4), so
/// that flags, results and errno values are the native runtime's (nova_rt_io.c). Linux and macOS only.
/// </summary>
internal static class HostIo
{
    static HostIo()
    {
        NativeLibrary.SetDllImportResolver(typeof(HostIo).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path) =>
        name != "libc" ? IntPtr.Zero
        : OperatingSystem.IsLinux() ? NativeLibrary.Load("libc.so.6")
        : OperatingSystem.IsMacOS() ? NativeLibrary.Load("/usr/lib/libSystem.B.dylib")
        : IntPtr.Zero;

    public static bool Supported => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    private static bool Mac => OperatingSystem.IsMacOS();

    // errno values (the host's), as nova_io_kind maps them.
    public const int EPERM = 1, ENOENT = 2, EINTR = 4, EIO = 5, ENOMEM = 12, EACCES = 13, EEXIST = 17, ENOTDIR = 20,
        EISDIR = 21, EINVAL = 22, EPIPE = 32;

    public static int ENAMETOOLONG => Mac ? 63 : 36;

    public static int ECONNREFUSED => Mac ? 61 : 111;

    public static int EADDRINUSE => Mac ? 48 : 98;

    private static int ENOTSOCK => Mac ? 38 : 88;

    private const int AF_INET = 2, SOCK_STREAM = 1, SHUT_WR = 1, O_RDONLY = 0;

    private static int SOL_SOCKET => Mac ? 0xFFFF : 1;

    private static int SO_REUSEADDR => Mac ? 4 : 2;

    private const int SO_NOSIGPIPE = 0x1022; // macOS
    private const int MSG_NOSIGNAL = 0x4000; // Linux

    /// <summary>nova_io_kind: the IoError variant for an errno value.</summary>
    public static uint Kind(int error)
    {
        if (error == ENOENT) return 0;
        if (error is EACCES or EPERM) return 1;
        if (error == EEXIST) return 2;
        if (error == EISDIR) return 3;
        if (error == ENOTDIR) return 4;
        if (error == EINVAL || error == ENAMETOOLONG) return 5;
        if (error == EPIPE) return 6;
        if (error == ECONNREFUSED) return 7;
        if (error == EADDRINUSE) return 8;
        if (error == ENOMEM) return 9;
        return 10;
    }

    private static int Errno()
    {
        var error = Marshal.GetLastPInvokeError();
        return error != 0 ? error : EIO;
    }

    /// <summary>
    /// A NUL-terminated path; null if it contains a zero byte or is too long (nova_c_path). A relative path is
    /// resolved against <paramref name="directory"/> when one is given (the interpreter's working directory).
    /// </summary>
    private static byte[]? CPath(byte[] path, string? directory)
    {
        if (path.Length >= 4096 || Array.IndexOf(path, (byte)0) >= 0)
        {
            return null;
        }
        if (directory is not null && (path.Length == 0 || path[0] != '/'))
        {
            path = [.. System.Text.Encoding.UTF8.GetBytes(directory.TrimEnd('/') + "/"), .. path];
        }
        var buffer = new byte[path.Length + 1];
        path.CopyTo(buffer, 0);
        return buffer;
    }

    /// <summary>nova_file_open: mode 0 reads, 1 creates or truncates for writing (creat is open(O_WRONLY|O_CREAT|O_TRUNC)).</summary>
    public static (long Value, int Error) Open(byte[] path, uint mode, string? directory)
    {
        if (CPath(path, directory) is not { } name)
        {
            return (0, EINVAL);
        }
        int fd;
        do
        {
            fd = mode == 0 ? open(name, O_RDONLY) : creat(name, 0x1A4 /* 0644 */);
        } while (fd < 0 && Marshal.GetLastPInvokeError() == EINTR);
        return fd < 0 ? (0, Errno()) : (fd, 0);
    }

    public static (long Value, int Error) Remove(byte[] path, string? directory) =>
        CPath(path, directory) is not { } name ? (0, EINVAL) : unlink(name) < 0 ? (0, Errno()) : (0, 0);

    public static (long Value, int Error) Read(int fd, byte[] buffer)
    {
        nint n;
        do
        {
            n = read(fd, buffer, Math.Min(buffer.Length, 1 << 30));
        } while (n < 0 && Marshal.GetLastPInvokeError() == EINTR);
        return n < 0 ? (0, Errno()) : (n, 0);
    }

    /// <summary>nova_fd_write: all the bytes, or an error.</summary>
    public static (long Value, int Error) Write(int fd, byte[] bytes)
    {
        var done = 0;
        while (done < bytes.Length)
        {
            var chunk = bytes[done..Math.Min(bytes.Length, done + (1 << 30))];
            nint n;
            if (OperatingSystem.IsLinux())
            {
                n = send(fd, chunk, chunk.Length, MSG_NOSIGNAL);
                if (n < 0 && Marshal.GetLastPInvokeError() == ENOTSOCK)
                {
                    n = write(fd, chunk, chunk.Length);
                }
            }
            else
            {
                n = write(fd, chunk, chunk.Length);
            }
            if (n < 0 && Marshal.GetLastPInvokeError() == EINTR)
            {
                continue;
            }
            if (n < 0)
            {
                return (0, Errno());
            }
            if (n == 0)
            {
                return (0, EIO);
            }
            done += (int)n;
        }
        return (0, 0);
    }

    public static void Close(int fd)
    {
        if (fd >= 0)
        {
            _ = close(fd);
        }
    }

    /// <summary>Dotted IPv4 text to its four bytes (nova_ipv4).</summary>
    private static byte[]? Ipv4(byte[] text)
    {
        var bytes = new byte[4];
        var i = 0;
        for (var part = 0; part < 4; part++)
        {
            if (part > 0)
            {
                if (i >= text.Length || text[i] != '.')
                {
                    return null;
                }
                i++;
            }
            uint value = 0;
            var digits = 0;
            while (i < text.Length && text[i] is >= (byte)'0' and <= (byte)'9' && digits < 3)
            {
                value = value * 10 + (uint)(text[i] - '0');
                i++;
                digits++;
            }
            if (digits == 0 || value > 255)
            {
                return null;
            }
            bytes[part] = (byte)value;
        }
        return i == text.Length ? bytes : null;
    }

    /// <summary>A sockaddr_in (macOS: with sin_len).</summary>
    private static byte[] Address(byte[] ip, ushort port)
    {
        var a = new byte[16];
        if (Mac)
        {
            a[0] = 16;
            a[1] = AF_INET;
        }
        else
        {
            a[0] = AF_INET;
        }
        a[2] = (byte)(port >> 8);
        a[3] = (byte)port;
        ip.CopyTo(a, 4);
        return a;
    }

    private static int Socket()
    {
        var fd = socket(AF_INET, SOCK_STREAM, 0);
        if (fd >= 0 && Mac)
        {
            var one = 1;
            _ = setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, ref one, 4);
        }
        return fd;
    }

    public static (long Value, int Error) Listen(byte[] address, ushort port)
    {
        if (Ipv4(address) is not { } ip)
        {
            return (0, EINVAL);
        }
        var fd = Socket();
        if (fd < 0)
        {
            return (0, Errno());
        }
        var one = 1;
        _ = setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, ref one, 4);
        var a = Address(ip, port);
        if (bind(fd, a, 16) < 0 || listen(fd, 16) < 0)
        {
            var error = Errno();
            _ = close(fd);
            return (0, error);
        }
        return (fd, 0);
    }

    public static (long Value, int Error) Port(int fd)
    {
        var a = new byte[16];
        var length = 16u;
        return getsockname(fd, a, ref length) < 0 ? (0, Errno()) : ((a[2] << 8) | a[3], 0);
    }

    public static (long Value, int Error) Accept(int fd)
    {
        int peer;
        do
        {
            peer = accept(fd, IntPtr.Zero, IntPtr.Zero);
        } while (peer < 0 && Marshal.GetLastPInvokeError() == EINTR);
        if (peer < 0)
        {
            return (0, Errno());
        }
        if (Mac)
        {
            var one = 1;
            _ = setsockopt(peer, SOL_SOCKET, SO_NOSIGPIPE, ref one, 4);
        }
        return (peer, 0);
    }

    public static (long Value, int Error) Connect(byte[] address, ushort port)
    {
        if (Ipv4(address) is not { } ip)
        {
            return (0, EINVAL);
        }
        var fd = Socket();
        if (fd < 0)
        {
            return (0, Errno());
        }
        if (connect(fd, Address(ip, port), 16) < 0)
        {
            var error = Errno();
            _ = close(fd);
            return (0, error);
        }
        return (fd, 0);
    }

    public static (long Value, int Error) ShutdownWrite(int fd) => shutdown(fd, SHUT_WR) < 0 ? (0, Errno()) : (0, 0);

#pragma warning disable SYSLIB1054, CA5392, IDE1006 // plain P/Invoke of the C library, with C's names
    [DllImport("libc", SetLastError = true)]
    private static extern int open(byte[] path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int creat(byte[] path, int mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int unlink(byte[] path);

    [DllImport("libc", SetLastError = true)]
    private static extern nint read(int fd, byte[] buffer, nint count);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int fd, byte[] buffer, nint count);

    [DllImport("libc", SetLastError = true)]
    private static extern nint send(int fd, byte[] buffer, nint count, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int socket(int domain, int type, int protocol);

    [DllImport("libc", SetLastError = true)]
    private static extern int setsockopt(int fd, int level, int name, ref int value, uint length);

    [DllImport("libc", SetLastError = true)]
    private static extern int bind(int fd, byte[] address, uint length);

    [DllImport("libc", SetLastError = true)]
    private static extern int listen(int fd, int backlog);

    [DllImport("libc", SetLastError = true)]
    private static extern int accept(int fd, IntPtr address, IntPtr length);

    [DllImport("libc", SetLastError = true)]
    private static extern int connect(int fd, byte[] address, uint length);

    [DllImport("libc", SetLastError = true)]
    private static extern int getsockname(int fd, byte[] address, ref uint length);

    [DllImport("libc", SetLastError = true)]
    private static extern int shutdown(int fd, int how);
#pragma warning restore SYSLIB1054, CA5392, IDE1006
}

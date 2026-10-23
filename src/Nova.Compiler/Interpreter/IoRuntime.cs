using System.Diagnostics;
using System.Text;
using Nova.Compiler.Hir;
using Nova.Compiler.Nir;
using Nova.Compiler.Semantics;

namespace Nova.Compiler.Interpreter;

// Program arguments, files, sockets and processes (type-system.md §26; nova_rt_io.c and the emitter's
// nvIOR_/nvARG helpers), for the interpreter (intermediate-representation.md §5.4).
public sealed partial class NirInterpreter
{
    private static readonly string[] s_ioMessages =
    [
        "No such file or directory", "Permission denied", "File exists", "Is a directory",
        "Not a directory", "Invalid argument", "Broken pipe", "Connection refused",
        "Address already in use", "Out of memory", "Input/output error",
    ];

    private readonly List<(ulong Pointer, ulong Length, bool Valid)> _arguments = [];

    /// <summary>nova_rt_args: argv as static memory (never copied by the native runtime either).</summary>
    private void StartArguments()
    {
        foreach (var argument in _options.Arguments)
        {
            var bytes = Encoding.UTF8.GetBytes(argument);
            var pointer = bytes.Length == 0 ? _emptyVector : _memory.Static(bytes, "a program argument").Base;
            _arguments.Add((pointer, (ulong)bytes.Length, true));
        }
    }

    private static void CloseDescriptor(int fd) => HostIo.Close(fd);

    /// <summary>nvIOR_: <c>Err</c> with the IoError variant for the errno (its number kept for <c>Other</c>), or <c>Ok</c>.</summary>
    private static Value IoResult(NovaType result, (long Value, int Error) outcome)
    {
        var (ok, error) = result.Enum!.ResultTypes!.Value;
        if (outcome.Error != 0)
        {
            var kind = HostIo.Kind(outcome.Error);
            var variant = error.Enum!.Variants[(int)kind];
            var payload = variant.Payload.Count == 0 ? [] : new[] { FromInteger(outcome.Error, variant.Payload[0].Type) };
            return Err(result, EnumValue(error, variant, payload));
        }
        return ok.Kind == TypeKind.Void ? Ok(result, null) : Ok(result, FromInteger(outcome.Value, ok));
    }

    private static void RequireHostIo()
    {
        if (!HostIo.Supported)
        {
            throw new InterpreterFault("unsupported: files, sockets and processes need Linux or macOS");
        }
    }

    private Value? IoBuiltin(Frame frame, NirBuiltin builtin)
    {
        var arguments = builtin.Arguments;
        Value Argument(int i) => Operand(frame, arguments[i]);
        int Descriptor(int i) => (int)(uint)_memory.LoadBits(Argument(i).Bits(0, 8), 4);
        switch (builtin.Function)
        {
            case BuiltinFunction.ArgumentCount:
                return Value.Of((ulong)_arguments.Count, 8);
            case BuiltinFunction.Argument:
            {
                var index = (ulong)Integer(Argument(0), arguments[0].Type);
                return index >= (ulong)_arguments.Count
                    ? None(builtin.Type)
                    : Some(builtin.Type, View(_arguments[(int)index].Pointer, _arguments[(int)index].Length));
            }
            case BuiltinFunction.IoErrorMessage:
            {
                var (_, _, tagSize, _) = TypeTable.EnumLayout(arguments[0].Type);
                var kind = (int)Math.Min(Argument(0).Bits(0, tagSize), 10);
                var text = s_ioMessages[kind];
                return View(Literal(text), (ulong)text.Length);
            }
            case BuiltinFunction.FileOpen or BuiltinFunction.FileCreate:
                RequireHostIo();
                return IoResult(builtin.Type, HostIo.Open(Bytes(Argument(0)), builtin.Function == BuiltinFunction.FileOpen ? 0u : 1u, _options.WorkingDirectory));
            case BuiltinFunction.FileRemove:
                RequireHostIo();
                return IoResult(builtin.Type, HostIo.Remove(Bytes(Argument(0)), _options.WorkingDirectory));
            case BuiltinFunction.ResourceRead:
            {
                RequireHostIo();
                var fd = Descriptor(0);
                return IoResult(builtin.Type, ReadInto(Argument(1), buffer => HostIo.Read(fd, buffer)));
            }
            case BuiltinFunction.ConsoleRead:
            {
                var input = _options.StandardInput ?? (_standardInput ??= Console.OpenStandardInput());
                return IoResult(builtin.Type, ReadInto(Argument(0), buffer => (input.Read(buffer, 0, Math.Min(buffer.Length, 1 << 30)), 0)));
            }
            case BuiltinFunction.ResourceWrite:
                RequireHostIo();
                return IoResult(builtin.Type, HostIo.Write(Descriptor(0), Bytes(Argument(1))));
            case BuiltinFunction.TcpListen or BuiltinFunction.TcpConnect:
            {
                RequireHostIo();
                var address = Bytes(Argument(0));
                var port = (ushort)Integer(Argument(1), arguments[1].Type);
                return IoResult(builtin.Type, builtin.Function == BuiltinFunction.TcpListen ? HostIo.Listen(address, port) : HostIo.Connect(address, port));
            }
            case BuiltinFunction.TcpPort:
                RequireHostIo();
                return IoResult(builtin.Type, HostIo.Port(Descriptor(0)));
            case BuiltinFunction.TcpAccept:
                RequireHostIo();
                return IoResult(builtin.Type, HostIo.Accept(Descriptor(0)));
            case BuiltinFunction.TcpShutdownWrite:
                RequireHostIo();
                return IoResult(builtin.Type, HostIo.ShutdownWrite(Descriptor(0)));
            case BuiltinFunction.ProcessRun:
                RequireHostIo();
                return IoResult(builtin.Type, ProcessRun(Argument(0), Argument(1).Bits(0, 8)));
            default:
                throw new InvalidOperationException($"cannot interpret built-in {builtin.Function}");
        }
    }

    private Stream? _standardInput;

    /// <summary>Reads into the buffer a <c>SliceMut&lt;u8&gt;</c> names; the bytes read become initialised.</summary>
    private (long Value, int Error) ReadInto(Value slice, Func<byte[], (long Value, int Error)> read)
    {
        var pointer = slice.Bits(0, 8);
        var length = slice.Bits(8, 8);
        var buffer = new byte[Math.Min(length, 1UL << 30)];
        if (buffer.Length > 0)
        {
            _memory.Load(pointer, buffer.Length); // the whole buffer must be live and in bounds
        }
        var outcome = read(buffer);
        if (outcome.Error == 0 && outcome.Value > 0)
        {
            _memory.Store(pointer, Value.Initialised(buffer[..(int)outcome.Value]));
        }
        return outcome;
    }

    /// <summary>
    /// nova_process_run: the argument copies are counted allocations (argv, then each string, stopping at the
    /// first failure); standard output is flushed; the child's exit status, or 128 + its signal.
    /// </summary>
    private (long Value, int Error) ProcessRun(Value program, ulong vector)
    {
        var count = _memory.LoadBits(vector + 8, 8);
        if (count > 4096)
        {
            return (0, HostIo.EINVAL);
        }
        var texts = new List<byte[]> { Bytes(program) };
        for (ulong i = 0; i < count; i++)
        {
            texts.Add(Bytes(_memory.Load(Pointer(vector) + i * 16, 16)));
        }
        var argv = Alloc((count + 2) * 8, AllocSystem);
        if (argv == 0)
        {
            return (0, HostIo.ENOMEM);
        }
        var made = new List<ulong>();
        var error = 0;
        foreach (var text in texts)
        {
            if (Array.IndexOf(text, (byte)0) >= 0)
            {
                error = HostIo.EINVAL;
                break;
            }
            var copy = Alloc((ulong)text.Length + 1, AllocSystem);
            if (copy == 0)
            {
                error = HostIo.ENOMEM;
                break;
            }
            made.Add(copy);
        }
        (long Value, int Error) result = (0, error);
        if (error == 0)
        {
            _options.StandardOutput.Flush();
            result = Spawn([.. texts.Select(t => Encoding.UTF8.GetString(t))]);
        }
        foreach (var copy in made)
        {
            Free(copy, AllocSystem);
        }
        Free(argv, AllocSystem);
        return result;
    }

    private (long Value, int Error) Spawn(string[] argv)
    {
        var info = new ProcessStartInfo(argv[0]) { UseShellExecute = false, WorkingDirectory = _options.WorkingDirectory ?? "" };
        foreach (var argument in argv.Skip(1))
        {
            info.ArgumentList.Add(argument);
        }
        var capture = !_options.InheritStandardStreams;
        info.RedirectStandardOutput = capture;
        info.RedirectStandardError = capture;
        try
        {
            using var process = Process.Start(info)!;
            if (capture)
            {
                var output = Task.Run(() => process.StandardOutput.BaseStream.CopyTo(_options.StandardOutput));
                var errors = new MemoryStream();
                var errorCopy = Task.Run(() => process.StandardError.BaseStream.CopyTo(errors));
                process.WaitForExit();
                Task.WaitAll(output, errorCopy);
                if (errors.Length > 0)
                {
                    WriteError(errors.ToArray());
                }
            }
            else
            {
                process.WaitForExit();
            }
            return (process.ExitCode, 0);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return (0, e.NativeErrorCode != 0 ? e.NativeErrorCode : HostIo.ENOENT);
        }
    }
}

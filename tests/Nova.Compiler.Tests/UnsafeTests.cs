using Nova.Compiler.Interpreter;

namespace Nova.Compiler.Tests;

/// <summary>
/// Unsafe code (unsafe.md §5): the interpreter reports invalid accesses through raw pointers as faults.
/// The checker's rules are in the corpus (check-fail/unsafe-*.nova, raw-pointer-types.nova), and valid
/// pointer programs run in every C configuration and in the interpreter (run-pass/raw-*.nova).
/// </summary>
public class UnsafeTests
{
    private const string Prelude = """
        /// Safety: none; each program below breaks the contract on purpose.
        fn dangling() -> *u32 {
            let x = 7u32;
            return borrow x as *u32;
        }

        """;

    [Theory]
    [InlineData("Console.writeLine(unsafe { dangling().read() });", "access to ended memory")]
    [InlineData("var a = [1u32, 2]; let p = borrow a[0] as *u32; Console.writeLine(unsafe { p.add(2).read() });", "access out of bounds")]
    [InlineData("var a = [1u32, 2]; let p = borrow a[0] as *u32; Console.writeLine(unsafe { (((p as usize) + 1) as *u32).read() });", "misaligned access")]
    [InlineData("Console.writeLine(unsafe { (0 as *u32).read() });", "dereference of null")]
    [InlineData("let p = \"abc\".ptr as *mut u8; unsafe { p.write(65); }", "write to static memory")]
    [InlineData("var a = [1u8, 2]; let s = unsafe { Slice.fromRaw(borrow a[0] as *u8, 3) }; Console.writeLine(s[2]);", "access out of bounds")]
    public void Invalid_accesses_through_raw_pointers_are_interpreter_faults(string body, string fault)
    {
        var program = Prelude + "/// Safety: none.\nfn main() -> i32 { " + body + " return 0; }\n";
        var outcome = InterpreterTests.Interpret("t.nova", program);
        Assert.Equal(NirInterpreter.FaultExitStatus, outcome.ExitCode);
        Assert.StartsWith($"nova interp: fault: {fault}", outcome.Stderr, StringComparison.Ordinal);
        Assert.Contains(" at t.nova:", outcome.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Valid_accesses_through_raw_pointers_run()
    {
        const string Program = """
            /// Safety: the pointer stays inside the live array.
            fn main() -> i32 {
                var a = [1u32, 2, 3];
                let p = borrow mut a[0] as *mut u32;
                unsafe { p.add(2).write(p.read() + p.add(1).read()); }
                Console.writeLine(a[2]);
                return 0;
            }
            """;
        var outcome = InterpreterTests.Interpret("t.nova", Program);
        Assert.Equal(("3\n", "", 0), (outcome.Stdout, outcome.Stderr, outcome.ExitCode));
    }
}

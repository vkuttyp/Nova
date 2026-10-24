using Nova.Compiler.Diagnostics;
using Nova.Compiler.Semantics;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Tests;

/// <summary>
/// <c>private</c> struct fields (type-system.md §12, C7c): reachable only in the file that declares the struct. The corpus
/// has one file per test, so the cross-file rule is tested here.
/// </summary>
public class PrivateFieldTests
{
    private const string Library = """
        struct Cell<T> {
            private value: T;
            public label: u32;
            fn make(v: T) -> Cell<T> => Cell { value: v, label: 1 };
            fn get(borrow this) -> borrow T => borrow this.value;
        }

        /// Another type in the same file shares the fields.
        struct Peek { n: u32; }
        fn peek(c: borrow Cell<u32>) -> u32 => c.value + c.label;
        """;

    private static List<(int Line, string Code)> Errors(string user)
    {
        var compilation = Compilation.Create([new SourceText("lib.nova", Library), new SourceText("user.nova", user)], TargetInfo.Lp64);
        return [.. compilation.Diagnostics.Items.Select(d => (d.Source.Path == "user.nova" ? d.Position.Line : -d.Position.Line, d.Code))];
    }

    [Fact]
    public void Other_files_reach_private_fields_only_through_the_struct_s_functions()
    {
        const string User = """
            fn main() -> i32 {
                let c = Cell<u32>.make(41);
                let fine = c.get() + c.label + peek(borrow c);
                return truncate<i32>(fine);
            }
            """;
        Assert.Empty(Errors(User));
    }

    [Theory]
    [InlineData("fn f(c: borrow Cell<u32>) -> u32 => c.value;", 1)]                       // read
    [InlineData("fn f(c: borrow mut Cell<u32>) { c.value = 2; }", 1)]                      // assigned
    [InlineData("fn f(c: borrow Cell<u32>) -> borrow u32 => borrow c.value;", 1)]          // borrowed
    [InlineData("fn f() -> Cell<u8> => Cell { value: 1u8, label: 2 };", 1)]                // given in a literal
    public void Naming_a_private_field_in_another_file_is_N0296(string user, int line)
    {
        Assert.Equal([(line, DiagnosticCodes.PrivateField)], Errors(user));
    }
}

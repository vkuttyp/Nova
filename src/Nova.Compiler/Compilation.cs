using System.Collections.Immutable;
using Nova.Compiler.Diagnostics;
using Nova.Compiler.Hir;
using Nova.Compiler.Semantics;
using Nova.Compiler.Syntax;

namespace Nova.Compiler;

/// <summary>
/// One compilation: a set of source files of one package, compiled for one target.
/// Semantic analysis runs only if every file parsed without errors. This avoids cascades
/// from error placeholders (docs/diagnostics.md).
/// </summary>
public sealed class Compilation
{
    private Compilation(
        ImmutableArray<CompilationUnit> units,
        SymbolTable symbols,
        TypeTable types,
        DiagnosticBag diagnostics,
        HirProgram? hir)
    {
        Units = units;
        Symbols = symbols;
        Types = types;
        Diagnostics = diagnostics;
        Hir = hir;
    }

    public ImmutableArray<CompilationUnit> Units { get; }

    public SymbolTable Symbols { get; }

    public TypeTable Types { get; }

    public DiagnosticBag Diagnostics { get; }

    /// <summary>The checked program, or null when parsing failed. May still contain errors: check <see cref="Diagnostics"/>.</summary>
    public HirProgram? Hir { get; }

    public static Compilation Create(IEnumerable<SourceText> sources, TargetInfo target)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(target);

        var symbols = new SymbolTable();
        var diagnostics = new DiagnosticBag();
        var types = new TypeTable(target);
        var units = sources.Select(source => Parser.ParseText(source, symbols, diagnostics)).ToImmutableArray();

        HirProgram? hir = null;
        if (!diagnostics.HasErrors)
        {
            hir = new TypeChecker(units, symbols, types, diagnostics).Check();
        }
        return new Compilation(units, symbols, types, diagnostics, hir);
    }
}

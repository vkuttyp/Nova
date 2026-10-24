using System.Collections.Immutable;
using Nova.Compiler.Analysis;
using Nova.Compiler.Diagnostics;
using Nova.Compiler.Hir;
using Nova.Compiler.Nir;
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
        Profile profile,
        ImmutableArray<CompilationUnit> units,
        SymbolTable symbols,
        TypeTable types,
        DiagnosticBag diagnostics,
        HirProgram? hir,
        NirProgram? nir)
    {
        Profile = profile;
        Nir = nir;
        Units = units;
        Symbols = symbols;
        Types = types;
        Diagnostics = diagnostics;
        Hir = hir;
    }

    public ImmutableArray<CompilationUnit> Units { get; }

    /// <summary>What the package may use and which runtime it gets (cosmoos/profiles.md §4).</summary>
    public Profile Profile { get; }

    public SymbolTable Symbols { get; }

    public TypeTable Types { get; }

    public DiagnosticBag Diagnostics { get; }

    /// <summary>The checked program, or null when parsing failed. May still contain errors: check <see cref="Diagnostics"/>.</summary>
    public HirProgram? Hir { get; }

    /// <summary>The NIR of a program that type-checked; borrow-checked. Null if earlier phases failed.</summary>
    public NirProgram? Nir { get; }

    public static Compilation Create(IEnumerable<SourceText> sources, TargetInfo target, Profile profile = Profile.Hosted)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(target);

        var symbols = new SymbolTable();
        var diagnostics = new DiagnosticBag();
        var types = new TypeTable(target);
        // lib/core comes first, so a package that redeclares one of its names gets the error (type-system.md §29.2).
        var units = CoreLibrary.Sources.Concat(sources).Select(source => Parser.ParseText(source, symbols, diagnostics)).ToImmutableArray();

        HirProgram? hir = null;
        NirProgram? nir = null;
        if (!diagnostics.HasErrors)
        {
            hir = new TypeChecker(units, symbols, types, diagnostics, profile).Check();
        }
        if (hir is not null && !diagnostics.HasErrors)
        {
            // Ownership analyses run on NIR (lifetimes.md), after type checking succeeded.
            nir = NirBuilder.Build(hir, types);
            // Generic code is checked once, in its generic version; instantiations repeat it (type-system.md §20.3).
            foreach (var function in nir.Functions.Where(f => !f.Source.IsInstantiation))
            {
                MoveChecker.Check(function, symbols, diagnostics);
                BorrowChecker.Check(function, symbols, diagnostics);
            }
            if (!diagnostics.HasErrors)
            {
                // Destruction is inserted only into programs that passed every check (type-system.md §14.5),
                // and only into code that is compiled: not into generic versions.
                foreach (var function in nir.Functions.Where(f => !f.Source.IsGeneric))
                {
                    DropElaborator.Elaborate(function, types);
                }
            }
        }
        return new Compilation(profile, units, symbols, types, diagnostics, hir, nir);
    }
}

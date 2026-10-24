using System.Collections.Immutable;
using System.Reflection;
using Nova.Compiler.Syntax;

namespace Nova.Compiler;

/// <summary>
/// <c>lib/core</c>: Nova sources embedded in the compiler and part of every package (type-system.md §29.2).
/// Their paths start with <see cref="PathPrefix"/>, which also marks their items as library code.
/// </summary>
public static class CoreLibrary
{
    public const string PathPrefix = "lib/core/";

    /// <summary>The library's files, in name order (deterministic).</summary>
    public static ImmutableArray<SourceText> Sources { get; } = Load();

    public static bool Contains(SourceText source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Path.StartsWith(PathPrefix, StringComparison.Ordinal);
    }

    private static ImmutableArray<SourceText> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return
        [
            .. assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(PathPrefix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .Select(n =>
                {
                    using var stream = assembly.GetManifestResourceStream(n)!;
                    using var reader = new StreamReader(stream);
                    return new SourceText(n, reader.ReadToEnd().ReplaceLineEndings("\n"));
                }),
        ];
    }
}

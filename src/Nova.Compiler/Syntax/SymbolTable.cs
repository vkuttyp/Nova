namespace Nova.Compiler.Syntax;

/// <summary>An interned identifier. Ids are assigned in first-seen order, so they are deterministic per input.</summary>
public readonly record struct Symbol(int Id);

/// <summary>Interns identifier text. One table is shared by all files of a compilation.</summary>
public sealed class SymbolTable
{
    private readonly Dictionary<string, Symbol> _ids = new(StringComparer.Ordinal);
    private readonly List<string> _names = [];

    public Symbol Intern(string name)
    {
        if (!_ids.TryGetValue(name, out var symbol))
        {
            symbol = new Symbol(_names.Count);
            _names.Add(name);
            _ids.Add(name, symbol);
        }
        return symbol;
    }

    public string GetName(Symbol symbol) => _names[symbol.Id];
}

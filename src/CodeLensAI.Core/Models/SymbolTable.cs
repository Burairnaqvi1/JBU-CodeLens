using CodeLensAI.Shared.Structural;

namespace CodeLensAI.Core.Models;

public class SymbolTable
{
    private readonly Dictionary<string, Symbol> _symbols = new();

    public void Add(Symbol symbol)
    {
        _symbols[symbol.FullName] = symbol;
        if (!_symbols.ContainsKey(symbol.Name))
            _symbols[symbol.Name] = symbol;
    }

    public Symbol? Lookup(string name)
    {
        _symbols.TryGetValue(name, out var symbol);
        return symbol;
    }

    public Symbol? LookupFull(string fullName)
    {
        _symbols.TryGetValue(fullName, out var symbol);
        return symbol;
    }

    public List<Symbol> All() => _symbols.Values.ToList();

    public void Clear() => _symbols.Clear();

    public void BuildFrom(ProjectIR ir)
    {
        // Rebuild from scratch each scan — without this, a long-lived table accumulates
        // symbols from every previous scan and grows unboundedly.
        Clear();

        foreach (var cls in ir.Classes)
        {
            Add(new Symbol
            {
                Name = cls.Name,
                FullName = cls.FullName,
                Kind = cls.Kind,
                FilePath = cls.FilePath,
                Namespace = cls.NamespaceName,
            });
            foreach (var method in cls.Methods)
            {
                Add(new Symbol
                {
                    Name = method.Name,
                    FullName = method.FullName,
                    Kind = "method",
                    FilePath = cls.FilePath,
                    Namespace = cls.NamespaceName,
                });
            }
        }
    }
}

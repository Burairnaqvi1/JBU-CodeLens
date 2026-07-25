using JBU.CodeLens.Shared.Structural;

namespace JBU.CodeLens.Core.Analysis;

public static class CallGraphBuilder
{
    public static Dictionary<string, List<string>> Build(ProjectIR ir)
    {
        ArgumentNullException.ThrowIfNull(ir);

        var callGraph = new Dictionary<string, List<string>>();

        foreach (var cls in ir.Classes)
        {
            foreach (var method in cls.Methods)
            {
                if (method.Calls.Count > 0)
                {
                    callGraph[method.FullName] = new List<string>(method.Calls);
                }
            }
        }

        return callGraph;
    }
}

namespace FFCleaner.RegistryAnalysis;

/// <summary>
/// Evaluador puro. No abre claves, no lee archivos, no ejecuta destinos:
/// el estado del destino llega exclusivamente por el <c>probe</c> inyectado.
/// </summary>
public static class RegistryEvaluator
{
    internal const string ManualReview = "Revisión manual";
    internal const string UnknownConfidence = "Desconocida";

    public static ReferenceFinding? Evaluate(RegistryInput input, Func<string, TargetState> probe)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(input.KeyPath);
        ArgumentNullException.ThrowIfNull(input.ValueName);

        var view = CanonicalView(input.View);
        if (view is null)
        {
            return UnknownFinding(
                input,
                input.View,
                resolved: null,
                $"Vista de Registro desconocida ('{input.View}'): solo Registry32 o Registry64 son analizables.");
        }

        var target = input.RawTarget;
        var analysis = RegistryPathSyntax.Analyze(target);
        if (!analysis.IsUsable)
        {
            return UnknownFinding(input, view, resolved: null, analysis.Reason);
        }

        var resolved = analysis.Resolved!;
        var state = probe(resolved);

        return state switch
        {
            TargetState.Present => null,

            TargetState.Missing => new ReferenceFinding(
                RegistryIds.For(view, input.KeyPath, input.ValueName),
                view,
                input.KeyPath,
                input.ValueName,
                input.RawTarget,
                resolved,
                "Referencia huérfana potencial: el destino no se encontró. Revisión manual; no es prueba definitiva de invalidez.",
                ManualReview,
                Selectable: true),

            _ => UnknownFinding(
                input,
                view,
                resolved,
                "Estado del destino desconocido (posible permiso denegado o unidad no disponible); no se trata como ausencia."),
        };
    }

    private static string? CanonicalView(string? view)
    {
        var trimmed = (view ?? string.Empty).Trim();
        if (trimmed.Equals("Registry32", StringComparison.OrdinalIgnoreCase))
        {
            return "Registry32";
        }

        if (trimmed.Equals("Registry64", StringComparison.OrdinalIgnoreCase))
        {
            return "Registry64";
        }

        return null;
    }

    private static ReferenceFinding UnknownFinding(RegistryInput input, string view, string? resolved, string reason) =>
        new(
            RegistryIds.For(view, input.KeyPath, input.ValueName),
            view,
            input.KeyPath,
            input.ValueName,
            input.RawTarget,
            resolved,
            reason,
            UnknownConfidence,
            Selectable: false);
}

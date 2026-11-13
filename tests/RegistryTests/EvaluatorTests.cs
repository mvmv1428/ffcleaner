using System;
using System.Collections.Generic;
using FFCleaner.RegistryAnalysis;

namespace FFCleaner.RegistryTests;

internal static class EvaluatorTests
{
    private const string Key = @"HKCU\Software\Microsoft\Windows\CurrentVersion\App Paths\ghost.exe";
    private const string Target = @"C:\Program Files\Ghost\ghost.exe";

    public static void Register()
    {
        TestRegistry.Add("Missing produce hallazgo de revision manual (potencial huerfano)", Missing_Produces_Manual_Review_Finding);
        TestRegistry.Add("Unknown no es ausencia y nunca es seleccionable", Unknown_Is_Not_Selectable_And_Not_Absence);
        TestRegistry.Add("Sintaxis ambigua nunca llega al probe y no es seleccionable", Ambiguous_Syntax_Never_Reaches_Probe);
        TestRegistry.Add("Rutas entrecomilladas y con espacios llegan normalizadas al probe", Quoted_And_Spaced_Paths_Are_Verified_Normalized);
        TestRegistry.Add("Vista desconocida no es seleccionable; vistas validas se canonizan", View_Validation_And_Canonicalization);
    }

    internal static RegistryInput Input(
        string rawTarget = Target,
        string view = "Registry64",
        string keyPath = Key,
        string valueName = "") =>
        new(view, keyPath, valueName, rawTarget);

    internal static Func<string, TargetState> CountingProbe(
        TargetState state,
        List<string> calls) =>
        path =>
        {
            calls.Add(path);
            return state;
        };

    // Caso 1: destino ausente => hallazgo revisable, NO veredicto de invalidez.
    private static void Missing_Produces_Manual_Review_Finding()
    {
        var calls = new List<string>();
        var finding = RegistryEvaluator.Evaluate(Input(), CountingProbe(TargetState.Missing, calls));

        Check.NotNull(finding, "Missing debe producir un hallazgo (potencial referencia huerfana).");
        Check.Equal("Revisión manual", finding!.Confidence, "Confianza esperada para Missing.");
        Check.True(finding.Selectable, "Missing no es Unknown: la entrada queda disponible para revision.");
        Check.Contains("huérfan", finding.Reason, "El motivo debe nombrar la referencia huerfana potencial.");
        Check.Equal(1, calls.Count, "El probe debe invocarse una vez.");
        Check.Equal(Target, calls[0], "El probe debe recibir la ruta resuelta.");
        Check.Equal(Target, finding.ResolvedTarget, "ResolvedTarget debe ser la ruta verificada.");
    }

    // Caso 2: Unknown (permiso denegado, unidad no disponible) jamas se degrada a "falta".
    private static void Unknown_Is_Not_Selectable_And_Not_Absence()
    {
        var calls = new List<string>();
        var finding = RegistryEvaluator.Evaluate(Input(), CountingProbe(TargetState.Unknown, calls));

        Check.NotNull(finding, "Unknown debe reportarse como hallazgo informativo.");
        Check.False(finding!.Selectable, "Unknown nunca es seleccionable.");
        Check.NotEqual("Revisión manual", finding.Confidence, "Unknown no comparte la confianza de un huerfano confirmado.");
        Check.Contains("desconocid", finding.Reason, "El motivo debe declarar el estado desconocido.");
        Check.DoesNotContain("no existe", finding.Reason, "Un estado desconocido no se convierte en ausencia.");
        Check.Equal(1, calls.Count, "El probe se consulta una sola vez.");
    }

    // Caso 3: entradas ambiguas (entorno, UNC, relativas, argumentos, invalidas) => Unknown sin seleccion.
    private static void Ambiguous_Syntax_Never_Reaches_Probe()
    {
        var cases = new (string Raw, string Fragment)[]
        {
            (@"%ProgramFiles%\Ghost\ghost.exe", "variable"),
            (@"%SystemRoot%\ghost.exe", "variable"),
            (@"\\server\share\ghost.exe", "UNC"),
            ("ghost.exe", "relativa"),
            (@".\ghost.exe", "relativa"),
            (@"..\ghost.exe", "relativa"),
            ("C:ghost.exe", "relativa"),
            ("\"C:\\Program Files\\Ghost\\ghost.exe\" --flag", "argument"),
            ("\"C:\\Program Files\\Ghost\\ghost.exe", "comilla"),
            (@"C:\Program Files\Ghost\ghost.txt", ".exe"),
            (string.Empty, "vacío"),
            (@"C:\Program Files\Ghost\gho|st.exe", "inválid"),
        };

        foreach (var (raw, fragment) in cases)
        {
            var calls = new List<string>();
            var finding = RegistryEvaluator.Evaluate(
                Input(rawTarget: raw),
                path =>
                {
                    calls.Add(path);
                    return TargetState.Present;
                });

            var label = raw.Length == 0 ? "<vacío>" : raw;
            Check.Equal(0, calls.Count, $"'{label}': una entrada ambigua no debe consultarse con el probe.");
            Check.NotNull(finding, $"'{label}': debe reportarse como hallazgo no seleccionable.");
            Check.False(finding!.Selectable, $"'{label}': las entradas ambiguas nunca son seleccionables.");
            Check.Null(finding.ResolvedTarget, $"'{label}': no hay ruta resuelta verificable.");
            Check.Contains(fragment, finding.Reason, $"'{label}': el motivo debe explicar la ambiguedad.");
            Check.DoesNotContain("no existe", finding.Reason, $"'{label}': ambiguedad no es ausencia.");
        }
    }

    // Caso 4: comillas completas sin argumentos y espacios son validas; se verifica la ruta limpia.
    private static void Quoted_And_Spaced_Paths_Are_Verified_Normalized()
    {
        var cases = new (string Raw, string Expected)[]
        {
            ("\"C:\\Program Files\\Ghost\\ghost.exe\"", @"C:\Program Files\Ghost\ghost.exe"),
            ("  \"C:\\Program Files\\Ghost\\ghost.exe\"  ", @"C:\Program Files\Ghost\ghost.exe"),
            ("\"C:\\Program Files\\Ghost\\ghost.exe \"", @"C:\Program Files\Ghost\ghost.exe"),
            (@"C:\Program Files\Ghost\ghost.exe", @"C:\Program Files\Ghost\ghost.exe"),
            ("   C:\\Program Files\\Ghost\\ghost.exe\t", @"C:\Program Files\Ghost\ghost.exe"),
            (@"C:\PROGRA~1\Ghost\ghost.exe", @"C:\PROGRA~1\Ghost\ghost.exe"),
        };

        foreach (var (raw, expected) in cases)
        {
            var calls = new List<string>();
            var finding = RegistryEvaluator.Evaluate(
                Input(rawTarget: raw),
                path =>
                {
                    calls.Add(path);
                    return TargetState.Missing;
                });

            Check.Equal(1, calls.Count, $"'{raw}': la ruta verificable debe consultarse una sola vez.");
            Check.Equal(expected, calls[0], $"'{raw}': el probe debe recibir la ruta normalizada.");
            Check.NotNull(finding, $"'{raw}': destino ausente produce hallazgo revisable.");
            Check.Equal(expected, finding!.ResolvedTarget, $"'{raw}': ResolvedTarget normalizado.");
            Check.True(finding.Selectable, $"'{raw}': un huerfano potencial queda disponible para revision.");
        }
    }

    // Caso 5: solo Registry32/Registry64 son analizables; el resto es Unknown sin probe.
    private static void View_Validation_And_Canonicalization()
    {
        foreach (var view in new[] { string.Empty, "Registry128", "HKLM64", "32", "Registry" })
        {
            var calls = new List<string>();
            var finding = RegistryEvaluator.Evaluate(
                Input(view: view),
                path =>
                {
                    calls.Add(path);
                    return TargetState.Present;
                });

            var label = view.Length == 0 ? "<vacía>" : view;
            Check.Equal(0, calls.Count, $"vista '{label}': no se consulta el probe sin una vista valida.");
            Check.NotNull(finding, $"vista '{label}': debe reportarse como hallazgo no seleccionable.");
            Check.False(finding!.Selectable, $"vista '{label}': nunca seleccionable.");
            Check.Contains("vista", finding.Reason, $"vista '{label}': el motivo debe nombrar la vista.");
        }

        var canonical = RegistryEvaluator.Evaluate(
            Input(view: "registry32"),
            _ => TargetState.Missing);
        Check.NotNull(canonical, "vista valida en minusculas debe evaluarse.");
        Check.Equal("Registry32", canonical!.View, "La vista aceptada se reporta canonica.");

        var canonical64 = RegistryEvaluator.Evaluate(
            Input(view: "Registry64"),
            _ => TargetState.Missing);
        Check.Equal("Registry64", canonical64!.View, "Registry64 se conserva canonica.");
    }
}

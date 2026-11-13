using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FFCleaner.RegistryAnalysis;

namespace FFCleaner.RegistryTests;

internal static class ScannerTests
{
    public static void Register()
    {
        TestRegistry.Add("Scanner real (solo lectura): todas las familias, hallazgos coherentes y sin duplicados", Scan_Real_ReadOnly);
        TestRegistry.Add("Scanner respeta la cancelacion: resultado marcado y sin hallazgos", Scan_Honors_Cancellation);
        TestRegistry.Add("Scanner declara seis familias con identificadores estables", Families_Declared);
    }

    // Lectura real del Registro de este equipo. Solo lectura: no se repara nada fuera del sandbox.
    private static void Scan_Real_ReadOnly()
    {
        var scanner = new RegistryScanner();
        var result = scanner.Scan(RegistryScanner.AllFamilies.Select(f => f.Family).ToArray());

        Check.False(result.Cancelled, "El análisis completo no debe constar como cancelado.");
        Check.Equal("production", result.ScopeId, "Ámbito de producción.");
        Check.Equal(6, result.Stats.Count, "Una traza por familia.");
        Check.True(result.Stats.Sum(s => s.KeysVisited) > 6, "Se abrieron claves más allá de HKCU App Paths.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var finding in result.Findings)
        {
            Check.True(ids.Add(finding.Id), $"Id duplicado: {finding.Id} ({finding.Location.Display})");
            Check.Equal(finding.Risk == RegistryRisk.Seguro, finding.CanRepair, "Solo lo seguro es reparable.");
            if (finding.CanRepair) Check.True(finding.StateHash.Length == 64, "Un hallazgo reparable lleva la huella de su estado.");
            if (finding.Location.Hive == RegHive.LocalMachine && !RegistryScope.IsElevated)
                Check.False(finding.CanRepair, "Sin privilegios, nada de HKLM es reparable: " + finding.Location.Display);
            Check.False(string.IsNullOrWhiteSpace(finding.Evidence), "Todo hallazgo explica su evidencia.");
        }

        foreach (var stat in result.Stats)
            Console.WriteLine($"      {stat.Family,-17} claves={stat.KeysVisited,5} entradas={stat.EntriesExamined,5} hallazgos={stat.Findings,4} reparables={stat.Repairable,4} {stat.ElapsedMs,7:F1} ms");
        Console.WriteLine($"      (scanner real: {result.Findings.Count} hallazgos, {result.Issues.Count} incidencias; por riesgo: " +
            string.Join(", ", result.Findings.GroupBy(f => f.Risk).Select(g => g.Key + "=" + g.Count())) + ")");
    }

    private static void Scan_Honors_Cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = new RegistryScanner().Scan(RegistryScanner.AllFamilies.Select(f => f.Family).ToArray(), cts.Token);
        Check.True(result.Cancelled, "Un token ya cancelado produce un resultado cancelado.");
        Check.Equal(0, result.Findings.Count, "Sin hallazgos tras cancelar antes de leer.");
        Check.Equal(0, result.Stats.Sum(s => s.KeysVisited), "No se abrió ninguna clave.");
    }

    private static void Families_Declared()
    {
        var families = RegistryScanner.AllFamilies;
        Check.Equal(6, families.Count, "Seis familias.");
        Check.Equal(6, families.Select(f => f.Id).Distinct().Count(), "Identificadores únicos.");
        Check.True(families.Select(f => f.Id).SequenceEqual(new[] { "apppaths", "uninstall", "fileassoc", "startup", "muicache", "shareddlls" }), "Identificadores estables.");
        Check.True(Enum.GetValues<RegistryFamily>().All(f => families.Any(i => i.Family == f)), "Toda familia del enum está declarada.");
    }
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FFCleaner.Core;
using FFCleaner.RegistryAnalysis;

// CLI de diagnóstico: solo lectura. La limpieza y la reparación se hacen desde la aplicación de escritorio,
// donde hay selección, confirmación y resultados visibles.
Console.OutputEncoding = new UTF8Encoding(false);
var json = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
try
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
    {
        Console.WriteLine("FFCleaner · CLI de diagnóstico · solo lectura\n" +
            "catalog [--out JSON]                              Aplicaciones, categorías y perfiles detectados\n" +
            "analyze --select APP[/CATEGORIA[/PERFIL]] … [--out JSON]\n" +
            "                                                  Analiza la selección y muestra tiempos por fase\n" +
            "registry [--family ID …] [--out JSON]             Analiza el Registro por familias\n" +
            "Las salidas --out deben ser archivos nuevos. No hay comandos de limpieza ni de reparación.");
        return 0;
    }
    List<string> Values(string name)
    {
        var values = new List<string>();
        for (var i = 1; i < args.Length; i++)
            if (args[i] == name)
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Falta valor para " + name);
                values.Add(args[++i]);
            }
        return values;
    }
    void Emit(object value)
    {
        var text = JsonSerializer.Serialize(value, json);
        var destination = Values("--out").SingleOrDefault();
        if (destination is null) { Console.WriteLine(text); return; }
        var path = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(file, new UTF8Encoding(false));
        writer.Write(text);
        Console.Error.WriteLine("Exportado: " + path);
    }
    var allowed = args[0] switch
    {
        "catalog" => new[] { "--out" },
        "analyze" => new[] { "--select", "--out" },
        "registry" => new[] { "--family", "--out" },
        _ => throw new ArgumentException("Comando desconocido; usa --help"),
    };
    for (var i = 1; i < args.Length; i += 2)
        if (!allowed.Contains(args[i], StringComparer.Ordinal) || i + 1 >= args.Length) throw new ArgumentException("Opción o valor inválido: " + args[i]);

    var analysis = new FileAnalysisService(new SystemFolderProvider());
    switch (args[0])
    {
        case "catalog":
            var discovered = analysis.Discover();
            Emit(new
            {
                discovered.Version, discovered.DiscoveryMs, discovered.Notes,
                Apps = discovered.Apps.Select(a => new
                {
                    a.Id, a.Name, Group = a.Group.ToString(),
                    Categories = a.Categories.Select(c => new { c.Id, c.Name, Targets = c.Targets.Select(t => new { Key = t.Key.ToString(), t.ProfileName, Roots = t.Roots.Select(r => r.Path) }) }),
                }),
            });
            break;
        case "analyze":
            var selection = new SelectionModel(analysis.Discover());
            foreach (var item in Values("--select"))
            {
                var parts = item.Split('/');
                _ = parts.Length switch
                {
                    1 => selection.SetApp(parts[0], true),
                    2 => selection.SetCategory(parts[0], parts[1], true),
                    3 => selection.SetTarget(new(parts[0], parts[1], parts[2]), true),
                    _ => throw new ArgumentException("Selección inválida: " + item),
                };
            }
            if (selection.SelectedCount == 0) throw new ArgumentException("Indica al menos un --select; no se analiza todo el equipo de forma implícita.");
            var session = await analysis.AnalyzeAsync(selection.Snapshot(), null, cancel.Token);
            Emit(new
            {
                session.CreatedUtc, session.Cancelled, session.Limited, session.TotalFiles, session.TotalBytes, session.EligibleFiles, session.EligibleBytes, session.FreeableBytes,
                Timings = session.Timings,
                Targets = session.Targets.Select(t => new { Key = t.Target.Key.ToString(), t.FileCount, t.TotalBytes, t.EligibleCount, t.EligibleBytes, t.DataItems, t.DataBytes, t.FreeableBytes, t.ProtectedCount, ProtectedBy = t.ProtectedByVerdict.ToDictionary(p => p.Key.ToString(), p => p.Value), t.Refusal }),
                session.Issues,
            });
            if (session.Cancelled || session.Limited) return 2;
            break;
        case "registry":
            var scanner = new RegistryScanner();
            var families = Values("--family").Select(id => scanner.Families.FirstOrDefault(f => f.Id == id)?.Family ?? throw new ArgumentException("Familia desconocida: " + id)).ToList();
            if (families.Count == 0) families = [.. scanner.Families.Select(f => f.Family)];
            var scan = scanner.Scan(families, cancel.Token);
            Emit(new
            {
                scan.CreatedUtc, scan.ScopeId, scan.Cancelled, Elevated = RegistryScope.IsElevated, scan.Stats, scan.Issues,
                Findings = scan.Findings.Select(f => new { f.Family, f.Risk, Key = f.Location.Display, Value = f.ValueDisplay, f.Repair, f.RawTarget, f.ResolvedTarget, f.Evidence }),
            });
            if (scan.Cancelled) return 2;
            break;
    }
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Cancelado sin cambios."); return 2; }
catch (Exception error) { Console.Error.WriteLine("Error: " + error.Message); return 1; }

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using FFCleaner.Core;
using FFCleaner.Tests;
using Legacy = FFCleaner.Benchmarks.Legacy;

namespace FFCleaner.Benchmarks;

/// <summary>
/// Benchmark comparativo de la Etapa 1: escáner y presentación de la versión 0.1.0 (copia congelada)
/// frente a los actuales, sobre el mismo conjunto de archivos de un sandbox. Verifica además que ambos
/// ven exactamente los mismos archivos, tamaños, identidades y elegibilidad.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        var fileCount = Arg(args, "--files", 40_000);
        var iterations = Arg(args, "--iterations", 5);
        var output = args.SkipWhile(a => a != "--out").Skip(1).FirstOrDefault();

        using var fixture = SandboxFixture.Create();
        Console.WriteLine($"Generando {fileCount:N0} archivos de prueba en {fixture.Root} …");
        var created = Populate(fixture, fileCount);
        Console.WriteLine($"Fixture: {created:N0} archivos de caché en 2 navegadores × 2 perfiles.");

        var before = new List<Sample>();
        var after = new List<Sample>();
        Legacy.ScanResult? legacyResult = null;
        AnalysisSession? session = null;
        for (var i = 0; i <= iterations; i++)
        {
            var (legacySample, legacyScan) = RunLegacy(fixture);
            var (newSample, newSession) = RunCurrent(fixture);
            legacyResult = legacyScan;
            session = newSession;
            if (i == 0) continue;   // primera pasada: calentamiento de la caché del sistema de archivos
            before.Add(legacySample);
            after.Add(newSample);
            Console.WriteLine($"  pasada {i}: antes {legacySample.TotalMs:F0} ms · después {newSample.TotalMs:F0} ms");
        }

        var precision = ComparePrecision(legacyResult!, session!);
        var uiBefore = new List<double>();
        var uiAfter = new List<double>();
        for (var i = 0; i <= Math.Min(iterations, 3); i++)
        {
            var legacyUi = LegacyUi(legacyResult!);
            var currentUi = CurrentUi(session!);
            if (i == 0) continue;
            uiBefore.Add(legacyUi);
            uiAfter.Add(currentUi);
            Console.WriteLine($"  interfaz {i}: antes {legacyUi:F0} ms · después {currentUi:F1} ms");
        }

        var report = new
        {
            fechaUtc = DateTime.UtcNow,
            archivos = created,
            pasadas = iterations,
            metodo = "Mediana de las pasadas tras una de calentamiento; mismo sandbox para ambos escáneres.",
            antes = Summarize(before, uiBefore),
            despues = Summarize(after, uiAfter),
            precision,
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        if (output is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, json);
        }
        Print("Descubrimiento de rutas", report.antes.descubrimientoMs, report.despues.descubrimientoMs);
        Print("Recorrido del sistema de archivos", report.antes.recorridoMs, report.despues.recorridoMs);
        Print("Lectura de metadatos", report.antes.metadatosMs, report.despues.metadatosMs);
        Print("Presentación en la interfaz", report.antes.interfazMs, report.despues.interfazMs);
        Print("TOTAL", report.antes.totalMs, report.despues.totalMs);
        Console.WriteLine(precision.identico ? "PASS precisión idéntica entre el escáner antiguo y el nuevo" : "FAIL precisión distinta");
        var faster = report.despues.totalMs * 3 < report.antes.totalMs;
        Console.WriteLine(faster ? "PASS reducción de tiempo total superior a 3×" : "FAIL sin reducción suficiente");
        return precision.identico && faster ? 0 : 1;
    }

    private static int Arg(string[] args, string name, int fallback)
    {
        var value = args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        return value is null ? fallback : int.Parse(value, CultureInfo.InvariantCulture);
    }

    private static void Print(string phase, double before, double after) =>
        Console.WriteLine($"{phase,-36} antes {before,10:F1} ms   después {after,10:F1} ms   ×{(after > 0 ? before / after : 0):F1}");

    /// <summary>Cuatro hojas de caché por perfil, con subcarpetas, como en un perfil real.</summary>
    private static int Populate(SandboxFixture fixture, int total)
    {
        var leaves = new[] { ("Cache", 0.70), ("Code Cache", 0.22), ("GPUCache", 0.04), ("DawnCache", 0.04) };
        var profiles = new[] { ("chrome", "Default"), ("chrome", "Profile 1"), ("brave", "Default"), ("brave", "Profile 1") };
        var payload = new byte[1536];
        var created = 0;
        foreach (var (browser, profile) in profiles)
        {
            var dir = fixture.Profile(browser, profile);
            foreach (var personal in new[] { "Preferences", "Cookies", "History", "Login Data" }) fixture.Write(Path.Combine(dir, personal), 128);
            foreach (var (leaf, share) in leaves)
            {
                var count = (int)(total / profiles.Length * share);
                for (var i = 0; i < count; i++)
                {
                    // 256 subcarpetas de dos dígitos hexadecimales, similar a «Code Cache\js» o «cache2\entries».
                    var sub = Path.Combine(dir, leaf, leaf == "Cache" ? "Cache_Data" : "js", (i & 0xFF).ToString("x2", CultureInfo.InvariantCulture));
                    if (i < 256) Directory.CreateDirectory(sub);
                    using var stream = new FileStream(Path.Combine(sub, "f_" + i.ToString("x6", CultureInfo.InvariantCulture)), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, false);
                    stream.Write(payload, 0, 64 + (i * 37) % 1400);
                    created++;
                }
            }
        }
        return created;
    }

    private sealed record Sample(double DiscoveryMs, double TraversalMs, double MetadataMs, double TotalMs, double PathChecksMs, long PathCheckCalls, long MetadataCalls);

    private static (Sample, Legacy.ScanResult) RunLegacy(SandboxFixture fixture)
    {
        // El catálogo antiguo lee LOCALAPPDATA/APPDATA/TEMP del entorno: se apuntan al sandbox solo durante su ejecución.
        var saved = new[] { "LOCALAPPDATA", "APPDATA", "TEMP" }.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", fixture.Folders.LocalAppData);
            Environment.SetEnvironmentVariable("APPDATA", fixture.Folders.RoamingAppData);
            Environment.SetEnvironmentVariable("TEMP", fixture.Folders.Temp);
            var total = Stopwatch.StartNew();
            var roots = Legacy.RuleCatalog.Discover().Where(r => r.Id.StartsWith("browser-cache:", StringComparison.Ordinal)).ToList();
            var discovery = total.Elapsed.TotalMilliseconds;
            var scanner = new Legacy.StorageScanner();
            var result = scanner.Scan(roots);
            total.Stop();
            double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
            // Recorrido = enumeración + comprobaciones de ruta/atributos repetidas por cada entrada.
            return (new(discovery, Ms(scanner.EnumerationTicks) + Ms(scanner.PathCheckTicks), Ms(scanner.MetadataTicks),
                total.Elapsed.TotalMilliseconds, Ms(scanner.PathCheckTicks), scanner.PathCheckCalls, scanner.MetadataCalls), result);
        }
        finally
        {
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    private static (Sample, AnalysisSession) RunCurrent(SandboxFixture fixture)
    {
        var total = Stopwatch.StartNew();
        var analysis = new FileAnalysisService(fixture.Folders);
        var catalog = analysis.Discover();
        var selection = new SelectionModel(catalog);
        foreach (var app in new[] { "chrome", "brave" })
            foreach (var category in new[] { "cache", "code-cache", "gpu-cache" })
                selection.SetCategory(app, category, true);
        var session = analysis.AnalyzeAsync(selection.Snapshot()).GetAwaiter().GetResult();
        total.Stop();
        return (new(session.Timings.DiscoveryMs, session.Timings.TraversalMs, session.Timings.MetadataMs,
            total.Elapsed.TotalMilliseconds, 0, 0, 0), session);
    }

    private static Precision ComparePrecision(Legacy.ScanResult legacy, AnalysisSession session)
    {
        var old = legacy.Files.ToDictionary(f => f.FullPath, StringComparer.OrdinalIgnoreCase);
        long bytes = 0, files = 0, eligible = 0, mismatches = 0;
        foreach (var target in session.Targets)
        {
            for (var i = 0; i < target.FileCount; i++)
            {
                var entry = target.FileAt(i);
                var volume = target.RootVolumes[target.DirectoryAt(entry.Directory).RootIndex];
                files++;
                bytes += entry.Length;
                if (entry.Verdict == FileVerdict.Eligible) eligible++;
                if (!old.TryGetValue(target.PathOf(i), out var record) ||
                    record.LogicalBytes != entry.Length || record.AllocatedBytes != entry.Allocated ||
                    record.LastWriteUtc != entry.LastWriteUtc || record.Eligible != (entry.Verdict == FileVerdict.Eligible) ||
                    record.Identity != $"win128:{volume.Serial:X16}:{entry.IdLow:X16}{entry.IdHigh:X16}")
                    mismatches++;
            }
        }
        var identical = mismatches == 0 && files == legacy.Files.Count && bytes == legacy.Files.Sum(f => f.LogicalBytes) &&
                        eligible == legacy.Files.Count(f => f.Eligible) && legacy.Issues.Count == session.Issues.Count;
        return new Precision(identical, legacy.Files.Count, files, legacy.Files.Sum(f => f.LogicalBytes), bytes,
            legacy.Files.Count(f => f.Eligible), eligible, mismatches, legacy.Issues.Count, session.Issues.Count);
    }

    private sealed record Precision(bool identico, long archivosAntes, long archivosDespues, long bytesAntes, long bytesDespues,
        long elegiblesAntes, long elegiblesDespues, long diferenciasPorArchivo, int incidenciasAntes, int incidenciasDespues);

    private sealed record Summary(double descubrimientoMs, double recorridoMs, double metadatosMs, double interfazMs, double totalMs,
        double comprobacionesDeRutaMs, long llamadasComprobacionRuta, long aperturasDeArchivoParaMetadatos);

    private static Summary Summarize(List<Sample> samples, List<double> ui)
    {
        static double Median(IEnumerable<double> values) { var sorted = values.OrderBy(v => v).ToArray(); return sorted[sorted.Length / 2]; }
        var uiMs = Median(ui);
        return new(Median(samples.Select(s => s.DiscoveryMs)), Median(samples.Select(s => s.TraversalMs)), Median(samples.Select(s => s.MetadataMs)),
            uiMs, Median(samples.Select(s => s.TotalMs)) + uiMs, Median(samples.Select(s => s.PathChecksMs)), samples[0].PathCheckCalls, samples[0].MetadataCalls);
    }

    // ───────────── Presentación: antes (una fila por archivo, añadida de una en una) ─────────────

    private static double LegacyUi(Legacy.ScanResult result)
    {
        var files = new ObservableCollection<LegacyFileRow>();
        var grid = NewGrid(("Archivo / ruta", "Path"), ("Tamaño lógico", "Size"), ("Enlaces", "Links"), ("Categoría", "Category"));
        grid.ItemsSource = files;
        CollectionViewSource.GetDefaultView(files).Filter = o => o is LegacyFileRow r && (r.Path.Length >= 0);
        var window = Host(grid);
        var watch = Stopwatch.StartNew();
        // Igual que MainWindow 0.1.0: una inserción (y un evento CollectionChanged) por archivo, en el hilo de interfaz.
        foreach (var file in result.Files) files.Add(new LegacyFileRow(file, file.RuleId, () => { }));
        _ = Human(result.Files.Sum(f => f.LogicalBytes));
        _ = files.Where(f => f.Selected).ToList();
        Idle();
        watch.Stop();
        window.Close();
        return watch.Elapsed.TotalMilliseconds;
    }

    // ───────────── Presentación: después (resumen por objetivo en un lote + detalle del mayor objetivo) ─────────────

    private static double CurrentUi(AnalysisSession session)
    {
        var summary = NewGrid(("Aplicación", "App"), ("Categoría", "Category"), ("Perfil", "Profile"), ("Archivos", "Files"), ("Tamaño", "Size"));
        var detail = NewGrid(("Archivo", "Path"), ("Tamaño", "Size"), ("Estado", "State"));
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition());
        panel.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(detail, 1);
        panel.Children.Add(summary);
        panel.Children.Add(detail);
        var window = Host(panel);
        var watch = Stopwatch.StartNew();
        summary.ItemsSource = session.Targets.Select(t => new TargetRow(t.Target.AppName, t.Target.CategoryName, t.Target.ProfileName,
            t.EligibleCount.ToString("N0"), Human(t.EligibleBytes))).ToList();
        var largest = session.Targets.MaxBy(t => t.FileCount)!;
        var rows = new DetailRow[largest.FileCount];
        for (var i = 0; i < rows.Length; i++) rows[i] = new DetailRow(largest, i);
        detail.ItemsSource = rows;
        Idle();
        watch.Stop();
        window.Close();
        return watch.Elapsed.TotalMilliseconds;
    }

    private static DataGrid NewGrid(params (string Header, string Property)[] columns)
    {
        var grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, EnableRowVirtualization = true, RowHeight = 34 };
        foreach (var (header, property) in columns)
            grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(property), IsReadOnly = true });
        return grid;
    }

    private static Window Host(UIElement content)
    {
        var window = new Window { Width = 1320, Height = 850, Content = content, ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        window.Show();
        Idle();
        return window;
    }

    /// <summary>Espera a que el hilo de interfaz termine enlaces, diseño y pintado pendientes.</summary>
    private static void Idle() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

    private static string Human(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double n = bytes; var index = 0;
        while (n >= 1024 && index < units.Length - 1) { n /= 1024; index++; }
        return n.ToString(index == 0 ? "N0" : "N2", CultureInfo.GetCultureInfo("es-CL")) + " " + units[index];
    }

    private sealed class LegacyFileRow(Legacy.FileRecord file, string category, Action changed) : INotifyPropertyChanged
    {
        private bool _selected;
        public event PropertyChangedEventHandler? PropertyChanged;
        public string Path => file.FullPath;
        public string Size => Human(file.LogicalBytes);
        public uint Links => file.LinkCount;
        public string Category => category;
        public bool Selected { get => _selected; set { _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); changed(); } }
    }

    private sealed record TargetRow(string App, string Category, string Profile, string Files, string Size);

    private sealed class DetailRow(TargetAnalysis target, int index)
    {
        public string Path => target.PathOf(index);
        public string Size => Human(target.FileAt(index).Length);
        public string State => CleaningGuard.Describe(target.FileAt(index).Verdict);
    }
}

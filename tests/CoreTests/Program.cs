using FFCleaner.Core;
using FFCleaner.Tests;

var tests = new TestRunner();

static TargetKey K(string app, string category, string profile = "Default") => new(app, category, profile);

static (SandboxFixture Fixture, FileAnalysisService Analysis, SelectionModel Selection) Setup(Action<SandboxFixture>? extra = null)
{
    var fixture = SandboxFixture.Create();
    fixture.AddChromiumProfile("chrome", "Default", friendlyName: "Personal");
    fixture.AddChromiumProfile("chrome", "Profile 1");
    fixture.AddChromiumProfile("brave", "Default");
    fixture.AddChromiumProfile("brave", "Profile 1");
    extra?.Invoke(fixture);
    var analysis = new FileAnalysisService(fixture.Folders);
    return (fixture, analysis, new SelectionModel(analysis.Discover()));
}

// ───────────── Etapa 1: precisión y seguridad del recorrido optimizado ─────────────

tests.Add("enumerador_coincide_con_metadatos_nativos_por_archivo", async () =>
{
    var (fixture, analysis, selection) = Setup(f =>
    {
        var cache = Path.Combine(f.Profile("chrome", "Default"), "Cache", "Cache_Data");
        f.Write(Path.Combine(cache, "vacio"), 0);
        f.Write(Path.Combine(cache, "un-byte"), 1);
        f.Write(Path.Combine(cache, "grande"), 300_000);
        f.Write(Path.Combine(cache, "ñandú-日本語-" + new string('x', 120)), 8193);
        f.Write(Path.Combine(cache, "a", "b", "c", "d", "profundo"), 77);
    });
    using (fixture)
    {
        selection.SetApp("chrome", true);
        var session = await analysis.AnalyzeAsync(selection.Snapshot());
        var compared = 0;
        foreach (var target in session.Targets)
        {
            for (var i = 0; i < target.FileCount; i++)
            {
                var entry = target.FileAt(i);
                var path = target.PathOf(i);
                var native = NativeMetadata.Read(path);
                var volume = target.RootVolumes[target.DirectoryAt(entry.Directory).RootIndex];
                Check.True(volume.Extended, "Se esperaba identidad de 128 bits en NTFS");
                Check.Equal(native.Length, entry.Length, "Tamaño lógico de " + path);
                Check.Equal(native.Allocated ?? -1, entry.Allocated, "Tamaño asignado de " + path);
                Check.Equal(native.LastWriteUtc, entry.LastWriteUtc, "Fecha de " + path);
                Check.Equal(native.Identity, $"win128:{volume.Serial:X16}:{entry.IdLow:X16}{entry.IdHigh:X16}", "Identidad de " + path);
                Check.Equal(new FileInfo(path).Length, entry.Length, "Tamaño según .NET de " + path);
                compared++;
            }
        }
        var expected = new[] { "Cache", "Code Cache", "GPUCache", "DawnCache", @"Service Worker\CacheStorage", @"Service Worker\ScriptCache" }
            .Sum(leaf => SandboxFixture.CountFiles(Path.Combine(fixture.Profile("chrome", "Default"), leaf)) +
                         SandboxFixture.CountFiles(Path.Combine(fixture.Profile("chrome", "Profile 1"), leaf)));
        Check.Equal(expected, compared, "Archivos enumerados frente a los existentes");
        Check.True(session.Timings.TraversalMs > 0 && session.Timings.MetadataMs > 0 && session.Timings.DiscoveryMs > 0, "Tiempos por fase instrumentados");
    }
});

tests.Add("progreso_por_lotes_no_por_archivo", async () =>
{
    var (fixture, analysis, selection) = Setup(f =>
    {
        var cache = Path.Combine(f.Profile("chrome", "Default"), "Cache", "Cache_Data");
        Directory.CreateDirectory(cache);
        for (var i = 0; i < 4000; i++) File.WriteAllBytes(Path.Combine(cache, "lote_" + i), [1]);
    });
    using (fixture)
    {
        selection.SetTarget(K("chrome", "cache"), true);
        var reports = 0;
        var session = await analysis.AnalyzeAsync(selection.Snapshot(), new InlineProgress<AnalysisProgress>(_ => reports++));
        Check.True(session.TotalFiles > 4000, "Se analizaron los archivos del lote");
        Check.True(reports >= 1 && reports < 100, "Notificaciones acotadas, no una por archivo: " + reports);
    }
});

tests.Add("no_se_siguen_junctions_ni_se_listan_sus_archivos", async () =>
{
    var (fixture, analysis, selection) = Setup();
    using (fixture)
    {
        var sentinel = fixture.Write(Path.Combine(fixture.Outside, "secreto", "no-tocar.bin"), 500);
        var link = Path.Combine(fixture.Profile("chrome", "Default"), "Cache", "enlace");
        if (!SandboxFixture.TryJunction(link, Path.GetDirectoryName(sentinel)!)) throw new SkipException("No se pudo crear junction");
        selection.SetTarget(K("chrome", "cache"), true);
        var session = await analysis.AnalyzeAsync(selection.Snapshot());
        var target = session.Targets.Single();
        for (var i = 0; i < target.FileCount; i++)
            Check.True(!target.PathOf(i).Contains("enlace", StringComparison.OrdinalIgnoreCase), "Archivo tras junction listado: " + target.PathOf(i));
        Check.True(session.Issues.Any(i => i.Code == "UnsafePath" && i.Path.EndsWith("enlace", StringComparison.Ordinal)), "La junction debe constar como incidencia");
    }
});

tests.Add("raiz_sustituida_por_junction_no_se_recorre", async () =>
{
    var (fixture, analysis, selection) = Setup();
    using (fixture)
    {
        selection.SetTarget(K("chrome", "code-cache"), true);
        var snapshot = selection.Snapshot();
        var root = Path.Combine(fixture.Profile("chrome", "Default"), "Code Cache");
        fixture.Write(Path.Combine(fixture.Outside, "destino", "ajeno.bin"), 10);
        Directory.Move(root, root + "-original");
        if (!SandboxFixture.TryJunction(root, Path.Combine(fixture.Outside, "destino"))) throw new SkipException("No se pudo crear junction");
        var session = await analysis.AnalyzeAsync(snapshot);
        Check.Equal(0L, session.TotalFiles, "Nada listado a través de la raíz sustituida");
        Check.True(session.Issues.Any(i => i.Code == "UnsafePath"), "Debe constar la raíz insegura");
    }
});

tests.Add("nombres_sensibles_y_bases_de_datos_quedan_protegidos", async () =>
{
    var (fixture, analysis, selection) = Setup(f =>
    {
        var cache = Path.Combine(f.Profile("chrome", "Default"), "Cache");
        foreach (var name in new[] { "Cookies", "Login Data", "History", "token-refresh", "sesion.sqlite", "datos.db", "x-journal", "y-wal", "clave.pem" })
            f.Write(Path.Combine(cache, name), 40);
        f.Write(Path.Combine(cache, "Sessions", "inocente.bin"), 40);
        f.Write(Path.Combine(cache, "Network", "inocente.bin"), 40);
    });
    using (fixture)
    {
        selection.SetTarget(K("chrome", "cache"), true);
        var target = (await analysis.AnalyzeAsync(selection.Snapshot())).Targets.Single();
        var verdicts = Enumerable.Range(0, target.FileCount).ToDictionary(i => Path.GetRelativePath(Path.Combine(fixture.Profile("chrome", "Default"), "Cache"), target.PathOf(i)), i => target.FileAt(i).Verdict);
        Check.Equal(FileVerdict.SensitiveName, verdicts["Cookies"], "Cookies");
        Check.Equal(FileVerdict.SensitiveName, verdicts["Login Data"], "Login Data");
        Check.Equal(FileVerdict.SensitiveName, verdicts["History"], "History");
        Check.Equal(FileVerdict.SensitiveName, verdicts["token-refresh"], "token*");
        Check.Equal(FileVerdict.ProtectedExtension, verdicts["sesion.sqlite"], ".sqlite");
        Check.Equal(FileVerdict.ProtectedExtension, verdicts["datos.db"], ".db");
        Check.Equal(FileVerdict.DatabaseSidecar, verdicts["x-journal"], "-journal");
        Check.Equal(FileVerdict.DatabaseSidecar, verdicts["y-wal"], "-wal");
        Check.Equal(FileVerdict.ProtectedExtension, verdicts["clave.pem"], ".pem");
        Check.Equal(FileVerdict.SensitiveDirectory, verdicts[@"Sessions\inocente.bin"], "Carpeta Sessions");
        Check.Equal(FileVerdict.SensitiveDirectory, verdicts[@"Network\inocente.bin"], "Carpeta Network");
        Check.Equal(FileVerdict.Eligible, verdicts[@"Cache_Data\index"], "La caché normal sigue siendo limpiable");
    }
});

tests.Add("enlaces_fisicos_no_se_cuentan_dos_veces_ni_son_limpiables", async () =>
{
    var (fixture, analysis, selection) = Setup();
    using (fixture)
    {
        var cache = Path.Combine(fixture.Profile("chrome", "Default"), "Cache", "Cache_Data");
        var original = fixture.Write(Path.Combine(cache, "enlazado-a"), 10_000);
        Check.True(SandboxFixture.CreateHardLink(Path.Combine(cache, "enlazado-b"), original, IntPtr.Zero), "CreateHardLink");
        selection.SetTarget(K("chrome", "cache"), true);
        var target = (await analysis.AnalyzeAsync(selection.Snapshot())).Targets.Single();
        var linked = Enumerable.Range(0, target.FileCount).Where(i => target.FileAt(i).Name.StartsWith("enlazado-", StringComparison.Ordinal)).Select(target.FileAt).ToList();
        Check.Equal(2, linked.Count, "Ambos nombres listados");
        Check.True(linked.All(f => f.Verdict == FileVerdict.MultipleLinks), "Ambos marcados como enlaces múltiples");
        Check.Equal(10_000L, linked.Sum(f => f.Length), "Bytes contados una sola vez");
    }
});

tests.Add("cancelacion_y_limite_producen_sesiones_no_limpiables", async () =>
{
    var (fixture, analysis, selection) = Setup();
    using (fixture)
    {
        selection.SetApp("chrome", true);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var cancelled = await analysis.AnalyzeAsync(selection.Snapshot(), null, cancel.Token);
        Check.True(cancelled.Cancelled, "Sesión marcada como cancelada");
        Check.True(cancelled.CleanBlocker(selection.Snapshot(), DateTime.UtcNow)!.Contains("canceló"), "Cancelada no es limpiable");

        var limited = await new FileAnalysisService(fixture.Folders, maxFiles: 5).AnalyzeAsync(selection.Snapshot());
        Check.True(limited.Limited && limited.TotalFiles == 5, "Límite respetado");
        Check.True(limited.CleanBlocker(selection.Snapshot(), DateTime.UtcNow)!.Contains("límite"), "Limitada no es limpiable");
    }
});

tests.Add("exclusiones_guardadas_siguen_protegiendo_sin_pagina_propia", async () =>
{
    var fixture = SandboxFixture.Create();
    using (fixture)
    {
        var profile = fixture.AddChromiumProfile("chrome", "Default");
        var excludedDir = Path.Combine(profile, "Code Cache", "js");
        var excludedFile = Path.Combine(profile, "Cache", "Cache_Data", "index");
        var analysis = new FileAnalysisService(fixture.Folders, [excludedDir, excludedFile]);
        var selection = new SelectionModel(analysis.Discover());
        selection.SetApp("chrome", true);
        var session = await analysis.AnalyzeAsync(selection.Snapshot());
        foreach (var target in session.Targets)
            for (var i = 0; i < target.FileCount; i++)
            {
                var path = target.PathOf(i);
                Check.True(!path.StartsWith(excludedDir + "\\", StringComparison.OrdinalIgnoreCase), "Carpeta excluida recorrida: " + path);
                if (path.Equals(excludedFile, StringComparison.OrdinalIgnoreCase))
                    Check.Equal(FileVerdict.Excluded, target.FileAt(i).Verdict, "Archivo excluido");
            }
        Check.True(session.Issues.Any(i => i.Code == "Excluded"), "La omisión por exclusión se informa");
    }
});

tests.Add("politica_de_temporales_cualquier_tipo_con_mas_de_24h", async () =>
{
    var (fixture, analysis, _) = Setup(f => f.AddSystemCaches());
    using (fixture)
    {
        var selection = new SelectionModel(analysis.Discover());
        selection.SetTarget(K("windows", "user-temp", "all"), true);
        var target = (await analysis.AnalyzeAsync(selection.Snapshot())).Targets.Single();
        var verdicts = Enumerable.Range(0, target.FileCount).ToDictionary(i => target.FileAt(i).Name, i => target.FileAt(i).Verdict);
        Check.Equal(FileVerdict.Eligible, verdicts["viejo.tmp"], "Temporal antiguo");
        Check.Equal(FileVerdict.Eligible, verdicts["registro.log"], "Log antiguo en subcarpeta");
        Check.Equal(FileVerdict.TooRecent, verdicts["reciente.tmp"], "Temporal reciente");
        Check.Equal(FileVerdict.Eligible, verdicts["documento.txt"], "Cualquier tipo de archivo antiguo");
        Check.Equal(TimeSpan.FromHours(24), target.Target.Roots[0].Policy.MinimumAge, "Plazo de 24 horas");
    }
});

// ───────────── Etapa 2: catálogo, jerarquía y aislamiento de la selección ─────────────

tests.Add("catalogo_modela_navegador_categoria_perfil", () =>
{
    var (fixture, analysis, selection) = Setup(f =>
    {
        f.AddFirefoxProfile("abcd1234.default-release");
        f.AddSystemCaches();
        Directory.CreateDirectory(Path.Combine(f.UserData("chrome"), "Sin Preferencias", "Cache"));
    });
    using (fixture)
    {
        var catalog = analysis.Discover();
        var chrome = catalog.Apps.Single(a => a.Id == "chrome");
        var brave = catalog.Apps.Single(a => a.Id == "brave");
        Check.Equal(AppGroup.Navegadores, chrome.Group, "Grupo");
        Check.True(chrome.Categories.Select(c => c.Id).SequenceEqual(["cache", "code-cache", "gpu-cache", "sw-cache", "history", "downloads", "cookies", "sessions", "forms"]), "Categorías de Chrome: " + string.Join(",", chrome.Categories.Select(c => c.Id)));
        Check.True(chrome.Categories.Skip(4).All(c => c.Risk == CategoryRisk.DatosPersonales) && chrome.Categories.Take(4).All(c => c.Risk == CategoryRisk.Normal), "Datos personales marcados como tales");
        Check.True(chrome.Categories.All(c => c.Targets.Select(t => t.Key.ProfileId).SequenceEqual(["Default", "Profile 1"])), "Perfiles por categoría; carpeta sin Preferences ignorada");
        Check.Equal("Personal (Default)", chrome.Categories[0].Targets[0].ProfileName, "Nombre visible del perfil");
        Check.True(brave.Categories.Count == 9 && catalog.Apps.All(a => a.Id != "edge"), "Brave presente; Edge ausente no se inventa");
        Check.True(catalog.Apps.Any(a => a.Id == "firefox") && catalog.Apps.Any(a => a.Id == "windows") && catalog.Apps.Any(a => a.Id == "nvidia") && catalog.Apps.Any(a => a.Id == "dev"), "Resto de categorías configuradas");
        foreach (var target in catalog.AllTargets())
        {
            Check.True(CleaningGuard.ValidateTarget(target, fixture.Folders) is null, "Objetivo válido: " + target.Key);
            if (target.Data is not null)
            {
                // Datos personales: solo sus archivos exactos y siempre de marcado individual.
                Check.True(target.Roots.All(r => r.Path.StartsWith(Path.Combine(target.DataRoot!, target.Key.ProfileId), StringComparison.OrdinalIgnoreCase)), "Datos dentro de su perfil");
                Check.True(target.Roots.All(r => r.Leaf is not ("Login Data" or "Bookmarks" or "Preferences")), "Nunca contraseñas, marcadores ni preferencias");
                continue;
            }
            foreach (var root in target.Roots)
            {
                Check.True(PathSafety.IsStrictlyWithin(root.Path, fixture.Root), "Raíz dentro del sandbox: " + root.Path);
                if (target.DataRoot is not null) Check.True(CleaningGuard.BrowserCacheLeaves.Contains(root.Leaf), "Hoja de caché conocida: " + root.Leaf);
                foreach (var forbidden in new[] { "Cookies", "History", "Login Data", "Sessions", "Network", "Local Storage", "IndexedDB", "Extensions" })
                    Check.True(!root.Path.Split('\\').Contains(forbidden, StringComparer.OrdinalIgnoreCase), "Raíz con datos personales: " + root.Path);
            }
        }
    }
});

tests.Add("marcar_chrome_no_afecta_a_brave_ni_viceversa", () =>
{
    var (fixture, _, selection) = Setup();
    using (fixture)
    {
        Check.True(selection.SetApp("chrome", true), "Cambio efectivo");
        Check.Equal<bool?>(true, selection.AppState("chrome"), "Chrome marcado");
        Check.Equal<bool?>(false, selection.AppState("brave"), "Brave intacto al marcar Chrome");
        Check.True(selection.SelectedKeys().All(k => k.AppId == "chrome"), "Solo objetivos de Chrome");

        selection.SetCategory("brave", "cache", true);
        var braveBefore = selection.SelectedKeys().Where(k => k.AppId == "brave").ToList();
        selection.SetApp("chrome", false);
        Check.Equal<bool?>(false, selection.AppState("chrome"), "Chrome desmarcado");
        Check.True(selection.SelectedKeys().SequenceEqual(braveBefore), "Brave intacto al desmarcar Chrome");
        Check.Equal<bool?>(null, selection.AppState("brave"), "Brave parcial");
        Check.Equal<bool?>(true, selection.CategoryState("brave", "cache"), "Categoría de Brave completa");

        selection.SetApp("brave", false);
        selection.SetApp("brave", true);
        Check.Equal<bool?>(false, selection.AppState("chrome"), "Chrome intacto al marcar Brave");
    }
});

tests.Add("perfiles_y_categorias_tienen_estado_independiente", () =>
{
    var (fixture, _, selection) = Setup();
    using (fixture)
    {
        selection.SetTarget(K("chrome", "cache", "Profile 1"), true);
        Check.True(selection.IsSelected(K("chrome", "cache", "Profile 1")), "Perfil marcado");
        Check.True(!selection.IsSelected(K("chrome", "cache")), "Otro perfil de la misma categoría intacto");
        Check.True(!selection.IsSelected(K("chrome", "code-cache", "Profile 1")), "Otra categoría del mismo perfil intacta");
        Check.True(!selection.IsSelected(K("brave", "cache", "Profile 1")), "Mismo perfil de otro navegador intacto");
        Check.Equal<bool?>(null, selection.CategoryState("chrome", "cache"), "Categoría parcial");
        Check.Equal<bool?>(false, selection.CategoryState("chrome", "code-cache"), "Categoría vecina sin marcar");
        selection.SetCategory("chrome", "cache", false);
        Check.Equal(0, selection.SelectedCount, "Categoría desmarcada por completo");
        Check.Throws<ArgumentException>(() => selection.SetTarget(K("chrome", "passwords"), true), "Objetivo fuera del catálogo");
        Check.Throws<ArgumentException>(() => selection.SetApp("opera", true), "Aplicación fuera del catálogo");
    }
});

tests.Add("cada_cambio_de_casilla_incrementa_version_y_notifica", () =>
{
    var (fixture, _, selection) = Setup();
    using (fixture)
    {
        var events = 0;
        selection.Changed += (_, _) => events++;
        var version = selection.Version;
        selection.SetTarget(K("chrome", "cache"), true);
        Check.True(selection.Version == version + 1 && events == 1, "Marcar notifica");
        Check.True(!selection.SetTarget(K("chrome", "cache"), true) && events == 1, "Repetir el mismo estado no notifica");
        var first = selection.Snapshot();
        selection.SetTarget(K("brave", "cache"), true);
        Check.True(first.Fingerprint != selection.Snapshot().Fingerprint, "La huella cambia con la selección");
        selection.SetTarget(K("brave", "cache"), false);
        Check.Equal(first.Fingerprint, selection.Snapshot().Fingerprint, "Misma selección, misma huella");
        Check.True(selection.Clear() && events == 4 && selection.SelectedCount == 0, "Limpiar selección notifica");
    }
});

tests.Add("cambiar_cualquier_casilla_invalida_el_analisis_al_instante", async () =>
{
    var (fixture, analysis, selection) = Setup();
    using (fixture)
    {
        var cleaning = new CountingCleaner();
        using var workflow = new CleanupWorkflow(selection, analysis, cleaning);
        selection.SetCategory("chrome", "cache", true);
        Check.True(workflow.CleanBlocker() is not null && workflow.State == WorkflowState.SinAnalisis, "Sin análisis no se limpia");

        foreach (Action change in new Action[]
                 {
                     () => selection.SetTarget(K("brave", "cache"), true),           // otro navegador
                     () => selection.SetTarget(K("chrome", "cache", "Profile 1"), false), // un perfil
                     () => selection.SetCategory("chrome", "gpu-cache", true),        // otra categoría
                     () => selection.SetApp("brave", false),                          // casilla de aplicación
                 })
        {
            var session = await workflow.AnalyzeAsync();
            Check.True(ReferenceEquals(session, workflow.CurrentSession) && workflow.State == WorkflowState.Analizado && workflow.CleanBlocker() is null, "Análisis vigente");
            change();
            Check.True(workflow.CurrentSession is null, "Sesión descartada en el mismo cambio");
            Check.Equal(WorkflowState.Invalidado, workflow.State, "Estado invalidado");
            Check.True(workflow.CleanBlocker()!.Contains("selección cambió"), "Motivo visible");
            var report = await workflow.CleanAsync();
            Check.True(report.Refused && report.DeletedFiles == 0, "Limpieza rechazada");
            // Aunque alguien conserve la sesión antigua, el servicio real también la rechaza.
            var direct = session.CleanBlocker(selection.Snapshot(), DateTime.UtcNow);
            Check.True(direct is not null && direct.Contains("selección cambió"), "La sesión antigua no autoriza la selección nueva");
        }
        Check.Equal(0, cleaning.Calls, "El ejecutor nunca recibió una selección antigua");
    }
});

tests.Add("volver_a_la_seleccion_anterior_no_revive_el_analisis", async () =>
{
    var (fixture, analysis, selection) = Setup();
    using (fixture)
    {
        using var workflow = new CleanupWorkflow(selection, analysis, new CountingCleaner());
        selection.SetCategory("chrome", "cache", true);
        await workflow.AnalyzeAsync();
        selection.SetTarget(K("brave", "cache"), true);
        selection.SetTarget(K("brave", "cache"), false);
        Check.True(workflow.CurrentSession is null && (await workflow.CleanAsync()).Refused, "Hay que volver a analizar");
    }
});

tests.Add("cambio_durante_el_analisis_descarta_su_resultado", async () =>
{
    var (fixture, analysis, selection) = Setup();
    using (fixture)
    {
        var gate = new TaskCompletionSource();
        var slow = new GatedAnalysis(analysis, gate.Task);
        using var workflow = new CleanupWorkflow(selection, slow, new CountingCleaner());
        selection.SetCategory("chrome", "cache", true);
        var running = workflow.AnalyzeAsync();
        await slow.Started.Task;
        Check.Equal(WorkflowState.Analizando, workflow.State, "Analizando");
        await Check.ThrowsAsync<InvalidOperationException>(() => workflow.AnalyzeAsync(), "Segundo análisis simultáneo");
        selection.SetTarget(K("brave", "cache"), true);
        gate.SetResult();
        await running;
        Check.True(workflow.CurrentSession is null && workflow.State == WorkflowState.Invalidado, "Resultado descartado");
        Check.True(slow.SawCancellation, "El análisis en curso recibió la cancelación");
    }
});

tests.Add("sin_seleccion_no_hay_analisis_ni_lote_residual", async () =>
{
    var (fixture, analysis, selection) = Setup();
    using (fixture)
    {
        var cleaning = new CountingCleaner();
        using var workflow = new CleanupWorkflow(selection, analysis, cleaning);
        await Check.ThrowsAsync<InvalidOperationException>(() => workflow.AnalyzeAsync(), "Analizar sin selección");
        selection.SetCategory("chrome", "cache", true);
        await workflow.AnalyzeAsync();
        selection.Clear();
        Check.True((await workflow.CleanAsync()).Refused && cleaning.Calls == 0, "Sin casillas no se ejecuta ningún lote anterior");
    }
});

tests.Add("sesion_caduca_y_es_de_un_solo_uso", async () =>
{
    var (fixture, analysis, selection) = Setup();
    using (fixture)
    {
        selection.SetCategory("chrome", "cache", true);
        var session = await analysis.AnalyzeAsync(selection.Snapshot());
        Check.True(session.CleanBlocker(selection.Snapshot(), DateTime.UtcNow) is null, "Vigente");
        Check.True(session.CleanBlocker(selection.Snapshot(), session.ExpiresUtc)!.Contains("caducó"), "Caducada a los 15 minutos");
        Check.True(session.CleanBlocker(selection.Snapshot(), session.CreatedUtc.AddSeconds(-1))!.Contains("caducó"), "Reloj anterior al análisis");
    }
});

// ───────────── Guardas de raíces y sandbox ─────────────

tests.Add("guardas_rechazan_raices_peligrosas_y_datos_de_navegador", () =>
{
    var (fixture, analysis, _) = Setup();
    using (fixture)
    {
        var real = new SystemFolderProvider();
        string Folder(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        foreach (var path in new[]
                 {
                     Path.GetPathRoot(Folder(Environment.SpecialFolder.Windows))!, Folder(Environment.SpecialFolder.Windows),
                     Path.Combine(Folder(Environment.SpecialFolder.Windows), "System32", "config"), Folder(Environment.SpecialFolder.ProgramFiles),
                     Path.Combine(Folder(Environment.SpecialFolder.ProgramFiles), "Alguna App", "Cache"), Folder(Environment.SpecialFolder.UserProfile),
                     Folder(Environment.SpecialFolder.MyDocuments), Path.Combine(Folder(Environment.SpecialFolder.MyDocuments), "Proyecto", "cache"),
                     Folder(Environment.SpecialFolder.DesktopDirectory), real.LocalAppData, real.RoamingAppData,
                     Path.Combine(Folder(Environment.SpecialFolder.UserProfile), "Downloads", "cache"), @"\\servidor\recurso\cache",
                 })
            Check.True(CleaningGuard.ValidateRoot(path, real) is not null, "Raíz peligrosa aceptada en producción: " + path);
        Check.True(CleaningGuard.ValidateRoot(Path.Combine(real.LocalAppData, "Google", "Chrome", "User Data", "Default", "Cache"), real) is null, "Raíz de caché legítima");
        Check.True(CleaningGuard.ValidateRoot(Path.Combine(real.LocalAppData, "Google", "Chrome", "User Data", "Default", "Cache"), fixture.Folders) is not null, "El sandbox no acepta rutas reales");

        var catalog = analysis.Discover();
        var legit = catalog.AllTargets().First(t => t.Key == K("chrome", "cache"));
        var profile = fixture.Profile("chrome", "Default");
        CleanupTarget Forge(string path, string leaf, string profileId = "Default") =>
            legit with { Key = legit.Key with { ProfileId = profileId }, Roots = [new(path, leaf, legit.Roots[0].Policy)] };
        Check.True(CleaningGuard.ValidateTarget(Forge(profile, "Default"), fixture.Folders) is not null, "Perfil completo como raíz");
        Check.True(CleaningGuard.ValidateTarget(Forge(Path.Combine(profile, "Network"), "Network"), fixture.Folders) is not null, "Carpeta Network (cookies)");
        Check.True(CleaningGuard.ValidateTarget(Forge(Path.Combine(profile, "Sessions"), "Sessions"), fixture.Folders) is not null, "Carpeta Sessions");
        Check.True(CleaningGuard.ValidateTarget(Forge(Path.Combine(profile, "Local Storage"), "Local Storage"), fixture.Folders) is not null, "Local Storage");
        Check.True(CleaningGuard.ValidateTarget(Forge(Path.Combine(profile, "Cache"), "Network"), fixture.Folders) is not null, "Hoja declarada distinta de la ruta");
        Check.True(CleaningGuard.ValidateTarget(Forge(Path.Combine(fixture.Profile("chrome", "Profile 1"), "Cache"), "Cache"), fixture.Folders) is not null, "Caché de otro perfil");
        Check.True(CleaningGuard.ValidateTarget(Forge(Path.Combine(fixture.Profile("brave", "Default"), "Cache"), "Cache"), fixture.Folders) is not null, "Caché de otro navegador");
        Check.True(CleaningGuard.ValidateTarget(Forge(fixture.UserData("chrome"), "User Data"), fixture.Folders) is not null, "User Data completo");
        Check.True(CleaningGuard.ValidateTarget(legit, fixture.Folders) is null, "El objetivo legítimo sigue siendo válido");
    }
});

tests.Add("objetivo_falsificado_no_se_analiza", async () =>
{
    var (fixture, analysis, _) = Setup();
    using (fixture)
    {
        var legit = analysis.Discover().AllTargets().First(t => t.Key == K("chrome", "cache"));
        var forged = legit with { Roots = [new(Path.Combine(fixture.Profile("chrome", "Default"), "Network"), "Network", legit.Roots[0].Policy)] };
        var catalog = new CleanupCatalog("x", [new("chrome", "Chrome", AppGroup.Navegadores, [], [new("cache", "Caché", "", [forged])])], 0, []);
        var selection = new SelectionModel(catalog);
        selection.SetApp("chrome", true);
        var session = await analysis.AnalyzeAsync(selection.Snapshot());
        Check.True(session.TotalFiles == 0 && session.Targets.Single().Refusal is not null, "Objetivo rechazado sin recorrer cookies");
    }
});

tests.Add("revision_nombres_compuestos_fechas_corruptas_y_raices_ajenas", async () =>
{
    var (fixture, analysis, selection) = Setup(f => f.AddSystemCaches());
    using (fixture)
    {
        var policy = new CleaningPolicy(TimeSpan.Zero, null);
        var now = DateTime.UtcNow;
        foreach (var name in new[] { @"sub\Cookies", "sub/Cookies", "archivo:flujo", "..", ".", "" })
            Check.Equal(FileVerdict.SensitiveName, CleaningGuard.CheckFile(policy, name, now.AddDays(-9), now), "Nombre que no es un único componente: «" + name + "»");
        Check.Equal(FileVerdict.Eligible, CleaningGuard.CheckFile(policy, "f_000001", now.AddDays(-9), now), "Nombre normal");

        // Un FILETIME corrupto no rompe el análisis y cuenta como «demasiado reciente».
        Check.Equal(DateTime.MaxValue, FileEntry.ToUtc(long.MaxValue), "Fecha fuera de rango");
        Check.Equal(DateTime.MaxValue, FileEntry.ToUtc(-5), "Fecha negativa");
        Check.Equal(new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc), FileEntry.ToUtc(new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc).ToFileTimeUtc()), "Fecha válida");
        var aged = new CleaningPolicy(TimeSpan.FromHours(48), null);
        Check.Equal(FileVerdict.TooRecent, CleaningGuard.CheckFile(aged, "x.tmp", FileEntry.ToUtc(long.MaxValue), now), "Fecha corrupta = reciente");

        // Una categoría que no es de navegador no puede apuntar dentro de los datos de un navegador.
        var d3d = analysis.Discover().AllTargets().First(t => t.Key == K("windows", "d3d-cache", "all"));
        var profile = fixture.Profile("chrome", "Default");
        var hijack = d3d with { Roots = [new(profile, "Default", policy)], Anchor = Path.GetDirectoryName(profile)! };
        Check.True(CleaningGuard.ValidateTarget(hijack, fixture.Folders)!.Contains("navegador"), "Perfil de navegador disfrazado de caché del sistema");
        Check.True(CleaningGuard.ValidateTarget(d3d, fixture.Folders) is null, "El objetivo legítimo sigue siendo válido");

        // La instantánea solo reconoce como suyo el objetivo con sus mismas raíces.
        selection.SetTarget(K("chrome", "cache"), true);
        var snapshot = selection.Snapshot();
        var legit = snapshot.Targets.Single();
        Check.True(snapshot.Matches(legit), "Objetivo seleccionado");
        Check.True(!snapshot.Matches(legit with { Roots = [new(Path.Combine(profile, "Code Cache"), "Code Cache", legit.Roots[0].Policy)] }), "Misma clave, otra raíz");
        Check.True(!snapshot.Matches(legit with { Key = K("brave", "cache") }), "Otra clave");
        await Task.CompletedTask;
    }
});

tests.Add("mejora_desglose_de_protegidos_incidencias_al_log_y_marcar_todo", async () =>
{
    var (fixture, _, _) = Setup(f =>
    {
        var cache = Path.Combine(f.Profile("chrome", "Default"), "Cache");
        foreach (var name in new[] { "Cookies", "Login Data", "datos.db", "x-journal" }) f.Write(Path.Combine(cache, name), 40);
        f.Write(Path.Combine(cache, "Sessions", "a.bin"), 40);
        f.Write(Path.Combine(f.Outside, "destino", "x.bin"), 10);
    });
    using (fixture)
    {
        var cache = Path.Combine(fixture.Profile("chrome", "Default"), "Cache");
        var linked = SandboxFixture.TryJunction(Path.Combine(cache, "enlace"), Path.Combine(fixture.Outside, "destino"));
        var excluded = Path.Combine(cache, "Cache_Data");
        var logPath = Path.Combine(fixture.Folders.StateDirectory, "Logs", "ffcleaner.log");
        var analysis = new FileAnalysisService(fixture.Folders, [excluded], log: new DiagnosticsLog(logPath));
        var selection = new SelectionModel(analysis.Discover());

        // «Marcar todo» marca cada objetivo del catálogo y «desmarcar» vuelve a cero.
        var regular = selection.Catalog.AllTargets().Count(t => !selection.IsOptIn(t.Key));
        Check.True(selection.SelectAll() && selection.SelectedCount == regular && !selection.SelectAll(), "Marcar todo: solo categorías normales");
        Check.True(selection.SelectedKeys().All(k => !selection.IsOptIn(k)), "«Marcar todo» nunca incluye datos personales ni modelos");
        Check.True(selection.AppState("chrome") == true && selection.AppState("brave") == true, "Todas las aplicaciones marcadas");
        selection.Clear();
        selection.SetApp("chrome", true);
        Check.True(selection.SelectedKeys().All(k => k.AppId == "chrome" && !selection.IsOptIn(k)), "Marcar la aplicación no incluye cookies ni historial");
        Check.Equal<bool?>(true, selection.AppState("chrome"), "La aplicación se ve marcada");
        selection.SetTarget(K("chrome", "cookies"), true);
        Check.Equal<bool?>(true, selection.AppState("chrome"), "Sigue marcada al añadir cookies a mano");
        selection.SetApp("chrome", false);
        Check.Equal(0, selection.SelectedKeys().Count(k => k.AppId == "chrome"), "Desmarcar la aplicación quita también lo individual");
        selection.Clear();
        selection.SetTarget(K("chrome", "cache"), true);

        var session = await analysis.AnalyzeAsync(selection.Snapshot());
        var target = session.Targets.Single();
        Check.Equal(target.ProtectedCount, target.ProtectedByVerdict.Values.Sum(), "El desglose suma todos los protegidos");
        Check.Equal(2, target.ProtectedByVerdict[FileVerdict.SensitiveName], "Por nombre");
        Check.Equal(1, target.ProtectedByVerdict[FileVerdict.ProtectedExtension], "Por extensión");
        Check.Equal(1, target.ProtectedByVerdict[FileVerdict.DatabaseSidecar], "Auxiliar de base de datos");
        Check.Equal(1, target.ProtectedByVerdict[FileVerdict.SensitiveDirectory], "Por carpeta");
        Check.True(!target.ProtectedByVerdict.ContainsKey(FileVerdict.Eligible), "Lo limpiable no figura en el desglose");

        // La carpeta excluida queda marcada para que tampoco se retire si está vacía.
        var directories = target.Directories.ToArray();
        Check.True(directories.Single(d => d.Path.Equals(excluded, StringComparison.OrdinalIgnoreCase)).Sensitive, "Carpeta excluida marcada como protegida");

        // Cada incidencia se escribe en el log con su código, su ruta y su motivo.
        var log = File.ReadAllText(logPath);
        Check.True(log.Contains("incidencia Excluded") && log.Contains(excluded), "Exclusión registrada con su ruta");
        if (linked) Check.True(log.Contains("incidencia UnsafePath") && log.Contains(Path.Combine(cache, "enlace")), "Enlace no recorrido registrado con su ruta");
    }
});

tests.Add("sandbox_solo_acepta_carpetas_de_prueba_marcadas", () =>
{
    Check.Throws<ArgumentException>(() => new SandboxFolderProvider(new SystemFolderProvider().LocalAppData), "Perfil real como sandbox");
    Check.Throws<ArgumentException>(() => new SandboxFolderProvider(Path.GetTempPath()), "TEMP completo como sandbox");
    var unmarked = Path.Combine(Path.GetTempPath(), SandboxFolderProvider.DirectoryPrefix + "sin-marcador-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(unmarked);
    try { Check.Throws<ArgumentException>(() => new SandboxFolderProvider(unmarked), "Sin marcador"); }
    finally { Directory.Delete(unmarked); }
    var misnamed = Path.Combine(Path.GetTempPath(), "otra-carpeta-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(misnamed);
    File.WriteAllText(Path.Combine(misnamed, SandboxFolderProvider.MarkerFile), "");
    try { Check.Throws<ArgumentException>(() => new SandboxFolderProvider(misnamed), "Nombre sin prefijo"); }
    finally { Directory.Delete(misnamed, true); }
});

tests.Add("carpetas_del_sistema_se_resuelven_sin_rutas_fijas", () =>
{
    var folders = new SystemFolderProvider();
    Check.Equal(PathSafety.Normalize(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), folders.LocalAppData, "LOCALAPPDATA");
    Check.Equal(PathSafety.Normalize(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)), folders.RoamingAppData, "APPDATA");
    Check.Equal(PathSafety.Normalize(Path.GetTempPath()), folders.Temp, "TEMP");
    Check.True(folders.StateDirectory.StartsWith(folders.LocalAppData, StringComparison.OrdinalIgnoreCase) && !folders.IsSandbox, "Estado bajo LOCALAPPDATA");
});

// ───────────── Ajustes: migración sin perder protecciones ─────────────

tests.Add("ajustes_v1_se_migran_conservando_exclusiones_y_copia", () =>
{
    using var fixture = SandboxFixture.Create();
    var path = Path.Combine(fixture.Folders.StateDirectory, "settings.json");
    File.WriteAllText(path, "{\"SelectedRootIds\":[\"user-temp\",\"browser-cache:chrome:Default:Cache\"],\"Exclusions\":[\"C:\\\\Datos\\\\NoTocar\"]}");
    var original = File.ReadAllText(path);
    var store = new SettingsStore(path);
    var loaded = store.Load();
    Check.True(loaded.Exclusions.SequenceEqual([@"C:\Datos\NoTocar"]), "Exclusiones conservadas");
    Check.Equal(0, loaded.SelectedTargets.Count, "La selección antigua no se traduce a objetivos nuevos");
    store.Save(loaded with { SelectedTargets = ["chrome/cache/Default"], RegistryFamilies = ["startup"] });
    Check.Equal(original, File.ReadAllText(store.LegacyBackupPath), "Copia del archivo anterior");
    var again = store.Load();
    Check.True(again.SelectedTargets.SequenceEqual(["chrome/cache/Default"]) && again.Exclusions.Count == 1 && again.RegistryFamilies.SequenceEqual(["startup"]), "Ida y vuelta");
    store.Save(again);
    Check.Equal(original, File.ReadAllText(store.LegacyBackupPath), "La copia no se reescribe");
});

tests.Add("ajustes_corruptos_no_se_sobrescriben", () =>
{
    using var fixture = SandboxFixture.Create();
    var path = Path.Combine(fixture.Folders.StateDirectory, "settings.json");
    File.WriteAllText(path, "{ esto no es json");
    var store = new SettingsStore(path);
    Check.Throws<InvalidDataException>(() => store.Load(), "Cargar corrupto");
    Check.Throws<InvalidDataException>(() => store.Save(UserSettings.Empty), "Guardar sobre corrupto");
    Check.Equal("{ esto no es json", File.ReadAllText(path), "Archivo intacto");
    Check.Throws<ArgumentException>(() => new SettingsStore(Path.Combine(fixture.Root, "ajustes.txt")), "Extensión ajena");
});

tests.Add("renombre_migra_los_datos_de_claro_sin_tocar_la_carpeta_antigua", () =>
{
    using var fixture = SandboxFixture.Create();
    var legacy = Path.Combine(fixture.Root, "Local", "Claro");
    var current = Path.Combine(fixture.Root, "Local", "FFCleaner");
    fixture.Write(Path.Combine(legacy, "settings.json"), 50);
    fixture.Write(Path.Combine(legacy, "Logs", "claro.log"), 50);
    fixture.Write(Path.Combine(legacy, "RegistryBackups", "20261112-150843-2105e8e8", "respaldo.json"), 50);
    fixture.Write(Path.Combine(legacy, "Journal", "limpieza-1.jsonl"), 50);
    var before = SandboxFixture.HashTree(legacy);

    Check.Equal(legacy, SystemFolderProvider.MigrateLegacyState(legacy, current), "Migración realizada");
    Check.SameTree(before, SandboxFixture.HashTree(legacy), "La carpeta antigua queda intacta");
    Check.True(File.Exists(Path.Combine(current, "settings.json")) && File.Exists(Path.Combine(current, "RegistryBackups", "20261112-150843-2105e8e8", "respaldo.json")) &&
               File.Exists(Path.Combine(current, "Journal", "limpieza-1.jsonl")), "Ajustes, respaldos y bitácoras copiados");
    Check.True(File.Exists(Path.Combine(current, "Logs", "ffcleaner.log")) && !File.Exists(Path.Combine(current, "Logs", "claro.log")), "El log pasa a llamarse ffcleaner.log");
    Check.True(Directory.GetDirectories(Path.Combine(fixture.Root, "Local")).All(d => !d.Contains(".migrando-")), "Sin carpetas temporales de migración");

    // Segunda vez: la carpeta nueva ya existe y no se vuelve a copiar nada encima.
    File.WriteAllText(Path.Combine(current, "settings.json"), "nuevo");
    Check.True(SystemFolderProvider.MigrateLegacyState(legacy, current) is null && File.ReadAllText(Path.Combine(current, "settings.json")) == "nuevo", "No se sobrescribe una instalación ya migrada");
    Check.True(SystemFolderProvider.MigrateLegacyState(Path.Combine(fixture.Root, "no-existe"), Path.Combine(fixture.Root, "otra")) is null, "Sin carpeta antigua no hay migración");
});

tests.Add("claves_de_objetivo_ida_y_vuelta", () =>
{
    Check.True(TargetKey.TryParse("chrome/cache/Profile 1", out var key) && key == new TargetKey("chrome", "cache", "Profile 1"), "Parse");
    Check.Equal("chrome/cache/Profile 1", key.ToString(), "ToString");
    Check.True(!TargetKey.TryParse("chrome/cache", out _) && !TargetKey.TryParse("a//b", out _) && !TargetKey.TryParse(null, out _), "Formas inválidas");
});

return await tests.RunAsync(args);

/// <summary>Ejecutor falso: solo cuenta llamadas. Sirve para probar que el flujo nunca le entrega un lote obsoleto.</summary>
internal sealed class CountingCleaner : IFileCleaningService
{
    public int Calls;
    public Task<CleaningReport> CleanAsync(AnalysisSession session, SelectionSnapshot currentSelection, IProgress<CleaningProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(new CleaningReport(session.Id, DateTime.UtcNow, DateTime.UtcNow, [], 0, 0, 0, false, null, "", null));
    }
}

/// <summary>Análisis real retenido hasta que la prueba lo libera.</summary>
internal sealed class GatedAnalysis(IAnalysisService inner, Task gate) : IAnalysisService
{
    public readonly TaskCompletionSource Started = new();
    public bool SawCancellation;
    public CleanupCatalog Discover() => inner.Discover();
    public async Task<AnalysisSession> AnalyzeAsync(SelectionSnapshot selection, IProgress<AnalysisProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Started.TrySetResult();
        await gate;
        SawCancellation = cancellationToken.IsCancellationRequested;
        return await inner.AnalyzeAsync(selection, progress, CancellationToken.None);
    }
}

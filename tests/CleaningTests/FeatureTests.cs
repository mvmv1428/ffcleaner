using FFCleaner.Cleaning;
using FFCleaner.Core;
using FFCleaner.Tests;

/// <summary>
/// Pruebas de las funciones nuevas: datos de navegador (A7), herramientas de IA y desarrollo (A1) y cachés de Windows (A3).
/// Todo ocurre en un sandbox con bases SQLite reales y carpetas que imitan las de cada aplicación.
/// </summary>
internal static class FeatureTests
{
    private static TargetKey K(string app, string category, string profile = "Default") => new(app, category, profile);

    private const string Profile = "abcd.default-release";

    /// <summary>Perfil de Firefox con places.sqlite y cookies.sqlite reales (esquema de Firefox) y contraseñas de prueba.</summary>
    private static string AddFirefoxWithDatabases(SandboxFixture fixture)
    {
        fixture.AddFirefoxProfile(Profile);
        var roaming = Path.Combine(fixture.Folders.RoamingAppData, "Mozilla", "Firefox", "Profiles", Profile);
        BrowserDbFixture.Db(Path.Combine(roaming, "places.sqlite"),
            "CREATE TABLE moz_origins(id INTEGER PRIMARY KEY, prefix TEXT, host TEXT);" +
            "CREATE TABLE moz_places(id INTEGER PRIMARY KEY, url TEXT, title TEXT, visit_count INTEGER DEFAULT 0, last_visit_date INTEGER, origin_id INTEGER);" +
            "CREATE TABLE moz_historyvisits(id INTEGER PRIMARY KEY, place_id INTEGER, visit_date INTEGER);" +
            "CREATE TABLE moz_bookmarks(id INTEGER PRIMARY KEY, fk INTEGER, title TEXT);" +
            "CREATE TABLE moz_annos(id INTEGER PRIMARY KEY, place_id INTEGER, content TEXT);" +
            "CREATE TABLE moz_keywords(id INTEGER PRIMARY KEY, keyword TEXT, place_id INTEGER);" +
            "CREATE TABLE moz_inputhistory(place_id INTEGER, input TEXT);" +
            "CREATE INDEX moz_historyvisits_placedateindex ON moz_historyvisits(place_id, visit_date);" +
            "INSERT INTO moz_origins VALUES(1,'https://','marcador.example'),(2,'https://','visitada.example'),(3,'https://','anotada.example');" +
            "INSERT INTO moz_places VALUES(1,'https://marcador.example/','Marcador',3,1700000000,1),(2,'https://visitada.example/','Visitada',2,1700000001,2)," +
            "(3,'https://anotada.example/','Anotada',0,NULL,3),(4,'https://visitada.example/otra','Otra',1,1700000002,2);" +
            "INSERT INTO moz_historyvisits(place_id, visit_date) VALUES(1,1),(1,2),(1,3),(2,4),(2,5),(4,6);" +
            "INSERT INTO moz_bookmarks VALUES(1,NULL,'Barra de marcadores'),(2,1,'Mi marcador');" +
            "INSERT INTO moz_annos VALUES(1,3,'anotación');" +
            "INSERT INTO moz_inputhistory VALUES(2,'vis'),(4,'otr');");
        BrowserDbFixture.Db(Path.Combine(roaming, "cookies.sqlite"),
            "CREATE TABLE moz_cookies(id INTEGER PRIMARY KEY, host TEXT, name TEXT, value TEXT);" +
            "INSERT INTO moz_cookies(host, name, value) VALUES('a.example','s','1'),('b.example','s','2'),('c.example','s','3');");
        File.WriteAllText(Path.Combine(roaming, "logins.json"), "{\"logins\":[{\"hostname\":\"https://banco.example\"}]}");
        return roaming;
    }

    private static Env Setup(Action<SandboxFixture>? extra = null, IProcessProbe? processes = null, Func<IFolderProvider, IFolderProvider>? wrap = null)
    {
        var fixture = SandboxFixture.Create();
        foreach (var (browser, profile) in new[] { ("chrome", "Default"), ("chrome", "Profile 1"), ("brave", "Default") })
        {
            fixture.AddChromiumProfile(browser, profile);
            BrowserDbFixture.Create(fixture.Profile(browser, profile));
        }
        extra?.Invoke(fixture);
        IFolderProvider folders = wrap is null ? fixture.Folders : wrap(fixture.Folders);
        var analysis = new FileAnalysisService(folders);
        var selection = new SelectionModel(analysis.Discover());
        var cleaning = new FileCleaningService(folders, processes: processes ?? new FakeProcesses());
        return new(fixture, analysis, selection, cleaning, new CleanupWorkflow(selection, analysis, cleaning));
    }

    private static string[] BackupFiles(Env env) =>
        Directory.Exists(env.Cleaning.BrowserBackupDirectory)
            ? Directory.GetFiles(env.Cleaning.BrowserBackupDirectory, "*", SearchOption.AllDirectories)
            : [];

    public static void Register(TestRunner tests)
    {
        // ───────────── Auditoría M3: un fallo al copiar una base no corta el lote ─────────────
        tests.Add("M3_si_no_se_puede_copiar_la_base_no_se_toca_y_el_resto_del_lote_sigue", async () =>
        {
            using var env = Setup();
            var profile = env.Fixture.Profile("chrome", "Default");
            // Un archivo con el nombre de la carpeta de copias impide crearla (como un disco lleno o sin permisos).
            Directory.CreateDirectory(env.Fixture.Folders.StateDirectory);
            File.WriteAllText(env.Cleaning.BrowserBackupDirectory, "bloquea la carpeta de copias");
            env.Selection.SetTarget(K("chrome", "history"), true);
            env.Selection.SetTarget(K("chrome", "cache"), true);
            await env.Workflow.AnalyzeAsync();
            var report = await env.Workflow.CleanAsync();
            var history = report.Targets.Single(t => t.Key.CategoryId == "history");
            var cache = report.Targets.Single(t => t.Key.CategoryId == "cache");
            Check.True(history.Failed > 0 && history.Details.Single().Detail.Contains("No se pudo copiar"), "El historial queda en error y lo explica: " + history.Details.Single().Detail);
            Check.Equal(5L, BrowserDbFixture.Count(Path.Combine(profile, "History"), "urls"), "Sin copia, la base no se modifica");
            Check.True(cache.Deleted > 0 && cache.DeletedBytes > 0, "La caché del mismo lote se limpia igualmente");
            Check.Equal(report.DeletedBytes, env.Cleaning.Totals.Read().Bytes, "Lo liberado se suma al total");
            var journal = File.ReadAllText(report.JournalPath!);
            Check.True(journal.Contains("\"evento\":\"fin\""), "La bitácora se cierra con su evento «fin»");
        });

        // ───────────── Auditoría A2: las copias antiguas se borran ─────────────
        tests.Add("A2_las_copias_de_bases_que_ya_no_hacen_falta_se_borran", () =>
        {
            using var env = Setup();
            var root = env.Cleaning.BrowserBackupDirectory;
            string Batch(string name, bool keep, TimeSpan age)
            {
                var folder = Path.Combine(root, name, "chrome", "Default");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "History"), "historial copiado");
                if (keep) File.WriteAllText(Path.Combine(folder, "CONSERVAR.txt"), "restauración sin comprobar");
                Directory.SetCreationTimeUtc(Path.Combine(root, name), DateTime.UtcNow - age);
                return Path.Combine(root, name);
            }
            var legacy = Batch("20261101-100000-aaaaaa", keep: false, TimeSpan.FromDays(2));
            var running = Batch("20261114-100000-bbbbbb", keep: false, TimeSpan.FromMinutes(5));
            var needed = Batch("20261110-100000-cccccc", keep: true, TimeSpan.FromDays(4));
            var expired = Batch("20261001-100000-dddddd", keep: true, TimeSpan.FromDays(40));
            Check.Equal(2, env.Cleaning.PruneBrowserBackups(), "Se borran dos lotes");
            Check.True(!Directory.Exists(legacy), "Copia de una versión anterior (sin aviso de conservar): borrada");
            Check.True(Directory.Exists(running), "Copia de la última hora (puede ser de una limpieza en curso en otro proceso): se respeta");
            Check.True(Directory.Exists(needed), "Copia necesaria para recuperar una base: se conserva");
            Check.True(!Directory.Exists(expired), "Copia necesaria de hace más de 30 días: borrada");
        });

        // ───────────── A7: datos de navegador ─────────────
        tests.Add("A7_historial_y_cookies_se_borran_dentro_de_las_bases_sin_tocar_descargas_formularios_ni_contrasenas", async () =>
        {
            using var env = Setup();
            var profile = env.Fixture.Profile("chrome", "Default");
            var other = SandboxFixture.HashTree(env.Fixture.Profile("chrome", "Profile 1"));
            var brave = SandboxFixture.HashTree(env.Fixture.UserData("brave"));
            var webData = SandboxFixture.HashTree(profile).Where(p => p.Key.StartsWith("Web Data") || p.Key.StartsWith("Login Data")).ToDictionary(p => p.Key, p => p.Value);
            env.Selection.SetTarget(K("chrome", "history"), true);
            env.Selection.SetTarget(K("chrome", "cookies"), true);
            var session = await env.Workflow.AnalyzeAsync();
            var history = session.Targets.Single(t => t.Target.Key.CategoryId == "history");
            Check.Equal(9L, history.DataItems, "Elementos de historial: 5 URL, 2 sitios frecuentes y 2 accesos directos");
            Check.Equal(4L, session.Targets.Single(t => t.Target.Key.CategoryId == "cookies").DataItems, "Cookies");
            Check.Equal(13L, session.EligibleFiles, "Elementos totales");
            // El espacio de los datos personales cuenta en el total: lo que ocupan sus tablas, no la base entera.
            var historyDb = new FileInfo(Path.Combine(profile, "History")).Length;
            Check.True(history.DataBytes > 0 && history.DataBytes < historyDb + new FileInfo(Path.Combine(profile, "Top Sites")).Length +
                new FileInfo(Path.Combine(profile, "Shortcuts")).Length + new FileInfo(Path.Combine(profile, "Visited Links")).Length,
                $"Historial: estimación ({history.DataBytes} B) por debajo del tamaño de sus bases");
            Check.Equal(session.EligibleBytes + session.Targets.Sum(t => t.DataBytes), session.FreeableBytes, "«Puedes liberar» suma los datos de navegador");
            Check.True(session.FreeableBytes > session.EligibleBytes, "Marcar datos personales aumenta lo que se puede liberar");

            var report = await env.Workflow.CleanAsync();
            Check.True(!report.Refused && report.NotDeleted == 0, "Limpieza completa: " + string.Join(" | ", report.Targets.SelectMany(t => t.Details).Select(d => d.Detail)));
            var hist = Path.Combine(profile, "History");
            Check.Equal(0L, BrowserDbFixture.Count(hist, "urls"), "URL borradas");
            Check.Equal(0L, BrowserDbFixture.Count(hist, "visits"), "Visitas borradas");
            Check.Equal(0L, BrowserDbFixture.Count(hist, "keyword_search_terms"), "Búsquedas borradas");
            Check.Equal(3L, BrowserDbFixture.Count(hist, "downloads"), "El historial de descargas se conserva");
            Check.Equal(1L, BrowserDbFixture.Count(hist, "meta"), "La tabla meta se conserva");
            Check.Equal(0L, BrowserDbFixture.Count(Path.Combine(profile, "Top Sites"), "top_sites"), "Sitios frecuentes borrados");
            Check.Equal(0L, BrowserDbFixture.Count(Path.Combine(profile, "Shortcuts"), "omni_box_shortcuts"), "Accesos de la barra borrados");
            Check.True(!File.Exists(Path.Combine(profile, "Visited Links")), "«Visited Links» eliminado");
            Check.Equal(0L, BrowserDbFixture.Count(Path.Combine(profile, "Network", "Cookies"), "cookies"), "Cookies borradas");
            Check.Equal("ok", BrowserDbFixture.Integrity(hist), "Integridad del historial");
            foreach (var (name, hash) in webData)
                Check.Equal(hash, SandboxFixture.HashTree(profile)[name], "Intacto: " + name);
            Check.SameTree(other, SandboxFixture.HashTree(env.Fixture.Profile("chrome", "Profile 1")), "Perfil no marcado");
            Check.SameTree(brave, SandboxFixture.HashTree(env.Fixture.UserData("brave")), "Brave");
            // La copia previa solo sirve para deshacer un fallo: si todo sale bien se borra y el historial no queda guardado en FFCleaner.
            Check.Equal(0, BackupFiles(env).Length, "Tras una limpieza correcta no queda ninguna copia de las bases");
            Check.True(report.Targets.All(t => t.Details.All(d => d.Detail.Contains("copia temporal"))), "El resultado dice que la copia temporal también se borró");
        });

        tests.Add("A7_descargas_formularios_y_sesiones_tocan_solo_lo_suyo", async () =>
        {
            using var env = Setup();
            var profile = env.Fixture.Profile("chrome", "Default");
            foreach (var category in new[] { "downloads", "forms", "sessions" }) env.Selection.SetTarget(K("chrome", category), true);
            await env.Workflow.AnalyzeAsync();
            var report = await env.Workflow.CleanAsync();
            Check.True(report.NotDeleted == 0, "Sin fallos");
            var hist = Path.Combine(profile, "History");
            Check.Equal(0L, BrowserDbFixture.Count(hist, "downloads"), "Descargas borradas");
            Check.Equal(5L, BrowserDbFixture.Count(hist, "urls"), "El historial de navegación se conserva");
            var web = Path.Combine(profile, "Web Data");
            Check.Equal(0L, BrowserDbFixture.Count(web, "autofill"), "Formularios borrados");
            Check.Equal(1L, BrowserDbFixture.Count(web, "credit_cards"), "Tarjetas conservadas");
            Check.Equal(1L, BrowserDbFixture.Count(web, "autofill_profiles"), "Direcciones conservadas");
            Check.Equal(2L, BrowserDbFixture.Count(web, "keywords"), "Buscadores conservados");
            Check.Equal(1L, BrowserDbFixture.Count(Path.Combine(profile, "Login Data"), "logins"), "Contraseñas conservadas");
            Check.Equal(0, Directory.GetFiles(Path.Combine(profile, "Sessions")).Length, "Sesiones borradas");
            Check.True(!File.Exists(Path.Combine(profile, "Current Session")), "Sesión antigua borrada");
            Check.Equal(4L, BrowserDbFixture.Count(Path.Combine(profile, "Network", "Cookies"), "cookies"), "Cookies conservadas");
            Check.True(File.Exists(Path.Combine(profile, "Bookmarks")) && File.Exists(Path.Combine(profile, "Preferences")), "Marcadores y preferencias");
        });

        tests.Add("A7_navegador_abierto_o_base_bloqueada_no_se_toca", async () =>
        {
            using (var env = Setup(processes: new FakeProcesses("chrome")))
            {
                env.Selection.SetTarget(K("chrome", "history"), true);
                await env.Workflow.AnalyzeAsync();
                var before = SandboxFixture.HashTree(env.Fixture.Profile("chrome", "Default"));
                var report = await env.Workflow.CleanAsync();
                var result = report.Targets.Single();
                Check.True(result.Locked > 0 && result.Deleted == 0 && result.Details.Single().Detail.Contains("está abierto"), "Bloqueado por navegador abierto");
                Check.SameTree(before, SandboxFixture.HashTree(env.Fixture.Profile("chrome", "Default")), "Perfil intacto");
            }
            using (var env = Setup())
            {
                var cookies = Path.Combine(env.Fixture.Profile("chrome", "Default"), "Network", "Cookies");
                env.Selection.SetTarget(K("chrome", "cookies"), true);
                await env.Workflow.AnalyzeAsync();
                CleaningReport report;
                using (BrowserDbFixture.LockExclusive(cookies)) report = await env.Workflow.CleanAsync();
                Check.True(report.Targets.Single().Locked > 0, "Base bloqueada: " + report.Targets.Single().Details.Single().Detail);
                Check.Equal(4L, BrowserDbFixture.Count(cookies, "cookies"), "Cookies intactas");
                Check.Equal(0, BackupFiles(env).Length, "Sin modificar la base, la copia previa no se conserva");
                Check.Equal("ok", BrowserDbFixture.Integrity(cookies), "Base íntegra");
            }
            using (var env = Setup())
            {
                // Con la base bloqueada, el análisis tampoco puede leerla y lo explica.
                var hist = Path.Combine(env.Fixture.Profile("chrome", "Default"), "History");
                env.Selection.SetTarget(K("chrome", "history"), true);
                AnalysisSession session;
                using (BrowserDbFixture.LockExclusive(hist)) session = await env.Analysis.AnalyzeAsync(env.Selection.Snapshot());
                Check.True(session.Issues.Any(i => i.Code == "Locked" && i.Message.Contains("cierra")), "Aviso de base en uso");
            }
        });

        tests.Add("Firefox_historial_y_cookies_conservan_marcadores_y_contrasenas", async () =>
        {
            string roaming = "";
            using var env = Setup(f => roaming = AddFirefoxWithDatabases(f));
            var firefox = env.Selection.Catalog.Apps.Single(a => a.Id == "firefox");
            Check.True(firefox.Categories.Any(c => c.Id == "history" && c.Risk == CategoryRisk.DatosPersonales) &&
                       firefox.Categories.Any(c => c.Id == "cookies" && c.Risk == CategoryRisk.DatosPersonales), "Firefox ofrece historial y cookies como datos personales");
            env.Selection.SelectAll();
            Check.True(!env.Selection.IsSelected(K("firefox", "history", Profile)) && !env.Selection.IsSelected(K("firefox", "cookies", Profile)), "«Marcar todo» no los incluye");
            env.Selection.Clear();
            var places = Path.Combine(roaming, "places.sqlite");
            var cookies = Path.Combine(roaming, "cookies.sqlite");
            var logins = File.ReadAllBytes(Path.Combine(roaming, "logins.json"));
            var keys = File.ReadAllBytes(Path.Combine(roaming, "key4.db"));
            env.Selection.SetTarget(K("firefox", "history", Profile), true);
            env.Selection.SetTarget(K("firefox", "cookies", Profile), true);
            var session = await env.Workflow.AnalyzeAsync();
            var history = session.Targets.Single(t => t.Target.Key.CategoryId == "history");
            Check.Equal(6L, history.DataItems, "Visitas que se borrarían");
            Check.Equal(3L, session.Targets.Single(t => t.Target.Key.CategoryId == "cookies").DataItems, "Cookies que se borrarían");
            Check.True(history.DataBytes > 0 && session.FreeableBytes > session.EligibleBytes, "El espacio estimado cuenta en el total");

            var report = await env.Workflow.CleanAsync();
            Check.True(!report.Refused && report.NotDeleted == 0, "Limpieza completa: " + string.Join(" | ", report.Targets.SelectMany(t => t.Details).Select(d => d.Detail)));
            Check.Equal(0L, BrowserDbFixture.Count(places, "moz_historyvisits"), "Visitas borradas");
            Check.Equal(0L, BrowserDbFixture.Count(places, "moz_inputhistory"), "Lo escrito en la barra, borrado");
            Check.Equal(2L, BrowserDbFixture.Count(places, "moz_places"), "Solo quedan la página marcada como marcador y la que tiene anotación");
            Check.Equal(2L, BrowserDbFixture.Count(places, "moz_bookmarks"), "Marcadores intactos");
            Check.Equal(1L, BrowserDbFixture.Count(places, "moz_annos"), "Anotaciones intactas");
            Check.Equal(2L, BrowserDbFixture.Count(places, "moz_origins"), "Se retiran los orígenes que quedaron sin páginas");
            using (var db = SqliteDb.Open(places, writable: false))
                Check.Equal(0L, db.Scalar("SELECT count(*) FROM moz_places WHERE visit_count <> 0 OR last_visit_date IS NOT NULL"), "Las páginas conservadas ya no cuentan visitas");
            Check.Equal(0L, BrowserDbFixture.Count(cookies, "moz_cookies"), "Cookies borradas");
            Check.Equal("ok", BrowserDbFixture.Integrity(places), "Integridad de places.sqlite");
            Check.True(File.ReadAllBytes(Path.Combine(roaming, "logins.json")).SequenceEqual(logins) && File.ReadAllBytes(Path.Combine(roaming, "key4.db")).SequenceEqual(keys), "Contraseñas y claves intactas");
            Check.Equal(0, BackupFiles(env).Length, "Tras una limpieza correcta no queda copia de places.sqlite ni de cookies.sqlite");
        });

        tests.Add("Total_liberado_se_acumula_y_se_siembra_desde_las_bitacoras_sin_contar_dos_veces", async () =>
        {
            using var env = Setup();
            var totals = env.Cleaning.Totals;
            Check.Equal(0, totals.Read().Cleanings, "Sin limpiezas, acumulado vacío");
            env.Selection.SetTarget(K("chrome", "cache"), true);
            await env.Workflow.AnalyzeAsync();
            var first = await env.Workflow.CleanAsync();
            var afterFirst = totals.Read();
            Check.True(first.DeletedBytes > 0 && afterFirst.Bytes == first.DeletedBytes && afterFirst.Files == first.DeletedFiles && afterFirst.Cleanings == 1,
                "La primera limpieza se suma una sola vez");
            env.Selection.Clear();
            env.Selection.SetTarget(K("brave", "cache"), true);
            await env.Workflow.AnalyzeAsync();
            var second = await env.Workflow.CleanAsync();
            var afterSecond = totals.Read();
            Check.True(afterSecond.Bytes == first.DeletedBytes + second.DeletedBytes && afterSecond.Cleanings == 2 && afterSecond.SinceUtc is not null, "La segunda se acumula");
            // Sin totals.json (p. ej. al migrar de Claro), se reconstruye desde las bitácoras con el mismo resultado.
            File.Delete(totals.FilePath);
            var seeded = totals.Read();
            Check.True(seeded.Bytes == afterSecond.Bytes && seeded.Files == afterSecond.Files && seeded.Cleanings == 2, "Sembrado desde las bitácoras sin duplicar");
        });

        tests.Add("Firefox_abierto_no_se_toca", async () =>
        {
            string roaming = "";
            using var env = Setup(f => roaming = AddFirefoxWithDatabases(f), processes: new FakeProcesses("firefox"));
            env.Selection.SetTarget(K("firefox", "cookies", Profile), true);
            await env.Workflow.AnalyzeAsync();
            var report = await env.Workflow.CleanAsync();
            Check.True(report.Targets.Single().Locked > 0 && report.Targets.Single().Deleted == 0, "Bloqueado mientras Firefox está abierto");
            Check.Equal(3L, BrowserDbFixture.Count(Path.Combine(roaming, "cookies.sqlite"), "moz_cookies"), "Cookies intactas");
        });

        tests.Add("A7_un_objetivo_falsificado_no_puede_apuntar_a_contrasenas_ni_a_otro_perfil", () =>
        {
            using var env = Setup();
            var legit = env.Selection.Catalog.AllTargets().First(t => t.Key == K("chrome", "forms"));
            var profile = env.Fixture.Profile("chrome", "Default");
            foreach (var (path, leaf) in new[] { (Path.Combine(profile, "Login Data"), "Login Data"), (Path.Combine(profile, "Bookmarks"), "Bookmarks"),
                         (Path.Combine(env.Fixture.Profile("chrome", "Profile 1"), "Web Data"), "Web Data"), (Path.Combine(profile, "Web Data"), "History") })
                Check.True(CleaningGuard.ValidateTarget(legit with { Roots = [new(path, leaf, legit.Roots[0].Policy)] }, env.Fixture.Folders) is not null, "Rechazado: " + leaf);
            Check.True(CleaningGuard.ValidateTarget(legit, env.Fixture.Folders) is null, "El legítimo es válido");
            Check.True(env.Selection.Catalog.AllTargets().All(t => t.Key.CategoryId is not ("passwords" or "logins")), "No existe categoría de contraseñas");
        });

        // ───────────── A1: IA y herramientas de desarrollo ─────────────
        tests.Add("A1_cachés_de_IA_se_limpian_y_conversaciones_ajustes_plugins_y_sesiones_se_conservan", async () =>
        {
            using var env = Setup(f =>
            {
                var claude = Path.Combine(f.Folders.RoamingAppData, "Claude");
                foreach (var leaf in new[] { "Cache", "Code Cache", "GPUCache", "logs", "Crashpad" }) f.Write(Path.Combine(claude, leaf, "data_" + leaf.Replace(' ', '_')), 900);
                f.Write(Path.Combine(claude, "Network", "Cookies"), 200);
                f.Write(Path.Combine(claude, "Local Storage", "leveldb", "000003.log"), 200);
                f.Write(Path.Combine(claude, "config.json"), 50);
                var home = f.Folders.UserProfile;
                f.Write(Path.Combine(home, ".claude", "cache", "changelog.md"), 400);
                f.Write(Path.Combine(home, ".claude", "projects", "proyecto", "sesion.jsonl"), 400);
                f.Write(Path.Combine(home, ".claude", "settings.json"), 40);
                f.Write(Path.Combine(home, ".claude", "plugins", "marketplace", "plugin.json"), 40);
                f.Write(Path.Combine(f.Folders.LocalAppData, "claude-cli-nodejs", "Cache", "x"), 300);
                var cursor = Path.Combine(f.Folders.RoamingAppData, "Cursor");
                f.Write(Path.Combine(cursor, "CachedData", "abc", "chrome", "js", "x.code"), 800);
                f.Write(Path.Combine(cursor, "CachedExtensionVSIXs", "ext.vsix"), 800);
                f.Write(Path.Combine(cursor, "User", "settings.json"), 80);
                f.Write(Path.Combine(cursor, "User", "globalStorage", "state.vscdb"), 80);
            });
            var home = env.Fixture.Folders.UserProfile;
            var keep = new[]
            {
                Path.Combine(env.Fixture.Folders.RoamingAppData, "Claude", "Network", "Cookies"), Path.Combine(env.Fixture.Folders.RoamingAppData, "Claude", "Local Storage", "leveldb", "000003.log"),
                Path.Combine(env.Fixture.Folders.RoamingAppData, "Claude", "config.json"), Path.Combine(home, ".claude", "projects", "proyecto", "sesion.jsonl"),
                Path.Combine(home, ".claude", "settings.json"), Path.Combine(home, ".claude", "plugins", "marketplace", "plugin.json"),
                Path.Combine(env.Fixture.Folders.RoamingAppData, "Cursor", "User", "settings.json"), Path.Combine(env.Fixture.Folders.RoamingAppData, "Cursor", "User", "globalStorage", "state.vscdb"),
            };
            var group = env.Selection.Catalog.Apps.Where(a => a.Group == AppGroup.InteligenciaArtificial).Select(a => a.Id).ToList();
            Check.True(group.SequenceEqual(["claude", "claude-code", "cursor"]), "Aplicaciones detectadas: " + string.Join(",", group));
            foreach (var app in group) env.Selection.SetApp(app, true);
            var session = await env.Workflow.AnalyzeAsync();
            Check.Equal(9L, session.EligibleFiles, "Archivos de caché encontrados");
            var report = await env.Workflow.CleanAsync();
            Check.Equal(9, report.DeletedFiles, "Todo lo anunciado se eliminó");
            foreach (var file in keep) Check.True(File.Exists(file), "Conservado: " + file);
            Check.True(!File.Exists(Path.Combine(home, ".claude", "cache", "changelog.md")) && !File.Exists(Path.Combine(env.Fixture.Folders.RoamingAppData, "Cursor", "CachedExtensionVSIXs", "ext.vsix")), "Cachés eliminadas");
        });

        tests.Add("A1_modelos_de_IA_solo_con_su_categoria_y_se_borran_completos", async () =>
        {
            using var env = Setup(f =>
            {
                var home = f.Folders.UserProfile;
                var hf = Path.Combine(home, ".cache", "huggingface");
                f.Write(Path.Combine(hf, "hub", "models--org--modelo", "blobs", "a1b2c3"), 5000);
                f.Write(Path.Combine(hf, "hub", "models--org--modelo", "snapshots", "rev", "model.safetensors"), 5000);
                f.Write(Path.Combine(hf, "hub", "models--org--modelo", "refs", "main"), 40);
                f.Write(Path.Combine(hf, "hub", "datasets--org--datos", "blobs", "d1"), 3000);
                f.Write(Path.Combine(hf, "hub", ".locks", "models--org--modelo", "a1b2c3.lock"), 0);
                f.Write(Path.Combine(hf, "token"), 30);
                f.Write(Path.Combine(hf, "hub", "models--org--modelo", "cert.pem"), 30);
                f.Write(Path.Combine(home, ".ollama", "models", "blobs", "sha256-abc"), 6000);
                f.Write(Path.Combine(home, ".ollama", "models", "manifests", "registry.ollama.ai", "library", "llama", "latest"), 100);
                f.Write(Path.Combine(home, ".ollama", "id_ed25519"), 100);
                f.Write(Path.Combine(home, ".lmstudio", "models", "autor", "modelo-Q4.gguf"), 7000);
                f.Write(Path.Combine(home, "Documents", "modelo-propio.gguf"), 7000);
            });
            var home = env.Fixture.Folders.UserProfile;
            var models = env.Selection.Catalog.Apps.SelectMany(a => a.Categories).Where(c => c.Id is "models" or "datasets").ToList();
            Check.True(models.All(c => c.Risk == CategoryRisk.MuyGrande), "Marcadas como muy grandes");
            env.Selection.SetTarget(K("huggingface", "cache", "all"), true);
            await env.Workflow.AnalyzeAsync();
            await env.Workflow.CleanAsync();
            Check.True(File.Exists(Path.Combine(home, ".cache", "huggingface", "hub", "models--org--modelo", "snapshots", "rev", "model.safetensors")), "Solo la caché: los modelos siguen");
            foreach (var (app, category) in new[] { ("huggingface", "models"), ("huggingface", "datasets"), ("ollama", "models"), ("lmstudio", "models") })
                env.Selection.SetTarget(K(app, category, "all"), true);
            env.Selection.SetTarget(K("huggingface", "cache", "all"), false);
            var session = await env.Workflow.AnalyzeAsync();
            var report = await env.Workflow.CleanAsync();
            Check.True(report.NotDeleted == 0, "Sin omisiones: " + string.Join(" | ", report.Reasons().Select(r => r.Key)));
            Check.Equal(0, SandboxFixture.CountFiles(Path.Combine(home, ".ollama", "models")), "Modelos de Ollama completos (blobs y manifiestos)");
            Check.True(!File.Exists(Path.Combine(home, ".lmstudio", "models", "autor", "modelo-Q4.gguf")), ".gguf de LM Studio");
            Check.True(!File.Exists(Path.Combine(home, ".cache", "huggingface", "hub", "models--org--modelo", "snapshots", "rev", "model.safetensors")), ".safetensors de Hugging Face");
            Check.True(File.Exists(Path.Combine(home, ".cache", "huggingface", "hub", "models--org--modelo", "cert.pem")), ".pem sigue protegido");
            Check.True(File.Exists(Path.Combine(home, ".ollama", "id_ed25519")) && File.Exists(Path.Combine(home, ".cache", "huggingface", "token")), "Claves y token fuera de las raíces de modelos");
            Check.True(File.Exists(Path.Combine(home, "Documents", "modelo-propio.gguf")), "Un modelo en Documentos nunca se toca");
            Check.True(session.EligibleBytes >= 21000, "El tamaño de los modelos se anuncia");
        });

        // ───────────── A3: Windows ─────────────
        tests.Add("A3_cachés_de_Windows_de_usuario_y_de_sistema_con_sus_filtros", async () =>
        {
            using var env = Setup(f =>
            {
                var local = f.Folders.LocalAppData;
                var explorer = Path.Combine(local, "Microsoft", "Windows", "Explorer");
                f.Write(Path.Combine(explorer, "thumbcache_256.db"), 3000);
                f.Write(Path.Combine(explorer, "thumbcache_idx.db"), 300);
                f.Write(Path.Combine(explorer, "iconcache_32.db"), 3000);
                f.Write(Path.Combine(local, "Microsoft", "Windows", "WER", "ReportArchive", "AppCrash_x", "Report.wer"), 900);
                f.Write(Path.Combine(local, "CrashDumps", "app.exe.1234.dmp"), 9000);
                f.Write(Path.Combine(local, "CrashDumps", "notas.txt"), 10);
                f.Write(Path.Combine(f.Folders.LocalLowAppData, "Microsoft", "CryptnetUrlCache", "Content", "ABCDEF"), 700);
                var win = f.Folders.WindowsDirectory;
                f.Write(Path.Combine(win, "Temp", "viejo.tmp"), 500);
                f.Write(Path.Combine(win, "Temp", "reciente.tmp"), 500, DateTime.UtcNow);
                f.Write(Path.Combine(win, "Minidump", "111324-01.dmp"), 4000);
                f.Write(Path.Combine(win, "Logs", "CBS", "CBS.log"), 4000);
                f.Write(Path.Combine(win, "Logs", "WindowsUpdate", "u.etl"), 4000);
                f.Write(Path.Combine(win, "Logs", "config.xml"), 40);
                f.Write(Path.Combine(win, "System32", "config", "SYSTEM"), 4000);
                f.Write(Path.Combine(win, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache", "f1"), 8000);
                f.Write(Path.Combine(f.Folders.ProgramData, "Microsoft", "Windows", "WER", "ReportQueue", "Kernel_x", "Report.wer"), 900);
            });
            var windows = env.Selection.Catalog.Apps.Single(a => a.Id == "windows").Categories;
            Check.True(windows.Where(c => c.Risk == CategoryRisk.Administrador).Select(c => c.Id).OrderBy(x => x).SequenceEqual(
                ["delivery-optimization", "memory-dumps", "system-error-reports", "system-temp", "windows-logs"]), "Categorías de administrador");
            env.Selection.SetApp("windows", true);
            await env.Workflow.AnalyzeAsync();
            var report = await env.Workflow.CleanAsync();
            var local = env.Fixture.Folders.LocalAppData;
            var win = env.Fixture.Folders.WindowsDirectory;
            Check.True(!File.Exists(Path.Combine(local, "Microsoft", "Windows", "Explorer", "thumbcache_256.db")), "Miniaturas");
            Check.True(File.Exists(Path.Combine(local, "Microsoft", "Windows", "Explorer", "iconcache_32.db")), "La caché de iconos no se toca");
            Check.True(!File.Exists(Path.Combine(local, "CrashDumps", "app.exe.1234.dmp")) && File.Exists(Path.Combine(local, "CrashDumps", "notas.txt")), "Volcados: solo .dmp");
            Check.Equal(0, SandboxFixture.CountFiles(Path.Combine(env.Fixture.Folders.LocalLowAppData, "Microsoft", "CryptnetUrlCache")), "Caché de certificados");
            Check.True(!File.Exists(Path.Combine(win, "Temp", "viejo.tmp")) && File.Exists(Path.Combine(win, "Temp", "reciente.tmp")), "Temporales del sistema: solo con más de 24 h");
            Check.True(!File.Exists(Path.Combine(win, "Minidump", "111324-01.dmp")), "Minidump");
            Check.True(!File.Exists(Path.Combine(win, "Logs", "CBS", "CBS.log")) && !File.Exists(Path.Combine(win, "Logs", "WindowsUpdate", "u.etl")) && File.Exists(Path.Combine(win, "Logs", "config.xml")), "Registros: solo .log y .etl");
            Check.True(File.Exists(Path.Combine(win, "System32", "config", "SYSTEM")), "System32 nunca se toca");
            Check.Equal(0, SandboxFixture.CountFiles(Path.Combine(win, "ServiceProfiles")), "Optimización de distribución");
            Check.Equal(0, SandboxFixture.CountFiles(Path.Combine(env.Fixture.Folders.ProgramData, "Microsoft", "Windows", "WER")), "Informes de errores del sistema");
            Check.True(report.DeletedFiles >= 10, "Elementos eliminados: " + report.DeletedFiles);

            // Raíces del sistema reales: solo la lista cerrada (comprobación de rutas, sin tocar el disco).
            var real = new SystemFolderProvider();
            Check.True(CleaningGuard.ValidateRoot(Path.Combine(real.WindowsDirectory, "Temp"), real) is null, "Windows\\Temp admitida");
            Check.True(CleaningGuard.ValidateRoot(Path.Combine(real.WindowsDirectory, "Logs", "CBS"), real) is null, "Subcarpeta de Windows\\Logs admitida");
            foreach (var bad in new[] { real.WindowsDirectory, Path.Combine(real.WindowsDirectory, "System32"), Path.Combine(real.WindowsDirectory, "System32", "config"),
                         Path.Combine(real.WindowsDirectory, "ServiceProfiles"), Path.Combine(real.WindowsDirectory, "Temp") + "Falsa", Path.Combine(real.ProgramData, "Microsoft") })
                Check.True(CleaningGuard.ValidateRoot(bad, real) is not null, "Rechazada: " + bad);
        });

        tests.Add("A3_sin_administrador_las_categorias_de_sistema_se_rechazan_sin_tocar_nada", async () =>
        {
            using var env = Setup(f => f.Write(Path.Combine(f.Folders.WindowsDirectory, "Temp", "viejo.tmp"), 500), wrap: inner => new NotElevated(inner));
            env.Selection.SetTarget(K("windows", "system-temp", "all"), true);
            var session = await env.Workflow.AnalyzeAsync();
            var target = session.Targets.Single();
            Check.True(target.Refusal!.StartsWith("Requiere administrador", StringComparison.Ordinal), "Rechazada: " + target.Refusal);
            Check.True(env.Selection.Catalog.Apps.Single(a => a.Id == "windows").Categories.Single(c => c.Id == "system-temp").Description.Contains("administrador"), "La descripción lo indica");
            Check.True(File.Exists(Path.Combine(env.Fixture.Folders.WindowsDirectory, "Temp", "viejo.tmp")), "Nada borrado");
        });

        tests.Add("A1_A3_raices_de_perfil_solo_en_carpetas_de_herramientas", () =>
        {
            using var env = Setup();
            var folders = env.Fixture.Folders;
            // En producción: la carpeta del perfil solo admite subcarpetas que empiezan por punto.
            var real = new SystemFolderProvider();
            Check.True(CleaningGuard.ValidateRoot(Path.Combine(real.UserProfile, ".cache", "huggingface", "xet"), real) is null, "~/.cache admitida");
            Check.True(CleaningGuard.ValidateRoot(Path.Combine(real.UserProfile, ".ollama", "models"), real) is null, "~/.ollama admitida");
            foreach (var bad in new[] { Path.Combine(real.UserProfile, "Documents", "cache"), Path.Combine(real.UserProfile, "proyectos", ".cache"), Path.Combine(real.UserProfile, ".ssh"), real.UserProfile })
                Check.True(CleaningGuard.ValidateRoot(bad, real) is not null, "Rechazada: " + bad);
            Check.True(CleaningGuard.ValidateRoot(Path.Combine(real.LocalLowAppData, "Microsoft", "CryptnetUrlCache"), real) is null, "LocalLow admitida");
            Check.True(CleaningGuard.ValidateRoot(Path.Combine(real.WindowsDirectory, "System32"), real) is not null, "System32 real rechazada");
        });
    }
}

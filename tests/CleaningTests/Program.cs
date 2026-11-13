using FFCleaner.Cleaning;
using FFCleaner.Core;
using FFCleaner.Tests;

var tests = new TestRunner();

static TargetKey K(string app, string category, string profile = "Default") => new(app, category, profile);

static Env Setup(Action<SandboxFixture>? extra = null)
{
    var fixture = SandboxFixture.Create();
    fixture.AddChromiumProfile("chrome", "Default");
    fixture.AddChromiumProfile("chrome", "Profile 1");
    fixture.AddChromiumProfile("brave", "Default");
    fixture.AddChromiumProfile("brave", "Profile 1");
    extra?.Invoke(fixture);
    var analysis = new FileAnalysisService(fixture.Folders);
    var selection = new SelectionModel(analysis.Discover());
    var cleaning = new FileCleaningService(fixture.Folders);
    return new(fixture, analysis, selection, cleaning, new CleanupWorkflow(selection, analysis, cleaning));
}

static string[] CacheLeaves() => ["Cache", "Code Cache", "GPUCache", "DawnCache", @"Service Worker\CacheStorage", @"Service Worker\ScriptCache"];

/// Huella de un perfil separando la hoja indicada (lo que se limpia) del resto (lo que debe quedar intacto).
static SortedDictionary<string, string> Except(SortedDictionary<string, string> tree, params string[] leaves)
{
    var result = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var pair in tree)
        if (!leaves.Any(l => pair.Key.StartsWith(l + "\\", StringComparison.OrdinalIgnoreCase))) result[pair.Key] = pair.Value;
    return result;
}

// ───────────── Etapa 3: verificación principal (A1–A6) ─────────────

tests.Add("A1_cache_de_chrome_borra_solo_cache_y_preserva_cookies_historial_contrasenas_y_brave", async () =>
{
    using var env = Setup();
    var chromeDefault = env.Fixture.Profile("chrome", "Default");
    var chromeProfile1 = env.Fixture.Profile("chrome", "Profile 1");
    var braveBefore = SandboxFixture.HashTree(env.Fixture.UserData("brave"));
    var defaultBefore = SandboxFixture.HashTree(chromeDefault);
    var profile1Before = SandboxFixture.HashTree(chromeProfile1);
    var cacheFilesBefore = SandboxFixture.CountFiles(Path.Combine(chromeDefault, "Cache")) + SandboxFixture.CountFiles(Path.Combine(chromeProfile1, "Cache"));
    Check.True(cacheFilesBefore > 20, "El fixture contiene caché que limpiar");

    env.Selection.SetCategory("chrome", "cache", true);
    var session = await env.Workflow.AnalyzeAsync();
    Check.Equal((long)cacheFilesBefore, session.EligibleFiles, "El análisis lista exactamente la caché marcada");
    var report = await env.Workflow.CleanAsync();

    Check.True(!report.Refused && !report.Cancelled, "Limpieza ejecutada: " + report.Refusal);
    Check.Equal(cacheFilesBefore, report.DeletedFiles, "Todos los archivos de caché eliminados");
    Check.Equal(session.EligibleBytes, report.DeletedBytes, "Bytes liberados = bytes analizados");
    Check.Equal(0, report.NotDeleted, "Sin omisiones");
    Check.Equal(0, SandboxFixture.CountFiles(Path.Combine(chromeDefault, "Cache")), "Caché de Default vacía");
    Check.Equal(0, SandboxFixture.CountFiles(Path.Combine(chromeProfile1, "Cache")), "Caché de Profile 1 vacía");

    // Todo lo demás, byte a byte: cookies, historial, contraseñas, sesiones, otras cachés no marcadas y Brave completo.
    Check.SameTree(Except(defaultBefore, "Cache"), SandboxFixture.HashTree(chromeDefault), "Chrome/Default fuera de Cache");
    Check.SameTree(Except(profile1Before, "Cache"), SandboxFixture.HashTree(chromeProfile1), "Chrome/Profile 1 fuera de Cache");
    Check.SameTree(braveBefore, SandboxFixture.HashTree(env.Fixture.UserData("brave")), "Brave completo");
    foreach (var personal in new[] { "Cookies", "History", "Login Data", "Web Data", "Bookmarks", "Preferences", @"Network\Cookies", @"Sessions\Session_13370000000000000", @"Local Storage\leveldb\000005.ldb" })
        Check.True(File.Exists(Path.Combine(chromeDefault, personal)), "Dato personal presente: " + personal);
    Check.True(report.JournalPath is not null && File.ReadAllText(report.JournalPath).Contains("\"evento\":\"fin\""), "Bitácora completa");
});

tests.Add("A2_cache_de_brave_no_toca_chrome", async () =>
{
    using var env = Setup();
    var chromeBefore = SandboxFixture.HashTree(env.Fixture.UserData("chrome"));
    var braveBefore = SandboxFixture.HashTree(env.Fixture.UserData("brave"));
    env.Selection.SetCategory("brave", "cache", true);
    await env.Workflow.AnalyzeAsync();
    var report = await env.Workflow.CleanAsync();
    Check.True(report.DeletedFiles > 0 && report.NotDeleted == 0, "Brave limpiado");
    Check.SameTree(chromeBefore, SandboxFixture.HashTree(env.Fixture.UserData("chrome")), "Chrome completo");
    Check.SameTree(Except(braveBefore, @"Default\Cache", @"Profile 1\Cache"), SandboxFixture.HashTree(env.Fixture.UserData("brave")), "Brave fuera de Cache");
});

tests.Add("A3_ambas_caches_y_todas_las_categorias_de_cache", async () =>
{
    using var env = Setup();
    var trees = new[] { "chrome", "brave" }.ToDictionary(b => b, b => SandboxFixture.HashTree(env.Fixture.UserData(b)));
    env.Selection.SetApp("chrome", true);
    env.Selection.SetApp("brave", true);
    var session = await env.Workflow.AnalyzeAsync();
    var report = await env.Workflow.CleanAsync();
    Check.Equal(session.EligibleFiles, (long)report.DeletedFiles, "Todo lo analizado se eliminó");
    foreach (var browser in trees.Keys)
    {
        var excluded = new[] { "Default", "Profile 1" }.SelectMany(p => CacheLeaves().Select(l => p + "\\" + l)).ToArray();
        Check.SameTree(Except(trees[browser], excluded), SandboxFixture.HashTree(env.Fixture.UserData(browser)), browser + " fuera de las cachés");
        Check.True(File.Exists(Path.Combine(env.Fixture.Profile(browser, "Default"), "Service Worker", "Database", "000003.log")), "La base de Service Worker no es caché");
    }
    var again = await env.Analysis.AnalyzeAsync(env.Selection.Snapshot());
    Check.Equal(0L, again.EligibleFiles, "Reanalizar tras limpiar no encuentra nada");
});

tests.Add("A6_solo_el_perfil_elegido", async () =>
{
    using var env = Setup();
    var other = SandboxFixture.HashTree(env.Fixture.Profile("chrome", "Profile 1"));
    env.Selection.SetTarget(K("chrome", "cache", "Default"), true);
    await env.Workflow.AnalyzeAsync();
    var report = await env.Workflow.CleanAsync();
    Check.True(report.DeletedFiles > 0, "Perfil elegido limpiado");
    Check.SameTree(other, SandboxFixture.HashTree(env.Fixture.Profile("chrome", "Profile 1")), "Perfil no elegido");
});

tests.Add("A4_A5_sin_seleccion_o_con_seleccion_cambiada_no_se_borra_nada", async () =>
{
    using var env = Setup();
    var before = SandboxFixture.HashTree(env.Fixture.Root);
    Check.True((await env.Workflow.CleanAsync()).Refused, "Sin análisis");
    env.Selection.SetCategory("chrome", "cache", true);
    var session = await env.Workflow.AnalyzeAsync();
    env.Selection.SetCategory("brave", "cache", true);
    Check.True((await env.Workflow.CleanAsync()).Refused, "Flujo: selección cambiada");
    // Ataque directo al ejecutor con la sesión antigua y la selección nueva.
    var direct = await env.Cleaning.CleanAsync(session, env.Selection.Snapshot());
    Check.True(direct.Refused && direct.Refusal!.Contains("selección cambió"), "Ejecutor: selección cambiada");
    env.Selection.Clear();
    Check.True((await env.Cleaning.CleanAsync(session, env.Selection.Snapshot())).Refused, "Ejecutor: selección vacía");
    Check.SameTree(before, SandboxFixture.HashTree(env.Fixture.Root), "Sandbox completo");
});

// ───────────── Fallos parciales, bloqueos y concurrencia ─────────────

tests.Add("archivo_en_uso_se_omite_sin_abortar_el_lote", async () =>
{
    using var env = Setup();
    var cache = Path.Combine(env.Fixture.Profile("chrome", "Default"), "Cache", "Cache_Data");
    env.Selection.SetTarget(K("chrome", "cache"), true);
    var session = await env.Workflow.AnalyzeAsync();
    CleaningReport report;
    using (new FileStream(Path.Combine(cache, "data_1"), FileMode.Open, FileAccess.Read, FileShare.Read))
    using (new FileStream(Path.Combine(cache, "index"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        report = await env.Workflow.CleanAsync();
    var result = report.Targets.Single();
    Check.Equal(2, result.Locked, "Dos archivos bloqueados");
    Check.Equal((int)session.EligibleFiles - 2, result.Deleted, "El resto se eliminó");
    Check.True(result.Details.All(d => d.Status == CleanStatus.Bloqueado) && result.Details.Count == 2, "Detalle por archivo bloqueado");
    Check.True(File.Exists(Path.Combine(cache, "data_1")) && File.Exists(Path.Combine(cache, "index")), "Los bloqueados siguen ahí");
    Check.Equal(2, SandboxFixture.CountFiles(Path.Combine(env.Fixture.Profile("chrome", "Default"), "Cache")), "Solo quedan los bloqueados");
});

tests.Add("archivo_modificado_reemplazado_o_enlazado_tras_el_analisis_se_conserva", async () =>
{
    using var env = Setup();
    var cache = Path.Combine(env.Fixture.Profile("chrome", "Default"), "Cache", "Cache_Data");
    env.Selection.SetTarget(K("chrome", "cache"), true);
    await env.Workflow.AnalyzeAsync();

    var modified = Path.Combine(cache, "f_000000");
    File.AppendAllText(modified, "contenido nuevo");
    var replaced = Path.Combine(cache, "f_000001");
    var size = new FileInfo(replaced).Length; var stamp = File.GetLastWriteTimeUtc(replaced);
    File.Delete(replaced);
    File.WriteAllBytes(replaced, new byte[size]);
    File.SetLastWriteTimeUtc(replaced, stamp);                       // mismo nombre, tamaño y fecha; otra identidad
    var linked = Path.Combine(cache, "f_000002");
    var external = Path.Combine(env.Fixture.Outside, "enlace-externo.bin");
    Check.True(SandboxFixture.CreateHardLink(external, linked, IntPtr.Zero), "CreateHardLink");
    var readOnly = Path.Combine(cache, "f_000003");
    File.SetAttributes(readOnly, FileAttributes.ReadOnly);
    File.Delete(Path.Combine(cache, "f_000004"));

    var result = (await env.Workflow.CleanAsync()).Targets.Single();
    string Detail(string path) => result.Details.Single(d => d.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).Detail;
    Check.True(File.Exists(modified) && Detail(modified).Contains("Cambió"), "Modificado conservado");
    Check.True(File.Exists(replaced) && Detail(replaced).Contains("Cambió"), "Reemplazado conservado");
    Check.True(File.Exists(linked) && File.Exists(external) && Detail(linked).Contains("enlaces"), "Enlazado conservado");
    Check.True(File.Exists(readOnly) && Detail(readOnly).Contains("Atributos"), "Solo lectura conservado");
    Check.True(Detail(Path.Combine(cache, "f_000004")).Contains("Ya no existe"), "Desaparecido informado");
    Check.Equal(5, result.Skipped, "Cinco omitidos");
    Check.True(result.Deleted > 5 && result.Failed == 0 && result.Unconfirmed == 0, "El resto se eliminó sin errores");
});

tests.Add("carpeta_sustituida_por_junction_tras_el_analisis_no_alcanza_el_exterior", async () =>
{
    using var env = Setup();
    var profile = env.Fixture.Profile("chrome", "Default");
    env.Selection.SetTarget(K("chrome", "cache"), true);
    await env.Workflow.AnalyzeAsync();
    // Copia exacta (mismos nombres, tamaños y fechas) fuera del objetivo; la carpeta real se cambia por una junction.
    var outside = Path.Combine(env.Fixture.Outside, "victima");
    Directory.CreateDirectory(outside);
    var real = Path.Combine(profile, "Cache", "Cache_Data");
    foreach (var file in Directory.GetFiles(real))
    {
        var copy = Path.Combine(outside, Path.GetFileName(file));
        File.Copy(file, copy);
        File.SetLastWriteTimeUtc(copy, File.GetLastWriteTimeUtc(file));
    }
    var outsideBefore = SandboxFixture.HashTree(outside);
    Directory.Move(real, real + "-apartada");
    if (!SandboxFixture.TryJunction(real, outside)) throw new SkipException("No se pudo crear junction");

    var report = await env.Workflow.CleanAsync();
    Check.Equal(0, report.DeletedFiles, "Nada eliminado a través de la junction");
    Check.True(report.Targets.Single().Details.All(d => d.Detail.Contains("insegura")), "Motivo: carpeta insegura");
    Check.SameTree(outsideBefore, SandboxFixture.HashTree(outside), "Exterior intacto");
});

tests.Add("cancelacion_a_mitad_detiene_los_siguientes_y_deja_bitacora_coherente", async () =>
{
    using var env = Setup(f =>
    {
        var cache = Path.Combine(f.Profile("chrome", "Default"), "Cache", "Cache_Data");
        for (var i = 0; i < 300; i++) f.Write(Path.Combine(cache, "extra_" + i), 100);
    });
    env.Selection.SetApp("chrome", true);
    var session = await env.Workflow.AnalyzeAsync();
    using var cancel = new CancellationTokenSource();
    // El reloj inyectado cancela tras 40 consultas: a mitad del primer objetivo, de forma determinista.
    var ticks = 0;
    var cleaning = new FileCleaningService(env.Fixture.Folders, clock: () => { if (++ticks == 40) cancel.Cancel(); return DateTime.UtcNow; });
    var report = await cleaning.CleanAsync(session, env.Selection.Snapshot(), null, cancel.Token);
    Check.True(report.Cancelled && !report.Refused, "Marcado como cancelado");
    Check.True(report.DeletedFiles > 0 && report.DeletedFiles < session.EligibleFiles, "Parcial: " + report.DeletedFiles + " de " + session.EligibleFiles);
    var cancelled = report.Targets.Sum(t => t.Cancelled);
    Check.Equal(session.EligibleFiles, (long)report.DeletedFiles + cancelled, "Cada archivo consta como eliminado o cancelado");
    var remaining = new[] { "Default", "Profile 1" }.SelectMany(p => CacheLeaves().Select(l => Path.Combine(env.Fixture.Profile("chrome", p), l))).Sum(SandboxFixture.CountFiles);
    Check.Equal(cancelled, remaining, "Los cancelados siguen en disco");
    var journal = File.ReadAllLines(report.JournalPath!);
    Check.True(journal[0].Contains("\"evento\":\"inicio\"") && journal[^1].Contains("\"cancelado\":true"), "Bitácora con inicio y fin cancelado");
    Check.True((await cleaning.CleanAsync(session, env.Selection.Snapshot())).Refused, "La sesión parcial no se reutiliza");
});

tests.Add("cancelado_antes_de_empezar_no_consume_ni_borra", async () =>
{
    using var env = Setup();
    env.Selection.SetTarget(K("chrome", "cache"), true);
    var session = await env.Workflow.AnalyzeAsync();
    using var cancel = new CancellationTokenSource();
    cancel.Cancel();
    var report = await env.Cleaning.CleanAsync(session, env.Selection.Snapshot(), null, cancel.Token);
    Check.True(report.Refused && report.DeletedFiles == 0 && !session.Consumed, "Rechazado sin cambios");
    Check.True((await env.Cleaning.CleanAsync(session, env.Selection.Snapshot())).DeletedFiles > 0, "La sesión sigue siendo válida después");
});

tests.Add("sesion_de_un_solo_uso_y_con_caducidad", async () =>
{
    using var env = Setup();
    env.Selection.SetTarget(K("chrome", "cache"), true);
    var session = await env.Analysis.AnalyzeAsync(env.Selection.Snapshot());
    var late = new FileCleaningService(env.Fixture.Folders, clock: () => session.ExpiresUtc.AddSeconds(1));
    Check.True((await late.CleanAsync(session, env.Selection.Snapshot())).Refusal!.Contains("caducó"), "Caducada");
    // Dos limpiezas simultáneas de la misma sesión: solo una actúa.
    var both = await Task.WhenAll(env.Cleaning.CleanAsync(session, env.Selection.Snapshot()), env.Cleaning.CleanAsync(session, env.Selection.Snapshot()));
    Check.Equal(1, both.Count(r => !r.Refused), "Una sola ejecución");
    Check.Equal(1, both.Count(r => r.Refused), "La otra, rechazada");
});

// ───────────── Blindaje de datos sensibles (sesiones falsificadas) ─────────────

tests.Add("blindaje_sesion_falsificada_no_puede_borrar_cookies_historial_ni_contrasenas", async () =>
{
    using var env = Setup();
    var profile = env.Fixture.Profile("chrome", "Default");
    var legit = env.Selection.Catalog.AllTargets().First(t => t.Key == K("chrome", "cache"));
    var policy = legit.Roots[0].Policy;

    // 1) Raíz = perfil completo.  2) Raíz = Network.  3) Raíz legítima, carpeta fuera de la raíz.
    // 4) Raíz legítima, nombre sensible dentro.     5) Raíz legítima, subcarpeta sensible.
    env.Fixture.Write(Path.Combine(profile, "Cache", "Cookies"), 50);
    env.Fixture.Write(Path.Combine(profile, "Cache", "Sessions", "Session_1"), 50);
    var before = SandboxFixture.HashTree(env.Fixture.Folders.LocalAppData);   // todos los datos de aplicación simulados
    var attempts = new (string Name, CleanupTarget Target, string Directory, string File)[]
    {
        ("perfil completo", legit with { Roots = [new(profile, "Default", policy)] }, profile, "Cookies"),
        ("carpeta Network", legit with { Roots = [new(Path.Combine(profile, "Network"), "Network", policy)] }, Path.Combine(profile, "Network"), "Cookies"),
        ("carpeta fuera de la raíz", legit, profile, "History"),
        ("carpeta fuera de la raíz (contraseñas)", legit, profile, "Login Data"),
        ("nombre sensible en caché", legit, Path.Combine(profile, "Cache"), "Cookies"),
        ("subcarpeta sensible en caché", legit, Path.Combine(profile, "Cache", "Sessions"), "Session_1"),
        ("otro navegador", legit, Path.Combine(env.Fixture.Profile("brave", "Default"), "Cache", "Cache_Data"), "index"),
    };
    foreach (var (name, target, directory, file) in attempts)
    {
        var session = Forge.Session(target, directory, file);
        Check.True(session.EligibleFiles == 1 && session.CleanBlocker(session.Selection, DateTime.UtcNow) is null, name + ": la sesión falsificada parece válida");
        var report = await env.Cleaning.CleanAsync(session, session.Selection);
        Check.Equal(0, report.DeletedFiles, name + ": nada eliminado");
        Check.True(report.Targets.Single().Skipped == 1, name + ": consta como omitido");
        Check.True(File.Exists(Path.Combine(directory, file)), name + ": el archivo sigue ahí");
    }
    Check.SameTree(before, SandboxFixture.HashTree(env.Fixture.Folders.LocalAppData), "Datos de aplicación tras todos los intentos");
});

tests.Add("blindaje_archivo_sensible_dentro_de_cache_sobrevive_a_una_limpieza_normal", async () =>
{
    using var env = Setup(f =>
    {
        var cache = Path.Combine(f.Profile("chrome", "Default"), "Cache");
        f.Write(Path.Combine(cache, "Cookies"), 70);
        f.Write(Path.Combine(cache, "Login Data"), 70);
        f.Write(Path.Combine(cache, "copia.sqlite"), 70);
        f.Write(Path.Combine(cache, "Sessions", "Session_9"), 70);
    });
    env.Selection.SetTarget(K("chrome", "cache"), true);
    await env.Workflow.AnalyzeAsync();
    var result = (await env.Workflow.CleanAsync()).Targets.Single();
    var cache = Path.Combine(env.Fixture.Profile("chrome", "Default"), "Cache");
    Check.Equal(4, result.Protected, "Cuatro protegidos sin intentar borrarlos");
    Check.Equal(4, SandboxFixture.CountFiles(cache), "Solo quedan los protegidos");
    Check.True(File.Exists(Path.Combine(cache, "Cookies")) && File.Exists(Path.Combine(cache, "Sessions", "Session_9")), "Protegidos presentes");
});

tests.Add("revision_carpeta_fijada_no_se_puede_renombrar_ni_sustituir_durante_la_limpieza", () =>
{
    using var env = Setup();
    var cache = Path.Combine(env.Fixture.Profile("chrome", "Default"), "Cache", "Cache_Data");
    using (var pins = new PinnedDirectoryChain())
    {
        Check.True(pins.Pin(cache) is null, "La carpeta se fija");
        Check.Throws<IOException>(() => Directory.Move(cache, cache + "-movida"), "Renombrar la carpeta fijada");
        Check.Throws<IOException>(() => Directory.Move(Path.GetDirectoryName(cache)!, Path.GetDirectoryName(cache) + "-movida"), "Renombrar un ancestro fijado");
        Check.True(Directory.Exists(cache), "La carpeta sigue en su sitio");
    }
    Directory.Move(cache, cache + "-movida");
    Check.True(Directory.Exists(cache + "-movida"), "Al soltar el fijado la carpeta vuelve a ser libre");
});

tests.Add("revision_sesion_con_clave_marcada_pero_raices_de_otro_objetivo_se_rechaza", async () =>
{
    using var env = Setup();
    env.Selection.SetTarget(K("chrome", "cache"), true);
    var legit = env.Selection.Snapshot().Targets.Single();
    var braveCache = Path.Combine(env.Fixture.Profile("brave", "Default"), "Cache");
    // Mismo identificador «chrome/cache/Default», pero con la raíz (válida en sí misma) de otro navegador.
    var impostor = legit with { Roots = [new(braveCache, "Cache", legit.Roots[0].Policy)], DataRoot = env.Fixture.UserData("brave"), Anchor = env.Fixture.UserData("brave") };
    var session = Forge.Session(impostor, Path.Combine(braveCache, "Cache_Data"), "index", env.Selection.Snapshot());
    var before = SandboxFixture.HashTree(env.Fixture.Folders.LocalAppData);
    var report = await env.Cleaning.CleanAsync(session, env.Selection.Snapshot());
    Check.True(report.DeletedFiles == 0 && report.Targets.Single().Details.Single().Detail.Contains("no coincide"), "Rechazado por no coincidir con la selección: " + report.Refusal);
    Check.SameTree(before, SandboxFixture.HashTree(env.Fixture.Folders.LocalAppData), "Nada eliminado");
});

// ───────────── Resto de categorías configuradas ─────────────

tests.Add("resto_de_categorias_firefox_windows_nvidia_y_desarrollo", async () =>
{
    using var env = Setup(f => { f.AddFirefoxProfile("abcd1234.default-release"); f.AddSystemCaches(); });
    var roaming = SandboxFixture.HashTree(env.Fixture.Folders.RoamingAppData);
    var chromium = SandboxFixture.HashTree(Path.Combine(env.Fixture.Folders.LocalAppData, "Google"));
    foreach (var app in new[] { "firefox", "windows", "nvidia", "dev" }) env.Selection.SetApp(app, true);
    var session = await env.Workflow.AnalyzeAsync();
    Check.Equal(9, session.Targets.Count, "Firefox ×2, temporales, D3D, NVIDIA ×2 y desarrollo ×3: " + string.Join(",", session.Targets.Select(t => t.Target.Key)));
    var report = await env.Workflow.CleanAsync();
    Check.Equal(session.EligibleFiles, (long)report.DeletedFiles, "Todo lo limpiable se eliminó");
    var local = env.Fixture.Folders.LocalAppData;
    foreach (var cleaned in new[] { Path.Combine(local, "Mozilla", "Firefox", "Profiles", "abcd1234.default-release", "cache2"), Path.Combine(local, "D3DSCache"), Path.Combine(local, "NVIDIA"), Path.Combine(local, "pip"), Path.Combine(local, "npm-cache") })
        Check.Equal(0, SandboxFixture.CountFiles(cleaned), "Vacío: " + cleaned);
    // uv: «wheels-v5\pypi\pkg\pkg-1.0.whl» no es sensible y se limpia.
    Check.Equal(0, SandboxFixture.CountFiles(Path.Combine(local, "uv")), "uv vacío");
    // Temporales: cualquier tipo con más de 24 horas; lo reciente se conserva.
    var temp = env.Fixture.Folders.Temp;
    Check.True(!File.Exists(Path.Combine(temp, "viejo.tmp")) && !File.Exists(Path.Combine(temp, "sub", "registro.log")), "Temporales antiguos eliminados");
    Check.True(File.Exists(Path.Combine(temp, "reciente.tmp")) && !File.Exists(Path.Combine(temp, "documento.txt")), "Reciente conservado; cualquier tipo antiguo eliminado");
    Check.SameTree(roaming, SandboxFixture.HashTree(env.Fixture.Folders.RoamingAppData), "Perfil Roaming de Firefox (cookies, logins)");
    Check.SameTree(chromium, SandboxFixture.HashTree(Path.Combine(local, "Google")), "Chrome sin marcar");
});

tests.Add("temporal_que_rejuvenece_tras_el_analisis_se_conserva", async () =>
{
    using var env = Setup(f => f.AddSystemCaches());
    env.Selection.SetTarget(K("windows", "user-temp", "all"), true);
    var session = await env.Workflow.AnalyzeAsync();
    // Reloj atrasado 3 días: el archivo «viejo» pasa a tener menos de 48 h en el momento de limpiar.
    var early = new FileCleaningService(env.Fixture.Folders, clock: () => session.CreatedUtc.AddDays(-3).AddMinutes(5));
    var report = await early.CleanAsync(session, env.Selection.Snapshot());
    Check.True(report.Refused, "Un reloj anterior al análisis invalida la sesión");
    var result = (await env.Workflow.CleanAsync()).Targets.Single();
    Check.Equal(3, result.Deleted, "Los tres temporales antiguos, de cualquier tipo");
    Check.Equal(1, result.Protected, "El reciente se conserva");
});

tests.Add("rutas_largas_y_unicode_se_limpian", async () =>
{
    using var env = Setup(f =>
    {
        var deep = Path.Combine(f.Profile("chrome", "Default"), "Cache", "Cache_Data", new string('d', 100), new string('e', 100));
        f.Write(Path.Combine(deep, "ñandú-日本語-" + new string('x', 100)), 500);
    });
    env.Selection.SetTarget(K("chrome", "cache"), true);
    var session = await env.Workflow.AnalyzeAsync();
    var report = await env.Workflow.CleanAsync();
    Check.True(report.NotDeleted == 0 && report.DeletedFiles == session.EligibleFiles, "Ruta de más de 260 caracteres eliminada: " + string.Join(" | ", report.Targets.SelectMany(t => t.Details).Select(d => d.Detail)));
});

// ───────────── Mejoras tras la primera ejecución real ─────────────

tests.Add("mejora_recuento_por_motivo_sin_tope_aunque_el_detalle_se_recorte", async () =>
{
    using var env = Setup(f =>
    {
        var cache = Path.Combine(f.Profile("chrome", "Default"), "Cache", "Cache_Data");
        for (var i = 0; i < 2100; i++) f.Write(Path.Combine(cache, "ro_" + i), 10);
    });
    var cache = Path.Combine(env.Fixture.Profile("chrome", "Default"), "Cache", "Cache_Data");
    env.Selection.SetTarget(K("chrome", "cache"), true);
    await env.Workflow.AnalyzeAsync();
    foreach (var file in Directory.GetFiles(cache, "ro_*")) File.SetAttributes(file, FileAttributes.ReadOnly);
    CleaningReport report;
    using (new FileStream(Path.Combine(cache, "index"), FileMode.Open, FileAccess.Read, FileShare.None))
        report = await env.Workflow.CleanAsync();
    var result = report.Targets.Single();
    Check.Equal(2101, result.NotDeleted, "No eliminados");
    Check.Equal(2000, result.Details.Count, "El detalle se recorta");
    Check.Equal(101, result.DetailsOmitted, "Detalle omitido");
    Check.Equal(result.NotDeleted, result.Reasons.Values.Sum(), "Los motivos suman todo lo no eliminado");
    Check.Equal(2100, result.Reasons.Single(p => p.Key.Contains("Atributos protegidos")).Value, "Motivo mayoritario completo");
    Check.Equal(1, result.Reasons.Single(p => p.Key.StartsWith("Bloqueado", StringComparison.Ordinal)).Value, "Motivo minoritario visible aunque su fila se recortara");
    Check.True(report.Reasons()[0].Value == 2100 && report.Reasons().Count == 2, "Resumen del lote ordenado");
    var journal = File.ReadAllText(report.JournalPath!);
    Check.True(journal.Contains("\"motivos\":[{\"motivo\":\"Omitido: Atributos protegidos") && journal.Contains("\"archivos\":2100"), "La bitácora guarda el recuento por motivo");
});

tests.Add("mejora_carpetas_vacias_se_retiran_sin_tocar_raices_protegidas_enlaces_ni_no_vacias", async () =>
{
    using var env = Setup(f =>
    {
        var cache = Path.Combine(f.Profile("chrome", "Default"), "Cache");
        Directory.CreateDirectory(Path.Combine(cache, "vacia-previa", "hija"));
        f.Write(Path.Combine(cache, "Sessions", "Session_1"), 30);            // carpeta protegida con contenido
        Directory.CreateDirectory(Path.Combine(cache, "Network"));             // carpeta protegida vacía
        f.Write(Path.Combine(f.Outside, "ajeno", "dentro", "archivo.bin"), 30);
        Directory.CreateDirectory(Path.Combine(f.Outside, "ajeno", "vacia"));
    });
    var profile = env.Fixture.Profile("chrome", "Default");
    var cache = Path.Combine(profile, "Cache");
    var linked = SandboxFixture.TryJunction(Path.Combine(cache, "enlace"), Path.Combine(env.Fixture.Outside, "ajeno"));
    env.Selection.SetTarget(K("chrome", "cache"), true);
    env.Selection.SetTarget(K("chrome", "code-cache"), true);
    await env.Workflow.AnalyzeAsync();
    CleaningReport report;
    using (new FileStream(Path.Combine(cache, "Cache_Data", "data_1"), FileMode.Open, FileAccess.Read, FileShare.None))
        report = await env.Workflow.CleanAsync();

    Check.True(Directory.Exists(cache) && Directory.Exists(Path.Combine(profile, "Code Cache")), "Las raíces nunca se retiran, aunque queden vacías");
    Check.True(Directory.Exists(Path.Combine(cache, "Cache_Data")), "Carpeta con un archivo en uso: se conserva");
    Check.True(!Directory.Exists(Path.Combine(cache, "vacia-previa")), "Carpetas ya vacías y anidadas: retiradas");
    Check.True(!Directory.Exists(Path.Combine(profile, "Code Cache", "js")) && !Directory.Exists(Path.Combine(profile, "Code Cache", "wasm")), "Carpetas vaciadas por la limpieza: retiradas");
    Check.True(File.Exists(Path.Combine(cache, "Sessions", "Session_1")) && Directory.Exists(Path.Combine(cache, "Network")), "Carpetas protegidas intactas, con o sin contenido");
    if (linked)
    {
        Check.True(Directory.Exists(Path.Combine(cache, "enlace")), "El enlace no se retira");
        Check.True(Directory.Exists(Path.Combine(env.Fixture.Outside, "ajeno", "vacia")) && File.Exists(Path.Combine(env.Fixture.Outside, "ajeno", "dentro", "archivo.bin")), "Nada se retira a través del enlace");
    }
    var byKey = report.Targets.ToDictionary(t => t.Key.CategoryId);
    Check.Equal(2, byKey["cache"].EmptyDirectoriesRemoved, "Caché: vacia-previa y su hija");
    Check.Equal(3, byKey["code-cache"].EmptyDirectoriesRemoved, "Código: js, js\\index-dir y wasm");
    Check.Equal(5, report.EmptyDirectoriesRemoved, "Total del lote");
    Check.True(File.ReadAllText(report.JournalPath!).Contains("\"carpetasVaciasRetiradas\":5"), "Bitácora");
    Check.True(Directory.Exists(Path.Combine(env.Fixture.Profile("chrome", "Profile 1"), "Code Cache", "js")), "Perfil no marcado intacto");
});

tests.Add("mejora_carpetas_vacias_respetan_antiguedad_exclusiones_y_cancelacion", async () =>
{
    using var fixture = SandboxFixture.Create();
    fixture.AddSystemCaches();
    var temp = fixture.Folders.Temp;
    var old = DateTime.UtcNow.AddDays(-5);
    Directory.CreateDirectory(Path.Combine(temp, "antigua-vacia"));
    Directory.SetCreationTimeUtc(Path.Combine(temp, "antigua-vacia"), old);
    Directory.CreateDirectory(Path.Combine(temp, "recien-creada"));
    fixture.Write(Path.Combine(temp, "antigua-con-log", "viejo.log"), 20);
    Directory.SetCreationTimeUtc(Path.Combine(temp, "antigua-con-log"), old);
    Directory.CreateDirectory(Path.Combine(temp, "excluida-vacia"));
    Directory.SetCreationTimeUtc(Path.Combine(temp, "excluida-vacia"), old);

    var analysis = new FileAnalysisService(fixture.Folders, [Path.Combine(temp, "excluida-vacia")]);
    var selection = new SelectionModel(analysis.Discover());
    var key = new TargetKey("windows", "user-temp", "all");
    selection.SetTarget(key, true);

    // Cancelado a mitad: no se retira ninguna carpeta.
    var first = await analysis.AnalyzeAsync(selection.Snapshot());
    using (var cancel = new CancellationTokenSource())
    {
        var ticks = 0;
        var cancelling = new FileCleaningService(fixture.Folders, clock: () => { if (++ticks == 2) cancel.Cancel(); return DateTime.UtcNow; });
        var partial = await cancelling.CleanAsync(first, selection.Snapshot(), null, cancel.Token);
        Check.True(partial.Cancelled && partial.EmptyDirectoriesRemoved == 0 && Directory.Exists(Path.Combine(temp, "antigua-vacia")), "Cancelado: carpetas intactas");
    }

    var session = await analysis.AnalyzeAsync(selection.Snapshot());
    var report = await new FileCleaningService(fixture.Folders).CleanAsync(session, selection.Snapshot());
    Check.True(!Directory.Exists(Path.Combine(temp, "antigua-vacia")), "Vacía y creada hace más de 48 h: retirada");
    Check.True(!Directory.Exists(Path.Combine(temp, "antigua-con-log")), "Vaciada por la limpieza y antigua: retirada");
    Check.True(Directory.Exists(Path.Combine(temp, "recien-creada")), "Recién creada: puede estar en uso, se conserva");
    Check.True(Directory.Exists(Path.Combine(temp, "sub")), "Vaciada pero creada hace poco: se conserva");
    Check.True(Directory.Exists(Path.Combine(temp, "excluida-vacia")), "Excluida por el usuario: se conserva");
    Check.True(Directory.Exists(temp), "La carpeta temporal en sí nunca se retira");
    Check.Equal(2, report.EmptyDirectoriesRemoved, "Dos carpetas retiradas");
});

tests.Add("decision_enlaces_de_uv_y_atributos_protegidos_se_conocen_ya_al_analizar", async () =>
{
    using var env = Setup(f => f.AddSystemCaches());
    var uv = Path.Combine(env.Fixture.Folders.LocalAppData, "uv", "cache", "archive-v0", "abc");
    var shared = env.Fixture.Write(Path.Combine(uv, "modulo.py"), 5000);
    var venv = Path.Combine(env.Fixture.Outside, "venv", "Lib", "modulo.py");
    Directory.CreateDirectory(Path.GetDirectoryName(venv)!);
    Check.True(SandboxFixture.CreateHardLink(venv, shared, IntPtr.Zero), "CreateHardLink");
    var fixedFile = env.Fixture.Write(Path.Combine(uv, "fijo.py"), 700);
    File.SetAttributes(fixedFile, FileAttributes.ReadOnly);
    env.Fixture.Write(Path.Combine(uv, "libre.py"), 900);

    env.Selection.SetTarget(K("dev", "uv-cache", "all"), true);
    var session = await env.Workflow.AnalyzeAsync();
    var target = session.Targets.Single();
    var verdicts = Enumerable.Range(0, target.FileCount).ToDictionary(i => target.FileAt(i).Name, i => target.FileAt(i).Verdict);
    Check.Equal(FileVerdict.MultipleLinks, verdicts["modulo.py"], "Compartido con un entorno virtual: se sabe al analizar");
    Check.Equal(FileVerdict.ProtectedAttributes, verdicts["fijo.py"], "Solo lectura: se sabe al analizar");
    Check.Equal(FileVerdict.Eligible, verdicts["libre.py"], "Archivo normal");
    Check.Equal(900L + 4000, session.EligibleBytes, "Lo compartido y lo protegido no se anuncia como limpiable");
    Check.Equal(1, target.ProtectedByVerdict[FileVerdict.MultipleLinks], "Desglose: enlaces");

    var report = await env.Workflow.CleanAsync();
    Check.Equal(2, report.DeletedFiles, "Se elimina lo anunciado");
    Check.Equal(0, report.NotDeleted, "Sin sorpresas al limpiar: nada anunciado queda sin eliminar");
    Check.Equal(session.EligibleBytes, report.DeletedBytes, "Bytes anunciados = bytes eliminados");
    Check.True(File.Exists(shared) && File.Exists(venv) && File.Exists(fixedFile), "Compartido, su enlace y el de solo lectura intactos");

    // En una categoría sin esa comprobación (pip), el enlace sigue protegido en el momento de limpiar.
    var pip = Path.Combine(env.Fixture.Folders.LocalAppData, "pip", "Cache", "http-v2", "a", "b", "abcdef0123");
    Check.True(SandboxFixture.CreateHardLink(Path.Combine(env.Fixture.Outside, "otro-enlace.bin"), pip, IntPtr.Zero), "CreateHardLink pip");
    env.Selection.Clear();
    env.Selection.SetTarget(K("dev", "pip-cache", "all"), true);
    await env.Workflow.AnalyzeAsync();
    var second = await env.Workflow.CleanAsync();
    Check.True(second.DeletedFiles == 0 && second.Reasons().Single().Key.Contains("enlaces") && File.Exists(pip), "Sin comprobación previa, la limpieza lo omite igualmente");
});

tests.Add("decision_cachés_de_paquetes_sin_reglas_de_nombres_pero_con_extensiones_protegidas", async () =>
{
    using var env = Setup(f => f.AddSystemCaches());
    var local = env.Fixture.Folders.LocalAppData;
    var uv = Path.Combine(local, "uv", "cache", "archive-v0", "pkg", "Lib", "site-packages");
    var librerias = new[] { @"requests\sessions.py", @"google\auth\credentials.py", @"transformers\models\bert\token_classification.py", @"aiohttp\cookiejar.py", @"keyring\vault\backend.py", @"urllib3\network\connection.py" };
    foreach (var file in librerias) env.Fixture.Write(Path.Combine(uv, file), 300);
    var pem = env.Fixture.Write(Path.Combine(uv, @"certifi\cacert.pem"), 300);
    var key = env.Fixture.Write(Path.Combine(uv, @"tests\server.key"), 300);
    var onnx = env.Fixture.Write(Path.Combine(uv, @"models\small.onnx"), 300);
    var npmToken = env.Fixture.Write(Path.Combine(local, "npm-cache", "_cacache", "index-v5", "tokens", "token-abc"), 300);
    var pipSession = env.Fixture.Write(Path.Combine(local, "pip", "Cache", "http-v2", "session", "session-data"), 300);

    // Los navegadores conservan las reglas de nombres: un «Cookies» dentro de su caché sigue protegido.
    var chromeCookie = env.Fixture.Write(Path.Combine(env.Fixture.Profile("chrome", "Default"), "Cache", "Cookies"), 40);
    // Y los temporales también.
    var tempSession = env.Fixture.Write(Path.Combine(env.Fixture.Folders.Temp, "Sessions", "datos.bin"), 40);

    env.Selection.SetApp("dev", true);
    env.Selection.SetTarget(K("chrome", "cache"), true);
    env.Selection.SetTarget(K("windows", "user-temp", "all"), true);
    var session = await env.Workflow.AnalyzeAsync();
    var report = await env.Workflow.CleanAsync();
    Check.True(!report.Refused, "Limpieza ejecutada");

    foreach (var file in librerias) Check.True(!File.Exists(Path.Combine(uv, file)), "Librería con nombre «sensible» eliminada: " + file);
    Check.True(!File.Exists(npmToken) && !File.Exists(pipSession), "npm y pip: nombres token/session de su caché eliminados");
    Check.True(File.Exists(pem) && File.Exists(key) && File.Exists(onnx), ".pem, .key y .onnx siguen protegidos por extensión");
    Check.True(File.Exists(chromeCookie), "Caché de navegador: «Cookies» sigue protegido por nombre");
    Check.True(File.Exists(tempSession), "Temporales: carpeta «Sessions» sigue protegida por nombre");
    var uvResult = report.Targets.Single(t => t.Key.CategoryId == "uv-cache");
    Check.Equal(0, uvResult.NotDeleted, "Nada anunciado quedó sin eliminar en uv");
});

FeatureTests.Register(tests);

return await tests.RunAsync(args);

internal sealed record Env(SandboxFixture Fixture, FileAnalysisService Analysis, SelectionModel Selection,
    FileCleaningService Cleaning, CleanupWorkflow Workflow) : IDisposable
{
    public void Dispose() { Workflow.Dispose(); Fixture.Dispose(); }
}

/// <summary>
/// Construye sesiones de análisis falsas con metadatos reales del archivo víctima, como si un fallo del
/// análisis lo hubiera dado por limpiable. El ejecutor debe rechazarlas por sus propias guardas.
/// </summary>
internal static class Forge
{
    /// <param name="existing">Instantánea auténtica a la que se quiere colgar el objetivo falso; si es null se fabrica una propia.</param>
    public static AnalysisSession Session(CleanupTarget target, string directory, string fileName, SelectionSnapshot? existing = null)
    {
        var catalog = new CleanupCatalog("forged", [new(target.Key.AppId, target.AppName, AppGroup.Navegadores, [],
            [new(target.Key.CategoryId, target.CategoryName, "", [target])])], 0, []);
        var selection = new SelectionModel(catalog);
        selection.SetApp(target.Key.AppId, true);
        var snapshot = existing ?? selection.Snapshot();
        var path = Path.Combine(directory, fileName);
        var native = NativeMetadata.Read(path);
        var parts = native.Identity.Split(':');
        var entry = new FileEntry(0, fileName, native.Length, native.Allocated ?? 0, native.LastWriteUtc.ToFileTimeUtc(),
            Convert.ToUInt64(parts[2][..16], 16), Convert.ToUInt64(parts[2][16..], 16), FileVerdict.Eligible);
        var volume = new VolumeIdentity(Convert.ToUInt64(parts[1], 16), true);
        var analysis = new TargetAnalysis(existing is null ? snapshot.Targets[0] : target, [new(directory, 0, false)], [entry], [volume], [], null);
        return new AnalysisSession(snapshot, CleanupCatalogBuilder.Version, [analysis], [], false, false, new(0, 0, 0, 0), DateTime.UtcNow);
    }
}

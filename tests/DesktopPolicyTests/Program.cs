using FFCleaner.Desktop;

var failed = 0;
void Test(string name, Func<bool> body)
{
    bool ok;
    try { ok = body(); } catch (Exception ex) { ok = false; Console.WriteLine("      " + ex.Message); }
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
    if (!ok) failed++;
}
bool Throws(Action body) { try { body(); return false; } catch (ArgumentException) { return true; } }

// Derecha-a-izquierda (U+202E), nulo y salto de línea: deben verse como texto, no ejecutarse como formato.
var invisible = "a" + (char)0x202E + "b" + (char)0 + "c" + (char)10 + "d";
Test("texto_del_registro_muestra_caracteres_invisibles", () =>
    UiPolicy.VisibleText(invisible) == @"a\u202Eb\u0000c\u000Ad" && UiPolicy.VisibleText(null) == "" && UiPolicy.VisibleText("Normal ñ") == "Normal ñ");
Test("tamanos_en_unidades_binarias", () =>
    UiPolicy.Human(0) == "0 B" && UiPolicy.Human(1023) == "1.023 B" && UiPolicy.Human(1536) == "1,50 KiB" && UiPolicy.Human(5L * 1024 * 1024 * 1024) == "5,00 GiB");
Test("sin_argumentos_modo_normal", () => UiPolicy.ParseArguments([]) == (null, null));
Test("sandbox_completo_se_acepta", () => UiPolicy.ParseArguments(["--sandbox", @"X:\s", "--sandbox-registry", "abc"]) == (@"X:\s", "abc"));
Test("sandbox_a_medias_se_rechaza", () =>
    Throws(() => UiPolicy.ParseArguments(["--sandbox", @"X:\s"])) && Throws(() => UiPolicy.ParseArguments(["--sandbox-registry", "abc"])));
Test("argumentos_desconocidos_o_sin_valor_se_rechazan", () =>
    Throws(() => UiPolicy.ParseArguments(["--smoke-root", "x"])) && Throws(() => UiPolicy.ParseArguments(["--sandbox"])) &&
    Throws(() => UiPolicy.ParseArguments(["--sandbox", "--sandbox-registry"])) && Throws(() => UiPolicy.ParseArguments(["suelto"])));
// Actualizaciones: la fuente de prueba solo en sandbox y solo en este equipo; al reabrir se quitan las marcas de la anterior.
Test("fuente_de_actualizaciones_de_prueba_solo_en_sandbox_y_local", () =>
    Throws(() => UiPolicy.ParseOptions(["--update-feed", "http://127.0.0.1:9/x"])) &&
    Throws(() => UiPolicy.ParseOptions(["--sandbox", @"X:\s", "--sandbox-registry", "a", "--update-feed", "https://evil.example/x"])) &&
    UiPolicy.ParseOptions(["--sandbox", @"X:\s", "--sandbox-registry", "a", "--update-feed", "http://127.0.0.1:9/x"]).UpdateFeed!.IsLoopback);
Test("version_anterior_valida_y_argumentos_de_reapertura", () =>
    UiPolicy.ParseOptions(["--updated-from", "0.3.0"]).UpdatedFrom == "0.3.0" && Throws(() => UiPolicy.ParseOptions(["--updated-from", "x"])) &&
    UiPolicy.RelaunchArguments(["--sandbox", "S", "--sandbox-registry", "R", "--update-feed", "http://127.0.0.1/x", "--updated-from", "0.2.0"])
        .SequenceEqual(["--sandbox", "S", "--sandbox-registry", "R"]));

// Auditoría M1: una actualización que falló no se reintenta en cada arranque (evita el bucle de descargas).
Test("actualizacion_fallida_no_se_reintenta_en_24_horas", () =>
{
    var folder = Path.Combine(Path.GetTempPath(), "ffcleaner-marca-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    try
    {
        var marker = Path.Combine(folder, UpdateService.FailedMarkerName);
        var now = DateTime.UtcNow;
        var next = new Version(99, 0, 0);
        void Write(string version, DateTime when) => File.WriteAllText(marker,
            "{\"version\":\"" + version + "\",\"utc\":\"" + when.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "\"}");
        Write("99.0.0", now.AddHours(-1));
        var recent = UpdateService.RecentlyFailed(folder, next, now) && !UpdateService.RecentlyFailed(folder, new Version(99, 0, 1), now);
        Write("99.0.0", now.AddHours(-30));
        var expired = !UpdateService.RecentlyFailed(folder, next, now);
        Write(UpdateService.Current.ToString(3), now.AddMinutes(-5));
        var installed = !UpdateService.RecentlyFailed(folder, next, now) && !File.Exists(marker);
        return recent && expired && installed && !UpdateService.RecentlyFailed(folder, next, now);
    }
    finally { Directory.Delete(folder, recursive: true); }
});
Test("sin_otras_copias_abiertas_desde_la_carpeta_de_pruebas", () => UpdateService.OtherInstanceRunning() is null);

// Inicio de Windows: nombre real del programa a partir del ejecutable o del acceso directo (solo lectura).
var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
var cmdDescription = System.Diagnostics.FileVersionInfo.GetVersionInfo(cmd).FileDescription;
Test("nombre_del_programa_desde_el_ejecutable", () =>
{
    var identity = AppIdentityResolver.Resolve("EntradaRun", cmd);
    return identity.DisplayName == cmdDescription && identity.DisplayName != "EntradaRun" &&
           identity.Publisher?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) == true &&
           string.Equals(identity.ExecutablePath, cmd, StringComparison.OrdinalIgnoreCase);
});
Test("sin_ejecutable_se_usa_el_nombre_de_la_entrada", () =>
    AppIdentityResolver.Resolve("Huerfano", @"Q:\no\existe.exe") == new AppIdentity("Huerfano", null, null) &&
    AppIdentityResolver.Resolve("Nada", null).DisplayName == "Nada");
Test("acceso_directo_se_resuelve_a_su_destino", () =>
{
    var folder = Path.Combine(Path.GetTempPath(), "ffcleaner-identidad-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    try
    {
        var link = Path.Combine(folder, "Consola.lnk");
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        var shortcut = shell.CreateShortcut(link);
        shortcut.TargetPath = cmd;
        shortcut.Save();
        var broken = Path.Combine(folder, "Roto.lnk");
        File.WriteAllText(broken, "no es un acceso directo");
        var identity = AppIdentityResolver.Resolve("Consola", link);
        return string.Equals(AppIdentityResolver.ShortcutTarget(link), cmd, StringComparison.OrdinalIgnoreCase) &&
               identity.DisplayName == cmdDescription &&
               AppIdentityResolver.ShortcutTarget(broken) is null && AppIdentityResolver.Resolve("Roto", broken).DisplayName == "Roto";
    }
    finally { Directory.Delete(folder, recursive: true); }
});
// Una versión publicada rara nunca impide abrir FFCleaner (antes, una etiqueta «v1.0» lo cerraba al arrancar).
Test("version_publicada_con_etiqueta_de_dos_partes_o_url_mala_no_cierra_la_app", () =>
{
    var feed = new Uri("http://127.0.0.1:9/releases/latest");
    UpdateInfo? Check(string json) =>
        new UpdateService(FFCleaner.Core.DiagnosticsLog.Null, null, feed, new FakeFeed(json)).CheckAsync().GetAwaiter().GetResult();
    string Release(string tag, string asset, string url) =>
        "{\"tag_name\":\"" + tag + "\",\"draft\":false,\"prerelease\":false,\"assets\":[{\"name\":\"" + asset +
        "\",\"browser_download_url\":\"" + url + "\",\"size\":1,\"digest\":\"sha256:" + new string('a', 64) + "\"}]}";
    bool Refused(string json) { try { Check(json); return false; } catch (InvalidOperationException) { return true; } }
    var twoParts = Check(Release("v99.0", "FFCleaner-99.0.0-win-x64.zip", "http://127.0.0.1:9/a.zip"));
    return twoParts is { } info && info.Version == new Version(99, 0, 0) && info.AssetName == "FFCleaner-99.0.0-win-x64.zip" &&
           Refused(Release("v99.0", "FFCleaner-99.0-win-x64.zip", "http://127.0.0.1:9/a.zip")) &&
           Refused(Release("v99.0.0", "FFCleaner-99.0.0-win-x64.zip", "no es una url"));
});
return failed == 0 ? 0 : 1;

/// <summary>Fuente de versiones falsa: responde siempre el mismo JSON, sin red.</summary>
sealed class FakeFeed(string json) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) });
}

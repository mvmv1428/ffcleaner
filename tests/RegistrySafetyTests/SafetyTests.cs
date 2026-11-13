namespace FFCleaner.RegistrySafetyTests;

using FFCleaner.RegistryAnalysis;

/// <summary>
/// Auditoría del módulo Registro. Todo el análisis de amenazas se hace con datos
/// sintéticos y <c>probe</c> inyectado: no se crea, modifica ni elimina ninguna clave,
/// no se ejecuta ningún destino y no se escribe en disco.
/// </summary>
internal static class SafetyTests
{
    private const string Key = @"HKCU\Software\Microsoft\Windows\CurrentVersion\App Paths\app.exe";
    private const string GoodPath = @"C:\Program Files\App\app.exe";

    public static IEnumerable<(string Name, Action Body)> All()
    {
        // --- Contrato base: estado del destino llega solo por el probe -------------------
        yield return ("destino presente: sin hallazgo y probe con la ruta exacta", () =>
        {
            var calls = new List<string>();
            var finding = RegistryEvaluator.Evaluate(new RegistryInput("Registry32", Key, string.Empty, GoodPath),
                path => { calls.Add(path); return TargetState.Present; });

            Assert.IsNull(finding, "Present debe devolver null (no es referencia huérfana)");
            Assert.Equal(1, calls.Count, "probe invocado exactamente una vez");
            Assert.Equal(GoodPath, calls[0], "ruta entregada al probe");
        });

        yield return ("destino ausente: huérfana potencial, revisión manual y seleccionable", () =>
        {
            var finding = RegistryEvaluator.Evaluate(new RegistryInput("Registry32", Key, string.Empty, GoodPath),
                _ => TargetState.Missing);

            Assert.NotNull(finding, "Missing debe producir hallazgo");
            var f = finding!;
            Assert.True(f.Selectable, "Missing puede ser seleccionable (revisión manual)");
            Assert.Equal("Revisión manual", f.Confidence, "confianza");
            Assert.Contains(f.Reason, "huérfana", "razón debe calificar como referencia huérfana potencial");
            Assert.Contains(f.Reason, "no es prueba definitiva", "razón no debe afirmar invalidez definitiva");
            Assert.Equal(GoodPath, f.ResolvedTarget, "resolved target");
            Assert.Equal(GoodPath, f.RawTarget, "raw target preservado");
            Assert.Equal("Registry32", f.View, "vista canónica");
            Assert.True(f.Id.StartsWith("reg-", StringComparison.Ordinal) && f.Id.Length == 68,
                $"Id debe ser 'reg-' + SHA256 hex (68 chars), obtenido '{f.Id}'");
        });

        yield return ("destino desconocido: nunca se convierte en ausencia ni es seleccionable", () =>
        {
            var calls = new List<string>();
            var finding = RegistryEvaluator.Evaluate(new RegistryInput("Registry64", Key, string.Empty, GoodPath),
                path => { calls.Add(path); return TargetState.Unknown; });

            Assert.NotNull(finding, "Unknown debe producir hallazgo no seleccionable");
            var f = finding!;
            Assert.False(f.Selectable, "Unknown jamás debe ser seleccionable");
            Assert.Equal("Desconocida", f.Confidence, "confianza para Unknown");
            Assert.Contains(f.Reason, "no se trata como ausencia", "razón debe negar la ausencia");
            Assert.DoesNotContain(f.Reason, "huérfana", "Unknown no debe reportarse como huérfana");
            Assert.Equal(1, calls.Count, "probe invocado exactamente una vez");
        });

        // --- Amenaza: flujos alternativos de datos (ADS) -------------------------------
        yield return ("ADS con ejecutable en el flujo: no seleccionable y probe no invocado", () =>
        {
            MustReject(@"C:\App\app.exe:payload.exe", "ADS ':payload.exe'");
            MustReject(@"C:\App\app.exe::$DATA", "ADS '::$DATA'");
            MustReject(@"C:/App/app.exe:payload.exe", "ADS con separadores mixtos");
        });

        // --- Amenaza: rutas de red (UNC / dispositivo) ---------------------------------
        yield return ("UNC y rutas de dispositivo: no verificables como locales", () =>
        {
            MustReject(@"\\server\share\app.exe", "UNC directa");
            MustReject(@"""\\server\share\app.exe""", "UNC entrecomillada");
            MustReject(@"//server/share/app.exe", "UNC con barras normales");
            MustReject(@"\\.\C:\App\app.exe", "ruta de dispositivo \\\\.\\");
            MustReject(@"\\?\C:\App\app.exe", "prefijo extendido \\\\?\\");
        });

        // --- Amenaza: variables de entorno sin expandir --------------------------------
        yield return ("variables de entorno sin expandir: ambiguas, no seleccionables", () =>
        {
            MustReject(@"%ProgramFiles%\App\app.exe", "variable sin comillas");
            MustReject(@"""%SystemRoot%\notepad.exe""", "variable entrecomillada");
            MustReject(@"C:\App\%USERNAME%\app.exe", "variable en segmento intermedio");
            MustReject(@"C:\App\100%.exe", "porcentaje literal: se prefiere ambiguo a adivinar");
        });

        // --- Amenaza: argumentos o texto extra -----------------------------------------
        yield return ("argumentos o texto extra tras la ruta: no es ruta pura", () =>
        {
            MustReject(@"C:\App\app.exe -silent", "argumento sin comillas");
            MustReject(@"""C:\App\app.exe"" --x", "argumento tras ruta entrecomillada");
            MustReject(@"""C:\App\app.exe"" ""C:\other.exe""", "segundo ejecutable tras la ruta");
            MustReject(@"C:\App\app.exe /background", "conmutador con barra");
        });

        // --- Amenaza: extensión distinta de .exe ---------------------------------------
        yield return ("extensiones que no son .exe: no seleccionables", () =>
        {
            MustReject(@"C:\App\setup.cmd", ".cmd");
            MustReject(@"C:\App\setup.msi", ".msi");
            MustReject(@"C:\App\app.exe.txt", "doble extensión .exe.txt");
            MustReject(@"C:\App\app.EXE.", "punto final tras .EXE");
            // Nota: 'C:\App\.exe' (nombre base vacío) termina en .exe y el contrato no lo prohíbe;
            // se deja como observación de endurecimiento, no como defecto.
        });

        yield return ("extension .EXE en mayúsculas: se acepta como ejecutable", () =>
        {
            var calls = new List<string>();
            var finding = RegistryEvaluator.Evaluate(new RegistryInput("Registry32", Key, string.Empty, @"C:\App\APP.EXE"),
                path => { calls.Add(path); return TargetState.Present; });
            Assert.IsNull(finding, "ruta válida en mayúsculas debe resolverse sin hallazgo");
            Assert.Equal(1, calls.Count, "probe invocado para .EXE");
        });

        // --- Amenaza: rutas relativas o dependientes de unidad ------------------------
        yield return ("rutas relativas o dependientes de unidad: no absolutas locales", () =>
        {
            MustReject(@"app.exe", "relativa simple");
            MustReject(@"..\App\app.exe", "relativa con ..");
            MustReject(@"C:app.exe", "relativa a la unidad C:");
            MustReject(@"\App\app.exe", "raíz sin letra de unidad");
            MustReject(@".\app.exe", "relativa con .");
            MustReject(@"1:\App\app.exe", "letra de unidad inválida");
            MustReject(@"C:\", "raíz sin ejecutable");
            MustReject(@"/app.exe", "barra inicial sin unidad");
        });

        // --- Sintaxis malformada -------------------------------------------------------
        yield return ("comillas malformadas: sintaxis ambigua, nunca seleccionable", () =>
        {
            MustReject(@"""C:\App\app.exe", "comilla sin cerrar");
            MustReject(@"C:\App\a""b.exe", "comilla interna");
            MustReject(@"""C:\App\app.exe\""", "barra antes de cerrar comilla");
            MustReject(@"""C:\App\app.exe"" extra", "texto tras cerrar comilla");
            MustReject(@"""""", "comillas vacías");
        });

        yield return ("valores vacíos o solo espacios: no hay ruta que verificar", () =>
        {
            MustReject(string.Empty, "cadena vacía");
            MustReject("   ", "solo espacios");
            MustReject("\t\t", "solo tabuladores");
            MustReject(@"""""", "comillas con contenido vacío");
        });

        yield return ("caracteres de control y comodines: sintaxis ambigua", () =>
        {
            MustReject("C:\\App\\app\u0000.exe", "NUL embebido");
            MustReject("C:\\App\\\tapp.exe", "tabulador embebido");
            MustReject("C:\\App\\app\n.exe", "salto de línea embebido");
            MustReject(@"C:\App\*.exe", "comodín *");
            MustReject(@"C:\Ap?p\app.exe", "comodín ?");
            MustReject(@"C:\App<app>.exe", "caracteres < >");
            MustReject(@"C:\App|app.exe", "tubería |");
        });

        // --- Defecto detectado: caracteres de formato/bidi ----------------------------
        yield return ("DEFECTO: caracteres bidi/zero-width en la ruta deben impedir la selección", () =>
        {
            var bidi = "C:\\App\\app" + char.ConvertFromUtf32(0x202E) + "gnp.exe";
            var zwsp = "C:\\App\\app" + char.ConvertFromUtf32(0x200B) + ".exe";
            var bom = "C:\\App\\" + char.ConvertFromUtf32(0xFEFF) + "app.exe";

            var problems = new List<string>();
            foreach (var (raw, label) in new[] { (bidi, "U+202E (override bidi)"), (zwsp, "U+200B (zero width)"), (bom, "U+FEFF (BOM/zero width nobreak)") })
            {
                var calls = new List<string>();
                var finding = RegistryEvaluator.Evaluate(new RegistryInput("Registry32", Key, string.Empty, raw),
                    path => { calls.Add(path); return TargetState.Missing; });

                if (finding is null || finding.Selectable)
                {
                    problems.Add($"{label}: Selectable={(finding?.Selectable.ToString() ?? "null")} (esperado no seleccionable), probe invocado={calls.Count}");
                }
            }

            Assert.True(problems.Count == 0,
                "caracteres invisibles pueden enmascarar el ejecutable real en la UI: " + string.Join("; ", problems));
        });

        // --- Unidad no accesible / permiso denegado ------------------------------------
        yield return ("unidad no accesible o permiso denegado: Unknown, jamás ausencia", () =>
        {
            var removable = @"E:\Tools\app.exe";
            var finding = RegistryEvaluator.Evaluate(new RegistryInput("Registry32", Key, string.Empty, removable),
                _ => TargetState.Unknown);

            Assert.NotNull(finding, "unidad no lista debe producir hallazgo informativo");
            var f = finding!;
            Assert.False(f.Selectable, "unidad no accesible no debe ser seleccionable");
            Assert.Equal("Desconocida", f.Confidence, "confianza");
            Assert.Equal(removable, f.ResolvedTarget, "se conserva la ruta analizada");

            // Ruta ambigua con probe "Present": nunca debe devolverse null (no se limpia a ciegas).
            var ambiguous = RegistryEvaluator.Evaluate(new RegistryInput("Registry32", Key, string.Empty, @"\\server\share\app.exe"),
                _ => TargetState.Present);
            Assert.NotNull(ambiguous, "entrada ambigua nunca debe devolverse como null aunque el probe diga Present");
            Assert.False(ambiguous!.Selectable, "entrada ambigua no seleccionable");
        });

        // --- Vista de Registro --------------------------------------------------------
        yield return ("vista desconocida: hallazgo no seleccionable con confianza desconocida", () =>
        {
            foreach (var view in new[] { "HKLM", "CurrentUser", "Registry128", string.Empty })
            {
                var calls = new List<string>();
                var finding = RegistryEvaluator.Evaluate(new RegistryInput(view, Key, string.Empty, GoodPath),
                    path => { calls.Add(path); return TargetState.Present; });

                Assert.NotNull(finding, $"vista '{view}' debe producir hallazgo");
                Assert.False(finding!.Selectable, $"vista '{view}' no debe ser seleccionable");
                Assert.Equal("Desconocida", finding.Confidence, $"confianza para vista '{view}'");
                Assert.Contains(finding.Reason, "Vista de Registro desconocida", $"razón para vista '{view}'");
                Assert.Equal(0, calls.Count, $"vista '{view}': el probe no debe invocarse");
            }
        });

        yield return ("normalización de vista: espacios y minúsculas se aceptan", () =>
        {
            var calls = new List<string>();
            var finding = RegistryEvaluator.Evaluate(new RegistryInput(" registry32 ", Key, string.Empty, GoodPath),
                path => { calls.Add(path); return TargetState.Present; });
            Assert.IsNull(finding, "' registry32 ' debe canonicalizarse a Registry32");
            Assert.Equal(1, calls.Count, "probe invocado tras normalizar la vista");
        });

        // --- Identificadores estables y sin colisión ----------------------------------
        yield return ("IDs: estables, distintos por vista y sin colisión por concatenación", () =>
        {
            string Id(string view, string key, string value, string raw = GoodPath)
            {
                var finding = RegistryEvaluator.Evaluate(new RegistryInput(view, key, value, raw), _ => TargetState.Missing);
                Assert.NotNull(finding, "se requiere hallazgo para comparar Ids");
                return finding!.Id;
            }

            Assert.Equal(Id("Registry32", Key, "v"), Id("Registry32", Key, "v"), "estabilidad del Id");
            Assert.Equal(Id("registry32", Key.Replace('\\', '/'), "v"), Id("Registry32", Key, "v"), "vista y separadores normalizados");
            Assert.True(Id("Registry32", Key, "v") != Id("Registry64", Key, "v"), "vistas distintas deben dar Ids distintos");
            Assert.True(Id("Registry32", Key, "v") != Id("Registry32", Key, "w"), "valores distintos deben dar Ids distintos");

            var tupleA = Id("Registry32", @"HKCU\A|B", "c");
            var tupleB = Id("Registry32", @"HKCU\A", "B|c");
            Assert.True(tupleA != tupleB, "tuplas ('A|B','c') y ('A','B|c') no deben colisionar");

            var spacesA = Id("Registry32", @"HKCU\A B", "x");
            var spacesB = Id("Registry32", @"HKCU\A", "B x");
            Assert.True(spacesA != spacesB, "componentes con espacios no deben colisionar");

            var trailing = Id("Registry32", @"HKCU\A\B", "x");
            var noTrailing = Id("Registry32", @"HKCU\A\B\", "x");
            Assert.True(trailing != noTrailing, "claves con barra final distinta deben dar Ids distintos");

            // El Id depende de vista+clave+valor, no del destino crudo (misma entrada lógica).
            Assert.Equal(Id("Registry32", Key, "v", @"C:\a\app.exe"), Id("Registry32", Key, "v", @"C:\b\app.exe"),
                "mismo entrada lógica (vista+clave+valor) conserva el Id");
        });

        // --- Defecto detectado: entradas malformadas del API público ------------------
        yield return ("DEFECTO: KeyPath null no debe lanzar NullReferenceException", () =>
        {
            var input = new RegistryInput("Registry32", null!, string.Empty, GoodPath);
            ReferenceFinding? finding;
            try
            {
                finding = RegistryEvaluator.Evaluate(input, _ => TargetState.Missing);
            }
            catch (NullReferenceException ex)
            {
                throw new CheckFailedException(
                    $"Evaluate lanzó NullReferenceException con KeyPath null (mientras View null sí se tolera). " +
                    $"Se esperaba ArgumentException explícita o hallazgo no seleccionable. Detalle: {ex.Message}");
            }
            catch (ArgumentException)
            {
                return; // fallo explícito aceptable
            }

            Assert.True(finding is null || !finding.Selectable,
                "KeyPath null debe producir fallo explícito o hallazgo no seleccionable");
        });

        yield return ("DEFECTO: ValueName null no debe lanzar NullReferenceException", () =>
        {
            var input = new RegistryInput("Registry32", Key, null!, GoodPath);
            ReferenceFinding? finding;
            try
            {
                finding = RegistryEvaluator.Evaluate(input, _ => TargetState.Missing);
            }
            catch (NullReferenceException ex)
            {
                throw new CheckFailedException(
                    $"Evaluate lanzó NullReferenceException con ValueName null. " +
                    $"Se esperaba ArgumentException explícita o hallazgo no seleccionable. Detalle: {ex.Message}");
            }
            catch (ArgumentException)
            {
                return;
            }

            Assert.True(finding is null || !finding.Selectable,
                "ValueName null debe producir fallo explícito o hallazgo no seleccionable");
        });

        // --- Cancelación ---------------------------------------------------------------
        yield return ("cancelación: Scan con token cancelado no lee el Registro y no es reparable", () =>
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var result = new RegistryScanner().Scan(RegistryScanner.AllFamilies.Select(f => f.Family).ToArray(), cts.Token);
            Assert.True(result.Cancelled, "resultado marcado como cancelado");
            Assert.Equal(0, result.Findings.Count, "sin hallazgos");
            Assert.Equal(0, result.Stats.Sum(s => s.KeysVisited), "ninguna clave abierta");
        });

        // --- Escaneo real de solo lectura ---------------------------------------------
        yield return ("escaneo real de todas las familias: solo lectura, coherente y sin reparables indebidos", () =>
        {
            var result = new RegistryScanner().Scan(RegistryScanner.AllFamilies.Select(f => f.Family).ToArray());
            Assert.NotNull(result, "resultado del escaneo");
            Console.WriteLine($"      [scan real] findings={result.Findings.Count} issues={result.Issues.Count}");
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\";

            foreach (var finding in result.Findings)
            {
                Assert.Equal(finding.Risk == RegistryRisk.Seguro, finding.CanRepair, "solo lo seguro es reparable");
                if (!finding.CanRepair) continue;
                Assert.False(finding.Location.Hive == RegHive.LocalMachine && !RegistryScope.IsElevated,
                    $"HKLM reparable sin privilegios: '{finding.Location.Display}'");
                Assert.False((finding.Family is RegistryFamily.AppPaths or RegistryFamily.Startup or RegistryFamily.Uninstall or RegistryFamily.SharedDlls)
                             && finding.ResolvedTarget is not null && finding.ResolvedTarget.StartsWith(windows, StringComparison.OrdinalIgnoreCase),
                    $"destino del sistema reparable: '{finding.ResolvedTarget}'");
                Assert.False(System.Text.RegularExpressions.Regex.IsMatch(finding.RawTarget, @"%[A-Za-z_][^%\s""]*%") && finding.Family != RegistryFamily.MuiCache,
                    $"destino con variables de entorno reparable: '{finding.RawTarget}'");
            }

            foreach (var issue in result.Issues)
            {
                Assert.False(string.IsNullOrWhiteSpace(issue), "incidencia vacía");
            }
        });
    }

    /// <summary>
    /// Entrada ambigua: debe producir hallazgo NO seleccionable, con confianza desconocida,
    /// y el probe no debe invocarse (nada que verificar).
    /// </summary>
    private static void MustReject(string raw, string label)
    {
        var calls = new List<string>();
        var finding = RegistryEvaluator.Evaluate(new RegistryInput("Registry32", Key, string.Empty, raw),
            path => { calls.Add(path); return TargetState.Missing; });

        Assert.NotNull(finding, $"{label}: debe producir hallazgo, no null (raw='{Describe(raw)}')");
        Assert.False(finding!.Selectable, $"{label}: no debe ser seleccionable (raw='{Describe(raw)}')");
        Assert.Equal("Desconocida", finding.Confidence, $"{label}: confianza (raw='{Describe(raw)}')");
        Assert.Equal(0, calls.Count, $"{label}: el probe no debe invocarse (raw='{Describe(raw)}')");
        Assert.Equal(raw, finding.RawTarget, $"{label}: el valor crudo debe preservarse");
    }

    private static string Describe(string raw) => raw.Replace("\0", "\\0", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);
}

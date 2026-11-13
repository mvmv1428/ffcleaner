using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FFCleaner.Cleaning;
using FFCleaner.Core;
using FFCleaner.RegistryAnalysis;

namespace FFCleaner.Mcp;

/// <summary>
/// Herramientas MCP sobre los mismos servicios y guardas que la aplicación. Reglas de seguridad para agentes:
/// <list type="bullet">
/// <item>Limpiar exige un análisis previo de este mismo servidor (analysis_id, 15 minutos, un solo uso) y confirm=true.</item>
/// <item>Datos personales del navegador y modelos de IA solo con permiso expreso (allow_personal_data / allow_models).</item>
/// <item>El Registro solo repara hallazgos «Seguro», con respaldo verificado antes; restaurar nunca sobrescribe.</item>
/// <item>Programas de inicio: solo activar o desactivar (reversible); nunca se borra una entrada.</item>
/// </list>
/// </summary>
public sealed class McpServer
{
    private static readonly string[] Protocols = ["2025-06-18", "2025-03-26", "2024-11-05"];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly IFolderProvider _folders;
    private readonly RegistryScope _scope;
    private readonly DiagnosticsLog _log;
    private readonly FileAnalysisService _analysis;
    private readonly FileCleaningService _cleaning;
    private readonly RegistryScanner _scanner;
    private readonly RegistryBackupStore _backups;
    private readonly RegistryRepairService _repair;
    private readonly StartupManager _startup;
    private readonly Dictionary<string, AnalysisSession> _analyses = [];
    private readonly Dictionary<string, RegistryScanResult> _scans = [];
    private SelectionModel _selection;
    private CleanupWorkflow _workflow;

    public McpServer(IFolderProvider folders, RegistryScope scope)
    {
        _folders = folders;
        _scope = scope;
        _log = new DiagnosticsLog(Path.Combine(folders.StateDirectory, "Logs", "ffcleaner.log"));
        void Trace(string message) => _log.Write("mcp", message);
        _analysis = new FileAnalysisService(folders, log: _log);
        _cleaning = new FileCleaningService(folders, _log, processes: new SystemProcessProbe());
        (_selection, _workflow) = Rebuild();
        _scanner = new RegistryScanner(scope, trace: Trace);
        _backups = new RegistryBackupStore(scope, Path.Combine(folders.StateDirectory, "RegistryBackups"), Trace);
        _repair = new RegistryRepairService(_scanner, _backups, Trace);
        var userStartup = folders.IsSandbox ? Path.Combine(folders.RoamingAppData, "Microsoft", "Windows", "Start Menu", "Programs", "Startup")
            : Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        var commonStartup = folders.IsSandbox ? Path.Combine(folders.ProgramData, "Microsoft", "Windows", "Start Menu", "Programs", "StartUp")
            : Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
        _startup = new StartupManager(scope, userStartup, commonStartup, trace: Trace);
        _log.Write("mcp", "servidor iniciado" + (folders.IsSandbox ? " · sandbox " + folders.ContainmentRoot : "") + " · registro " + scope.Id +
            " · administrador=" + RegistryScope.IsElevated);
    }

    private (SelectionModel, CleanupWorkflow) Rebuild()
    {
        var selection = new SelectionModel(_analysis.Discover());
        return (selection, new CleanupWorkflow(selection, _analysis, _cleaning));
    }

    // ───────────────────────────── Protocolo ─────────────────────────────

    public async Task<JsonNode?> HandleAsync(JsonNode? message)
    {
        if (message is not JsonObject request) return Error(null, -32600, "Se esperaba un objeto JSON-RPC.");
        var id = request["id"]?.DeepClone();
        var method = request["method"]?.GetValue<string>();
        if (method is null) return id is null ? null : Error(id, -32600, "Falta «method».");
        if (id is null) return null;   // notificación (initialized, cancelled…): no se responde
        try
        {
            return method switch
            {
                "initialize" => Result(id, Initialize(request["params"] as JsonObject)),
                "ping" => Result(id, new JsonObject()),
                "tools/list" => Result(id, new JsonObject { ["tools"] = Tools() }),
                "tools/call" => Result(id, await CallAsync(request["params"] as JsonObject)),
                _ => Error(id, -32601, "Método no admitido: " + method),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            return Error(id, -32602, ex.Message);
        }
    }

    private static JsonObject Initialize(JsonObject? parameters)
    {
        var requested = parameters?["protocolVersion"]?.GetValue<string>();
        return new JsonObject
        {
            ["protocolVersion"] = requested is not null && Protocols.Contains(requested) ? requested : Protocols[0],
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "ffcleaner", ["title"] = "FFCleaner", ["version"] = typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "0" },
            ["instructions"] =
                "FFCleaner libera espacio y repara el Registro de Windows de forma segura. Flujo de limpieza: list_cleanup_targets → analyze_cleanup " +
                "(no borra nada) → muestra al usuario cuánto se libera → clean con el analysis_id y confirm=true. Registro: scan_registry → " +
                "repair_registry (solo hallazgos «Seguro», con respaldo) → restore_registry_backup si hace falta deshacer. No pidas datos personales " +
                "(historial, cookies, sesiones, formularios) ni modelos de IA salvo que el usuario lo pida expresamente.",
        };
    }

    public static JsonObject Result(JsonNode id, JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    public static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    // ───────────────────────────── Herramientas ─────────────────────────────

    private static JsonArray Tools() =>
    [
        Tool("list_cleanup_targets", "Lista qué se puede limpiar: aplicaciones, categorías y perfiles detectados en este equipo, con su riesgo. Solo lectura.",
            Schema(new() { ["refresh"] = Prop("boolean", "Vuelve a detectar aplicaciones y perfiles.") }), readOnly: true),
        Tool("analyze_cleanup", "Analiza (sin borrar nada) cuánto espacio se puede liberar. Usa preset=\"safe\" para todas las cachés y temporales, o targets con claves " +
             "«app», «app/categoría» o «app/categoría/perfil» de list_cleanup_targets. Devuelve analysis_id, válido 15 minutos y para una sola limpieza.",
            Schema(new()
            {
                ["preset"] = Prop("string", "«safe»: todas las categorías normales (cachés y temporales). Nunca incluye datos personales ni modelos."),
                ["targets"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Claves de objetivos." },
                ["allow_personal_data"] = Prop("boolean", "Permite historial, descargas, cookies, sesiones y formularios del navegador. Solo si el usuario lo pidió."),
                ["allow_models"] = Prop("boolean", "Permite modelos y datasets de IA descargados (muy grandes). Solo si el usuario lo pidió."),
            }), readOnly: true),
        Tool("clean", "Limpia lo analizado por analyze_cleanup. Borrado permanente: exige analysis_id vigente y confirm=true. Revalida cada archivo antes de borrarlo.",
            Schema(new() { ["analysis_id"] = Prop("string", "Id devuelto por analyze_cleanup."), ["confirm"] = Prop("boolean", "Debe ser true.") }, "analysis_id", "confirm"), destructive: true),
        Tool("scan_registry", "Analiza el Registro (solo lectura) en busca de referencias a programas que ya no existen. Devuelve scan_id y los hallazgos con su riesgo.",
            Schema(new()
            {
                ["families"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "apppaths, uninstall, fileassoc, startup, muicache, shareddlls. Por defecto, todas." },
                ["max_findings"] = Prop("integer", "Máximo de hallazgos devueltos (por defecto 200)."),
            }), readOnly: true),
        Tool("repair_registry", "Repara hallazgos «Seguro» de un scan_registry. Antes guarda y verifica un respaldo; si falla no cambia nada. Exige confirm=true.",
            Schema(new()
            {
                ["scan_id"] = Prop("string", "Id devuelto por scan_registry."),
                ["finding_ids"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Hallazgos a reparar." },
                ["all_safe"] = Prop("boolean", "Repara todos los hallazgos «Seguro» del análisis."),
                ["confirm"] = Prop("boolean", "Debe ser true."),
            }, "scan_id", "confirm"), destructive: true),
        Tool("list_registry_backups", "Lista los respaldos del Registro creados por FFCleaner.", Schema(new()), readOnly: true),
        Tool("restore_registry_backup", "Restaura un respaldo del Registro. Solo recrea lo que falta; nunca sobrescribe datos que cambiaron. Exige confirm=true.",
            Schema(new() { ["backup_id"] = Prop("string", "Id de list_registry_backups."), ["confirm"] = Prop("boolean", "Debe ser true.") }, "backup_id", "confirm")),
        Tool("list_startup_programs", "Lista los programas que se inician con Windows y si están activos. Solo lectura.", Schema(new()), readOnly: true),
        Tool("set_startup_program", "Activa o desactiva un programa de inicio de forma reversible (mismo mecanismo que el Administrador de tareas). No borra nada.",
            Schema(new() { ["id"] = Prop("string", "Id de list_startup_programs."), ["enabled"] = Prop("boolean", "true para activar, false para desactivar.") }, "id", "enabled")),
    ];

    private static JsonObject Tool(string name, string description, JsonObject schema, bool readOnly = false, bool destructive = false) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = schema,
        ["annotations"] = new JsonObject { ["readOnlyHint"] = readOnly, ["destructiveHint"] = destructive, ["idempotentHint"] = readOnly, ["openWorldHint"] = false },
    };

    private static JsonObject Schema(Dictionary<string, JsonNode> properties, params string[] required)
    {
        var props = new JsonObject();
        foreach (var (key, value) in properties) props[key] = value;
        var schema = new JsonObject { ["type"] = "object", ["properties"] = props, ["additionalProperties"] = false };
        if (required.Length > 0) schema["required"] = new JsonArray([.. required.Select(r => (JsonNode)r)]);
        return schema;
    }

    private static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };

    private async Task<JsonNode> CallAsync(JsonObject? parameters)
    {
        var name = parameters?["name"]?.GetValue<string>() ?? throw new ArgumentException("Falta el nombre de la herramienta.");
        var input = parameters["arguments"] as JsonObject ?? [];
        _log.Write("mcp", "herramienta " + name);
        try
        {
            object result = name switch
            {
                "list_cleanup_targets" => ListTargets(Bool(input, "refresh")),
                "analyze_cleanup" => await AnalyzeAsync(input),
                "clean" => await CleanAsync(input),
                "scan_registry" => await ScanAsync(input),
                "repair_registry" => await RepairAsync(input),
                "list_registry_backups" => ListBackups(),
                "restore_registry_backup" => await RestoreAsync(input),
                "list_startup_programs" => ListStartup(),
                "set_startup_program" => SetStartup(input),
                _ => throw new ArgumentException("Herramienta desconocida: " + name),
            };
            return ToolResult(result, isError: false);
        }
        catch (ToolRefusal refusal)
        {
            _log.Write("mcp", name + " rechazado: " + refusal.Message);
            return ToolResult(new { error = refusal.Message }, isError: true);
        }
    }

    private static JsonObject ToolResult(object value, bool isError)
    {
        var node = JsonSerializer.SerializeToNode(value, Json)!;
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = node.ToJsonString(Json) }),
            ["structuredContent"] = node,
            ["isError"] = isError,
        };
    }

    private sealed class ToolRefusal(string message) : Exception(message);

    // ───────────────────────────── Limpieza ─────────────────────────────

    private object ListTargets(bool refresh)
    {
        if (refresh) (_selection, _workflow) = Rebuild();
        var catalog = _selection.Catalog;
        return new
        {
            elevated = RegistryScope.IsElevated,
            notes = catalog.Notes,
            apps = catalog.Apps.Select(a => new
            {
                id = a.Id,
                name = a.Name,
                group = a.Group.ToString(),
                categories = a.Categories.Select(c => new
                {
                    key = a.Id + "/" + c.Id,
                    name = c.Name,
                    description = c.Description,
                    risk = c.Risk.ToString(),
                    needs = c.Risk switch { CategoryRisk.DatosPersonales => "allow_personal_data", CategoryRisk.MuyGrande => "allow_models", CategoryRisk.Administrador => "administrador", _ => null },
                    profiles = c.Targets.Select(t => new { key = t.Key.ToString(), profile = t.ProfileName }),
                }),
            }),
        };
    }

    private async Task<object> AnalyzeAsync(JsonObject input)
    {
        var allowPersonal = Bool(input, "allow_personal_data");
        var allowModels = Bool(input, "allow_models");
        var preset = input["preset"]?.GetValue<string>();
        var targets = Strings(input, "targets");
        if (preset is null && targets.Count == 0) throw new ToolRefusal("Indica preset=\"safe\" o una lista de targets; no se analiza nada de forma implícita.");
        if (preset is not null && preset != "safe") throw new ToolRefusal("Preset desconocido: " + preset + ". Solo existe «safe».");

        _selection.Clear();
        if (preset == "safe") _selection.SelectAll();
        foreach (var item in targets)
        {
            var parts = item.Split('/');
            var known = parts.Length switch
            {
                1 => _selection.Catalog.Apps.Any(a => a.Id == parts[0]) && SelectApp(parts[0], allowPersonal, allowModels),
                2 => _selection.SetCategory(parts[0], parts[1], true) || _selection.CategoryState(parts[0], parts[1]) == true,
                3 => _selection.SetTarget(new(parts[0], parts[1], parts[2]), true) || _selection.IsSelected(new(parts[0], parts[1], parts[2])),
                _ => false,
            };
            if (!known) throw new ToolRefusal("Objetivo desconocido: «" + item + "». Consulta list_cleanup_targets.");
        }
        var blocked = _selection.SelectedKeys().Where(k => _selection.IsOptIn(k)).Select(k => (Key: k, Risk: RiskOf(k)))
            .Where(p => p.Risk == CategoryRisk.DatosPersonales && !allowPersonal || p.Risk == CategoryRisk.MuyGrande && !allowModels).ToList();
        if (blocked.Count > 0)
        {
            _selection.Clear();
            throw new ToolRefusal("Estos objetivos requieren permiso expreso del usuario (" +
                string.Join(", ", blocked.Select(b => b.Key + " → " + (b.Risk == CategoryRisk.DatosPersonales ? "allow_personal_data" : "allow_models"))) + ").");
        }
        if (_selection.SelectedCount == 0) throw new ToolRefusal("La selección está vacía.");

        var session = await _workflow.AnalyzeAsync();
        if (!ReferenceEquals(session, _workflow.CurrentSession)) throw new ToolRefusal(_workflow.InvalidationReason ?? "El análisis no quedó vigente; repítelo.");
        var id = session.CreatedUtc.ToString("HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..10];
        _analyses.Clear();
        _analyses[id] = session;
        return new
        {
            analysis_id = id,
            expires_utc = session.ExpiresUtc,
            freeable = Human(session.FreeableBytes),
            freeable_bytes = session.FreeableBytes,
            browser_data_is_estimate = session.Targets.Any(t => t.DataBytes > 0),
            items = session.EligibleFiles,
            kept_for_safety = session.Targets.Sum(t => (long)t.ProtectedCount),
            nothing_deleted_yet = true,
            targets = session.Targets.OrderByDescending(t => t.FreeableBytes).Select(t => new
            {
                key = t.Target.Key.ToString(),
                app = t.Target.AppName,
                category = t.Target.CategoryName,
                profile = t.Target.ProfileName,
                freeable = t.Target.Data is null ? Human(t.EligibleBytes) : "≈ " + Human(t.DataBytes) + " (estimado, dentro de las bases)",
                items = t.EligibleItems,
                kept = t.ProtectedCount,
                kept_reasons = t.ProtectedByVerdict.ToDictionary(p => CleaningGuard.Describe(p.Key), p => p.Value),
                refused = t.Refusal,
                issues = t.Issues.Select(i => i.Message).Take(5),
            }),
            issues = session.Issues.Take(20).Select(i => new { code = i.Code, path = i.Path, message = i.Message }),
            next_step = session.EligibleFiles == 0 ? "No hay nada que limpiar." : "Muestra al usuario lo que se libera y, si está de acuerdo, llama a clean con este analysis_id y confirm=true.",
        };
    }

    private bool SelectApp(string appId, bool allowPersonal, bool allowModels)
    {
        _selection.SetApp(appId, true);   // nunca incluye las categorías que requieren permiso
        var app = _selection.Catalog.Apps.First(a => a.Id == appId);
        foreach (var category in app.Categories)
            if (category.Risk == CategoryRisk.DatosPersonales && allowPersonal || category.Risk == CategoryRisk.MuyGrande && allowModels)
                _selection.SetCategory(appId, category.Id, true);
        return true;
    }

    private CategoryRisk RiskOf(TargetKey key) =>
        _selection.Catalog.Apps.First(a => a.Id == key.AppId).Categories.First(c => c.Id == key.CategoryId).Risk;

    private async Task<object> CleanAsync(JsonObject input)
    {
        if (!Bool(input, "confirm")) throw new ToolRefusal("Limpiar es permanente: repite la llamada con confirm=true cuando el usuario lo haya aprobado.");
        var id = input["analysis_id"]?.GetValue<string>() ?? throw new ToolRefusal("Falta analysis_id.");
        if (!_analyses.TryGetValue(id, out var session) || !ReferenceEquals(session, _workflow.CurrentSession))
            throw new ToolRefusal("analysis_id desconocido, ya usado o sustituido por otro análisis. Vuelve a llamar a analyze_cleanup.");
        var blocker = _workflow.CleanBlocker();
        if (blocker is not null) throw new ToolRefusal(blocker);
        _analyses.Remove(id);
        var report = await _workflow.CleanAsync();
        if (report.Refused) throw new ToolRefusal("No se limpió nada: " + report.Refusal);
        return new
        {
            freed = Human(report.DeletedBytes),
            freed_bytes = report.DeletedBytes,
            deleted = report.DeletedFiles,
            not_deleted = report.NotDeleted,
            empty_folders_removed = report.EmptyDirectoriesRemoved,
            cancelled = report.Cancelled,
            reasons_not_deleted = report.Reasons(),
            journal = report.JournalPath,
            total_freed_with_ffcleaner = Human(_cleaning.Totals.Read().Bytes),
            targets = report.Targets.OrderByDescending(t => t.DeletedBytes).Select(t => new
            {
                key = t.Key.ToString(), freed = Human(t.DeletedBytes), deleted = t.Deleted, not_deleted = t.NotDeleted, reasons = t.Reasons,
            }),
        };
    }

    // ───────────────────────────── Registro ─────────────────────────────

    private async Task<object> ScanAsync(JsonObject input)
    {
        var ids = Strings(input, "families");
        var families = ids.Count == 0 ? [.. _scanner.Families.Select(f => f.Family)]
            : ids.Select(id => _scanner.Families.FirstOrDefault(f => f.Id == id)?.Family ?? throw new ToolRefusal("Familia desconocida: " + id)).ToList();
        var max = input["max_findings"]?.GetValue<int>() ?? 200;
        var scan = await _scanner.ScanAsync(families);
        _scans.Clear();
        _scans[scan.ScanId.ToString("N")] = scan;
        return new
        {
            scan_id = scan.ScanId.ToString("N"),
            elevated = RegistryScope.IsElevated,
            findings_total = scan.Findings.Count,
            repairable = scan.Findings.Count(f => f.CanRepair),
            by_risk = scan.Findings.GroupBy(f => f.Risk.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            findings = scan.Findings.OrderByDescending(f => f.CanRepair).Take(Math.Clamp(max, 1, 2000)).Select(f => new
            {
                id = f.Id, family = f.Family.ToString(), risk = f.Risk.ToString(), can_repair = f.CanRepair, key = f.Location.Display,
                value = f.ValueDisplay, target = f.ResolvedTarget ?? f.RawTarget, evidence = f.Evidence,
            }),
            issues = scan.Issues,
            nothing_changed = true,
            next_step = "Para reparar, llama a repair_registry con este scan_id, finding_ids o all_safe=true, y confirm=true.",
        };
    }

    private async Task<object> RepairAsync(JsonObject input)
    {
        if (!Bool(input, "confirm")) throw new ToolRefusal("Reparar modifica el Registro: repite la llamada con confirm=true cuando el usuario lo haya aprobado.");
        var scanId = input["scan_id"]?.GetValue<string>() ?? throw new ToolRefusal("Falta scan_id.");
        if (!_scans.TryGetValue(scanId, out var scan)) throw new ToolRefusal("scan_id desconocido o sustituido; vuelve a llamar a scan_registry.");
        var ids = Bool(input, "all_safe") ? scan.Findings.Where(f => f.CanRepair).Select(f => f.Id).ToList() : Strings(input, "finding_ids");
        if (ids.Count == 0) throw new ToolRefusal("No hay hallazgos que reparar: indica finding_ids o all_safe=true.");
        var notSafe = ids.Where(id => scan.Findings.FirstOrDefault(f => f.Id == id) is not { CanRepair: true }).ToList();
        if (notSafe.Count > 0) throw new ToolRefusal("Solo se reparan hallazgos «Seguro» de este análisis; no lo son: " + string.Join(", ", notSafe.Take(10)));
        _scans.Remove(scanId);
        var report = await _repair.RepairAsync(scan, ids);
        if (report.Refused) throw new ToolRefusal("No se reparó nada: " + report.Refusal);
        return new
        {
            repaired = report.Repaired,
            total = report.Outcomes.Count,
            backup = report.BackupPath,
            reg_file = report.RegFilePath,
            backup_id = _backups.List().FirstOrDefault(b => string.Equals(b.Path, report.BackupPath, StringComparison.OrdinalIgnoreCase))?.BackupId.ToString("N"),
            outcomes = report.Outcomes.Select(o => new { id = o.FindingId, key = o.Location, status = o.Status.ToString(), detail = o.Detail }),
            undo = "restore_registry_backup con el backup_id devuelve las entradas eliminadas.",
        };
    }

    private object ListBackups() => new
    {
        directory = _backups.BackupDirectory,
        backups = _backups.List().Select(b => new
        {
            backup_id = b.BackupId.ToString("N"), created_utc = b.CreatedUtc, entries = b.Entries, valid = b.Valid, problem = b.Problem,
        }),
    };

    private async Task<object> RestoreAsync(JsonObject input)
    {
        if (!Bool(input, "confirm")) throw new ToolRefusal("Restaurar modifica el Registro: repite la llamada con confirm=true.");
        var id = input["backup_id"]?.GetValue<string>() ?? throw new ToolRefusal("Falta backup_id.");
        var backup = _backups.List().FirstOrDefault(b => b.BackupId.ToString("N") == id.Replace("-", "", StringComparison.Ordinal))
            ?? throw new ToolRefusal("Respaldo desconocido: " + id);
        if (!backup.Valid) throw new ToolRefusal("El respaldo no es válido: " + backup.Problem);
        var report = await _backups.RestoreAsync(backup.Path);
        if (report.Refused) throw new ToolRefusal("No se restauró nada: " + report.Refusal);
        return new
        {
            restored = report.Restored, already_present = report.AlreadyPresent, conflicts = report.Conflicts, failed = report.Failed,
            outcomes = report.Outcomes.Select(o => new { key = o.Location, status = o.Status.ToString(), detail = o.Detail }),
        };
    }

    // ───────────────────────────── Inicio de Windows ─────────────────────────────

    private object ListStartup() => new
    {
        can_change_all_users = _startup.CanChangeMachineEntries,
        programs = _startup.List().Select(e => new
        {
            id = e.Id, name = e.Name, enabled = e.Enabled, source = e.SourceLabel, command = e.Command,
            program_on_disk = e.TargetState.ToString(), requires_admin = e.RequiresAdmin,
        }),
    };

    private object SetStartup(JsonObject input)
    {
        var id = input["id"]?.GetValue<string>() ?? throw new ToolRefusal("Falta id.");
        var enabled = input["enabled"]?.GetValue<bool>() ?? throw new ToolRefusal("Falta enabled.");
        var entry = _startup.List().FirstOrDefault(e => e.Id == id) ?? throw new ToolRefusal("Programa de inicio desconocido: " + id);
        var result = _startup.SetEnabled(entry, enabled);
        if (!result.Success) throw new ToolRefusal(result.Message);
        return new { id, enabled, message = result.Message };
    }

    // ───────────────────────────── Utilidades ─────────────────────────────

    private static bool Bool(JsonObject input, string name) => input[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static List<string> Strings(JsonObject input, string name) =>
        input[name] is JsonArray array ? [.. array.Select(n => n?.GetValue<string>() ?? throw new ArgumentException(name + " solo admite textos."))] : [];

    private static string Human(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return value.ToString(index == 0 ? "N0" : "N2", CultureInfo.InvariantCulture) + " " + units[index];
    }
}

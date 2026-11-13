namespace FFCleaner.RegistryAnalysis;

using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Elemento respaldado. Para <see cref="RepairKind.DeleteValue"/> <see cref="KeyPath"/> es la clave que
/// contiene el valor; para <see cref="RepairKind.DeleteKey"/> es la propia clave eliminada.
/// </summary>
public sealed record BackupEntry(string FindingId, RegistryFamily Family, RegHive Hive, RegView View, string KeyPath,
    string PhysicalPath, RepairKind Operation, BackupValue? Value, BackupKey? Tree, string Evidence)
{
    [JsonIgnore] public RegLocation Location => new(Hive, View, KeyPath);
}

/// <summary>Documento de respaldo: instantánea estructurada con suma de verificación.</summary>
public sealed record RegistryBackupDocument(string Format, Guid BackupId, Guid ScanId, DateTime CreatedUtc, string ScopeId,
    string UserSid, string Machine, string RegFile, string RegFileSha256, List<BackupEntry> Entries, string ContentSha256);

public sealed class RegistryBackupException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Respaldos del Registro en una carpeta propia de FFCleaner. Cada respaldo es una carpeta con «respaldo.json»
/// (tipos y bytes exactos, hive, vista y suma SHA-256) y «registro.reg» (formato estándar de Windows para
/// importar manualmente). Tras escribirlos se vuelven a leer del disco y se comparan antes de darlos por válidos.
/// </summary>
public sealed class RegistryBackupStore : IRegistryBackupService
{
    public const string Format = "ffcleaner-registry-backup/1";

    /// <summary>Formato de los respaldos hechos cuando la aplicación se llamaba Claro; siguen siendo restaurables.</summary>
    public const string LegacyFormat = "claro-registry-backup/1";
    public const string DocumentName = "respaldo.json";
    public const string RegName = "registro.reg";
    private static readonly RegLocation Anchor = new(RegHive.LocalMachine, RegView.Registry64, @"Software\FFCleaner\RegistryBackups");
    private static readonly RegLocation LegacyAnchor = new(RegHive.LocalMachine, RegView.Registry64, @"Software\Claro\RegistryBackups");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly RegistryScope _scope;
    private readonly Action<string>? _trace;

    public RegistryBackupStore(RegistryScope scope, string directory, Action<string>? trace = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _scope = scope;
        BackupDirectory = Path.GetFullPath(directory);
        _trace = trace;
    }

    public string BackupDirectory { get; }
    internal RegistryScope Scope => _scope;

    /// <summary>
    /// Escribe el respaldo y lo verifica releyéndolo del disco. Lanza <see cref="RegistryBackupException"/>
    /// si cualquier paso falla; en ese caso quien repara no debe modificar nada.
    /// </summary>
    internal (RegistryBackupDocument Document, string Path, string RegPath) CreateVerified(Guid scanId, IReadOnlyList<BackupEntry> entries)
    {
        if (entries.Count == 0) throw new RegistryBackupException("No hay nada que respaldar.");
        string? folder = null;
        try
        {
            var id = Guid.NewGuid();
            var created = DateTime.UtcNow;
            folder = Path.Combine(BackupDirectory, created.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + id.ToString("N")[..8]);
            Directory.CreateDirectory(folder);

            var regPath = Path.Combine(folder, RegName);
            var regBytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(RegFileWriter.Write(entries))).ToArray();
            WriteDurable(regPath, regBytes);
            var regHash = Convert.ToHexString(SHA256.HashData(regBytes));
            if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(regPath))) != regHash)
                throw new RegistryBackupException("El archivo .reg leído del disco no coincide con el escrito.");

            using var identity = WindowsIdentity.GetCurrent();
            var document = new RegistryBackupDocument(Format, id, scanId, created, _scope.Id, identity.User?.Value ?? string.Empty,
                Environment.MachineName, RegName, regHash, [.. entries], string.Empty);
            document = document with { ContentSha256 = Checksum(document) };
            var path = Path.Combine(folder, DocumentName);
            WriteDurable(path, Serialize(document));

            // Verificación: lo que hay en disco debe deserializar, cuadrar con su suma y con lo que se quiso guardar.
            var reread = Load(path);
            if (reread.ContentSha256 != document.ContentSha256 || reread.Entries.Count != entries.Count || reread.BackupId != id)
                throw new RegistryBackupException("El respaldo leído del disco no coincide con el escrito.");

            // Las entradas de HKLM solo se restauran si un ancla en HKLM (no modificable sin privilegios) confirma la suma.
            if (entries.Any(e => e.Hive == RegHive.LocalMachine))
            {
                using var anchor = _scope.Create(Anchor);
                RawRegistry.Set(anchor, id.ToString("N"), new(RawValue.Sz, Encoding.Unicode.GetBytes(document.ContentSha256 + "\0")));
            }
            _trace?.Invoke("respaldo " + id.ToString("N") + " verificado: " + entries.Count.ToString(CultureInfo.InvariantCulture) + " entradas en " + folder);
            return (document, path, regPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or JsonException or NotSupportedException or System.ComponentModel.Win32Exception or RegistryBackupException)
        {
            // Un respaldo a medias no debe quedar en la lista como si correspondiera a una reparación real.
            try { if (folder is not null && Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { _trace?.Invoke("respaldo incompleto no eliminado: " + folder); }
            throw ex as RegistryBackupException ?? new RegistryBackupException("No se pudo escribir o verificar el respaldo: " + ex.Message, ex);
        }
    }

    /// <summary>Lee un respaldo y comprueba formato y suma de verificación.</summary>
    internal static RegistryBackupDocument Load(string path)
    {
        RegistryBackupDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<RegistryBackupDocument>(File.ReadAllBytes(path), Json);
        }
        catch (JsonException ex)
        {
            throw new RegistryBackupException("El respaldo no es un JSON válido.", ex);
        }
        if (document is null || (document.Format != Format && document.Format != LegacyFormat) || document.Entries is null)
            throw new RegistryBackupException("Formato de respaldo no reconocido.");
        foreach (var entry in document.Entries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.KeyPath) || entry.PhysicalPath is null ||
                (entry.Operation == RepairKind.DeleteValue) != (entry.Value is not null) ||
                (entry.Operation == RepairKind.DeleteKey) != (entry.Tree is not null) ||
                (entry.Value is not null && !WellFormed(entry.Value)) || (entry.Tree is not null && !WellFormed(entry.Tree, root: true)))
                throw new RegistryBackupException("El respaldo contiene una entrada incompleta.");
        }
        if (!string.Equals(Checksum(document), document.ContentSha256, StringComparison.Ordinal))
            throw new RegistryBackupException("La suma de verificación del respaldo no coincide: el archivo fue alterado o está dañado.");
        return document;
    }

    private static bool WellFormed(BackupValue value)
    {
        if (value is null || value.Name is null || value.Data is null) return false;
        try { _ = Convert.FromBase64String(value.Data); return true; }
        catch (FormatException) { return false; }
    }

    /// <summary>Todo el árbol debe estar completo: listas presentes y nombres de subclave de un solo segmento.</summary>
    private static bool WellFormed(BackupKey key, bool root = false) =>
        key is not null && key.Values is not null && key.SubKeys is not null && key.Name is not null &&
        (root || (key.Name.Length > 0 && !key.Name.Contains('\\'))) &&
        key.Values.All(WellFormed) && key.SubKeys.All(sub => WellFormed(sub));

    public IReadOnlyList<RegistryBackupInfo> List()
    {
        var result = new List<RegistryBackupInfo>();
        if (!Directory.Exists(BackupDirectory)) return result;
        foreach (var folder in Directory.GetDirectories(BackupDirectory).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(folder, DocumentName);
            if (!File.Exists(path)) continue;
            try
            {
                var document = Load(path);
                result.Add(new(document.BackupId, document.CreatedUtc, path, Path.Combine(folder, document.RegFile), document.Entries.Count, document.ScopeId, true, null));
            }
            catch (Exception ex) when (ex is RegistryBackupException or IOException or UnauthorizedAccessException)
            {
                result.Add(new(Guid.Empty, Directory.GetCreationTimeUtc(folder), path, Path.Combine(folder, RegName), 0, string.Empty, false, ex.Message));
            }
        }
        return result;
    }

    public Task<RegistryRestoreReport> RestoreAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        return Task.Run(() => Restore(backupPath, cancellationToken), CancellationToken.None);
    }

    private RegistryRestoreReport Restore(string backupPath, CancellationToken token)
    {
        var started = DateTime.UtcNow;
        RegistryRestoreReport Refuse(Guid id, string reason)
        {
            _trace?.Invoke("restauración rechazada: " + reason);
            return new(id, started, DateTime.UtcNow, [], 0, 0, 0, 0, reason, false);
        }

        var full = Path.GetFullPath(backupPath);
        var folder = Path.GetDirectoryName(full);
        if (Path.GetFileName(full) != DocumentName || folder is null ||
            !string.Equals(Path.GetDirectoryName(folder), BackupDirectory, StringComparison.OrdinalIgnoreCase))
            return Refuse(Guid.Empty, "Solo se restauran respaldos de la carpeta de FFCleaner.");

        RegistryBackupDocument document;
        try { document = Load(full); }
        catch (Exception ex) when (ex is RegistryBackupException or IOException or UnauthorizedAccessException)
        {
            return Refuse(Guid.Empty, ex.Message);
        }
        if (document.ScopeId != _scope.Id) return Refuse(document.BackupId, "El respaldo pertenece a otro ámbito (" + document.ScopeId + ").");
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (!string.Equals(document.UserSid, identity.User?.Value, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(document.Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                return Refuse(document.BackupId, "El respaldo se creó con otro usuario o en otro equipo.");
        }

        var machineTrusted = MachineEntriesTrusted(document);
        var outcomes = new List<RestoreOutcome>();
        foreach (var entry in document.Entries)
        {
            var label = entry.Location.Display + (entry.Value is not null ? "  →  " + (entry.Value.Name.Length == 0 ? "(Predeterminado)" : entry.Value.Name) : string.Empty);
            if (token.IsCancellationRequested) { outcomes.Add(new(label, RestoreStatus.Cancelado, "Cancelado antes de restaurarlo.")); continue; }
            if (!RegistryFamilyRules.Owns(entry.Family, entry.KeyPath, entry.Operation, entry.Value?.Name))
            {
                outcomes.Add(new(label, RestoreStatus.Error, "La entrada no pertenece a las claves que FFCleaner gestiona; no se restaura."));
                continue;
            }
            // Un tipo de archivo solo se restaura como clave de usuario si no existe para todo el equipo:
            // así un respaldo manipulado no puede superponer una copia propia a una clase del sistema.
            if (entry.Family == RegistryFamily.FileAssociations && entry.Operation == RepairKind.DeleteKey && entry.Hive == RegHive.CurrentUser &&
                !entry.KeyPath.StartsWith(@"Software\Classes\Applications\", StringComparison.OrdinalIgnoreCase) && MachineClassExists(entry.KeyPath))
            {
                outcomes.Add(new(label, RestoreStatus.Error, "El tipo existe para todo el equipo; no se crea una copia de usuario."));
                continue;
            }
            if (entry.Hive == RegHive.LocalMachine && !machineTrusted)
            {
                outcomes.Add(new(label, RestoreStatus.Error, "Respaldo no verificable para HKLM (falta el ancla de integridad); no se restaura."));
                continue;
            }
            try
            {
                var result = entry.Operation == RepairKind.DeleteValue
                    ? RestoreValue(entry.Location, entry.Value!)
                    : RestoreTree(entry.Location, entry.Tree!);
                // Verificación: se vuelve a leer el Registro y se compara con lo respaldado.
                if (result.Status is RestoreStatus.Restaurado or RestoreStatus.YaPresente && !MatchesBackup(entry))
                    result = (RestoreStatus.Error, "La relectura tras restaurar no coincide con el respaldo.");
                outcomes.Add(new(label, result.Status, result.Detail));
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or System.ComponentModel.Win32Exception or FormatException or ArgumentException or InvalidOperationException)
            {
                outcomes.Add(new(label, RestoreStatus.Error, ex is UnauthorizedAccessException or SecurityException ? "Acceso denegado (requiere administrador)." : ex.Message));
            }
        }

        var report = new RegistryRestoreReport(document.BackupId, started, DateTime.UtcNow, outcomes,
            outcomes.Count(o => o.Status == RestoreStatus.Restaurado), outcomes.Count(o => o.Status == RestoreStatus.YaPresente),
            outcomes.Count(o => o.Status == RestoreStatus.Conflicto), outcomes.Count(o => o.Status == RestoreStatus.Error),
            null, token.IsCancellationRequested);
        _trace?.Invoke(string.Create(CultureInfo.InvariantCulture,
            $"restauración {document.BackupId:N}: {report.Restored} restauradas, {report.AlreadyPresent} ya presentes, {report.Conflicts} conflictos, {report.Failed} errores"));
        TryWriteLog(Path.Combine(folder, "restauracion-" + started.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json"), report);
        return report;
    }

    /// <summary>Guarda junto al respaldo el resultado de una reparación o restauración.</summary>
    internal void TryWriteLog(string path, object report)
    {
        try { File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(report, Json)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _trace?.Invoke("no se pudo guardar el registro de la operación: " + ex.Message);
        }
    }

    /// <summary>¿Existe esta misma clave en HKLM, en alguna vista? Ante la duda se responde que sí.</summary>
    private bool MachineClassExists(string keyPath)
    {
        try
        {
            foreach (var view in _scope.Views)
            {
                using var key = _scope.Open(new(RegHive.LocalMachine, view, keyPath), writable: false);
                if (key is not null) return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }

    private bool MachineEntriesTrusted(RegistryBackupDocument document)
    {
        if (document.Entries.All(e => e.Hive != RegHive.LocalMachine)) return true;
        try
        {
            // Los respaldos de la versión anterior guardaron su ancla con el nombre antiguo.
            foreach (var location in document.Format == LegacyFormat ? new[] { LegacyAnchor, Anchor } : [Anchor])
            {
                using var anchor = _scope.Open(location, writable: false);
                if (anchor is not null &&
                    string.Equals(RawRegistry.Query(anchor, document.BackupId.ToString("N"))?.AsString(), document.ContentSha256, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>Restaura un valor solo si falta; si existe con otros datos es un conflicto y no se sobrescribe.</summary>
    private (RestoreStatus Status, string Detail) RestoreValue(RegLocation location, BackupValue value)
    {
        using var key = _scope.Create(location);
        var wanted = new RawValue(value.Kind, Convert.FromBase64String(value.Data));
        var current = RawRegistry.Query(key, value.Name);
        if (current is null)
        {
            RawRegistry.Set(key, value.Name, wanted);
            return (RestoreStatus.Restaurado, "Valor restaurado con su tipo y bytes originales.");
        }
        return current.Value.Kind == wanted.Kind && current.Value.Data.AsSpan().SequenceEqual(wanted.Data)
            ? (RestoreStatus.YaPresente, "Ya contenía los datos respaldados.")
            : (RestoreStatus.Conflicto, "El valor existe ahora con otros datos; no se sobrescribió.");
    }

    private (RestoreStatus Status, string Detail) RestoreTree(RegLocation location, BackupKey tree)
    {
        int restored = 0, present = 0, conflicts = 0;
        void Walk(RegLocation at, BackupKey node)
        {
            using (_scope.Create(at)) { }
            foreach (var value in node.Values)
            {
                switch (RestoreValue(at, value).Status)
                {
                    case RestoreStatus.Restaurado: restored++; break;
                    case RestoreStatus.YaPresente: present++; break;
                    default: conflicts++; break;
                }
            }
            foreach (var sub in node.SubKeys) Walk(at.Child(sub.Name), sub);
        }
        using var existing = _scope.Open(location, writable: false);
        var existed = existing is not null;
        Walk(location, tree);
        if (conflicts > 0) return (RestoreStatus.Conflicto, conflicts.ToString(CultureInfo.InvariantCulture) + " valores existen ahora con otros datos; no se sobrescribieron.");
        return !existed || restored > 0
            ? (RestoreStatus.Restaurado, "Clave restaurada con sus valores y subclaves.")
            : (RestoreStatus.YaPresente, "La clave ya contenía los datos respaldados.");
    }

    /// <summary>¿El Registro contiene ahora, al menos, todo lo respaldado en esta entrada?</summary>
    private bool MatchesBackup(BackupEntry entry)
    {
        bool Contains(RegLocation at, BackupKey node)
        {
            using var key = _scope.Open(at, writable: false);
            if (key is null) return false;
            foreach (var value in node.Values)
                if (RegistrySnapshot.Hash(RegistrySnapshot.CaptureValue(key, value.Name)) != RegistrySnapshot.Hash(value)) return false;
            return node.SubKeys.All(sub => Contains(at.Child(sub.Name), sub));
        }
        if (entry.Operation == RepairKind.DeleteKey) return Contains(entry.Location, entry.Tree!);
        using var key = _scope.Open(entry.Location, writable: false);
        return key is not null && RegistrySnapshot.Hash(RegistrySnapshot.CaptureValue(key, entry.Value!.Name)) == RegistrySnapshot.Hash(entry.Value);
    }

    internal static byte[] Serialize(RegistryBackupDocument document) => JsonSerializer.SerializeToUtf8Bytes(document, Json);

    internal static string Checksum(RegistryBackupDocument document) =>
        Convert.ToHexString(SHA256.HashData(Serialize(document with { ContentSha256 = string.Empty })));

    private static void WriteDurable(string path, byte[] bytes)
    {
        var temporary = path + ".escribiendo";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: false);
    }
}

/// <summary>Claves que cada familia puede tocar. Limita lo que una reparación o una restauración pueden modificar.</summary>
internal static class RegistryFamilyRules
{
    private const string CurrentVersion = @"Software\Microsoft\Windows\CurrentVersion\";

    /// <param name="valueName">Nombre del valor en operaciones sobre valores; null en operaciones sobre claves.</param>
    /// <summary>Contenedores de Software\Classes que no son tipos de archivo y nunca se tratan como tales.</summary>
    private static readonly HashSet<string> ReservedClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Applications", "CLSID", "Interface", "TypeLib", "AppID", "Local Settings", "Wow6432Node", "SystemFileAssociations",
        "Directory", "Folder", "Drive", "*", "AllFilesystemObjects", "ActivatableClasses", "Extensions", "PackagedCom",
        "MIME", "Protocols", "Installer", "Component Categories", "Record", "VirtualStore",
    };

    /// <summary>Nombre de un tipo de archivo (ProgID) normal: ni extensión, ni contenedor del sistema, ni de app empaquetada.</summary>
    internal static bool IsUserFileType(string name) =>
        name.Length > 0 && !name.StartsWith('.') && !ReservedClasses.Contains(name) &&
        !name.StartsWith("AppX", StringComparison.OrdinalIgnoreCase);

    internal static bool Owns(RegistryFamily family, string keyPath, RepairKind operation, string? valueName)
    {
        // Hijo directo: exactamente un segmento bajo la clave de la familia, nunca una ruta más profunda.
        string? Child(string parent) =>
            keyPath.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase) && keyPath.Length > parent.Length + 1 &&
            keyPath.IndexOf('\\', parent.Length + 1) < 0 ? keyPath[(parent.Length + 1)..] : null;
        bool Is(string key) => keyPath.Equals(key, StringComparison.OrdinalIgnoreCase);
        return family switch
        {
            RegistryFamily.AppPaths => operation == RepairKind.DeleteKey && Child(CurrentVersion + "App Paths") is not null,
            RegistryFamily.Uninstall => operation == RepairKind.DeleteKey && Child(CurrentVersion + "Uninstall") is not null,
            // Solo el valor predeterminado de una extensión o la clave de un programa de «Abrir con».
            // El valor predeterminado de una extensión, la clave de un programa de «Abrir con» o un tipo de archivo del usuario.
            RegistryFamily.FileAssociations => operation == RepairKind.DeleteValue
                ? valueName is { Length: 0 } && Child(@"Software\Classes") is { } extension && extension.StartsWith('.')
                : Child(@"Software\Classes\Applications") is not null ||
                  (Child(@"Software\Classes") is { } type && IsUserFileType(type)),
            RegistryFamily.Startup => operation == RepairKind.DeleteValue && (Is(CurrentVersion + "Run") || Is(CurrentVersion + "RunOnce")),
            RegistryFamily.MuiCache => operation == RepairKind.DeleteValue &&
                (Is(@"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache") ||
                 Is(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store")),
            RegistryFamily.SharedDlls => operation == RepairKind.DeleteValue && Is(CurrentVersion + "SharedDLLs"),
            _ => false,
        };
    }
}

/// <summary>Genera un archivo .reg estándar («Windows Registry Editor Version 5.00») con los datos exactos respaldados.</summary>
internal static class RegFileWriter
{
    internal static string Write(IEnumerable<BackupEntry> entries)
    {
        var text = new StringBuilder("Windows Registry Editor Version 5.00\r\n");
        foreach (var entry in entries)
        {
            if (entry.Operation == RepairKind.DeleteValue)
            {
                text.Append("\r\n[").Append(entry.PhysicalPath).Append("]\r\n");
                AppendValue(text, entry.Value!);
            }
            else
            {
                AppendKey(text, entry.PhysicalPath, entry.Tree!);
            }
        }
        return text.ToString();
    }

    private static void AppendKey(StringBuilder text, string path, BackupKey key)
    {
        text.Append("\r\n[").Append(path).Append("]\r\n");
        foreach (var value in key.Values) AppendValue(text, value);
        foreach (var sub in key.SubKeys) AppendKey(text, path + "\\" + sub.Name, sub);
    }

    private static void AppendValue(StringBuilder text, BackupValue value)
    {
        var data = Convert.FromBase64String(value.Data);
        var lineStart = text.Length;
        text.Append(value.Name.Length == 0 ? "@" : "\"" + Escape(value.Name) + "\"").Append('=');
        if (value.Kind == RawValue.Sz && PlainString(data) is { } plain)
        {
            text.Append('"').Append(Escape(plain)).Append("\"\r\n");
            return;
        }
        if (value.Kind == RawValue.Dword && data.Length == 4)
        {
            text.Append("dword:").Append(BitConverter.ToUInt32(data, 0).ToString("x8", CultureInfo.InvariantCulture)).Append("\r\n");
            return;
        }
        var prefix = value.Kind == RawValue.Binary ? "hex:" : "hex(" + value.Kind.ToString("x", CultureInfo.InvariantCulture) + "):";
        text.Append(prefix);
        var column = text.Length - lineStart;
        for (var i = 0; i < data.Length; i++)
        {
            var piece = data[i].ToString("x2", CultureInfo.InvariantCulture) + (i < data.Length - 1 ? "," : string.Empty);
            if (column + piece.Length > 76 && i < data.Length - 1)
            {
                text.Append(piece).Append("\\\r\n  ");
                column = 2;
            }
            else
            {
                text.Append(piece);
                column += piece.Length;
            }
        }
        text.Append("\r\n");
    }

    /// <summary>Texto de un REG_SZ bien formado (un único terminador, sin controles); null si hay que escribirlo en hexadecimal.</summary>
    private static string? PlainString(byte[] data)
    {
        if (data.Length < 2 || data.Length % 2 != 0 || data[^1] != 0 || data[^2] != 0) return null;
        var text = Encoding.Unicode.GetString(data, 0, data.Length - 2);
        return text.Any(c => char.IsControl(c) || char.IsSurrogate(c)) ? null : text;
    }

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

namespace FFCleaner.RegistryAnalysis;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

/// <summary>Valor respaldado: nombre exacto, tipo numérico y bytes en Base64.</summary>
public sealed record BackupValue(string Name, int Kind, string Data);

/// <summary>Clave respaldada con todos sus valores y subclaves.</summary>
public sealed record BackupKey(string Name, List<BackupValue> Values, List<BackupKey> SubKeys);

/// <summary>La clave tiene más contenido del que una reparación acotada puede asumir.</summary>
internal sealed class RegistryTreeTooLargeException() : Exception("La clave contiene demasiados elementos para una reparación acotada.");

/// <summary>Captura exacta de valores y árboles, y huellas para detectar cambios posteriores.</summary>
internal static class RegistrySnapshot
{
    internal const int MaxKeys = 256, MaxValues = 4096;
    internal const long MaxBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Un nombre con medio par sustituto no se puede guardar en JSON sin alterarlo; respaldarlo así
    /// restauraría otro nombre. Esas entradas se tratan como no respaldables (y por tanto no reparables).
    /// </summary>
    internal static void EnsureRepresentable(string name)
    {
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsHighSurrogate(name[i]) && i + 1 < name.Length && char.IsLowSurrogate(name[i + 1])) { i++; continue; }
            if (char.IsSurrogate(name[i])) throw new IOException("Nombre con caracteres Unicode no representables en el respaldo.");
        }
    }

    internal static BackupValue? CaptureValue(RegistryKey key, string name)
    {
        EnsureRepresentable(name);
        var raw = RawRegistry.Query(key, name);
        return raw is null ? null : new(name, raw.Value.Kind, Convert.ToBase64String(raw.Value.Data));
    }

    /// <summary>Captura la clave completa. Lanza si supera los límites de una reparación acotada.</summary>
    internal static BackupKey CaptureTree(RegistryKey key, string name)
    {
        var budget = new Budget();
        return Capture(key, name, budget);
    }

    private static BackupKey Capture(RegistryKey key, string name, Budget budget)
    {
        if (++budget.Keys > MaxKeys) throw new RegistryTreeTooLargeException();
        EnsureRepresentable(name);
        var values = new List<BackupValue>();
        foreach (var valueName in key.GetValueNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var value = CaptureValue(key, valueName);
            if (value is null) continue;
            budget.Bytes += value.Data.Length;
            if (++budget.Values > MaxValues || budget.Bytes > MaxBytes) throw new RegistryTreeTooLargeException();
            values.Add(value);
        }
        var subKeys = new List<BackupKey>();
        foreach (var subName in key.GetSubKeyNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            using var sub = key.OpenSubKey(subName, writable: false)
                            ?? throw new IOException("Subclave no accesible: " + subName);
            subKeys.Add(Capture(sub, subName, budget));
        }
        return new(name, values, subKeys);
    }

    internal static string Hash(BackupValue? value)
    {
        if (value is null) return "ausente";
        var text = new StringBuilder();
        Append(text, value);
        return Digest(text);
    }

    internal static string Hash(BackupKey tree)
    {
        var text = new StringBuilder();
        Append(text, tree);
        return Digest(text);
    }

    private static void Append(StringBuilder text, BackupValue value) =>
        text.Append("v\u0001").Append(value.Name).Append('\u0001').Append(value.Kind).Append('\u0001').Append(value.Data).Append('\n');

    private static void Append(StringBuilder text, BackupKey key)
    {
        text.Append("k\u0001").Append(key.Name.ToUpperInvariant()).Append('\n');
        foreach (var value in key.Values) Append(text, value);
        foreach (var sub in key.SubKeys) Append(text, sub);
        text.Append("e\n");
    }

    private static string Digest(StringBuilder text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));

    private sealed class Budget
    {
        public int Keys, Values;
        public long Bytes;
    }
}

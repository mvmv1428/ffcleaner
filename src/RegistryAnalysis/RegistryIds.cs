namespace FFCleaner.RegistryAnalysis;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Identificadores estables y resistentes a colisión: misma vista + clave + valor
/// producen siempre el mismo Id; entradas distintas producen Ids distintos.
/// </summary>
internal static class RegistryIds
{
    public static string For(string view, string keyPath, string valueName)
    {
        var material = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            view.Trim().ToLowerInvariant(), NormalizeKey(keyPath), valueName.ToLowerInvariant()
        });

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return "reg-" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Identificador estable de un hallazgo dentro de su ámbito, familia, hive, vista, clave y valor.</summary>
    public static string ForFinding(string scopeId, RegistryFamily family, RegLocation location, string? valueName)
    {
        var material = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            scopeId, family.ToString(), location.Hive.ToString(), location.View.ToString(),
            NormalizeKey(location.KeyPath), valueName is null ? "clave" : "valor", (valueName ?? string.Empty).ToLowerInvariant(),
        });
        return "reg-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    private static string NormalizeKey(string keyPath) =>
        keyPath.Trim().Replace('/', '\\').ToLowerInvariant();
}

namespace FFCleaner.RegistrySafetyTests;

using System.Text;

/// <summary>
/// Runner de consola sin paquetes externos. Exit 0 solo si todas las pruebas pasan.
/// Auditoría de solo lectura: no crea, modifica ni elimina claves del Registro,
/// no ejecuta rutas y no escribe archivos.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        var passed = 0;
        var failures = new List<string>();

        foreach (var (name, action) in SafetyTests.All())
        {
            try
            {
                action();
                passed++;
                Console.WriteLine($"PASS  {name}");
            }
            catch (CheckFailedException ex)
            {
                failures.Add($"{name} => {ex.Message}");
                Console.WriteLine($"FAIL  {name}");
                Console.WriteLine($"      {ex.Message}");
            }
            catch (Exception ex)
            {
                failures.Add($"{name} => EXCEPCION {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"FAIL  {name}");
                Console.WriteLine($"      EXCEPCION {ex.GetType().Name}: {ex.Message}");
            }
        }

        var total = passed + failures.Count;
        Console.WriteLine();
        Console.WriteLine($"RESULT total={total} passed={passed} failed={failures.Count}");
        if (failures.Count > 0)
        {
            Console.WriteLine("FAILURES:");
            foreach (var failure in failures)
            {
                Console.WriteLine($" - {failure}");
            }
        }

        return failures.Count == 0 ? 0 : 1;
    }
}

internal sealed class CheckFailedException(string message) : Exception(message);

internal static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new CheckFailedException(message);
        }
    }

    public static void False(bool condition, string message) => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new CheckFailedException($"{what}: esperado '{expected}', obtenido '{actual}'");
        }
    }

    public static void NotNull(object? value, string what)
    {
        if (value is null)
        {
            throw new CheckFailedException($"{what}: se esperaba un valor, se obtuvo null");
        }
    }

    public static void IsNull(object? value, string what)
    {
        if (value is not null)
        {
            throw new CheckFailedException($"{what}: se esperaba null, se obtuvo '{value}'");
        }
    }

    public static void Contains(string haystack, string needle, string what)
    {
        if (!haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            throw new CheckFailedException($"{what}: '{haystack}' no contiene '{needle}'");
        }
    }

    public static void DoesNotContain(string haystack, string needle, string what)
    {
        if (haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            throw new CheckFailedException($"{what}: '{haystack}' contiene '{needle}'");
        }
    }
}

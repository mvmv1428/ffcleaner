namespace FFCleaner.RegistryTests;

using System;
using System.Collections.Generic;

/// <summary>Fallo de aserción con mensaje legible.</summary>
internal sealed class AssertionException : Exception
{
    public AssertionException(string message) : base(message)
    {
    }
}

/// <summary>Aserciones mínimas, sin paquetes externos.</summary>
internal static class Check
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new AssertionException(message);
        }
    }

    public static void False(bool condition, string message) => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new AssertionException($"{message} (esperado: {Show(expected)}, actual: {Show(actual)})");
        }
    }

    public static void NotEqual<T>(T unexpected, T actual, string message)
    {
        if (EqualityComparer<T>.Default.Equals(unexpected, actual))
        {
            throw new AssertionException($"{message} (no debía ser: {Show(unexpected)})");
        }
    }

    public static T NotNull<T>(T? value, string message) where T : class
    {
        if (value is null)
        {
            throw new AssertionException(message);
        }

        return value;
    }

    public static void Null(object? value, string message)
    {
        if (value is not null)
        {
            throw new AssertionException($"{message} (actual: {value})");
        }
    }

    public static void Contains(string needle, string? haystack, string message)
    {
        if (haystack is null || !haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            throw new AssertionException($"{message} (no contiene '{needle}': {haystack ?? "<null>"})");
        }
    }

    public static void DoesNotContain(string needle, string? haystack, string message)
    {
        if (haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            throw new AssertionException($"{message} (contiene '{needle}': {haystack})");
        }
    }

    private static string Show(object? value) => value is null ? "<null>" : value.ToString() ?? "<null>";
}

/// <summary>
/// Runner de consola propio: sin paquetes, sin descubrimiento mágico.
/// Sale con código != 0 si alguna prueba falla.
/// </summary>
internal static class TestRegistry
{
    private static readonly List<(string Name, Action Body)> Tests = new();

    public static void Add(string name, Action body) => Tests.Add((name, body));

    public static int RunAll()
    {
        var failed = 0;
        foreach (var (name, body) in Tests)
        {
            try
            {
                body();
                Console.WriteLine($"PASS  {name}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"FAIL  {name}");
                Console.WriteLine($"      {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{Tests.Count - failed}/{Tests.Count} pruebas aprobadas, {failed} fallidas.");
        return failed == 0 ? 0 : 1;
    }
}

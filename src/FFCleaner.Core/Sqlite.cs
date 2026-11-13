using System.Runtime.InteropServices;
using System.Text;

namespace FFCleaner.Core;

/// <summary>Error de SQLite con su código. 5 (BUSY) y 6 (LOCKED) significan que otro programa tiene la base abierta.</summary>
public sealed class SqliteException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
    public bool IsLocked => (Code & 0xFF) is 5 or 6;
}

/// <summary>
/// Acceso mínimo a SQLite mediante <c>winsqlite3.dll</c>, la biblioteca que Windows 10 y 11 ya incluyen en System32.
/// No añade dependencias al paquete. Solo se usa con sentencias fijas del propio programa (sin datos de usuario).
/// </summary>
internal sealed class SqliteDb : IDisposable
{
    private const int ReadOnly = 0x1, ReadWrite = 0x2, Create = 0x4, NoMutex = 0x8000;
    private const int Ok = 0, Row = 100, Done = 101;
    private IntPtr _db;

    private SqliteDb(IntPtr db) => _db = db;

    private static readonly Lazy<bool> AvailableProbe = new(() =>
    {
        try { return sqlite3_libversion() != IntPtr.Zero; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return false; }
    });

    /// <summary>
    /// ¿Está disponible la SQLite que incluye Windows? Si no lo está, los datos de navegador se informan
    /// como no disponibles en lugar de interrumpir el análisis con una excepción de carga de DLL.
    /// </summary>
    public static bool IsAvailable => AvailableProbe.Value;

    /// <summary>Motivo único de ausencia, para mostrarlo junto a la categoría.</summary>
    public const string UnavailableMessage =
        "Este Windows no incluye la biblioteca SQLite (winsqlite3.dll); los datos del navegador no se pueden analizar ni limpiar.";

    public static SqliteDb Open(string path, bool writable, bool create = false)
    {
        var flags = (writable ? ReadWrite : ReadOnly) | (create ? Create : 0) | NoMutex;
        int code;
        IntPtr db;
        try
        {
            code = sqlite3_open_v2(Utf8(path), out db, flags, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new SqliteException(-1, UnavailableMessage);
        }
        if (code != Ok)
        {
            var message = db == IntPtr.Zero ? "No se pudo abrir la base." : Message(db);
            if (db != IntPtr.Zero) sqlite3_close_v2(db);
            throw new SqliteException(code, message);
        }
        sqlite3_busy_timeout(db, 1500);
        return new SqliteDb(db);
    }

    public void Exec(string sql)
    {
        var code = sqlite3_exec(_db, Utf8(sql), IntPtr.Zero, IntPtr.Zero, out var error);
        if (code == Ok) return;
        var message = error != IntPtr.Zero ? Marshal.PtrToStringUTF8(error) ?? "" : Message(_db);
        if (error != IntPtr.Zero) sqlite3_free(error);
        throw new SqliteException(code, message);
    }

    /// <summary>Primer valor entero de una consulta, o 0 si no devuelve filas.</summary>
    public long Scalar(string sql)
    {
        var code = sqlite3_prepare_v2(_db, Utf8(sql), -1, out var statement, IntPtr.Zero);
        if (code != Ok) throw new SqliteException(code, Message(_db));
        try
        {
            code = sqlite3_step(statement);
            if (code == Row) return sqlite3_column_int64(statement, 0);
            if (code == Done) return 0;
            throw new SqliteException(code, Message(_db));
        }
        finally { sqlite3_finalize(statement); }
    }

    /// <summary>Primer valor de texto de una consulta.</summary>
    public string? Text(string sql)
    {
        var code = sqlite3_prepare_v2(_db, Utf8(sql), -1, out var statement, IntPtr.Zero);
        if (code != Ok) throw new SqliteException(code, Message(_db));
        try
        {
            code = sqlite3_step(statement);
            if (code == Row) return Marshal.PtrToStringUTF8(sqlite3_column_text(statement, 0));
            if (code == Done) return null;
            throw new SqliteException(code, Message(_db));
        }
        finally { sqlite3_finalize(statement); }
    }

    /// <summary>¿Existe la tabla? El nombre es siempre una constante del programa.</summary>
    public bool HasTable(string table) =>
        Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='" + table.Replace("'", "''") + "'") > 0;

    public void Dispose()
    {
        if (_db == IntPtr.Zero) return;
        sqlite3_close_v2(_db);
        _db = IntPtr.Zero;
    }

    private static string Message(IntPtr db) => Marshal.PtrToStringUTF8(sqlite3_errmsg(db)) ?? "Error de SQLite";

    private static byte[] Utf8(string text)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(text) + 1];
        Encoding.UTF8.GetBytes(text, bytes);
        return bytes;
    }

    private const string Lib = "winsqlite3";
    // Solo se carga desde System32: un «winsqlite3.dll» colocado junto al ejecutable no se ejecuta.
    [DllImport(Lib), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Lib), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(Lib), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
    [DllImport(Lib), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr argument, out IntPtr error);
    [DllImport(Lib), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void sqlite3_free(IntPtr pointer);
    [DllImport(Lib), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr statement, IntPtr tail);
    [DllImport(Lib), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_step(IntPtr statement);
    [DllImport(Lib), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern long sqlite3_column_int64(IntPtr statement, int column);
    [DllImport(Lib), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport(Lib), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport(Lib), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr sqlite3_errmsg(IntPtr db);
    [DllImport(Lib), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr sqlite3_libversion();
}

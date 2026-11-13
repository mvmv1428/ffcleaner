using FFCleaner.Core;

namespace FFCleaner.Tests;

/// <summary>Bases SQLite reales con el esquema mínimo de Chromium, para probar el borrado selectivo de datos de navegador.</summary>
internal static class BrowserDbFixture
{
    public static void Create(string profile)
    {
        Directory.CreateDirectory(Path.Combine(profile, "Network"));
        Directory.CreateDirectory(Path.Combine(profile, "Sessions"));
        Db(Path.Combine(profile, "History"),
            "CREATE TABLE meta(key TEXT, value TEXT); INSERT INTO meta VALUES('version','70');" +
            "CREATE TABLE urls(id INTEGER PRIMARY KEY, url TEXT, title TEXT);" +
            "CREATE TABLE visits(id INTEGER PRIMARY KEY, url INTEGER);" +
            "CREATE TABLE keyword_search_terms(keyword_id INTEGER, url_id INTEGER, term TEXT);" +
            "CREATE TABLE segments(id INTEGER PRIMARY KEY, name TEXT);" +
            "CREATE TABLE downloads(id INTEGER PRIMARY KEY, target_path TEXT);" +
            "CREATE TABLE downloads_url_chains(id INTEGER, chain_index INTEGER, url TEXT);" +
            string.Concat(Enumerable.Range(1, 5).Select(i => $"INSERT INTO urls VALUES({i},'https://sitio{i}.example','Sitio {i}');INSERT INTO visits VALUES({i},{i});")) +
            "INSERT INTO keyword_search_terms VALUES(1,1,'búsqueda privada');INSERT INTO segments VALUES(1,'sitio1');" +
            string.Concat(Enumerable.Range(1, 3).Select(i => $"INSERT INTO downloads VALUES({i},'C:\\\\Descargas\\\\archivo{i}.zip');INSERT INTO downloads_url_chains VALUES({i},0,'https://d{i}.example');")));
        Db(Path.Combine(profile, "Top Sites"), "CREATE TABLE top_sites(url TEXT); INSERT INTO top_sites VALUES('https://a.example'),('https://b.example');");
        Db(Path.Combine(profile, "Shortcuts"), "CREATE TABLE omni_box_shortcuts(id TEXT, text TEXT); INSERT INTO omni_box_shortcuts VALUES('1','sit'),('2','bus');");
        File.WriteAllBytes(Path.Combine(profile, "Visited Links"), new byte[2048]);
        Db(Path.Combine(profile, "Network", "Cookies"),
            "CREATE TABLE cookies(host_key TEXT, name TEXT, value TEXT);" +
            "INSERT INTO cookies VALUES('.a.example','sid','1'),('.b.example','sid','2'),('.c.example','x','3'),('.d.example','y','4');");
        Db(Path.Combine(profile, "Web Data"),
            "CREATE TABLE autofill(name TEXT, value TEXT);" +
            "CREATE TABLE autofill_profiles(guid TEXT, street TEXT);" +
            "CREATE TABLE credit_cards(guid TEXT, name_on_card TEXT);" +
            "CREATE TABLE keywords(id INTEGER, short_name TEXT);" +
            "INSERT INTO autofill VALUES('email','yo@example.com'),('nombre','Ana'),('ciudad','Lima');" +
            "INSERT INTO autofill_profiles VALUES('p1','Calle 1');INSERT INTO credit_cards VALUES('c1','ANA');INSERT INTO keywords VALUES(1,'Google'),(2,'Bing');");
        Db(Path.Combine(profile, "Login Data"), "CREATE TABLE logins(origin_url TEXT, password_value BLOB); INSERT INTO logins VALUES('https://banco.example', x'0102');");
        File.WriteAllBytes(Path.Combine(profile, "Sessions", "Session_13370000000000000"), new byte[300]);
        File.WriteAllBytes(Path.Combine(profile, "Sessions", "Tabs_13370000000000000"), new byte[200]);
        File.WriteAllBytes(Path.Combine(profile, "Current Session"), new byte[100]);
    }

    public static void Db(string path, string sql)
    {
        if (File.Exists(path)) File.Delete(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var db = SqliteDb.Open(path, writable: true, create: true);
        db.Exec(sql);
    }

    public static long Count(string path, string table)
    {
        using var db = SqliteDb.Open(path, writable: false);
        return db.Scalar("SELECT count(*) FROM \"" + table + "\"");
    }

    public static string Integrity(string path)
    {
        using var db = SqliteDb.Open(path, writable: false);
        return db.Text("PRAGMA integrity_check") ?? "";
    }

    /// <summary>Abre la base con un bloqueo exclusivo, como hace Chromium mientras está abierto.</summary>
    public static IDisposable LockExclusive(string path)
    {
        var db = SqliteDb.Open(path, writable: true);
        db.Exec("PRAGMA locking_mode=EXCLUSIVE; BEGIN EXCLUSIVE; SELECT count(*) FROM sqlite_master;");
        return db;
    }
}

/// <summary>Sonda de procesos controlada por la prueba.</summary>
internal sealed class FakeProcesses(params string[] running) : IProcessProbe
{
    public bool IsRunning(string processName) => running.Contains(processName, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Igual que el sandbox, pero sin privilegios de administrador.</summary>
internal sealed class NotElevated(IFolderProvider inner) : IFolderProvider
{
    public string LocalAppData => inner.LocalAppData;
    public string RoamingAppData => inner.RoamingAppData;
    public string Temp => inner.Temp;
    public string UserProfile => inner.UserProfile;
    public string LocalLowAppData => inner.LocalLowAppData;
    public string ProgramData => inner.ProgramData;
    public string WindowsDirectory => inner.WindowsDirectory;
    public bool IsElevated => false;
    public string StateDirectory => inner.StateDirectory;
    public string? ContainmentRoot => inner.ContainmentRoot;
    public bool IsSandbox => inner.IsSandbox;
}

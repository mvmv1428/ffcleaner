using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FFCleaner.Cleaning;
using FFCleaner.Core;
using FFCleaner.Mcp;
using FFCleaner.RegistryAnalysis;

// Servidor MCP de FFCleaner (stdio, JSON-RPC 2.0, un mensaje por línea). La salida estándar es solo para el
// protocolo; los diagnósticos van a stderr y a ffcleaner.log. Acepta --sandbox DIR --sandbox-registry ID para
// pruebas, con las mismas reglas que la aplicación de escritorio.
Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);
string? sandboxDir = null, sandboxRegistry = null;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] is "--sandbox" && i + 1 < args.Length) sandboxDir = args[++i];
    else if (args[i] is "--sandbox-registry" && i + 1 < args.Length) sandboxRegistry = args[++i];
    else { Console.Error.WriteLine("Argumento no reconocido: " + args[i]); return 2; }
}
if ((sandboxDir is null) != (sandboxRegistry is null))
{
    Console.Error.WriteLine("El modo sandbox requiere --sandbox y --sandbox-registry a la vez.");
    return 2;
}

IFolderProvider folders = sandboxDir is null ? new SystemFolderProvider() : new SandboxFolderProvider(sandboxDir);
var scope = sandboxRegistry is null ? RegistryScope.Production : RegistryScope.Sandbox(sandboxRegistry);
var server = new McpServer(folders, scope);

var stdout = Console.Out;
string? line;
while ((line = await Console.In.ReadLineAsync()) is not null)
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    JsonNode? response;
    try { response = await server.HandleAsync(JsonNode.Parse(line)); }
    catch (JsonException ex) { response = McpServer.Error(null, -32700, "JSON no válido: " + ex.Message); }
    // Última red: ningún mensaje puede terminar el bucle; el agente recibe un error y el servidor sigue.
    catch (Exception ex) { response = McpServer.Error(null, -32603, "Error interno: " + ex.Message); }
    if (response is null) continue;
    await stdout.WriteLineAsync(response.ToJsonString());
    await stdout.FlushAsync();
}
return 0;

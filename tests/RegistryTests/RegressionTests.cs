using FFCleaner.RegistryAnalysis;
namespace FFCleaner.RegistryTests;
internal static class RegressionTests
{
 public static void Register()
 {
  TestRegistry.Add("Una entrada ilegible se anota y no interrumpe la familia", () =>
  {
   var context=new DetectorContext(RegistryScope.Sandbox("entrada-ilegible"), new FileSystemTargetProbe(), RegistryFamily.Startup, CancellationToken.None);
   var examined=new List<string>();
   foreach (var name in new[]{"a","b","c"})
    context.Entry("HKCU\\Run → "+name, () =>
    {
     if (name=="b") throw new System.ComponentModel.Win32Exception(87);   // valor que Windows no devuelve
     examined.Add(name);
    });
   Check.Equal("a,c", string.Join(",", examined), "Las demás entradas se siguen examinando");
   Check.Equal(1, context.Issues.Count, "El fallo queda anotado");
   Check.Contains("HKCU\\Run → b", context.Issues[0], "El aviso dice qué entrada se omitió");
   var cancelled=false;
   try { context.Entry("x", () => throw new OperationCanceledException()); }
   catch (OperationCanceledException) { cancelled=true; }
   Check.True(cancelled, "La cancelación no se trata como entrada ilegible");
  });
  TestRegistry.Add("ADS nunca se considera destino ejecutable revisable", () =>
  {
   int probes=0;
   var f=RegistryEvaluator.Evaluate(new RegistryInput("Registry64", @"HKCU\Software\Test", "", @"C:\app.exe:payload.exe"), _ => { probes++; return TargetState.Missing; });
   Check.NotNull(f,"Debe explicar ambigüedad");
   Check.False(f!.Selectable,"ADS no seleccionable");
   Check.Equal(0,probes,"No consultar destino ADS");
  });
  TestRegistry.Add("IDs no colisionan por separadores ni recortan nombres de valor", () =>
  {
   ReferenceFinding F(string key,string name)=> RegistryEvaluator.Evaluate(new RegistryInput("Registry64",key,name,@"C:\missing.exe"), _=>TargetState.Missing)!;
   Check.NotEqual(F("A|B","C").Id,F("A","B|C").Id,"Separadores entre componentes no son identidad");
   Check.NotEqual(F("A"," valor").Id,F("A","valor").Id,"No recortar nombre de valor");
   Check.Equal(F("A","valor").Id,F("A","valor").Id,"Identidad estable");
  });
  TestRegistry.Add("Análisis de solo lectura: detectores sin APIs de modificación y con lectura cruda", () =>
  {
   var dir=new DirectoryInfo(AppContext.BaseDirectory);
   while(dir is not null && !Directory.Exists(System.IO.Path.Combine(dir.FullName,"src","RegistryAnalysis"))) dir=dir.Parent;
   Check.NotNull(dir,"No se encontró el árbol fuente");
   var folder=System.IO.Path.Combine(dir!.FullName,"src","RegistryAnalysis");
   // Las mutaciones solo pueden vivir en el reparador, el restaurador y el acceso crudo.
   foreach(var name in new[]{"RegistryScanner.cs","RegistryDetectors.cs","TargetResolution.cs","RegistryEvaluator.cs","RegistryPathSyntax.cs","RegistrySnapshot.cs"})
   {
    var source=File.ReadAllText(System.IO.Path.Combine(folder,name));
    Check.False(System.Text.RegularExpressions.Regex.IsMatch(source,@"\b(SetValue|DeleteValue|DeleteSubKey|DeleteSubKeyTree|CreateSubKey)\s*\(|RawRegistry\.Set\s*\(|[Ss]cope\.Create\s*\("),"APIs mutadoras en "+name);
    Check.False(System.Text.RegularExpressions.Regex.IsMatch(source,@"\bProcess\.Start\b"),"Ejecución de destinos en "+name);
   }
   var detectors=File.ReadAllText(System.IO.Path.Combine(folder,"RegistryDetectors.cs"));
   Check.Contains("RawRegistry.Query",detectors,"Los detectores leen valores crudos (REG_EXPAND_SZ sin expansión implícita)");
   Check.False(System.Text.RegularExpressions.Regex.IsMatch(detectors,@"\.GetValue\s*\("),"Los detectores no usan GetValue, que expande variables");
  });
 }
}

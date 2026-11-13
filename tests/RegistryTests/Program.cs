using System;
using System.Text;
using FFCleaner.RegistryTests;

try
{
    Console.OutputEncoding = Encoding.UTF8;
}
catch (Exception)
{
    // Consola sin soporte UTF-8: se continua con la codificacion por defecto.
}

EvaluatorTests.Register();
ScannerTests.Register();
RegressionTests.Register();
SandboxCycleTests.Register();
StartupTests.Register();

return TestRegistry.RunAll();

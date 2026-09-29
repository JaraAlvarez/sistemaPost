using Pos.License.Simulator;

// Simulador de POS por consola. El estado (instalación, huella, último token y claves conocidas) se guarda en un archivo JSON
// para encadenar comandos: activate → checkin → checkin … → deactivate.
return await SimulatorCli.RunAsync(args, Console.Out, Console.Error);

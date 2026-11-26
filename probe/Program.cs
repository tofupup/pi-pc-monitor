using PcmonProbe;

// Run as a service (the normal case, started by the service manager), or with --console
// from an elevated terminal for a quick interactive check before installing the service.

if (args.Contains("--console"))
{
    Log.Open(echo: true);
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Log.Line("console: Ctrl+C");
        cts.Cancel();
    };
    Probe.Run("console", cts.Token);
    Log.Line("console: stopped");
    return 0;
}

return ServiceHost.Run();

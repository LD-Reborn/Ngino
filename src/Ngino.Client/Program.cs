using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ngino.Client;

try
{
    var options = ClientOptions.Parse(args);

    Console.WriteLine("Ngino client");
    Console.WriteLine($"  client id: {options.ClientId}");
    Console.WriteLine($"  server tunnel: {options.TunnelUri}");
    Console.WriteLine($"  local upstream: {options.Upstream}");
    if (options.InsecureSkipTlsVerify)
    {
        Console.WriteLine("  WARNING: server TLS certificate validation is disabled");
    }

    // Args are parsed by ClientOptions; keep them away from the host configuration.
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [] });
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton<TunnelClient>();
    builder.Services.AddHostedService<TunnelWorker>();
    builder.Services.AddWindowsService(service => service.ServiceName = "NginoClient");
    // The EventLog provider defaults to Warning; connection state is worth seeing there.
    builder.Logging.AddFilter<Microsoft.Extensions.Logging.EventLog.EventLogLoggerProvider>("Ngino.Client", LogLevel.Information);

    await builder.Build().RunAsync();
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(ClientOptions.Usage);
    return 1;
}

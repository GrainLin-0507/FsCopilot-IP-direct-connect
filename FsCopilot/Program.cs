namespace FsCopilot;

using System.Globalization;
using System.Reflection;
using Connection;
using Microsoft.Extensions.DependencyInjection;
using Network;
using ReactiveUI.Avalonia;
using ReactiveUI.Avalonia.Splat;
using Serilog;
using Serilog.Events;
using Simulation;
using ViewModels;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

        // Elevated one-shot helper: adds the firewall rule and exits. No UI, no log file.
        if (args.Any(a => string.Equals(a, "--install-firewall", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = Firewall.Apply() ? 0 : 1;
            return;
        }

        if (args.Any(a => string.Equals(a, "--uninstall-firewall", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = Firewall.Remove() ? 0 : 1;
            return;
        }

        var isDev = args.Any(a => string.Equals(a, "--dev", StringComparison.OrdinalIgnoreCase));
        var isDebug = args.Any(a => string.Equals(a, "--debug", StringComparison.OrdinalIgnoreCase));
        var version = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            .Split('+')[0] ?? "unknown";

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File(
                path: "log",
                rollingInterval: RollingInterval.Infinite,
                rollOnFileSizeLimit: false,
                shared: true,
                retainedFileCountLimit: null,
                fileSizeLimitBytes: null,
                outputTemplate: "[{Timestamp:HH:mm:ss.fff}] {Message:lj}{NewLine}{Exception}",
                restrictedToMinimumLevel: isDev || isDebug ? LogEventLevel.Verbose : LogEventLevel.Debug
            )
            .CreateLogger();

        var config = NetworkConfig.Load(args);
        Log.Information("[Network] Mode={Mode} Server={Server} DirectPort={Port}",
            config.Direct ? "direct" : "rendezvous", config.Server, config.DirectPort);

        // Direct mode needs an inbound UDP rule; ask once (UAC) instead of making the user do it.
        // Rendezvous mode is untouched - the official build never needed a custom rule here.
        // FSCOPILOT_SKIP_FIREWALL=1 disables the automatic elevation (troubleshooting / unattended runs).
        var skipFirewall = Environment.GetEnvironmentVariable("FSCOPILOT_SKIP_FIREWALL") is "1";
        var firewall = !config.Direct
            ? new FirewallState(true, string.Empty, config.DirectPort)
            : skipFirewall
                ? new FirewallState(false, "Automatic firewall setup skipped (FSCOPILOT_SKIP_FIREWALL=1)", config.DirectPort)
                : Firewall.Ensure(config.DirectPort);

        Log.Information("[Firewall] {Status}", string.IsNullOrEmpty(firewall.Status) ? "not used" : firewall.Status);

        try
        {
            Log.Information("[Application] Loaded {Version} version", version);
            BuildAvaloniaApp(args, config, firewall).StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Application] Something went wrong");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp([], NetworkConfig.Load([]), new FirewallState(true, string.Empty, NetworkConfig.DefaultDirectPort));

    public static AppBuilder BuildAvaloniaApp(string[] args) =>
        BuildAvaloniaApp(args, NetworkConfig.Load(args), new FirewallState(true, string.Empty, NetworkConfig.DefaultDirectPort));

    public static AppBuilder BuildAvaloniaApp(string[] args, NetworkConfig config, FirewallState firewall)
    {
        var isDev = args.Any(a => string.Equals(a, "--dev", StringComparison.OrdinalIgnoreCase));
        // var isExperimental = args.Any(a => string.Equals(a, "--experimental", StringComparison.OrdinalIgnoreCase));
        var peerId = Random.String(8);
        var name = Environment.UserName;

        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseReactiveUIWithMicrosoftDependencyResolver(
                services =>
                {
                    services.AddSingleton(new SimClient(!isDev ? "FS Copilot" : "FS Copilot DEV"));
                    services.AddSingleton<SetupViewModel>();
                    services.AddSingleton(config);
                    services.AddSingleton(firewall);
                    services.AddSingleton(new Updater("http://p2p.fscopilot.com:2320"));
                    
                    if (!isDev)
                    {
                        services.AddSingleton<INetwork>(config.Direct
                            ? new DirectNetwork(peerId, name, config.DirectPort, config.DirectBind)
                            : new HybridNetwork(config.Server, peerId, name));
                        // services.AddSingleton<INetwork>(!isExperimental
                        //     ? new P2PNetwork("p2p.fscopilot.com", peerId, name)
                        //     : new HybridNetwork("p2p.fscopilot.com", peerId, name));
                        services.AddSingleton<MasterSwitch>();
                        services.AddSingleton<Coordinator>();
                        services.AddSingleton(sp => new MainViewModel(
                            peerId,
                            name,
                            sp.GetRequiredService<INetwork>(),
                            sp.GetRequiredService<SimClient>(),
                            sp.GetRequiredService<MasterSwitch>(),
                            sp.GetRequiredService<Coordinator>(),
                            sp.GetRequiredService<Updater>(),
                            config,
                            firewall
                        ));
                    }
                    else
                    {
                        services.AddSingleton<DevelopViewModel>();
                    }
                },
                null)
            .RegisterReactiveUIViewsFromEntryAssembly()
            .WithInterFont()
            .LogToTrace();
    }
}

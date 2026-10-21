namespace FsCopilot;

using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

/// <summary>
/// Windows Firewall helper for the direct ("type the partner's IP") mode.
///
/// The application keeps running as a normal user process (no UAC, so SimConnect and file access
/// behave exactly like the official build). Only the one-shot rule installation is elevated,
/// because Windows requires administrator rights to change firewall policy:
///
///   fsCopilot.exe --install-firewall      (started with Verb="runas", adds the rule, exits)
///
/// The rule is program-scoped (the current executable) and covers UDP on every port, so it does
/// not have to be re-created when <c>directPort</c> changes, and it also covers the case where the
/// n2n adapter is classified as a "public" network.
/// </summary>
public static class Firewall
{
    public const string RuleName = "FS Copilot Direct";

    /// <summary>Exit code of the elevated helper when the user declines the UAC prompt.</summary>
    public const int DeclinedExitCode = 2;

    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when an inbound allow rule for this executable already exists.</summary>
    public static bool IsRuleReady(string? exePath = null)
    {
        exePath ??= Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath)) return false;

        var (code, output) = Run($"/c netsh advfirewall firewall show rule name=\"{RuleName}\" verbose");
        if (code != 0) return false;

        // The rule exists; make sure it still points at this executable (the folder may have moved).
        return output.Contains(exePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Creates/updates the rule. Must run elevated.</summary>
    public static bool Apply(string? exePath = null)
    {
        exePath ??= Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        if (!IsAdministrator()) return false;

        // Replace any previous rule with our name (path or scope may have changed).
        Run($"/c netsh advfirewall firewall delete rule name=\"{RuleName}\"");

        // Block rules win over allow rules; drop ones aimed at this executable if they exist.
        var (blockCode, blockOutput) = Run("/c netsh advfirewall firewall show rule name=all dir=in verbose");
        if (blockCode == 0 &&
            blockOutput.Contains(exePath, StringComparison.OrdinalIgnoreCase) &&
            blockOutput.Contains("Block", StringComparison.OrdinalIgnoreCase))
        {
            Run($"/c netsh advfirewall firewall delete rule name=all dir=in program=\"{exePath}\"");
        }

        var (code, output) = Run(
            $"/c netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow " +
            $"program=\"{exePath}\" protocol=UDP profile=any enable=yes");

        if (code != 0)
        {
            Log.Warning("[Firewall] add rule failed ({Code}): {Output}", code, output.Trim());
            return false;
        }

        return IsRuleReady(exePath);
    }

    /// <summary>
    /// Makes sure the rule exists, asking for elevation once if needed.
    /// Called on startup in direct mode; returns the state that the UI displays.
    /// </summary>
    public static FirewallState Ensure(int port)
    {
        var exePath = Environment.ProcessPath;

        if (IsRuleReady(exePath))
            return new FirewallState(true, "Network ready (firewall rule present)", port);

        if (IsAdministrator())
        {
            var applied = Apply(exePath);
            return applied
                ? new FirewallState(true, "Network ready (firewall rule added)", port)
                : new FirewallState(false, "Could not add the firewall rule: administrator rights required", port);
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(exePath!)
            {
                Arguments = "--install-firewall",
                UseShellExecute = true,
                Verb = "runas"
            });

            if (process is null)
                return new FirewallState(false, "Could not start the elevated helper; add the rule manually", port);

            if (!process.WaitForExit(60000))
                return new FirewallState(false, "Elevation timed out, please retry", port);

            if (process.ExitCode == 0 && IsRuleReady(exePath))
                return new FirewallState(true, "Network ready (firewall rule added)", port);

            if (process.ExitCode == DeclinedExitCode)
                return new FirewallState(false, "Elevation declined: partners may not reach you. Press Fix to retry.", port);

            return new FirewallState(false, $"Elevation failed (code {process.ExitCode}). Retry or configure manually.", port);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return new FirewallState(false, "Elevation declined: partners may not reach you. Press Fix to retry.", port);
        }
        catch (Exception e)
        {
            Log.Warning(e, "[Firewall] ensure failed");
            return new FirewallState(false, $"Elevation failed: {e.Message}", port);
        }
    }

    /// <summary>Deletes the rule. Must run elevated. Used by <c>--uninstall-firewall</c>.</summary>
    public static bool Remove()
    {
        if (!IsAdministrator()) return false;

        var (code, _) = Run($"/c netsh advfirewall firewall delete rule name=\"{RuleName}\"");
        return code == 0;
    }

    private static (int Code, string Output) Run(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null) return (-1, string.Empty);

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(15000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (-1, output + error);
            }

            return (process.ExitCode, output + error);
        }
        catch (Exception e)
        {
            return (-1, e.Message);
        }
    }
}

/// <summary>Firewall availability snapshot shown in the UI (direct mode only).</summary>
public sealed class FirewallState(bool ready, string status, int port)
{
    public bool Ready { get; } = ready;
    public string Status { get; } = status;
    public int Port { get; } = port;
}

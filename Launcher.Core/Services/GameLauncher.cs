// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System.Diagnostics;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Installers;
using CmlLib.Core.ProcessBuilder;
using Launcher.Core.Models;

namespace Launcher.Core.Services;

/// <summary>
/// Installs the required files for an instance (with progress) and launches the
/// game, streaming its stdout/stderr. All progress and logs are surfaced via
/// events so the UI only subscribes; it never polls and never touches CmlLib.
/// </summary>
public sealed class GameLauncher
{
    private readonly LauncherPaths _paths;
    private readonly LoaderInstaller _loaderInstaller;
    private readonly IInstanceService _instances;
    private readonly LogHub _logs;

    // One live process per instance id. Instances run independently and in the
    // background, so this is keyed rather than a single "current" process.
    private readonly object _runGate = new();
    private readonly Dictionary<string, Process> _running = new();

    public GameLauncher(LauncherPaths paths, LoaderInstaller loaderInstaller, IInstanceService instances, LogHub logs)
    {
        _paths = paths;
        _loaderInstaller = loaderInstaller;
        _instances = instances;
        _logs = logs;
    }

    /// <summary>Coarse file-count progress (which file, how many done).</summary>
    public event EventHandler<FileProgressInfo>? FileProgress;

    /// <summary>Byte-level download progress (0..1 ratio).</summary>
    public event EventHandler<ByteProgressInfo>? ByteProgress;

    /// <summary>Raised with the instance id when its process starts (true) or exits (false).</summary>
    public event EventHandler<(string InstanceId, bool Running)>? RunningChanged;

    /// <summary>True if the given instance currently has a live process.</summary>
    public bool IsRunning(string instanceId)
    {
        lock (_runGate)
            return _running.TryGetValue(instanceId, out var p) && p is { HasExited: false };
    }

    /// <summary>True if any instance is currently running.</summary>
    public bool IsAnyRunning
    {
        get { lock (_runGate) return _running.Values.Any(p => !p.HasExited); }
    }

    /// <summary>
    /// Installs the loader (if needed), installs game files, then starts Minecraft.
    /// </summary>
    /// <returns>The started game process (already reading its output).</returns>
    public async Task<Process> LaunchAsync(Instance instance, MSession session, CancellationToken ct = default)
    {
        if (IsRunning(instance.Id))
            throw new InvalidOperationException("This instance is already running.");

        // Every line for this launch goes to the instance's own log session, so
        // switching instances in the UI switches the log instead of mixing runs.
        var log = _logs.Get(instance.Id);

        var minecraftDir = _paths.InstanceMinecraft(instance);
        var mcPath = new MinecraftPath(minecraftDir);
        var launcher = new MinecraftLauncher(mcPath);

        // Progress relays, shared by the base-file install and the game-file install.
        var fileProgress = new Progress<InstallerProgressChangedEventArgs>(e =>
            FileProgress?.Invoke(this, new FileProgressInfo(
                e.Name ?? "", e.EventType.ToString(), e.TotalTasks, e.ProgressedTasks)));

        var byteProgress = new Progress<ByteProgress>(e =>
            ByteProgress?.Invoke(this, new ByteProgressInfo(
                e.TotalBytes, e.ProgressedBytes, e.TotalBytes > 0 ? e.ToRatio() : 0)));

        // 1) Install the mod loader on first launch and remember its version id.
        if (instance.Loader != LoaderType.Vanilla && string.IsNullOrEmpty(instance.LoaderVersion))
        {
            // Fetch the base game files first (this also downloads a Java runtime) so the
            // Forge/NeoForge installer can run on machines with no system JDK installed.
            log.Append($"[install] Preparing base files for {instance.McVersion} (also fetches Java)...");
            await launcher.InstallAsync(instance.McVersion, fileProgress, byteProgress, ct).ConfigureAwait(false);

            // Prefer the instance's Java, else the runtime we just downloaded (system JDK last).
            var installerJava = !string.IsNullOrWhiteSpace(instance.JavaPath)
                ? instance.JavaPath
                : JavaLocator.FindBundledJava(minecraftDir);

            log.Append($"[loader] Installing {instance.Loader} for {instance.McVersion}...");
            var loaderLog = new Progress<string>(line => log.Append(line));
            instance.LoaderVersion = await _loaderInstaller
                .InstallAsync(instance, launcher, minecraftDir, loaderLog, ct, installerJava).ConfigureAwait(false);
            await _instances.UpdateAsync(instance, ct).ConfigureAwait(false);
            log.Append($"[loader] Installed: {instance.LoaderVersion}");
        }

        // 2) Install/verify game files (manifests, libraries, assets, java runtime).
        var versionId = instance.LaunchVersionId;
        log.Append($"[install] Verifying files for {versionId}...");
        await launcher.InstallAsync(versionId, fileProgress, byteProgress, ct).ConfigureAwait(false);

        // 3) Build the launch options from the instance config.
        var option = new MLaunchOption
        {
            Session = session,
            MaximumRamMb = instance.MaxRamMb,
            MinimumRamMb = instance.MinRamMb
        };
        if (!string.IsNullOrWhiteSpace(instance.JavaPath))
            option.JavaPath = instance.JavaPath;

        var extraArgs = SplitJvmArgs(instance.JvmArgs);
        if (extraArgs.Count > 0)
            option.ExtraJvmArguments = extraArgs.Select(a => new MArgument(a));

        log.Append($"[launch] Starting {instance.Name} as {session.Username}...");
        var process = await launcher.BuildProcessAsync(versionId, option, ct).ConfigureAwait(false);

        // 4) Capture the process output and lifetime.
        var instanceId = instance.Id;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.EnableRaisingEvents = true;
        process.OutputDataReceived += (_, e) => { if (e.Data != null) log.Append(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) log.Append(e.Data); };
        process.Exited += (_, _) =>
        {
            lock (_runGate)
            {
                if (_running.TryGetValue(instanceId, out var p) && ReferenceEquals(p, process))
                    _running.Remove(instanceId);
            }
            RunningChanged?.Invoke(this, (instanceId, false));
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        lock (_runGate) _running[instanceId] = process;
        RunningChanged?.Invoke(this, (instanceId, true));
        return process;
    }

    /// <summary>Force-stops the given instance's game, if it is running.</summary>
    public void Stop(string instanceId)
    {
        Process? p;
        lock (_runGate) _running.TryGetValue(instanceId, out p);
        if (p is { HasExited: false })
            p.Kill(entireProcessTree: true);
    }

    /// <summary>Force-stops every running instance (e.g. on app shutdown).</summary>
    public void StopAll()
    {
        Process[] all;
        lock (_runGate) all = _running.Values.ToArray();
        foreach (var p in all)
            if (!p.HasExited) p.Kill(entireProcessTree: true);
    }

    private static List<string> SplitJvmArgs(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new List<string>();
        return raw
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}

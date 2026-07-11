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
/// events so the UI only subscribes — it never polls and never touches CmlLib.
/// </summary>
public sealed class GameLauncher
{
    private readonly LauncherPaths _paths;
    private readonly LoaderInstaller _loaderInstaller;
    private readonly IInstanceService _instances;

    private Process? _current;

    public GameLauncher(LauncherPaths paths, LoaderInstaller loaderInstaller, IInstanceService instances)
    {
        _paths = paths;
        _loaderInstaller = loaderInstaller;
        _instances = instances;
    }

    /// <summary>Coarse file-count progress (which file, how many done).</summary>
    public event EventHandler<FileProgressInfo>? FileProgress;

    /// <summary>Byte-level download progress (0..1 ratio).</summary>
    public event EventHandler<ByteProgressInfo>? ByteProgress;

    /// <summary>A line of game/installer log output.</summary>
    public event EventHandler<string>? Log;

    /// <summary>Raised true when the game process starts, false when it exits.</summary>
    public event EventHandler<bool>? RunningChanged;

    public bool IsRunning => _current is { HasExited: false };

    /// <summary>
    /// Installs the loader (if needed), installs game files, then starts Minecraft.
    /// </summary>
    /// <returns>The started game process (already reading its output).</returns>
    public async Task<Process> LaunchAsync(Instance instance, MSession session, CancellationToken ct = default)
    {
        if (IsRunning)
            throw new InvalidOperationException("A game instance is already running.");

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
            // Fetch the base game files first — this also downloads a Java runtime — so the
            // Forge/NeoForge installer can run on machines with no system JDK installed.
            Log?.Invoke(this, $"[install] Preparing base files for {instance.McVersion} (also fetches Java)...");
            await launcher.InstallAsync(instance.McVersion, fileProgress, byteProgress, ct).ConfigureAwait(false);

            // Prefer the instance's Java, else the runtime we just downloaded (system JDK last).
            var installerJava = !string.IsNullOrWhiteSpace(instance.JavaPath)
                ? instance.JavaPath
                : JavaLocator.FindBundledJava(minecraftDir);

            Log?.Invoke(this, $"[loader] Installing {instance.Loader} for {instance.McVersion}...");
            var loaderLog = new Progress<string>(line => Log?.Invoke(this, line));
            instance.LoaderVersion = await _loaderInstaller
                .InstallAsync(instance, launcher, minecraftDir, loaderLog, ct, installerJava).ConfigureAwait(false);
            await _instances.UpdateAsync(instance, ct).ConfigureAwait(false);
            Log?.Invoke(this, $"[loader] Installed: {instance.LoaderVersion}");
        }

        // 2) Install/verify game files (manifests, libraries, assets, java runtime).
        var versionId = instance.LaunchVersionId;
        Log?.Invoke(this, $"[install] Verifying files for {versionId}...");
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

        Log?.Invoke(this, $"[launch] Starting {instance.Name} as {session.Username}...");
        var process = await launcher.BuildProcessAsync(versionId, option, ct).ConfigureAwait(false);

        // 4) Capture the process output and lifetime.
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.EnableRaisingEvents = true;
        process.OutputDataReceived += (_, e) => { if (e.Data != null) Log?.Invoke(this, e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) Log?.Invoke(this, e.Data); };
        process.Exited += (_, _) =>
        {
            RunningChanged?.Invoke(this, false);
            _current = null;
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _current = process;
        RunningChanged?.Invoke(this, true);
        return process;
    }

    /// <summary>Force-stops the running game, if any.</summary>
    public void Stop()
    {
        var p = _current;
        if (p is { HasExited: false })
            p.Kill(entireProcessTree: true);
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

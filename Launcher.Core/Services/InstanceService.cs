// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using Launcher.Core.Models;

namespace Launcher.Core.Services;

/// <summary>CRUD over instances: the index file plus each instance's isolated folder.</summary>
public interface IInstanceService
{
    Task<IReadOnlyList<Instance>> ListAsync(CancellationToken ct = default);
    Task<Instance> CreateAsync(string name, string mcVersion, LoaderType loader, CancellationToken ct = default);
    Task UpdateAsync(Instance instance, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
}

public sealed class InstanceService : IInstanceService
{
    private readonly LauncherPaths _paths;

    public InstanceService(LauncherPaths paths) => _paths = paths;

    public async Task<IReadOnlyList<Instance>> ListAsync(CancellationToken ct = default)
    {
        var list = await JsonStore.ReadAsync<List<Instance>>(_paths.InstancesIndex, ct).ConfigureAwait(false);
        return list ?? new List<Instance>();
    }

    public async Task<Instance> CreateAsync(string name, string mcVersion, LoaderType loader, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Instance name cannot be empty.", nameof(name));
        if (string.IsNullOrWhiteSpace(mcVersion))
            throw new ArgumentException("Minecraft version cannot be empty.", nameof(mcVersion));

        var instance = new Instance
        {
            Name = name.Trim(),
            McVersion = mcVersion.Trim(),
            Loader = loader
        };
        instance.FolderName = $"{Sanitize(instance.Name)}_{instance.Id[..6]}";

        // Create the isolated .minecraft + mods folders up front.
        Directory.CreateDirectory(_paths.InstanceModsDir(instance));

        var all = new List<Instance>(await ListAsync(ct).ConfigureAwait(false)) { instance };
        await PersistAsync(all, instance, ct).ConfigureAwait(false);
        return instance;
    }

    public async Task UpdateAsync(Instance instance, CancellationToken ct = default)
    {
        var all = new List<Instance>(await ListAsync(ct).ConfigureAwait(false));
        var idx = all.FindIndex(i => i.Id == instance.Id);
        if (idx < 0)
            throw new InvalidOperationException($"Instance '{instance.Name}' ({instance.Id}) does not exist.");

        all[idx] = instance;
        Directory.CreateDirectory(_paths.InstanceModsDir(instance));
        await PersistAsync(all, instance, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        var all = new List<Instance>(await ListAsync(ct).ConfigureAwait(false));
        var instance = all.FirstOrDefault(i => i.Id == id)
            ?? throw new InvalidOperationException($"Instance {id} does not exist.");

        all.RemoveAll(i => i.Id == id);
        await JsonStore.WriteAsync(_paths.InstancesIndex, all, ct).ConfigureAwait(false);

        var dir = _paths.InstanceDir(instance);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    private async Task PersistAsync(List<Instance> all, Instance instance, CancellationToken ct)
    {
        // instances.json is the index; instance.json is the per-instance config (spec requirement).
        await JsonStore.WriteAsync(_paths.InstancesIndex, all, ct).ConfigureAwait(false);
        await JsonStore.WriteAsync(_paths.InstanceConfig(instance), instance, ct).ConfigureAwait(false);
    }

    private static string Sanitize(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        var s = new string(chars).Trim('_');
        return string.IsNullOrEmpty(s) ? "instance" : s;
    }
}

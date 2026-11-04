namespace Quantum.Plugins;

public sealed class PluginDependencyPlanner
{
    public PluginLoadPlan CreatePlan(IEnumerable<PluginCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var candidateList = candidates.ToArray();
        var failures = new List<PluginLoadFailure>();
        var duplicateIds = candidateList
            .GroupBy(static candidate => candidate.Manifest.Id)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToHashSet();

        foreach (var duplicateId in duplicateIds)
        {
            failures.Add(new PluginLoadFailure(duplicateId, "Multiple plugin directories declare the same id."));
        }

        var active = candidateList
            .Where(candidate => !duplicateIds.Contains(candidate.Manifest.Id))
            .ToDictionary(static candidate => candidate.Manifest.Id);

        var conflicts = active.Values
            .SelectMany(candidate => candidate.Manifest.Capabilities.ProvidedKinds
                .Select(capability => (candidate.Manifest.Id, Name: capability.Key, Kind: capability.Value)))
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .Where(group => group.Select(item => item.Kind).Distinct().Skip(1).Any())
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToArray();
        foreach (var group in conflicts)
        {
            foreach (var item in group)
            {
                if (active.Remove(item.Id))
                {
                    failures.Add(new PluginLoadFailure(item.Id, $"Capability '{group.Key}' has conflicting kinds."));
                }
            }
        }

        var notificationConflicts = active.Values
            .SelectMany(candidate => candidate.Manifest.Capabilities.Notifications
                .Select(item => (candidate.Manifest.Id, Name: item.Key, Contract: item.Value)))
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .Where(group => group.Select(item => (item.Contract.Dispatch, Stops: string.Join(",", item.Contract.StopObservers)))
                .Distinct().Skip(1).Any())
            .ToArray();
        foreach (var group in notificationConflicts)
        {
            foreach (var item in group)
            {
                if (active.Remove(item.Id))
                {
                    failures.Add(new PluginLoadFailure(item.Id, $"Notification '{group.Key}' has conflicting dispatch policies."));
                }
            }
        }

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var candidate in active.Values.ToArray())
            {
                var failure = FindDependencyFailure(candidate, active);
                if (failure is null)
                {
                    continue;
                }

                active.Remove(candidate.Manifest.Id);
                failures.Add(new PluginLoadFailure(candidate.Manifest.Id, failure));
                changed = true;
            }
        }

        var integrationPrerequisites = active.ToDictionary(
            static pair => pair.Key,
            pair => pair.Value.Manifest.Integrations
                .Where(integration => IsCompatibleIntegration(integration, active))
                .Select(static integration => integration.Id)
                .ToHashSet());

        var ordered = new List<PluginCandidate>(active.Count);
        var remaining = active.Keys.ToHashSet();
        var loadedCapabilities = new HashSet<string>(StringComparer.Ordinal);
        var availableCapabilities = active.Values.SelectMany(candidate => candidate.Manifest.Capabilities.Provides)
            .ToHashSet(StringComparer.Ordinal);
        while (remaining.Count > 0)
        {
            var ready = remaining
                .Where(pluginId => active[pluginId].Manifest.Dependencies.All(dependency => !remaining.Contains(dependency.Id))
                    && active[pluginId].Manifest.Capabilities.Requires.All(loadedCapabilities.Contains))
                .OrderBy(static pluginId => (string)pluginId, StringComparer.Ordinal)
                .ToArray();
            if (ready.Length == 0)
            {
                break;
            }

            var preferred = ready
                .Where(pluginId => integrationPrerequisites[pluginId].All(
                    integrationId => !remaining.Contains(integrationId))
                    && active[pluginId].Manifest.Capabilities.Integrates.All(capability =>
                        loadedCapabilities.Contains(capability) || !availableCapabilities.Contains(capability)))
                .ToArray();
            var pluginId = preferred.Length > 0 ? preferred[0] : ready[0];

            ordered.Add(active[pluginId]);
            remaining.Remove(pluginId);
            loadedCapabilities.UnionWith(active[pluginId].Manifest.Capabilities.Provides);
        }

        foreach (var pluginId in remaining.OrderBy(static pluginId => (string)pluginId, StringComparer.Ordinal))
        {
            failures.Add(new PluginLoadFailure(
                pluginId,
                "Required plugin dependencies contain a cycle or depend on one."));
        }

        return new PluginLoadPlan(ordered, failures);
    }

    private static string? FindDependencyFailure(
        PluginCandidate candidate,
        IReadOnlyDictionary<string, PluginCandidate> active)
    {
        foreach (var dependency in candidate.Manifest.Dependencies)
        {
            if (!active.TryGetValue(dependency.Id, out var dependencyCandidate))
            {
                return $"Required plugin '{dependency.Id}' is missing or invalid.";
            }

            if (!VersionRange.Contains(dependency.VersionRange, dependencyCandidate.Manifest.Version))
            {
                return $"Plugin '{dependency.Id}' version {dependencyCandidate.Manifest.Version} "
                    + $"does not satisfy range {dependency.VersionRange}.";
            }
        }

        var missingCapability = FindMissingCapability(candidate.Manifest, active.Values.Select(value => value.Manifest));
        return missingCapability is null ? null : $"Required capability '{missingCapability}' is missing or invalid.";
    }

    // Recheck readiness after each attempt so a failed provider does not prevent
    // a different provider later in the plan from satisfying the same requirement.
    internal static IEnumerable<T> OrderAttempts<T>(
        IEnumerable<T> items,
        Func<T, PluginManifest> getManifest,
        IReadOnlySet<string> successfulIds)
    {
        var pending = items.ToList();
        var manifests = pending.Select(getManifest).ToArray();
        while (pending.Count > 0)
        {
            var providers = manifests.Where(manifest => successfulIds.Contains(manifest.Id)).ToArray();
            var index = pending.FindIndex(item =>
            {
                var manifest = getManifest(item);
                return manifest.Dependencies.All(dependency => successfulIds.Contains(dependency.Id))
                    && FindMissingCapability(manifest, providers) is null;
            });
            if (index < 0)
            {
                foreach (var blocked in pending)
                {
                    yield return blocked;
                }

                yield break;
            }

            var next = pending[index];
            pending.RemoveAt(index);
            yield return next;
        }
    }

    internal static string? FindMissingCapability(PluginManifest manifest, IEnumerable<PluginManifest> providers)
    {
        if (manifest.Capabilities.Requires.Count == 0)
        {
            return null;
        }

        var available = providers.SelectMany(provider => provider.Capabilities.Provides)
            .ToHashSet(StringComparer.Ordinal);
        return manifest.Capabilities.Requires.FirstOrDefault(capability => !available.Contains(capability));
    }

    private static bool IsCompatibleIntegration(
        PluginIntegration integration,
        IReadOnlyDictionary<string, PluginCandidate> active)
        => active.TryGetValue(integration.Id, out var candidate)
            && VersionRange.Contains(integration.VersionRange, candidate.Manifest.Version);
}

public sealed record PluginLoadPlan(
    IReadOnlyList<PluginCandidate> OrderedCandidates,
    IReadOnlyList<PluginLoadFailure> Failures);

public sealed record PluginLoadFailure(string? PluginId, string Reason, Exception? Exception = null);

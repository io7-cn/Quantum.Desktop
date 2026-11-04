using Quantum.Plugins;

namespace Quantum.Tests;

public sealed class PluginDependencyPlannerTests
{
    private readonly PluginDependencyPlanner _planner = new();

    [Fact]
    public void CreatePlan_SortsDependenciesBeforeDependents()
    {
        var core = Candidate("core", "1.0.0");
        var feature = Candidate(
            "feature",
            "1.0.0",
            dependencies: [new PluginDependency(core.Manifest.Id, VersionRange.Of("[1.0.0,)"))]);

        var plan = _planner.CreatePlan([feature, core]);

        Assert.Equal([core.Manifest.Id, feature.Manifest.Id], plan.OrderedCandidates.Select(static item => item.Manifest.Id));
        Assert.Empty(plan.Failures);
    }

    [Fact]
    public void CreatePlan_RejectsMissingDependencyAndItsDependents()
    {
        var feature = Candidate(
            "feature",
            "1.0.0",
            dependencies: [new PluginDependency(PluginId.Of("missing"), VersionRange.Of("[1.0.0,)"))]);
        var child = Candidate(
            "child",
            "1.0.0",
            dependencies: [new PluginDependency(feature.Manifest.Id, VersionRange.Of("[1.0.0,)"))]);

        var plan = _planner.CreatePlan([child, feature]);

        Assert.Empty(plan.OrderedCandidates);
        Assert.Equal(2, plan.Failures.Count);
    }

    [Fact]
    public void CreatePlan_RejectsIncompatibleRequiredDependency()
    {
        var core = Candidate("core", "1.0.0");
        var feature = Candidate(
            "feature",
            "1.0.0",
            dependencies: [new PluginDependency(core.Manifest.Id, VersionRange.Of("[2.0.0,)"))]);

        var plan = _planner.CreatePlan([core, feature]);

        Assert.Equal(core.Manifest.Id, Assert.Single(plan.OrderedCandidates).Manifest.Id);
        Assert.Contains(plan.Failures, failure => failure.PluginId == feature.Manifest.Id);
    }

    [Fact]
    public void CreatePlan_AcceptsPrereleaseFromUnionRange()
    {
        var core = Candidate("core", "1.4.0-beta");
        var feature = Candidate(
            "feature",
            "1.0.0",
            dependencies: [new PluginDependency(
                core.Manifest.Id,
                VersionRange.Of("{1.2.3}|[1.4.0-alpha,1.4.0)"))]);

        var plan = _planner.CreatePlan([feature, core]);

        Assert.Equal([core.Manifest.Id, feature.Manifest.Id], plan.OrderedCandidates.Select(static item => item.Manifest.Id));
        Assert.Empty(plan.Failures);
    }

    [Fact]
    public void CreatePlan_TreatsBuildMetadataAsRangeEquivalent()
    {
        var core = Candidate("core", "1.2.3+linux-x64");
        var feature = Candidate(
            "feature",
            "1.0.0",
            dependencies: [new PluginDependency(core.Manifest.Id, VersionRange.Of("{1.2.3}"))]);

        var plan = _planner.CreatePlan([feature, core]);

        Assert.Equal([core.Manifest.Id, feature.Manifest.Id], plan.OrderedCandidates.Select(static item => item.Manifest.Id));
        Assert.Empty(plan.Failures);
    }

    [Fact]
    public void CreatePlan_RejectsDependencyCycles()
    {
        var firstId = PluginId.Of("first");
        var secondId = PluginId.Of("second");
        var first = Candidate(
            "first",
            "1.0.0",
            dependencies: [new PluginDependency(secondId, VersionRange.Of("[1.0.0,)"))]);
        var second = Candidate(
            "second",
            "1.0.0",
            dependencies: [new PluginDependency(firstId, VersionRange.Of("[1.0.0,)"))]);

        var plan = _planner.CreatePlan([first, second]);

        Assert.Empty(plan.OrderedCandidates);
        Assert.All(plan.Failures, static failure => Assert.Contains("cycle", failure.Reason, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CreatePlan_AllowsMissingIntegration()
    {
        var plugin = Candidate(
            "standalone",
            "1.0.0",
            integrations: [new PluginIntegration(PluginId.Of("optional-addon"), VersionRange.Of("[1.0.0,)"))]);

        var plan = _planner.CreatePlan([plugin]);

        Assert.Equal(plugin.Manifest.Id, Assert.Single(plan.OrderedCandidates).Manifest.Id);
        Assert.Empty(plan.Failures);
    }

    [Fact]
    public void CreatePlan_PrefersCompatibleIntegrationBeforeOwner()
    {
        var target = Candidate("z-target", "1.2.0");
        var owner = Candidate(
            "a-owner",
            "1.0.0",
            integrations: [new PluginIntegration(target.Manifest.Id, VersionRange.Of("[1.1.0,2.0.0)"))]);

        var plan = _planner.CreatePlan([owner, target]);

        Assert.Equal([target.Manifest.Id, owner.Manifest.Id], plan.OrderedCandidates.Select(static item => item.Manifest.Id));
        Assert.Empty(plan.Failures);
    }

    [Fact]
    public void CreatePlan_IgnoresIncompatibleIntegration()
    {
        var target = Candidate("z-target", "1.0.0");
        var owner = Candidate(
            "a-owner",
            "1.0.0",
            integrations: [new PluginIntegration(target.Manifest.Id, VersionRange.Of("[2.0.0,)"))]);

        var plan = _planner.CreatePlan([target, owner]);

        Assert.Equal([owner.Manifest.Id, target.Manifest.Id], plan.OrderedCandidates.Select(static item => item.Manifest.Id));
        Assert.Empty(plan.Failures);
    }

    [Fact]
    public void CreatePlan_BreaksIntegrationCyclesWithoutRejectingPlugins()
    {
        var firstId = PluginId.Of("first");
        var secondId = PluginId.Of("second");
        var first = Candidate(
            "first",
            "1.0.0",
            integrations: [new PluginIntegration(secondId, VersionRange.Of("[1.0.0,)"))]);
        var second = Candidate(
            "second",
            "1.0.0",
            integrations: [new PluginIntegration(firstId, VersionRange.Of("[1.0.0,)"))]);

        var plan = _planner.CreatePlan([second, first]);

        Assert.Equal([firstId, secondId], plan.OrderedCandidates.Select(static item => item.Manifest.Id));
        Assert.Empty(plan.Failures);
    }

    [Fact]
    public void Manifest_RejectsDuplicateStrongAndWeakRelationship()
    {
        var targetId = PluginId.Of("target");

        Assert.Throws<ArgumentException>(() => Candidate(
            "owner",
            "1.0.0",
            dependencies: [new PluginDependency(targetId, VersionRange.Of("[1.0.0,)"))],
            integrations: [new PluginIntegration(targetId, VersionRange.Of("[1.0.0,)"))]));
    }

    [Fact]
    public void CreatePlan_UsesIndependentProvidersEvenWhenAnAlternativeWouldFormACycle()
    {
        var consumer = Candidate("a-consumer", "1.0.0",
            capabilities: new(provides: ["consumer.read"], requires: ["source.read", "metadata.read"]));
        var cyclicProvider = Candidate("b-cyclic", "1.0.0",
            capabilities: new(provides: ["source.read"], requires: ["consumer.read"]));
        var source = Candidate("c-source", "1.0.0", capabilities: new(provides: ["source.read"]));
        var metadata = Candidate("d-metadata", "1.0.0", capabilities: new(provides: ["metadata.read"]));

        var plan = _planner.CreatePlan([consumer, cyclicProvider, source, metadata]);

        Assert.Equal([source, metadata, consumer, cyclicProvider], plan.OrderedCandidates);
        Assert.Empty(plan.Failures);
        Assert.Contains(_planner.CreatePlan([consumer, cyclicProvider, metadata]).Failures,
            failure => failure.PluginId == consumer.Manifest.Id);
    }

    [Fact]
    public void OrderAttempts_WaitsForAlternateProviderAfterFirstProviderFails()
    {
        var first = Candidate("a-first", "1.0.0", capabilities: new(provides: ["source.read"]));
        var consumer = Candidate("b-consumer", "1.0.0", capabilities: new(requires: ["source.read"]));
        var second = Candidate("c-second", "1.0.0", capabilities: new(provides: ["source.read"]));
        var successful = new HashSet<string>();
        using var attempts = PluginDependencyPlanner.OrderAttempts(
            [first, consumer, second], (PluginCandidate candidate) => candidate.Manifest, successful).GetEnumerator();

        Assert.True(attempts.MoveNext());
        Assert.Equal(first, attempts.Current);
        Assert.True(attempts.MoveNext());
        Assert.Equal(second, attempts.Current);
        successful.Add(second.Manifest.Id);
        Assert.True(attempts.MoveNext());
        Assert.Equal(consumer, attempts.Current);
    }

    [Fact]
    public void CreatePlan_ReportsMissingCapabilityButAllowsMissingIntegration()
    {
        var required = Candidate("required", "1.0.0", capabilities: new(requires: ["source.read"]));
        var optional = Candidate("optional", "1.0.0", capabilities: new(integrates: ["source.read"]));

        var plan = _planner.CreatePlan([required, optional]);

        Assert.Equal(optional, Assert.Single(plan.OrderedCandidates));
        Assert.Contains("source.read", Assert.Single(plan.Failures).Reason);
    }

    [Fact]
    public void CreatePlan_RejectsProvidersThatAssignDifferentKindsToTheSameName()
    {
        var rpc = Candidate("rpc", "1.0.0", capabilities: new(provides: ["example.suggest"]));
        var contributor = Candidate("publisher", "1.0.0", capabilities: new(provides: ["example.suggest"],
            notifications: new Dictionary<string, QuantumNotificationContract>
            {
                ["example.suggest"] = new(null, System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object" }))
            }));
        var consumer = Candidate("consumer", "1.0.0", capabilities: new(requires: ["example.suggest"]));
        var independent = Candidate("independent", "1.0.0");

        var plan = _planner.CreatePlan([rpc, contributor, consumer, independent]);

        Assert.Equal(independent, Assert.Single(plan.OrderedCandidates));
        Assert.Equal(3, plan.Failures.Count);
        Assert.Contains(plan.Failures, failure => failure.PluginId == rpc.Manifest.Id && failure.Reason.Contains("conflicting kinds"));
        Assert.Contains(plan.Failures, failure => failure.PluginId == contributor.Manifest.Id && failure.Reason.Contains("conflicting kinds"));
        Assert.Contains(plan.Failures, failure => failure.PluginId == consumer.Manifest.Id && failure.Reason.Contains("missing"));
    }

    private static PluginCandidate Candidate(
        string id,
        string version,
        IReadOnlyList<PluginDependency>? dependencies = null,
        IReadOnlyList<PluginIntegration>? integrations = null,
        PluginCapabilities? capabilities = null)
        => new(
            new PluginManifest(
                PluginId.Of(id),
                SemanticVersion.Of(version),
                $"{id}.dll",
                dependencies,
                integrations,
                capabilities: capabilities),
            Path.Combine("plugins", id));
}

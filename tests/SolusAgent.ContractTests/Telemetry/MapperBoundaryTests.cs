using System.Xml.Linq;
using SolusAgent.ContractTests.Architecture;
using SolusAgent.Telemetry;
using Xunit;

namespace SolusAgent.ContractTests.Telemetry;

public sealed class MapperBoundaryTests
{
    private static readonly string[] Allowed =
        [RepositoryLayout.ProductionProjectPath("SolusAgent.Api"), RepositoryLayout.ProductionProjectPath("Solus.Telemetry.Contracts")];

    [Fact]
    public void ActualMapperHasOnlyApiAndContractsDependenciesAndOwnManagedSources()
    {
        var path = RepositoryLayout.ProductionProjectPath("SolusAgent.Telemetry");
        var evaluated = MsbuildProjectEvaluation.Evaluate(path);
        ProjectBoundaryAssertions.AssertExactProjectReferences(evaluated, Allowed);
        ProjectBoundaryAssertions.AssertNoPackages(evaluated);
        ProjectBoundaryAssertions.AssertManagedNet10(evaluated);
        ProjectBoundaryAssertions.AssertCompileSourcesWithinRoot(evaluated, Path.GetDirectoryName(path)!);
        Assert.Equal("true", evaluated.PropertyValue("IsPackable"));
        Assert.Equal("0.1.0-internal.4", XDocument.Load(path).Descendants("SolusAgentApiBaseline").Single().Value);
        var references = typeof(AgentTelemetryMapper).Assembly.GetReferencedAssemblies().Select(r => r.Name!).ToArray();
        Assert.Contains("SolusAgent.Api", references); Assert.Contains("Solus.Telemetry.Contracts", references);
        Assert.All(references, name => Assert.True(name is "SolusAgent.Api" or "Solus.Telemetry.Contracts" || name.StartsWith("System", StringComparison.Ordinal), name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MapperBoundaryRejectsDirectAndImportedRuntimeEdgesUsingActualEvaluation(bool imported)
    {
        using var fixture = new SyntheticConsumerWorkspace(); fixture.AddDirectProjectReference(Allowed[1]);
        ProjectBoundaryAssertions.AssertExactProjectReferences(MsbuildProjectEvaluation.Evaluate(fixture.ConsumerProjectPath), Allowed);
        if (imported) fixture.AddImportedProjectReference(RepositoryLayout.ProductionProjectPath("SolusAgent.Runtime.Api"));
        else fixture.AddDirectProjectReference(RepositoryLayout.ProductionProjectPath("SolusAgent.Runtime"));
        var invalid = MsbuildProjectEvaluation.Evaluate(fixture.ConsumerProjectPath);
        var error = Assert.Throws<BoundaryViolationException>(() => ProjectBoundaryAssertions.AssertExactProjectReferences(invalid, Allowed));
        Assert.Contains(imported ? "SolusAgent.Runtime.Api.csproj" : "SolusAgent.Runtime.csproj", error.Message);
    }
}

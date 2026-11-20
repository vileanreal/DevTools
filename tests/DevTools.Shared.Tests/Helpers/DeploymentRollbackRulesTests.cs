using DevTools.Shared.Helpers;

namespace DevTools.Shared.Tests.Helpers;

public sealed class DeploymentRollbackRulesTests
{
    private const int WebApi = 1, Worker = 2;
    private static readonly IReadOnlyDictionary<int, string> Names = new Dictionary<int, string> { [WebApi] = "WebApi", [Worker] = "Worker" };
    private static readonly DateTime Start = new(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);

    private static RollbackDeployment Deployment(int id, int minutes, int[]? modules, bool inPlace = true, bool backup = true) =>
        new(id, $"package-{id}", Start.AddMinutes(minutes), inPlace, backup, modules, Names);

    private static IReadOnlyDictionary<int, RollbackAvailability> Evaluate(params RollbackDeployment[] deployments) =>
        DeploymentRollbackRules.Evaluate(deployments);

    [Fact]
    public void DeploymentsOfDifferentModules_CanBeRolledBackWithoutWarning()
    {
        var result = Evaluate(Deployment(1, 0, [WebApi]), Deployment(2, 10, [Worker]));

        Assert.Equal(new RollbackAvailability(true, null), result[1]);
        Assert.Equal(new RollbackAvailability(true, null), result[2]);
    }

    [Fact]
    public void OlderDeploymentOfTheSameModule_CanBeRolledBackWithWarning()
    {
        var result = Evaluate(Deployment(1, 0, [WebApi, Worker]), Deployment(2, 10, [WebApi]));

        Assert.True(result[1].CanRollback);
        Assert.Null(result[1].Reason);
        Assert.Equal("Newer deployments changed the same modules and their files will be overwritten: package-2 (changed WebApi).", result[1].Warning);
        Assert.Equal(new RollbackAvailability(true, null), result[2]);
    }

    [Fact]
    public void RolledBackNewerDeployment_DoesNotTriggerAWarning()
    {
        var result = Evaluate(Deployment(1, 0, [WebApi]), Deployment(2, 10, [WebApi], inPlace: false));

        Assert.Equal(new RollbackAvailability(true, null), result[1]);
        Assert.False(result[2].CanRollback);
    }

    [Fact]
    public void WarningListsEveryNewerOverlappingDeployment_OldestFirst()
    {
        var result = Evaluate(Deployment(1, 0, [WebApi, Worker]), Deployment(3, 20, [Worker]), Deployment(2, 10, [WebApi]));

        Assert.True(result[1].CanRollback);
        Assert.Equal("Newer deployments changed the same modules and their files will be overwritten: package-2 (changed WebApi); package-3 (changed Worker).", result[1].Warning);
    }

    [Fact]
    public void MissingBackup_CannotBeRolledBack()
    {
        var result = Evaluate(Deployment(1, 0, [WebApi], backup: false));

        Assert.False(result[1].CanRollback);
        Assert.Equal("The backup file for this deployment no longer exists.", result[1].Reason);
    }

    [Fact]
    public void NotInPlace_CannotBeRolledBack()
    {
        var result = Evaluate(Deployment(1, 0, [WebApi], inPlace: false));

        Assert.Equal(new RollbackAvailability(false, null), result[1]);
    }

    [Fact]
    public void UnknownModules_AreTreatedAsOverlapping()
    {
        var result = Evaluate(Deployment(1, 0, [WebApi]), Deployment(2, 10, null));

        Assert.True(result[1].CanRollback);
        Assert.Contains("modules that couldn't be determined", result[1].Warning);
    }
}

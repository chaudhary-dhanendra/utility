using System.Reflection;
using MigrationStudio.Application.Navigation;
using MigrationStudio.Domain.Operations;
using MigrationStudio.Infrastructure.Platform;

namespace MigrationStudio.Tests.Architecture;

public sealed class DependencyRulesTests
{
    [Fact]
    public void Domain_HasNoReferencesToOtherSolutionProjects()
    {
        var references = typeof(OperationId).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(
            references,
            reference => reference.Name?.StartsWith("MigrationStudio.", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Application_DoesNotReferenceDesktopOrInfrastructure()
    {
        var references = typeof(NavigationRoute).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(references, reference => reference.Name is "MigrationStudio.Desktop");
        Assert.DoesNotContain(references, reference => reference.Name is "MigrationStudio.Infrastructure");
    }

    [Fact]
    public void Infrastructure_DoesNotReferenceDesktop()
    {
        Assembly infrastructure = typeof(ApplicationPaths).Assembly;

        Assert.DoesNotContain(
            infrastructure.GetReferencedAssemblies(),
            reference => reference.Name is "MigrationStudio.Desktop");
    }
}

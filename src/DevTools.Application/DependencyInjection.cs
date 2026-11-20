using DevTools.Application.Services;
using DevTools.Application.Services.Deployment;
using DevTools.Application.Services.Patching;
using Microsoft.Extensions.DependencyInjection;

namespace DevTools.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddDevToolsApplication(this IServiceCollection services) => services
        .AddSingleton<IHashService, HashService>()
        .AddSingleton<IJsonFormatterService, JsonFormatterService>()
        .AddSingleton<IXmlFormatterService, XmlFormatterService>()
        .AddSingleton<ICodeFormatterService, CodeFormatterService>()
        .AddSingleton<ILogParserService, LogParserService>()
        .AddSingleton<IDatabaseSchemaDiffService, DatabaseSchemaDiffService>()
        .AddSingleton<ISystemServiceRuntime, LocalSystemServiceRuntime>()
        .AddSingleton<IPackageArchiveService, PackageArchiveService>()
        .AddSingleton<IDeploymentExecutor, DeploymentExecutor>()
        .AddSingleton<IPatchScanner, PatchScanner>();
}

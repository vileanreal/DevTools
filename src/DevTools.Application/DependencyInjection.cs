using DevTools.Application.Services;
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
        .AddSingleton<ISystemServiceRuntime, LocalSystemServiceRuntime>();
}

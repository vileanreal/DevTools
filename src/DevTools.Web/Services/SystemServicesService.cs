using DevTools.Application.Services;
using DevTools.Shared.Models;
using DevTools.Web.Data;
using DevTools.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DevTools.Web.Services;

public interface ISystemServicesService
{
    Task<IReadOnlyList<ServiceSystemDto>> ListAsync(CancellationToken cancellationToken);
    Task<ServiceSystemDto?> GetAsync(int systemId, bool refreshStatus, CancellationToken cancellationToken);
    Task<ServiceSystemDto> CreateSystemAsync(string name, CancellationToken cancellationToken);
    Task<ManagedServiceDto> AddServiceAsync(int systemId, string name, string kind, string targetName, CancellationToken cancellationToken);
    Task<ManagedServiceDto?> ExecuteAsync(int serviceId, string action, CancellationToken cancellationToken);
    Task<bool> DeleteSystemAsync(int systemId, CancellationToken cancellationToken);
    Task<bool> DeleteServiceAsync(int serviceId, CancellationToken cancellationToken);
}

public sealed class SystemServicesService(DevToolsDbContext database, ISystemServiceRuntime runtime) : ISystemServicesService
{
    public async Task<IReadOnlyList<ServiceSystemDto>> ListAsync(CancellationToken cancellationToken)
    {
        var systems = await database.ServiceSystems.AsNoTracking().Include(item => item.Services).OrderBy(item => item.Name).ToListAsync(cancellationToken);
        return systems.Select(item => ToDto(item, item.Services.Select(NotChecked).ToList())).ToList();
    }

    public async Task<ServiceSystemDto?> GetAsync(int systemId, bool refreshStatus, CancellationToken cancellationToken)
    {
        var system = await database.ServiceSystems.AsNoTracking().Include(item => item.Services).SingleOrDefaultAsync(item => item.Id == systemId, cancellationToken);
        if (system is null) return null;
        var orderedServices = system.Services.OrderBy(item => item.Name).ToList();
        var services = refreshStatus
            ? await ReadStatusesAsync(orderedServices, cancellationToken)
            : orderedServices.Select(NotChecked).ToArray();
        return ToDto(system, services);
    }

    public async Task<ServiceSystemDto> CreateSystemAsync(string name, CancellationToken cancellationToken)
    {
        var entity = new ServiceSystem { Name = name.Trim() };
        database.ServiceSystems.Add(entity);
        await database.SaveChangesAsync(cancellationToken);
        return new ServiceSystemDto(entity.Id, entity.Name, []);
    }

    public async Task<ManagedServiceDto> AddServiceAsync(int systemId, string name, string kind, string targetName, CancellationToken cancellationToken)
    {
        if (!await database.ServiceSystems.AnyAsync(item => item.Id == systemId, cancellationToken))
            throw new KeyNotFoundException("System not found.");

        var entity = new ManagedService
        {
            ServiceSystemId = systemId,
            Name = name.Trim(),
            Kind = NormalizeKind(kind),
            TargetName = targetName.Trim()
        };
        database.ManagedServices.Add(entity);
        await database.SaveChangesAsync(cancellationToken);
        return await WithStatusAsync(entity, cancellationToken);
    }

    public async Task<ManagedServiceDto?> ExecuteAsync(int serviceId, string action, CancellationToken cancellationToken)
    {
        var service = await database.ManagedServices.AsNoTracking().SingleOrDefaultAsync(item => item.Id == serviceId, cancellationToken);
        if (service is null) return null;
        var result = await runtime.ExecuteAsync(service.Kind, service.TargetName, action, cancellationToken);
        return ToDto(service, result);
    }

    public async Task<bool> DeleteSystemAsync(int systemId, CancellationToken cancellationToken) =>
        await database.ServiceSystems.Where(item => item.Id == systemId).ExecuteDeleteAsync(cancellationToken) > 0;

    public async Task<bool> DeleteServiceAsync(int serviceId, CancellationToken cancellationToken) =>
        await database.ManagedServices.Where(item => item.Id == serviceId).ExecuteDeleteAsync(cancellationToken) > 0;

    private async Task<ManagedServiceDto> WithStatusAsync(ManagedService service, CancellationToken cancellationToken)
    {
        var status = await runtime.GetStatusAsync(service.Kind, service.TargetName, cancellationToken);
        return ToDto(service, status);
    }

    private async Task<ManagedServiceDto[]> ReadStatusesAsync(IReadOnlyList<ManagedService> services, CancellationToken cancellationToken)
    {
        using var concurrency = new SemaphoreSlim(6, 6);
        return await Task.WhenAll(services.Select(async service =>
        {
            await concurrency.WaitAsync(cancellationToken);
            try { return await WithStatusAsync(service, cancellationToken); }
            finally { concurrency.Release(); }
        }));
    }

    private static ServiceSystemDto ToDto(ServiceSystem system, IReadOnlyList<ManagedServiceDto> services) =>
        new(system.Id, system.Name, services);

    private static ManagedServiceDto NotChecked(ManagedService service) =>
        new(service.Id, service.ServiceSystemId, service.Name, service.Kind, service.TargetName, "NotChecked", null, null);

    private static ManagedServiceDto ToDto(ManagedService service, ServiceRuntimeStatus status) =>
        new(service.Id, service.ServiceSystemId, service.Name, service.Kind, service.TargetName, status.Status, status.Detail, status.CheckedAt);

    private static string NormalizeKind(string kind) => kind.Trim().ToLowerInvariant() switch
    {
        "windows-service" => "windows-service",
        "app-pool" => "app-pool",
        _ => throw new ArgumentException("Choose Windows service or IIS application pool.", nameof(kind))
    };
}

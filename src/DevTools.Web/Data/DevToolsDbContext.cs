using DevTools.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DevTools.Web.Data;

public sealed class DevToolsDbContext(DbContextOptions<DevToolsDbContext> options) : DbContext(options)
{
    public DbSet<LogSystem> LogSystems => Set<LogSystem>();
    public DbSet<LogSource> LogSources => Set<LogSource>();
    public DbSet<SqlDocSource> SqlDocSources => Set<SqlDocSource>();
    public DbSet<SqlDocDiagram> SqlDocDiagrams => Set<SqlDocDiagram>();
    public DbSet<ServiceSystem> ServiceSystems => Set<ServiceSystem>();
    public DbSet<ManagedService> ManagedServices => Set<ManagedService>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LogSystem>(entity =>
        {
            entity.Property(item => item.Name).HasMaxLength(120).IsRequired();
            entity.HasIndex(item => item.Name).IsUnique();
            entity.HasMany(item => item.Sources).WithOne(item => item.LogSystem).HasForeignKey(item => item.LogSystemId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<LogSource>(entity =>
        {
            entity.Property(item => item.ModuleName).HasMaxLength(120).IsRequired();
            entity.Property(item => item.Path).HasMaxLength(2048).IsRequired();
            entity.Property(item => item.Format).HasMaxLength(20).HasDefaultValue("auto").IsRequired();
            entity.Property(item => item.CustomPattern).HasMaxLength(2000);
            entity.Property(item => item.TimestampFormat).HasMaxLength(100);
            entity.Property(item => item.IncludeSubdirectories).HasDefaultValue(true).IsRequired();
        });
        modelBuilder.Entity<SqlDocSource>(entity =>
        {
            entity.Property(item => item.Name).HasMaxLength(120).IsRequired();
            entity.Property(item => item.Provider).HasMaxLength(40).IsRequired();
            entity.Property(item => item.ConnectionString).HasMaxLength(4000).IsRequired();
            entity.HasIndex(item => item.Name).IsUnique();
            entity.HasMany(item => item.Diagrams).WithOne(item => item.SqlDocSource).HasForeignKey(item => item.SqlDocSourceId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<SqlDocDiagram>(entity =>
        {
            entity.Property(item => item.Name).HasMaxLength(160).IsRequired();
            entity.Property(item => item.DiagramJson).IsRequired();
            entity.HasIndex(item => new { item.SqlDocSourceId, item.Name }).IsUnique();
        });
        modelBuilder.Entity<ServiceSystem>(entity =>
        {
            entity.Property(item => item.Name).HasMaxLength(120).IsRequired();
            entity.HasIndex(item => item.Name).IsUnique();
            entity.HasMany(item => item.Services).WithOne(item => item.ServiceSystem).HasForeignKey(item => item.ServiceSystemId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<ManagedService>(entity =>
        {
            entity.Property(item => item.Name).HasMaxLength(120).IsRequired();
            entity.Property(item => item.Kind).HasMaxLength(30).IsRequired();
            entity.Property(item => item.TargetName).HasMaxLength(260).IsRequired();
            entity.HasIndex(item => new { item.ServiceSystemId, item.Name }).IsUnique();
        });
    }
}

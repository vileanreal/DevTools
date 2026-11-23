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
    public DbSet<DeploymentSystem> DeploymentSystems => Set<DeploymentSystem>();
    public DbSet<DeploymentModule> DeploymentModules => Set<DeploymentModule>();
    public DbSet<DeploymentPackage> DeploymentPackages => Set<DeploymentPackage>();
    public DbSet<DeploymentEvent> DeploymentEvents => Set<DeploymentEvent>();
    public DbSet<PatchSystem> PatchSystems => Set<PatchSystem>();
    public DbSet<PatchModule> PatchModules => Set<PatchModule>();
    public DbSet<PatchSnapshot> PatchSnapshots => Set<PatchSnapshot>();
    public DbSet<PatchPackage> PatchPackages => Set<PatchPackage>();

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
            entity.Property(item => item.FileNameFilter).HasMaxLength(500);
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
        modelBuilder.Entity<DeploymentSystem>(entity =>
        {
            entity.Property(item => item.Name).HasMaxLength(120).IsRequired();
            entity.Property(item => item.BackupDirectory).HasMaxLength(2048);
            entity.Property(item => item.PackageDirectory).HasMaxLength(2048);
            entity.Property(item => item.BackupFormat).HasMaxLength(8).IsRequired();
            entity.HasIndex(item => item.Name).IsUnique();
            entity.HasMany(item => item.Modules).WithOne(item => item.DeploymentSystem).HasForeignKey(item => item.DeploymentSystemId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(item => item.Packages).WithOne(item => item.DeploymentSystem).HasForeignKey(item => item.DeploymentSystemId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<DeploymentModule>(entity =>
        {
            entity.Property(item => item.Name).HasMaxLength(120).IsRequired();
            entity.Property(item => item.TargetPath).HasMaxLength(2048).IsRequired();
            entity.Property(item => item.AppPoolName).HasMaxLength(260);
            entity.Property(item => item.ServiceName).HasMaxLength(260);
            entity.Property(item => item.DefaultExclusions).HasMaxLength(4000);
            entity.Property(item => item.VersionFile).HasMaxLength(500);
            entity.HasIndex(item => new { item.DeploymentSystemId, item.Name }).IsUnique();
        });
        modelBuilder.Entity<DeploymentPackage>(entity =>
        {
            entity.Property(item => item.FileName).HasMaxLength(260).IsRequired();
            entity.Property(item => item.Description).HasMaxLength(500);
            entity.HasMany(item => item.Events).WithOne(item => item.DeploymentPackage).HasForeignKey(item => item.DeploymentPackageId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(item => item.StoredPath).HasMaxLength(2048).IsRequired();
            entity.Property(item => item.Status).HasMaxLength(30).IsRequired();
            entity.Property(item => item.AnalysisJson).IsRequired();
            entity.Property(item => item.BackupPath).HasMaxLength(2048);
            entity.Property(item => item.Error).HasMaxLength(2000);
            entity.HasIndex(item => new { item.DeploymentSystemId, item.UploadedAtUtc });
        });
        modelBuilder.Entity<PatchSystem>(entity =>
        {
            entity.Property(item => item.Name).HasMaxLength(120).IsRequired();
            entity.Property(item => item.OutputDirectory).HasMaxLength(2048);
            entity.HasIndex(item => item.Name).IsUnique();
            entity.HasMany(item => item.Modules).WithOne(item => item.PatchSystem).HasForeignKey(item => item.PatchSystemId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(item => item.Snapshots).WithOne(item => item.PatchSystem).HasForeignKey(item => item.PatchSystemId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(item => item.Packages).WithOne(item => item.PatchSystem).HasForeignKey(item => item.PatchSystemId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<PatchModule>(entity =>
        {
            entity.Property(item => item.Name).HasMaxLength(120).IsRequired();
            entity.Property(item => item.SourcePath).HasMaxLength(2048).IsRequired();
            entity.Property(item => item.Exclusions).HasMaxLength(4000);
            entity.HasIndex(item => new { item.PatchSystemId, item.Name }).IsUnique();
        });
        modelBuilder.Entity<PatchSnapshot>(entity =>
        {
            entity.Property(item => item.Source).HasMaxLength(20).IsRequired();
            entity.Property(item => item.Label).HasMaxLength(500);
            entity.Property(item => item.CreatedBy).HasMaxLength(120);
            entity.Property(item => item.ManifestGz).IsRequired();
            entity.HasIndex(item => new { item.PatchSystemId, item.CreatedAtUtc });
        });
        modelBuilder.Entity<PatchPackage>(entity =>
        {
            entity.Property(item => item.Format).HasMaxLength(10).IsRequired();
            entity.Property(item => item.FileName).HasMaxLength(260).IsRequired();
            entity.Property(item => item.StoredPath).HasMaxLength(2048).IsRequired();
            entity.Property(item => item.Label).HasMaxLength(500);
            entity.Property(item => item.CreatedBy).HasMaxLength(120);
            entity.HasIndex(item => new { item.PatchSystemId, item.CreatedAtUtc });
        });
        modelBuilder.Entity<DeploymentEvent>(entity =>
        {
            entity.Property(item => item.Kind).HasMaxLength(30).IsRequired();
            entity.Property(item => item.Detail).HasMaxLength(2000);
            entity.HasIndex(item => new { item.DeploymentPackageId, item.AtUtc });
        });
    }
}

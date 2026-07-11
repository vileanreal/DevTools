using DevTools.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DevTools.Web.Data;

public sealed class LogIndexDbContext(DbContextOptions<LogIndexDbContext> options) : DbContext(options)
{
    public DbSet<IndexedLogFile> IndexedLogFiles => Set<IndexedLogFile>();
    public DbSet<IndexedLogEntry> IndexedLogEntries => Set<IndexedLogEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IndexedLogFile>(entity =>
        {
            entity.Property(item => item.Path).HasMaxLength(2048).IsRequired();
            entity.Property(item => item.FileName).HasMaxLength(260).IsRequired();
            entity.Property(item => item.ParserVersion).HasDefaultValue(0);
            entity.Property(item => item.ContentHash).HasMaxLength(64);
            entity.Ignore(item => item.LogSource);
            entity.HasIndex(item => new { item.LogSourceId, item.Path }).IsUnique();
            entity.HasMany(item => item.Entries).WithOne(item => item.IndexedLogFile).HasForeignKey(item => item.IndexedLogFileId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<IndexedLogEntry>(entity =>
        {
            entity.Property(item => item.Level).HasMaxLength(16).IsRequired();
            entity.Property(item => item.TimestampSource).HasMaxLength(16).IsRequired();
            entity.Property(item => item.Message).IsRequired();
            entity.Ignore(item => item.LogSource);
            entity.HasIndex(item => new { item.LogSourceId, item.TimestampUtc });
            entity.HasIndex(item => new { item.LogSourceId, item.Level, item.TimestampUtc });
        });
    }
}

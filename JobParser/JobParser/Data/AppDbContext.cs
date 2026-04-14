using Microsoft.EntityFrameworkCore;
using JobParser.Models;

namespace JobParser.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options)
            : base(options)
        {
        }
        public DbSet<ProcessedLead> ProcessedLeads { get; set; }
        public DbSet<ParserProgress> ParserProgress { get; set; }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ProcessedLead>(entity =>
            {
                entity.ToTable("processed_leads");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.Url).IsUnique();
                entity.HasIndex(e => new { e.Source, e.ProcessedAt });
                entity.Property(e => e.Url).IsRequired().HasMaxLength(500);
                entity.Property(e => e.Source).IsRequired().HasMaxLength(50);
                entity.Property(e => e.ProcessedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            });

            modelBuilder.Entity<ParserProgress>(entity =>
            {
                entity.ToTable("parser_progress");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.Source).IsUnique();
                entity.Property(e => e.Source).IsRequired().HasMaxLength(50);
                entity.Property(e => e.LastProcessedPage).IsRequired();
                entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            });
        }
    }
}
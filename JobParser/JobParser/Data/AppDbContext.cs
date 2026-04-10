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

        public DbSet<PhoneRecord> PhoneNumbers { get; set; } 

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<PhoneRecord>(entity =>
            {
                entity.ToTable("phone_numbers");

                entity.HasIndex(e => e.Number)
                      .IsUnique();

                entity.Property(e => e.Number)
                      .IsRequired()
                      .HasMaxLength(20);

                entity.Property(e => e.CreatedAt)
                      .HasDefaultValueSql("CURRENT_TIMESTAMP");
            });
        }
    }
}
using Microsoft.EntityFrameworkCore;
using PUSL2020.Api.Models;

namespace PUSL2020.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ResearchArea> ResearchAreas => Set<ResearchArea>();
    public DbSet<SupervisorResearchArea> SupervisorResearchAreas => Set<SupervisorResearchArea>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Username)
            .IsUnique();

        modelBuilder.Entity<SupervisorResearchArea>()
            .HasKey(sra => new { sra.UserId, sra.ResearchAreaId });

        modelBuilder.Entity<SupervisorResearchArea>()
            .HasOne(sra => sra.Supervisor)
            .WithMany(u => u.SupervisorResearchAreas)
            .HasForeignKey(sra => sra.UserId);

        modelBuilder.Entity<SupervisorResearchArea>()
            .HasOne(sra => sra.ResearchArea)
            .WithMany(r => r.SupervisorResearchAreas)
            .HasForeignKey(sra => sra.ResearchAreaId);

        modelBuilder.Entity<Project>()
            .HasOne(p => p.ResearchArea)
            .WithMany(r => r.Projects)
            .HasForeignKey(p => p.ResearchAreaId);

        modelBuilder.Entity<Project>()
            .HasOne(p => p.Student)
            .WithMany(u => u.Projects)
            .HasForeignKey(p => p.StudentId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Project>()
            .HasOne(p => p.Supervisor)
            .WithMany()
            .HasForeignKey(p => p.SupervisorId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

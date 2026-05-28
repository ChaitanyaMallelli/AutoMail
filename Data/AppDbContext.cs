using Microsoft.EntityFrameworkCore;
using JobAutomation.Models;

namespace JobAutomation.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<JobPost> JobPosts => Set<JobPost>();
    public DbSet<GeneratedEmail> GeneratedEmails => Set<GeneratedEmail>();
    public DbSet<Resume> Resumes => Set<Resume>();
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // JobPost configuration
        modelBuilder.Entity<JobPost>(entity =>
        {
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.CompanyName);
            entity.Property(e => e.Status)
                  .HasConversion<string>();
            entity.Property(e => e.Source)
                  .HasConversion<string>();
            entity.Property(e => e.SourceType)
                  .HasConversion<string>();
        });

        // GeneratedEmail configuration
        modelBuilder.Entity<GeneratedEmail>(entity =>
        {
            entity.HasOne(e => e.JobPost)
                  .WithOne(j => j.GeneratedEmail)
                  .HasForeignKey<GeneratedEmail>(e => e.JobPostId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.IsSent);
        });

        // Resume configuration
        modelBuilder.Entity<Resume>(entity =>
        {
            entity.HasIndex(e => e.IsActive);
        });

        // Seed default user profile
        modelBuilder.Entity<UserProfile>().HasData(new UserProfile
        {
            Id = 1,
            FullName = "Chaitanya Mallelli",
            Email = "MallelliChaitanya5@gmail.com",
            Phone = "+91 9390981596",
            LinkedInUrl = "https://www.linkedin.com/in/chaitanya-mallelli-7a9b76204/"
        });
    }
}

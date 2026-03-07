using Microsoft.EntityFrameworkCore;
using BijliPoint.Models;

namespace BijliPoint.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        public DbSet<User> Users { get; set; }
        public DbSet<Station> Stations { get; set; }
        public DbSet<ChargingSession> ChargingSessions { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Seed Super Admin
            modelBuilder.Entity<User>().HasData(
                new User
                {
                    Id = 1,
                    Name = "Super Admin",
                    Email = "admin@bijlipoint.com",
                    Phone = "+923001234567",
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword("admin123"),
                    Role = "SuperAdmin",
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                }
            );
        }
    }
}

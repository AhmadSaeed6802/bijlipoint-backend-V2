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
        public DbSet<PasswordResetToken> PasswordResetTokens { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // NO SEED DATA - Clean start
            // Create SuperAdmin manually when needed via SQL or endpoint
        }
    }
}
using Microsoft.EntityFrameworkCore;
using CsgoPredictionSystem.Models; 

namespace CsgoPredictionSystem.Data
{
    public class DotaDbContext : DbContext
    {
        public DotaDbContext(DbContextOptions<DotaDbContext> options) : base(options)
        {
        }

        public DbSet<Role> Roles { get; set; }
        public DbSet<User> Users { get; set; }
        public DbSet<Team> Teams { get; set; }
        public DbSet<Player> Players { get; set; }
        public DbSet<Tournament> Tournaments { get; set; }
        public DbSet<Hero> Heroes { get; set; }
        public DbSet<HeroRoleDefinition> HeroRoleDefinitions { get; set; }
        public DbSet<HeroRole> HeroRoles { get; set; }
        public DbSet<Match> Matches { get; set; }
        public DbSet<MatchTeam> MatchTeams { get; set; }
        public DbSet<PlayerMatchStats> PlayerMatchStats { get; set; }
        public DbSet<MlTrainingHistory> MlTrainingHistory { get; set; }
        
        public DbSet<ApiSyncStatus> ApiSyncStatus { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Role>().HasIndex(r => r.RoleName).IsUnique();
            modelBuilder.Entity<User>().HasIndex(u => u.Username).IsUnique();
            modelBuilder.Entity<User>().HasIndex(u => u.Email).IsUnique();

            modelBuilder.Entity<Team>()
                .HasIndex(t => t.ExternalId)
                .IsUnique();
            
            modelBuilder.Entity<MatchTeam>().HasKey(mt => new { mt.MatchId, mt.TeamId });
            modelBuilder.Entity<HeroRole>().HasKey(hr => new { hr.HeroId, hr.RoleId });
            modelBuilder.Entity<MlTrainingHistory>()
                .HasOne(m => m.User)
                .WithMany()
                .HasForeignKey(m => m.UserId)
                .OnDelete(DeleteBehavior.SetNull);
            
            foreach (var entity in modelBuilder.Model.GetEntityTypes())
            {
                var tableName = entity.GetTableName();
                entity.SetTableName(ToSnakeCase(tableName));

                foreach (var property in entity.GetProperties())
                {
                    property.SetColumnName(ToSnakeCase(property.Name));
                }

                foreach (var key in entity.GetKeys())
                    key.SetName(ToSnakeCase(key.GetName()));

                foreach (var fk in entity.GetForeignKeys())
                    fk.SetConstraintName(ToSnakeCase(fk.GetConstraintName()));

                foreach (var index in entity.GetIndexes())
                    index.SetDatabaseName(ToSnakeCase(index.GetDatabaseName()));
            }

            base.OnModelCreating(modelBuilder);
        }

        private string ToSnakeCase(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (text.StartsWith("PK_") || text.StartsWith("FK_")) return text.ToLower();

            return string.Concat(text.Select((x, i) => i > 0 && char.IsUpper(x) ? "_" + x.ToString() : x.ToString())).ToLower();
        }
    }
}
using KeepWalletAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace KeepWalletAPI.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<BankAccount> BankAccounts => Set<BankAccount>();
    public DbSet<Saving> Savings => Set<Saving>();
    public DbSet<SavingItem> SavingItems => Set<SavingItem>();
    public DbSet<ScheduledPayment> ScheduledPayments => Set<ScheduledPayment>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<PopularCategoryLast30Days> PopularCategoriesLast30Days => Set<PopularCategoryLast30Days>();
    public DbSet<UserCategoryPreference> UserCategoryPreferences => Set<UserCategoryPreference>();
    public DbSet<Budget> Budgets => Set<Budget>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("pgcrypto");
        modelBuilder.HasPostgresEnum<UserRole>("user_role");
        modelBuilder.HasPostgresEnum<UserGroupRole>("user_group_role");
        modelBuilder.HasPostgresEnum<CategoryType>("category_type");

        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id)
                .HasColumnName("id")
                .HasDefaultValueSql("gen_random_uuid()");
            entity.Property(x => x.Role).HasColumnName("role").HasColumnType("user_role").HasDefaultValue(UserRole.User);
            entity.Property(x => x.Username).HasColumnName("username").HasMaxLength(45).IsRequired();
            entity.Property(x => x.Email).HasColumnName("email").HasMaxLength(45).IsRequired();
            entity.Property(x => x.PasswordHash).HasColumnName("password").HasMaxLength(255).IsRequired();
            entity.Property(x => x.FullName).HasColumnName("full_name").HasMaxLength(45);
            entity.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);
            entity.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("NOW()");

            entity.HasIndex(x => x.Username).IsUnique();
            entity.HasIndex(x => x.Email).HasDatabaseName("users_email_key").IsUnique();
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.ToTable("refresh_tokens");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id)
                .HasColumnName("id")
                .HasDefaultValueSql("gen_random_uuid()");
            entity.Property(x => x.UserId).HasColumnName("user_id");
            entity.Property(x => x.TokenHash).HasColumnName("token_hash").HasMaxLength(128).IsRequired();
            entity.Property(x => x.JwtId).HasColumnName("jwt_id");
            entity.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            entity.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            entity.Property(x => x.ReplacedByTokenId).HasColumnName("replaced_by_token_id");
            entity.Property(x => x.CreatedByIp).HasColumnName("created_by_ip").HasMaxLength(64);
            entity.Property(x => x.RevokedByIp).HasColumnName("revoked_by_ip").HasMaxLength(64);
            entity.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("NOW()");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("NOW()");

            entity.HasOne(x => x.User)
                .WithMany(x => x.RefreshTokens)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(x => x.UserId).HasDatabaseName("idx_refresh_tokens_user_id");
            entity.HasIndex(x => x.TokenHash).HasDatabaseName("ux_refresh_tokens_token_hash").IsUnique();
            entity.HasIndex(x => x.ExpiresAt).HasDatabaseName("idx_refresh_tokens_expires_at");
            entity.HasIndex(x => x.JwtId).HasDatabaseName("idx_refresh_tokens_jwt_id");
        });

        modelBuilder.Entity<Transaction>(entity =>
        {
            entity.ToTable("transactions");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.AccountId).HasColumnName("account_id");
            entity.Property(x => x.GroupId).HasColumnName("group_id");
            entity.Property(x => x.CategoryId).HasColumnName("category_id");
            entity.Property(x => x.SavingId).HasColumnName("saving_id");
            entity.Property(x => x.RecurringPaymentId).HasColumnName("recurring_payments_id");
            entity.Property(x => x.Amount).HasColumnName("amount").HasColumnType("numeric(15,2)");
            entity.Property(x => x.Description).HasColumnName("description").HasMaxLength(500);
            entity.Property(x => x.TransactionDate).HasColumnName("transaction_date");

            entity.HasOne(x => x.Account)
                .WithMany()
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.Group)
                .WithMany()
                .HasForeignKey(x => x.GroupId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(x => x.Saving)
                .WithMany()
                .HasForeignKey(x => x.SavingId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(x => new { x.AccountId, x.TransactionDate }).HasDatabaseName("idx_transactions_account_date");
            entity.HasIndex(x => new { x.AccountId, x.CategoryId }).HasDatabaseName("idx_transactions_account_type");
            entity.HasIndex(x => x.SavingId).HasDatabaseName("idx_transactions_saving_id");
        });

        modelBuilder.Entity<BankAccount>(entity =>
        {
            entity.ToTable("accounts");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id)
                .HasColumnName("id")
                .HasDefaultValueSql("gen_random_uuid()");
            entity.Property(x => x.UserId).HasColumnName("user_id");
            entity.Property(x => x.GroupId).HasColumnName("group_id");
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
            entity.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
            entity.Property(x => x.Balance).HasColumnName("balance").HasColumnType("numeric(15,2)");
            entity.Property(x => x.IsDefault).HasColumnName("is_default").HasDefaultValue(false);
            entity.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("NOW()");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("NOW()");

            entity.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.Group)
                .WithMany()
                .HasForeignKey(x => x.GroupId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(x => x.UserId).HasDatabaseName("idx_accounts_user_id");
        });

        modelBuilder.Entity<Saving>(entity =>
        {
            entity.ToTable("savings");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.UserId).HasColumnName("user_id");
            entity.Property(x => x.GroupId).HasColumnName("group_id");
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            entity.Property(x => x.TargetAmount).HasColumnName("target_amount").HasColumnType("numeric(15,2)");
            entity.Property(x => x.CurrentAmount).HasColumnName("current_amount").HasColumnType("numeric(15,2)");
            entity.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3).HasDefaultValue("UAH").IsRequired();
            entity.Property(x => x.IconKey).HasColumnName("icon_key").HasMaxLength(50);
            entity.Property(x => x.Color).HasColumnName("color").HasMaxLength(10);
            entity.Property(x => x.Deadline).HasColumnName("deadline");
            entity.Property(x => x.IsCompleted).HasColumnName("is_completed").HasDefaultValue(false);
            entity.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("NOW()");

            entity.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(x => x.UserId).HasDatabaseName("idx_savings_user_id");
            entity.HasIndex(x => new { x.UserId, x.IsCompleted }).HasDatabaseName("idx_savings_user_completed");
        });

        modelBuilder.Entity<SavingItem>(entity =>
        {
            entity.ToTable("wish_list");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.SavingId).HasColumnName("saving_id");
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(255).IsRequired();
            entity.Property(x => x.Price).HasColumnName("price").HasColumnType("numeric(15,2)");
            entity.Property(x => x.Priority).HasColumnName("priority");
            entity.Property(x => x.IsPurchased).HasColumnName("is_purchased").HasDefaultValue(false);

            entity.HasOne(x => x.Saving)
                .WithMany()
                .HasForeignKey(x => x.SavingId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(x => x.SavingId).HasDatabaseName("idx_wish_list_savings");
        });

        modelBuilder.Entity<ScheduledPayment>(entity =>
        {
            entity.ToTable("recurring_payments");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            entity.Property(x => x.RepeatInterval).HasColumnName("repeat_interval");
            entity.Property(x => x.NextDueDate).HasColumnName("next_due_date");
            entity.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        });

        modelBuilder.Entity<Category>(entity =>
        {
            entity.ToTable("categories");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
            entity.Property(x => x.Type).HasColumnName("type").HasColumnType("category_type");
            entity.Property(x => x.IconKey).HasColumnName("icon_key").HasMaxLength(50).HasDefaultValue("other");
            entity.Property(x => x.Color).HasColumnName("color").HasMaxLength(10);
        });

        modelBuilder.Entity<PopularCategoryLast30Days>(entity =>
        {
            entity.HasNoKey();
            entity.ToView("popular_categories_last_30_days");

            entity.Property(x => x.UserId).HasColumnName("user_id");
            entity.Property(x => x.CategoryId).HasColumnName("category_id");
            entity.Property(x => x.CategoryName).HasColumnName("category_name");
            entity.Property(x => x.CategoryType).HasColumnName("category_type").HasColumnType("category_type");
            entity.Property(x => x.TransactionsCount).HasColumnName("transactions_count");
            entity.Property(x => x.TotalAmount).HasColumnName("total_amount").HasColumnType("numeric(15,2)");
        });

        modelBuilder.Entity<UserCategoryPreference>(entity =>
        {
            entity.ToTable("user_category_preferences");
            entity.HasKey(x => new { x.UserId, x.CategoryId });

            entity.Property(x => x.UserId).HasColumnName("user_id");
            entity.Property(x => x.CategoryId).HasColumnName("category_id");

            entity.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.Category)
                .WithMany()
                .HasForeignKey(x => x.CategoryId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(x => x.UserId).HasDatabaseName("idx_user_category_preferences_user_id");
        });

        modelBuilder.Entity<Budget>(entity =>
        {
            entity.ToTable("budgets");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.UserId).HasColumnName("user_id");
            entity.Property(x => x.GroupId).HasColumnName("group_id");
            entity.Property(x => x.CategoryId).HasColumnName("category_id");
            entity.Property(x => x.Amount).HasColumnName("amount").HasColumnType("numeric(15,2)");
            entity.Property(x => x.BudgetPeriod).HasColumnName("budget_period");
            entity.Property(x => x.StartDate).HasColumnName("start_date").HasDefaultValueSql("CURRENT_DATE");
            entity.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);

            entity.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.Group)
                .WithMany()
                .HasForeignKey(x => x.GroupId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(x => x.Category)
                .WithMany()
                .HasForeignKey(x => x.CategoryId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(x => x.UserId).HasDatabaseName("idx_budgets_user_id");
            entity.HasIndex(x => new { x.UserId, x.CategoryId, x.IsActive }).HasDatabaseName("idx_budgets_user_category_active");
        });

        modelBuilder.Entity<Group>(entity =>
        {
            entity.ToTable("groups");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
            entity.Property(x => x.IconKey).HasColumnName("icon_key").HasMaxLength(50).HasDefaultValue("other");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("NOW()");
        });

        modelBuilder.Entity<GroupMember>(entity =>
        {
            entity.ToTable("group_members");
            entity.HasKey(x => new { x.GroupId, x.UserId });

            entity.Property(x => x.GroupId).HasColumnName("group_id");
            entity.Property(x => x.UserId).HasColumnName("user_id");
            entity.Property(x => x.Role).HasColumnName("role").HasColumnType("user_group_role").HasDefaultValue(UserGroupRole.Owner);
            entity.Property(x => x.JoinedAt).HasColumnName("joined_at").HasDefaultValueSql("NOW()");

            entity.HasOne(x => x.Group)
                .WithMany()
                .HasForeignKey(x => x.GroupId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(x => x.UserId).HasDatabaseName("idx_group_members_user_id");
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.ToTable("logs");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.UserId).HasColumnName("user_id");
            entity.Property(x => x.Action).HasColumnName("action").HasMaxLength(100).IsRequired();
            entity.Property(x => x.EntityType).HasColumnName("entity_type").HasMaxLength(100);
            entity.Property(x => x.Details).HasColumnName("details").HasColumnType("jsonb");
            entity.Property(x => x.Device).HasColumnName("device").HasMaxLength(45);
            entity.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("NOW()");

            entity.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(x => x.UserId).HasDatabaseName("idx_logs_user_id");
            entity.HasIndex(x => x.CreatedAt).HasDatabaseName("idx_logs_created_at");
            entity.HasIndex(x => x.Action).HasDatabaseName("idx_logs_action");
        });
    }
}

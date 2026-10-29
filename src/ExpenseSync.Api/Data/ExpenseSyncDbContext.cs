using ExpenseSync.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace ExpenseSync.Api.Data;

public class ExpenseSyncDbContext(DbContextOptions<ExpenseSyncDbContext> options) : DbContext(options)
{
    public DbSet<Expense> Expenses => Set<Expense>();

    public DbSet<ReceivedChange> ReceivedChanges => Set<ReceivedChange>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Expense>(expense =>
        {
            expense.HasKey(e => e.ExpenseId);
            expense.Property(e => e.ExpenseId).ValueGeneratedNever();

            expense.Property(e => e.Description)
                .IsRequired()
                .HasMaxLength(200);

            expense.Property(e => e.LastDeviceId)
                .IsRequired()
                .HasMaxLength(100);

            expense.Property(e => e.LastModifiedUtc)
                .IsRequired();
        });

        modelBuilder.Entity<ReceivedChange>(change =>
        {
            change.HasKey(c => c.PendingChangeId);
            change.Property(c => c.PendingChangeId).ValueGeneratedNever();

            change.Property(c => c.DeviceId)
                .IsRequired()
                .HasMaxLength(100);

            change.Property(c => c.Description)
                .HasMaxLength(200);

            change.HasIndex(c => c.IsProcessed);
            change.HasIndex(c => new { c.IsProcessed, c.CapturedAtUtc });
        });
    }
}

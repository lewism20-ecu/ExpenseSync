using ExpenseSync.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace ExpenseSync.Api.Data;

public class ExpenseSyncDbContext(DbContextOptions<ExpenseSyncDbContext> options) : DbContext(options)
{
    public DbSet<Expense> Expenses => Set<Expense>();

    public DbSet<ReceivedChange> ReceivedChanges => Set<ReceivedChange>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

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

            expense.Property(e => e.ServerUpdatedUtc)
                .IsRequired();
        });

        modelBuilder.Entity<ReceivedChange>(change =>
        {
            change.HasKey(c => c.PendingChangeId);
            change.Property(c => c.PendingChangeId).ValueGeneratedNever();

            change.Property(c => c.DeviceId)
                .IsRequired()
                .HasMaxLength(100);

            // Store Create/Update/Delete as text so the table is readable, matching the device's PendingChanges.
            change.Property(c => c.Operation)
                .HasConversion<string>()
                .HasMaxLength(10);

            change.Property(c => c.Description)
                .HasMaxLength(200);

            change.HasIndex(c => c.IsProcessed);
            change.HasIndex(c => new { c.IsProcessed, c.CapturedAtUtc });
        });

        modelBuilder.Entity<AuditEntry>(auditEntry =>
        {
            auditEntry.HasKey(a => a.AuditEntryId);
            auditEntry.Property(a => a.AuditEntryId).ValueGeneratedNever();

            auditEntry.Property(a => a.Operation)
                .HasConversion<string>()
                .HasMaxLength(10);

            auditEntry.Property(a => a.DeviceId)
                .IsRequired()
                .HasMaxLength(100);

            auditEntry.Property(a => a.UserId)
                .IsRequired()
                .HasMaxLength(100);

            auditEntry.Property(a => a.Details)
                .IsRequired();

            auditEntry.Property(a => a.OccurredAtUtc)
                .IsRequired();

            auditEntry.Property(a => a.ReceivedAtUtc)
                .IsRequired();
        });
    }
}

using ExpenseSync.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace ExpenseSync.Api.Data;

public class ExpenseSyncDbContext(DbContextOptions<ExpenseSyncDbContext> options) : DbContext(options)
{
    public DbSet<Expense> Expenses => Set<Expense>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Expense>(expense =>
        {
            expense.HasKey(e => e.ExpenseId);
            expense.Property(e => e.ExpenseId).ValueGeneratedNever();

            expense.Property(e => e.Description)
                .IsRequired()
                .HasMaxLength(200);
        });
    }
}

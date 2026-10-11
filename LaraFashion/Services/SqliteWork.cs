using LaraFashion.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LaraFashion.Services;

internal static class SqliteWork
{
    public static async Task<T> WriteAsync<T>(IDbContextFactory<AppDbContext> factory, Func<AppDbContext, Task<T>> action)
    {
        await using var db = await factory.CreateDbContextAsync();
        try
        {
            // Microsoft.Data.Sqlite uses BEGIN IMMEDIATE (non-deferred) here.
            // Acquire SQLite's writer lock before reading; never use tracked circuit snapshots.
            await using var transaction = await db.Database.BeginTransactionAsync();
            var result = await action(db);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return result;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            throw new InventoryException(InventoryFailureReason.ConcurrentChange, "البيانات مشغولة بتحديث آخر. حاول مجدداً بعد لحظات.");
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new InventoryException(InventoryFailureReason.ConcurrentChange, "تغيرت البيانات أثناء الحفظ. حدّث الصفحة وحاول مجدداً.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            throw new InvalidOperationException("تعذر الحفظ بسبب بيانات مكررة أو علاقة لم تعد متاحة. راجع الإدخال ثم احفظ مجدداً.");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException("تعذر الحفظ بسبب قيد حماية البيانات. لم تُحفظ تغييرات العملية.");
        }
    }
}

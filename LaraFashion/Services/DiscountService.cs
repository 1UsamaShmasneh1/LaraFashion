using LaraFashion.Data;
using LaraFashion.Models;
using Microsoft.EntityFrameworkCore;

namespace LaraFashion.Services;

public class DiscountService
{
    private readonly AppDbContext _db;

    public DiscountService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<Discount>> GetAllAsync()
    {
        return await _db.Discounts
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync();
    }

    public async Task<List<Discount>> GetActiveAsync()
    {
        return await _db.Discounts
            .Where(x => x.IsActive)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync();
    }

    public async Task AddAsync(Discount discount)
    {
        discount.Id = Guid.NewGuid();
        discount.CreatedAt = DateTime.Now;

        _db.Discounts.Add(discount);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateAsync(Discount discount)
    {
        var existing = await _db.Discounts
            .FirstOrDefaultAsync(x => x.Id == discount.Id);

        if (existing is null)
            return;

        existing.Name = discount.Name;
        existing.RuleType = discount.RuleType;
        existing.ValueType = discount.ValueType;
        existing.Value = discount.Value;
        existing.MinimumQuantity = discount.MinimumQuantity;
        existing.MinimumAmount = discount.MinimumAmount;
        existing.IsActive = discount.IsActive;
        existing.BundleFixedTotalPrice = discount.BundleFixedTotalPrice;

        await _db.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        var discount = await _db.Discounts
            .Include(x => x.ProductDiscounts)
            .FirstOrDefaultAsync(x => x.Id == id);

        if (discount is null)
            return;

        _db.ProductDiscounts.RemoveRange(discount.ProductDiscounts);
        _db.Discounts.Remove(discount);

        await _db.SaveChangesAsync();
    }
}
using LaraFashion.Data;
using LaraFashion.Models;
using Microsoft.EntityFrameworkCore;

namespace LaraFashion.Services;

public class CategoryService
{
    private readonly AppDbContext _db;

    public CategoryService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<Category>> GetAllAsync()
    {
        return await _db.Categories
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ToListAsync();
    }

    public async Task<List<Category>> GetActiveAsync()
    {
        return await _db.Categories
            .AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.Name)
            .ToListAsync();
    }

    public async Task AddAsync(Category category)
    {
        category.Id = Guid.NewGuid();
        category.CreatedAt = DateTime.Now;
        category.UpdatedAt = DateTime.Now;
        category.Name = category.Name.Trim();

        if (string.IsNullOrWhiteSpace(category.Name))
            throw new InvalidOperationException("اسم التصنيف مطلوب.");

        _db.Categories.Add(category);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateAsync(Category category)
    {
        var existing = await _db.Categories.FirstOrDefaultAsync(x => x.Id == category.Id);
        if (existing is null)
            return;

        existing.Name = category.Name.Trim();
        existing.IsActive = category.IsActive;
        existing.UpdatedAt = DateTime.Now;

        if (string.IsNullOrWhiteSpace(existing.Name))
            throw new InvalidOperationException("اسم التصنيف مطلوب.");

        await _db.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        var category = await _db.Categories
            .Include(x => x.ProductCategories)
            .FirstOrDefaultAsync(x => x.Id == id);

        if (category is null)
            return;

        _db.ProductCategories.RemoveRange(category.ProductCategories);
        _db.Categories.Remove(category);

        await _db.SaveChangesAsync();
    }
}

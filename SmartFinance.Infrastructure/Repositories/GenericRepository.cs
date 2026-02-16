using SmartFinance.Application.Interfaces;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using SmartFinance.Infrastructure.Context;


namespace SmartFinance.Infrastructure.Repositories;

public class GenericRepository<T> : IGenericRepository<T> where T : class{
    private readonly SmartFinanceDbContext _context;

    public GenericRepository(SmartFinanceDbContext context)
    {
        _context=context;
    }
    public async Task<T> GetByIdAsync(int id)
    {
        return await _context.Set<T>().FindAsync(id);
    }
    public async Task<IEnumerable<T>> GetAllAsync()
    {
        return await _context.Set<T>().ToListAsync();
    }
    public async Task<IEnumerable<T>> FindAsync(Expression<Func<T,bool>> predicate)
    {
        return await _context.Set<T>().Where(predicate).ToListAsync();
    }
    public async Task AddAsync(T entity)
    {
        await _context.Set<T>().AddAsync(entity);
    }
    public void Update(T entity)
    {
        _context.Set<T>().Update(entity);
    }
    public void Delete(T entity)
    {
        _context.Set<T>().Remove(entity);
    }

}
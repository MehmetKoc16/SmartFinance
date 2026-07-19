using System.Linq.Expressions;

namespace SmartFinance.Application.Interfaces;

public interface IGenericRepository<T> where T : class
{
    Task<T> GetByIdAsync(int id);
    Task<IEnumerable<T>> GetAllAsync();
    Task<IEnumerable<T>> FindAsync(Expression<Func<T,bool>> predicate);
    Task AddAsync(T entity);
    void Update(T entity);
    void Delete(T entity);

    // GetAllAsync() tum tabloyu (tum kullanicilarin kayitlarini) belleğe cekip
    // filtrelemeyi LINQ-to-Objects ile yapiyor - kullanici/kayit sayisi arttikca
    // her istek butun tabloyu okur. Query(), henuz calistirilmamis bir IQueryable
    // doner; cagiran taraf .Where(...)/.Skip()/.Take()/.CountAsync() ekleyip
    // filtrenin SQL'e itilmesini saglayabilir.
    IQueryable<T> Query();
}
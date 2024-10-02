using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using RelationCommandCacheRepro.ReproNamespace1;
using RelationCommandCacheRepro.ReproNamespace2;
using System.Collections;
using System.Reflection;
using System.Reflection.Emit;

Console.WriteLine("Hello, World!");

var services = new ServiceCollection()
    .AddMemoryCache()
    .AddScoped<ReproContext>()
    .BuildServiceProvider();

var dbContext = services.GetRequiredService<ReproContext>();
dbContext.Repros.Add(new ReproEntity { Id = Guid.NewGuid(), Name = "Test" });
dbContext.SaveChanges();

// Arrange
var case1s = new int[] { 1, 2, 3, 4, 5 }.Select(x => new ReproObject1() { Id = Guid.NewGuid() }).ToList();
IEnumerable<Guid> case1 = case1s.Select(x => x.Id); // Intentional ommit ToList() to reproduce the issue, many people 'forget' to ToList()
ReproObject2UnitOfWork case2 = new ReproObject2UnitOfWork(); // Say this was a unit of work helper object containing references to all the work we want to do

// Act
var query = dbContext.Repros
                .Where(x => case1.Contains(x.Id))
                .Select(x => new ReproEntityWrapper(case2, x))
                .ToList();

// Assert
var memoryCache = services.GetRequiredService<IMemoryCache>();
var case1Bug = MemoryLeaks.CheckMemoryCacheForRelationalCommandMemoryLeak(memoryCache, "ReproNamespace1");
var case2Bug = MemoryLeaks.CheckMemoryCacheForRelationalCommandMemoryLeak(memoryCache, "ReproNamespace2");

if (case1Bug)
{
    Console.WriteLine("case1Bug: sneeky one! How did an IEnumerable<Guid> bind up ReproObject1's? These could be entites btw, chaining DbContext to memory");
}

if (case2Bug)
{
    Console.WriteLine("case2Bug: our unit of work object containing references to untold amounts of objects via chains to other objects is holding everything in memory");
}



public class ReproContext(IMemoryCache memoryCache) : DbContext
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder
            .UseSqlServer("Server=localhost;Database=Repro;Trusted_Connection=True;Encrypt=False") // SQL Server required
            .UseMemoryCache(memoryCache);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
    }

    public DbSet<ReproEntity> Repros { get; set; }
}


public class ReproEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; }
}

internal class ReproEntityWrapper
{
    ReproEntity Entity { get; }
    internal ReproEntityWrapper(ReproObject2UnitOfWork reproObject2, ReproEntity entity)
    {
        Entity = entity;
    }
}


public static class MemoryLeaks
{
    public static bool CheckMemoryCacheForRelationalCommandMemoryLeak(IMemoryCache memoryCache, string namespaceToLookFor)
    {
        var entries = memoryCache.GetEntriesFromCache();
        foreach (var entry in entries)
        {
            var type = entry.GetType();
            var key = entry.GetType().GetProperty("Key")?.GetValue(entry, null);
            var value = entry.GetType().GetProperty("Value")?.GetValue(entry, null);
            if (key.GetType().FullName.Contains("CommandCacheKey"))
            {
                var propp = key.GetType().GetField("_parameterValues", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(key);
                if (propp != null && propp is Dictionary<string, object> lookup)
                {
                    foreach (var val in lookup.Values)
                    {
                        var valtype = val.GetType();
                        if (valtype.FullName.Contains(namespaceToLookFor, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }
        }
        return false;
    }
}

public static class MemoryCacheExtensions
{
    private static readonly Lazy<Func<MemoryCache, object>> GetCoherentState =
        new Lazy<Func<MemoryCache, object>>(() =>
            CreateGetter<MemoryCache, object>(typeof(MemoryCache)
                .GetField("_coherentState", BindingFlags.NonPublic | BindingFlags.Instance)));
    private static readonly Lazy<Func<object, IDictionary>> GetEntries7 =
        new Lazy<Func<object, IDictionary>>(() =>
            CreateGetter<object, IDictionary>(typeof(MemoryCache)
                .GetNestedType("CoherentState", BindingFlags.NonPublic)
                .GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance)));
    private static Func<TParam, TReturn> CreateGetter<TParam, TReturn>(FieldInfo field)
    {
        var methodName = $"{field.ReflectedType.FullName}.get_{field.Name}";
        var method = new DynamicMethod(methodName, typeof(TReturn), new[] { typeof(TParam) }, typeof(TParam), true);
        var ilGen = method.GetILGenerator();
        ilGen.Emit(OpCodes.Ldarg_0);
        ilGen.Emit(OpCodes.Ldfld, field);
        ilGen.Emit(OpCodes.Ret);
        return (Func<TParam, TReturn>)method.CreateDelegate(typeof(Func<TParam, TReturn>));
    }
    private static readonly Func<MemoryCache, IDictionary> GetEntries = cache => GetEntries7.Value(GetCoherentState.Value(cache));
    public static ICollection GetEntriesFromCache(this IMemoryCache memoryCache) =>
        GetEntries((MemoryCache)memoryCache);
    public static ICollection GetKeys(this IMemoryCache memoryCache) =>
        GetEntries((MemoryCache)memoryCache).Keys;
    public static IEnumerable<T> GetKeys<T>(this IMemoryCache memoryCache) =>
        memoryCache.GetKeys().OfType<T>();
}

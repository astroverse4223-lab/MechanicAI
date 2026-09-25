using MechanicAI.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace MechanicAI.Infrastructure.Persistence;

/// <summary>Adapts EF Core's pooled context factory to the application's persistence abstraction.</summary>
internal sealed class EfAppDbContextFactory<TContext>(IDbContextFactory<TContext> inner) : IAppDbContextFactory
    where TContext : AppDbContext
{
    public async Task<IAppDbContext> CreateAsync(CancellationToken cancellationToken = default) =>
        await inner.CreateDbContextAsync(cancellationToken);
}

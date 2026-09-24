using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Tests.Infrastructure;

/// <summary>
/// The real context plus one entity that exists only for these tests.
/// </summary>
/// <remarks>
/// What is under test here is the infrastructure — audit capture, the outbox,
/// append-only enforcement — not any particular business object. Putting a
/// stand-in entity in the production context to test the plumbing would leave a
/// table in the real schema that the business has no use for.
///
/// It derives from the real context rather than reimplementing it, so the
/// behaviour being asserted is the behaviour that ships.
/// </remarks>
public sealed class TestDbContext(
    DbContextOptions<TestDbContext> options,
    IClock clock,
    ICurrentUser currentUser)
    : AppDbContext(Rebind(options), clock, currentUser)
{
    public DbSet<Gadget> Gadgets => Set<Gadget>();

    public DbSet<Trinket> Trinkets => Set<Trinket>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Gadget>(gadget =>
        {
            gadget.ToTable("gadgets");
            gadget.HasKey(g => g.Id);

            /*
             * Two complex properties, one named in AuditExcludes and one not, because the
             * trail treats them differently and both branches are live code. Nothing in the
             * real model exercises the second one: all three complex properties in the
             * application are on Employee and all three are excluded — which is precisely
             * the situation in which an untested branch rots.
             */
            gadget.ComplexProperty(g => g.Shape, shape =>
            {
                shape.Property(one => one.Sides);
                shape.Property(one => one.Colour).HasMaxLength(50);
            });

            gadget.ComplexProperty(g => g.Innards, innards =>
            {
                innards.Property(one => one.SerialNumber).HasMaxLength(50);
                innards.Property(one => one.Firmware).HasMaxLength(50);
            });
            // Required, so a test can force the database to reject a write.
            // SQLite ignores column lengths, so an over-long value is not a
            // failure it will produce — a null in a NOT NULL column is.
            gadget.Property(g => g.Name).HasMaxLength(200).IsRequired();
            gadget.Property(g => g.Secret).HasMaxLength(200);
        });

        modelBuilder.Entity<Trinket>(trinket =>
        {
            trinket.ToTable("trinkets");
            trinket.HasKey(t => t.Id);
            trinket.Property(t => t.Name).HasMaxLength(200);
        });
    }

    /// <summary>
    /// EF ties options to the context type they were built for, and the base
    /// class wants its own. Same underlying configuration, retyped.
    /// </summary>
    private static DbContextOptions<AppDbContext> Rebind(DbContextOptions<TestDbContext> options)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();

        foreach (var extension in options.Extensions)
        {
            ((Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsBuilderInfrastructure)builder)
                .AddOrUpdateExtension(extension);
        }

        return builder.Options;
    }
}

/// <summary>An audited entity, standing in for anything the business cares about.</summary>
public sealed class Gadget : Entity, IAuditable
{
    public Gadget(string name)
    {
        Name = name;
        Raise(new GadgetMade(Id, name));
    }

    private Gadget()
    {
        Name = string.Empty;
    }

    public string Name { get; private set; }

    /// <summary>Stands in for a password hash or a token.</summary>
    public string? Secret { get; private set; }

    /// <summary>A complex property whose values the trail may write down.</summary>
    public Shape Shape { get; private set; } = new(0, null);

    /// <summary>A complex property whose values it may not.</summary>
    /// <remarks>
    /// Stands in for <c>Employee.Terms</c> — a salary. The trail records that it changed and
    /// refuses to record what it changed to.
    /// </remarks>
    public Innards Innards { get; private set; } = new(null, null);

    public static IReadOnlySet<string> AuditExcludes =>
        new HashSet<string> { nameof(Secret), nameof(Innards) };

    public void Rename(string name)
    {
        Name = name;
        Raise(new GadgetRenamed(Id, name));
    }

    public void SetSecret(string secret) => Secret = secret;

    public void Reshape(Shape shape) => Shape = shape;

    public void Rebuild(Innards innards) => Innards = innards;

    /// <summary>Assign the same value back, to prove nothing is recorded.</summary>
    public void TouchWithoutChanging() => Name = Name;
}

/// <summary>
/// A complex property the trail may record in full.
/// </summary>
/// <remarks>
/// A record struct replaced wholesale, which is how every complex property in this
/// application is written — <c>Details with { Phone = … }</c> — and the reason the trail
/// cannot trust EF's modified flag on one: assigning the whole value marks every member
/// assigned, so a change to a phone number would otherwise be recorded as a change to a
/// salary.
/// </remarks>
public readonly record struct Shape(int Sides, string? Colour);

/// <summary>A complex property whose values must stay out of the trail.</summary>
public readonly record struct Innards(string? SerialNumber, string? Firmware);

/// <summary>Not audited. Proves the trail is opt-in rather than everything.</summary>
public sealed class Trinket : Entity
{
    public Trinket(string name) => Name = name;

    private Trinket() => Name = string.Empty;

    public string Name { get; private set; }
}

public sealed record GadgetMade(Guid GadgetId, string Name) : DomainEvent;

public sealed record GadgetRenamed(Guid GadgetId, string Name) : DomainEvent;

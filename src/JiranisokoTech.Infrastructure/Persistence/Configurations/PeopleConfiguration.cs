using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.People;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiranisokoTech.Infrastructure.Persistence.Configurations;

public sealed class DepartmentConfiguration : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> builder)
    {
        builder.ToTable("departments");

        builder.HasKey(department => department.Id);

        builder.Property(department => department.Name).HasMaxLength(120).IsRequired();
        builder.Property(department => department.Slug).HasMaxLength(Slug.MaximumLength).IsRequired();
        builder.Property(department => department.Description).HasMaxLength(2000);

        // Derived from a value object on the way in, and not a column.
        builder.Ignore(department => department.Handle);

        // Unique, because the slug is what an address names. Two "field
        // operations" would make one of them unreachable.
        builder.HasIndex(department => department.Slug).IsUnique();

        builder.HasIndex(department => department.IsActive);

        /*
         * No foreign key to the head.
         *
         * Departments and employees point at each other — a department has a
         * head, an employee has a department — and a database cannot be given
         * both constraints without one of them having to be added second and
         * deferred. The pair is kept honest in the service that sets them, which
         * is also where the role change and the audit entry belong.
         */
        builder.HasIndex(department => department.HeadEmployeeId);
    }
}

public sealed class EmployeeConfiguration : IEntityTypeConfiguration<Employee>
{
    public void Configure(EntityTypeBuilder<Employee> builder)
    {
        builder.ToTable("employees");

        builder.HasKey(employee => employee.Id);

        builder.Property(employee => employee.FullName).HasMaxLength(200).IsRequired();
        builder.Property(employee => employee.JobTitle).HasMaxLength(150);

        // Stored as its number. An enum written as text is a value somebody
        // renames in code and orphans in the database.
        builder.Property(employee => employee.Status).HasConversion<int>().IsRequired();

        builder.Property(employee => employee.StartsOn).IsRequired();

        // Computed from Status, so there is nothing to store and nothing that
        // could disagree with it.
        builder.Ignore(employee => employee.IsAssignable);
        builder.Ignore(employee => employee.IsEmployed);

        /*
         * The department is a real foreign key; the line manager is not.
         *
         * A manager pointing at another row in this same table would make every
         * insert order-dependent and every delete a cascade nobody intended. The
         * loop rule is enforced in the domain, where it can look at the whole
         * chain, and a database constraint could not express it anyway.
         */
        builder.HasOne<Department>()
            .WithMany()
            .HasForeignKey(employee => employee.DepartmentId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(employee => employee.DepartmentId);
        builder.HasIndex(employee => employee.ReportsToId);
        builder.HasIndex(employee => employee.Status);

        /*
         * One account belongs to one person: two employee rows sharing a login
         * would make "who did this?" unanswerable.
         *
         * Unique without a filter, because both providers treat nulls in a
         * unique index as distinct from one another — so any number of people
         * can have no account, which is the ordinary case for somebody who has
         * not started yet.
         */
        builder.HasIndex(employee => employee.AccountId).IsUnique();
    }
}

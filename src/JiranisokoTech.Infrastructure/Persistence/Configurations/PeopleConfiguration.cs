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
         * Complex properties rather than owned entities, and the difference is not
         * cosmetic. An owned reference gets an identifying foreign key, so replacing
         * the whole object — which is what recording new details does, since these are
         * immutable records — makes EF complain that the key of an existing dependent
         * cannot change. A complex property has no identity at all: it is columns on
         * this table, which is what a value object is.
         *
         * All three live in the employees table rather than in joins. They are read
         * whenever the person is read and written whenever the person is written, and
         * separate tables would buy nothing but three more chances to forget an
         * Include.
         */
        builder.ComplexProperty(employee => employee.Details, details =>
        {
            details.Property(one => one.Phone).HasMaxLength(50);
            details.Property(one => one.PersonalEmail).HasMaxLength(200);
            details.Property(one => one.Address).HasMaxLength(500);
            details.Property(one => one.TimeZone).HasMaxLength(100);
            details.Property(one => one.Location).HasConversion<int?>();

            /*
             * The identity and tax numbers are stored as given rather than encrypted,
             * and that is a decision worth being explicit about rather than silently
             * making. Encrypting them here would put payroll behind a key ring that,
             * if lost, takes the firm's ability to file a return with it — and the
             * database is already the most protected thing in this deployment. What
             * they get instead is a narrower permission, masking on every screen, and
             * exclusion from the audit trail.
             */
            details.Property(one => one.NationalId).HasMaxLength(50);
            details.Property(one => one.TaxNumber).HasMaxLength(50);

            details.Ignore(one => one.MaskedNationalId);
            details.Ignore(one => one.MaskedTaxNumber);
            details.Ignore(one => one.IsEmpty);
        });

        builder.ComplexProperty(employee => employee.Emergency, contact =>
        {
            contact.Property(one => one.Name).HasMaxLength(200);
            contact.Property(one => one.Relationship).HasMaxLength(100);
            contact.Property(one => one.Phone).HasMaxLength(50);

            contact.Ignore(one => one.IsEmpty);
            contact.Ignore(one => one.IsReachable);
        });

        builder.ComplexProperty(employee => employee.Terms, terms =>
        {
            terms.Property(one => one.Contract).HasConversion<int?>();
            terms.Property(one => one.Frequency).HasConversion<int?>();
            terms.Property(one => one.SalaryCurrency).HasMaxLength(3);

            // Money is what the code works with; the column is a plain count of
            // minor units beside its currency, as a contract's value is.
            terms.Ignore(one => one.Salary);
            terms.Ignore(one => one.IsEmpty);
        });

        builder.OwnsMany(employee => employee.Skills, skill =>
        {
            skill.ToTable("employee_skills");
            skill.WithOwner().HasForeignKey("EmployeeId");
            skill.HasKey(one => one.Id);

            skill.Property(one => one.Name).HasMaxLength(100).IsRequired();
            skill.Property(one => one.Level).HasConversion<int>().IsRequired();

            // "Who here knows Rust" is the question this table exists to answer.
            skill.HasIndex(one => one.Name);
        });

        builder.OwnsMany(employee => employee.Certifications, certification =>
        {
            certification.ToTable("employee_certifications");
            certification.WithOwner().HasForeignKey("EmployeeId");
            certification.HasKey(one => one.Id);

            certification.Property(one => one.Name).HasMaxLength(200).IsRequired();
            certification.Property(one => one.Issuer).HasMaxLength(200).IsRequired();

            // What is about to lapse, which is the whole reason these are recorded.
            certification.HasIndex(one => one.ExpiresOn);
        });

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

public sealed class OffboardingConfiguration : IEntityTypeConfiguration<Offboarding>
{
    public void Configure(EntityTypeBuilder<Offboarding> builder)
    {
        builder.ToTable("offboardings");

        builder.HasKey(one => one.Id);

        builder.Property(one => one.ExitInterviewNotes).HasMaxLength(8000);

        builder.Ignore(one => one.IsComplete);
        builder.Ignore(one => one.HasOutstandingItems);

        /*
         * One departure per person. Somebody who leaves, is reinstated and leaves again is
         * a case this deliberately does not model: the second departure reuses the record,
         * because two open checklists for one person is how a laptop ends up on neither.
         */
        builder.HasIndex(one => one.EmployeeId).IsUnique();

        // The list that makes the feature worth having: what is not finished.
        builder.HasIndex(one => new { one.CompletedAt, one.LeavingOn });

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(one => one.EmployeeId)
            .OnDelete(DeleteBehavior.Cascade);

    }
}

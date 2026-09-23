using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.Contracts;
using JiranisokoTech.Domain.Money;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Recruitment;
using JiranisokoTech.Domain.Time;
using JiranisokoTech.Domain.Work;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiranisokoTech.Infrastructure.Persistence.Configurations;

public sealed class ClientConfiguration : IEntityTypeConfiguration<Client>
{
    public void Configure(EntityTypeBuilder<Client> builder)
    {
        builder.ToTable("clients");

        builder.HasKey(client => client.Id);

        builder.Property(client => client.Name).HasMaxLength(200).IsRequired();
        builder.Property(client => client.Code).HasMaxLength(Slug.MaximumLength).IsRequired();
        builder.Property(client => client.ContactName).HasMaxLength(200);
        builder.Property(client => client.ContactEmail).HasMaxLength(255);
        builder.Property(client => client.BillingEmail).HasMaxLength(255);
        builder.Property(client => client.Phone).HasMaxLength(40);
        builder.Property(client => client.Address).HasMaxLength(1000);
        builder.Property(client => client.Status).HasConversion<int>().IsRequired();

        builder.Ignore(client => client.IsCurrent);
        builder.Ignore(client => client.InvoiceAddress);

        // The code appears on invoices and in conversation, so two clients
        // cannot share one.
        builder.HasIndex(client => client.Code).IsUnique();
        builder.HasIndex(client => client.Status);
    }
}

public sealed class ContractConfiguration : IEntityTypeConfiguration<Contract>
{
    public void Configure(EntityTypeBuilder<Contract> builder)
    {
        builder.ToTable("contracts");

        builder.HasKey(contract => contract.Id);

        builder.Property(contract => contract.Reference).HasMaxLength(60).IsRequired();
        builder.Property(contract => contract.Title).HasMaxLength(300).IsRequired();
        builder.Property(contract => contract.Currency).HasMaxLength(3).IsRequired();
        builder.Property(contract => contract.State).HasConversion<int>().IsRequired();
        builder.Property(contract => contract.Outcome).HasMaxLength(2000);

        /*
         * The value is nullable because a draft has not agreed one yet, and it
         * is a plain count of minor units beside its currency for the same
         * reason a claim's is: a report can sum the column, and Money is what
         * the code works with.
         */
        builder.Ignore(contract => contract.Value);
        builder.Ignore(contract => contract.HasTerms);

        // What both sides quote at each other, so two contracts cannot share
        // one.
        builder.HasIndex(contract => contract.Reference).IsUnique();

        // The two readings: everything on one client, and what is running out.
        builder.HasIndex(contract => new { contract.ClientId, contract.State });
        builder.HasIndex(contract => new { contract.State, contract.EndsOn });

        builder.HasOne<Client>()
            .WithMany()
            .HasForeignKey(contract => contract.ClientId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class InterviewConfiguration : IEntityTypeConfiguration<Interview>
{
    public void Configure(EntityTypeBuilder<Interview> builder)
    {
        builder.ToTable("interviews");

        builder.HasKey(interview => interview.Id);

        builder.Property(interview => interview.Kind).HasConversion<int>().IsRequired();
        builder.Property(interview => interview.Status).HasConversion<int>().IsRequired();
        builder.Property(interview => interview.Where).HasMaxLength(500);
        builder.Property(interview => interview.Outcome).HasMaxLength(1000);

        builder.Ignore(interview => interview.IsScored);
        builder.Ignore(interview => interview.Outstanding);

        builder.HasOne<JobApplication>()
            .WithMany()
            .HasForeignKey(interview => interview.ApplicationId)
            .OnDelete(DeleteBehavior.Cascade);

        /*
         * Panel and scorecards are owned, not related.
         *
         * Every rule about them is about the set — who has submitted, who has
         * not, whether the same person scored twice — and a scorecard reachable
         * without its interview is one somebody can file against a conversation
         * that never happened.
         */
        builder.OwnsMany(interview => interview.Panel, panel =>
        {
            panel.ToTable("interview_panel");
            panel.WithOwner().HasForeignKey("InterviewId");
            panel.HasKey(one => one.Id);

            panel.HasIndex("InterviewId", nameof(InterviewPanellist.EmployeeId)).IsUnique();

            // "What am I interviewing this week?"
            panel.HasIndex(one => one.EmployeeId);
        });

        builder.OwnsMany(interview => interview.Scorecards, card =>
        {
            card.ToTable("scorecards");
            card.WithOwner().HasForeignKey("InterviewId");
            card.HasKey(one => one.Id);

            card.Property(one => one.Recommendation).HasConversion<int>().IsRequired();
            card.Property(one => one.Notes).HasMaxLength(8000).IsRequired();

            // One person, one view, once.
            card.HasIndex("InterviewId", nameof(Scorecard.InterviewerId)).IsUnique();
        });

        builder.HasIndex(interview => new { interview.Status, interview.ScheduledFor });
    }
}

public sealed class TimeEntryConfiguration : IEntityTypeConfiguration<TimeEntry>
{
    public void Configure(EntityTypeBuilder<TimeEntry> builder)
    {
        builder.ToTable("time_entries");

        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.Note).HasMaxLength(1000);
        builder.Property(entry => entry.On).IsRequired();

        builder.Ignore(entry => entry.IsLocked);
        builder.Ignore(entry => entry.Duration);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(entry => entry.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Project>()
            .WithMany()
            .HasForeignKey(entry => entry.ProjectId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<WorkItem>()
            .WithMany()
            .HasForeignKey(entry => entry.WorkItemId)
            .OnDelete(DeleteBehavior.SetNull);

        // The two readings: one person's week, and everything billable on a
        // project that has not been invoiced yet.
        builder.HasIndex(entry => new { entry.EmployeeId, entry.On });
        builder.HasIndex(entry => new { entry.ProjectId, entry.IsBillable, entry.InvoiceId });
    }
}

public sealed class LeaveRequestConfiguration : IEntityTypeConfiguration<LeaveRequest>
{
    public void Configure(EntityTypeBuilder<LeaveRequest> builder)
    {
        builder.ToTable("leave_requests");

        builder.HasKey(leave => leave.Id);

        builder.Property(leave => leave.Kind).HasConversion<int>().IsRequired();
        builder.Property(leave => leave.Status).HasConversion<int>().IsRequired();
        builder.Property(leave => leave.Reason).HasMaxLength(2000).IsRequired();
        builder.Property(leave => leave.Outcome).HasMaxLength(2000);

        // Counted from the dates rather than stored, so the two can never
        // disagree.
        builder.Ignore(leave => leave.Days);
        builder.Ignore(leave => leave.IsLive);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(leave => leave.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        // "Who is off next week?" and the overlap check on submission.
        builder.HasIndex(leave => new { leave.EmployeeId, leave.From });
        builder.HasIndex(leave => new { leave.Status, leave.From });
    }
}

public sealed class ExpenseClaimConfiguration : IEntityTypeConfiguration<ExpenseClaim>
{
    public void Configure(EntityTypeBuilder<ExpenseClaim> builder)
    {
        builder.ToTable("expense_claims");

        builder.HasKey(claim => claim.Id);

        builder.Property(claim => claim.Currency).HasMaxLength(3).IsRequired();
        builder.Property(claim => claim.Description).HasMaxLength(2000).IsRequired();
        builder.Property(claim => claim.Category).HasConversion<int>().IsRequired();
        builder.Property(claim => claim.Status).HasConversion<int>().IsRequired();
        builder.Property(claim => claim.Outcome).HasMaxLength(2000);
        builder.Property(claim => claim.ReceiptFileName).HasMaxLength(255);
        builder.Property(claim => claim.ReceiptStoredName).HasMaxLength(100);

        // Rebuilt from the two columns, so a report can still sum them in SQL.
        builder.Ignore(claim => claim.Amount);
        builder.Ignore(claim => claim.HasReceipt);
        builder.Ignore(claim => claim.IsOwed);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(claim => claim.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        // "What do we owe our staff?" is a status scan.
        builder.HasIndex(claim => new { claim.Status, claim.SpentOn });
        builder.HasIndex(claim => new { claim.EmployeeId, claim.SpentOn });
    }
}

public sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTable("invoices");

        builder.HasKey(invoice => invoice.Id);

        builder.Property(invoice => invoice.Number).HasMaxLength(40).IsRequired();
        builder.Property(invoice => invoice.Currency).HasMaxLength(3).IsRequired();
        builder.Property(invoice => invoice.Status).HasConversion<int>().IsRequired();
        builder.Property(invoice => invoice.Outcome).HasMaxLength(2000);

        // Totals are summed from the lines. A stored total is a number that can
        // disagree with what it is a total of.
        builder.Ignore(invoice => invoice.Total);
        builder.Ignore(invoice => invoice.Paid);
        builder.Ignore(invoice => invoice.Outstanding);
        builder.Ignore(invoice => invoice.IsSettled);

        // The number a client quotes back at us. Never reused.
        builder.HasIndex(invoice => invoice.Number).IsUnique();
        builder.HasIndex(invoice => new { invoice.Status, invoice.DueOn });

        builder.HasOne<Client>()
            .WithMany()
            .HasForeignKey(invoice => invoice.ClientId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.OwnsMany(invoice => invoice.Lines, line =>
        {
            line.ToTable("invoice_lines");
            line.WithOwner().HasForeignKey("InvoiceId");
            line.HasKey(one => one.Id);

            line.Property(one => one.Description).HasMaxLength(500).IsRequired();
            line.Property(one => one.Currency).HasMaxLength(3).IsRequired();

            line.Ignore(one => one.UnitPrice);
            line.Ignore(one => one.Amount);
        });

        builder.OwnsMany(invoice => invoice.Payments, payment =>
        {
            payment.ToTable("invoice_payments");
            payment.WithOwner().HasForeignKey("InvoiceId");
            payment.HasKey(one => one.Id);

            payment.Property(one => one.Reference).HasMaxLength(100);
        });
    }
}

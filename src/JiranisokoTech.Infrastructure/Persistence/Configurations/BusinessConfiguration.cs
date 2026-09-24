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

        /*
         * The approval queue, and the only partial index in this schema.
         *
         * Added because the scale check measured it: at a quarter of a million entries the
         * queue was a sequential scan of the whole table, because unapproved entries are a
         * fifth of it and a plain index on ApprovedAt would not have been worth using. A
         * partial index holds only the rows that are null, so it is the size of the queue
         * rather than the size of the history — and the queue is small in a firm where
         * somebody approves timesheets, which is the firm this is for.
         *
         * Ordered by the day, because that is what the queue is sorted by and what its cap
         * depends on: the index answers the filter and the order together, so the plan has
         * nothing left to sort.
         */
        builder.HasIndex(entry => entry.On)
            .HasFilter("\"ApprovedAt\" IS NULL")
            .HasDatabaseName("IX_time_entries_awaiting_approval");
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

        /*
         * Days is a column, and it used to be Ignore'd here with a note saying
         * that counting it from the dates meant the two could never disagree.
         * That stopped being available once public holidays came in: deciding
         * whether a date is a working day now needs the calendar, and an
         * aggregate cannot have one. The reasoning and what it costs are set out
         * on LeaveRequest.Days.
         *
         * Being a column is the part that earns it back. The leave list selects
         * it in SQL, which retired the second copy of the counting rule that
         * lived in the infrastructure's leave query purely to avoid loading
         * thirty aggregates to print thirty numbers.
         */
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

public sealed class HolidayConfiguration : IEntityTypeConfiguration<Holiday>
{
    public void Configure(EntityTypeBuilder<Holiday> builder)
    {
        builder.ToTable("public_holidays");

        builder.HasKey(holiday => holiday.Id);

        builder.Property(holiday => holiday.Name).HasMaxLength(120).IsRequired();

        /*
         * Unique, and enforced by the database rather than only by the service
         * that checks for it first. Two rows for one date do not break the day
         * count — the calendar is read as a set of dates and a set absorbs the
         * duplicate — but they make the screen misreport what is on the calendar,
         * and withdrawing the day then only half works. The check in the service
         * exists to give somebody a sentence rather than a constraint violation;
         * this is what holds when two people submit the same date at once.
         */
        builder.HasIndex(holiday => holiday.On).IsUnique();
    }
}

public sealed class ExpenseClaimConfiguration : IEntityTypeConfiguration<ExpenseClaim>
{
    public void Configure(EntityTypeBuilder<ExpenseClaim> builder)
    {
        builder.ToTable("expense_claims");

        builder.HasKey(claim => claim.Id);

        /*
         * The project a cost belongs to, so a project can show what it really took. Set
         * to null rather than cascading when a project is deleted: the money left the
         * firm and the claim is the record of that, whatever happens to the project it
         * was spent on.
         */
        builder.HasOne<Project>()
            .WithMany()
            .HasForeignKey(claim => claim.ProjectId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(claim => new { claim.ProjectId, claim.Status });

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
        // The same restriction, for the same reason, on the other side of the report.
        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(claim => claim.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

    }
}

public sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTable("invoices");

        builder.HasKey(invoice => invoice.Id);

        // Which project this bills for, so a project can show what it earned. Set to
        // null on delete, for the same reason a claim's is: the client was invoiced.
        builder.HasOne<Project>()
            .WithMany()
            .HasForeignKey(invoice => invoice.ProjectId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(invoice => new { invoice.ProjectId, invoice.Status });

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
        /*
         * Which income account this bill's revenue is. Restricted rather than nulled, unlike
         * the project above: a project going away leaves an invoice that was still issued, so
         * nulling that link loses nothing. The report is grouped by account, and nulling this
         * one would silently move money that has already been reported into the unclassified
         * row — a figure changing after somebody has read it.
         */
        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(invoice => invoice.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        // The income report's own reading: what was issued, over a window.
        builder.HasIndex(invoice => new { invoice.Status, invoice.IssuedOn });

    }
}

public sealed class AgreementConfiguration : IEntityTypeConfiguration<Agreement>
{
    public void Configure(EntityTypeBuilder<Agreement> builder)
    {
        builder.ToTable("agreements");

        builder.HasKey(one => one.Id);

        builder.Property(one => one.Kind).HasConversion<int>().IsRequired();
        builder.Property(one => one.Reference).HasMaxLength(60).IsRequired();
        builder.Property(one => one.Title).HasMaxLength(300).IsRequired();
        builder.Property(one => one.Party).HasMaxLength(300).IsRequired();
        builder.Property(one => one.State).HasConversion<int>().IsRequired();
        builder.Property(one => one.Outcome).HasMaxLength(500);
        builder.Property(one => one.Notes).HasMaxLength(2_000);

        builder.Ignore(one => one.IsInForce);
        builder.Ignore(one => one.IsLive);

        /*
         * The firm's own file number, and no two pieces of paper share one. It is what somebody
         * quotes in an email, and two answering to it make every reference ambiguous — including
         * the ones already sent.
         */
        builder.HasIndex(one => one.Reference).IsUnique();

        /*
         * The one query anything runs on its own: what runs out soon. The reminder job reads it
         * every morning.
         */
        builder.HasIndex(one => one.EndsOn);

        builder.HasIndex(one => one.EmployeeId);

        /*
         * SetNull rather than Cascade. A person deleted from the staff list must not take their
         * employment contract with them — it is the firm's record of what was agreed, and it is
         * precisely the document somebody asks for after they have gone.
         */
        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(one => one.EmployeeId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
